using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using ACE.Database.Models.Auth;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Packets;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// STAFF ACCOUNTS LOG IN ONLY FROM THE SHARD'S OWN NETWORK, AND PASSWORDS CANNOT BE GUESSED IN BULK.
    /// Release gate #4 (2026-09-24). Ten accounts on this shard are AccessLevel 5 (an admin account, admin, dev,
    /// devbot..devbot7), and the dev fleet's convention is password = account name (AcLogin's class
    /// note). That is fine on a LAN and fatal on a public port: anyone who types "devbot" twice is an
    /// admin. Rotating every bot's password would break the fleet, so the rule is where the login
    /// comes from instead:
    ///
    ///   * An account above Player logs in only from loopback, RFC 1918 private ranges, Tailscale
    ///     (100.64.0.0/10), IPv6 loopback/link-local/ULA, or an address listed in
    ///     REVIVAL_STAFF_ALLOW (comma-separated, exact IPs), or in the live property rg_staff_allow (the same form; owner
    ///     2026-11-03, away from home for about 5 days: staff tell us their current IP and it is added from the OVH
    ///     console with no restart, and removed when they are home). Anywhere else gets the wrong-password
    ///     boot, word for word, so the gate does not confirm that a staff account exists.
    ///   * Any address that fails a password FAILS_ALLOWED times inside WINDOW is refused for LOCKOUT
    ///     (ACE's own TO-DO at the mismatch: "temporary lockout ... preventing brute force").
    ///     Loopback and private ranges are exempt, so the fleet cannot lock itself out.
    ///   * A SHARED RELAY (rg_shared_relays, comma-separated exact IPs; default webAC's hosted bridge
    ///     184.193.176.126, its docs/operators.md) carries many players from one address, so one player's typos
    ///     must not lock out the rest: it is locked out only after RELAY_FAILS_ALLOWED. It is NOT local:
    ///     staff accounts are still refused from it. Owner 2026-11-03, before listing Hollowtide on the
    ///     community server list that webAC reads.
    ///
    /// REVIVAL_STAFF_GATE=0 turns the staff rule off (the throttle stays).
    /// </summary>
    [HarmonyPatch]
    static class StaffLoginGate
    {
        const int FAILS_ALLOWED = 8, RELAY_FAILS_ALLOWED = 60;
        internal const string P_RELAYS = "rg_shared_relays", DEFAULT_RELAYS = "184.193.176.126";
        internal const string P_STAFF_ALLOW = "rg_staff_allow";
        static readonly TimeSpan WINDOW = TimeSpan.FromMinutes(10);
        static readonly TimeSpan LOCKOUT = TimeSpan.FromMinutes(15);
        const string BAD_PASSWORD = " because the password entered for this account was not correct";

        static readonly bool s_GateOn = Environment.GetEnvironmentVariable("REVIVAL_STAFF_GATE") != "0";
        static readonly HashSet<string> s_Allow = ParseAllow();

        sealed class Fails { public readonly Queue<DateTime> At = new Queue<DateTime>(); public DateTime LockedUntil; }
        static readonly ConcurrentDictionary<string, Fails> s_Fails = new ConcurrentDictionary<string, Fails>();

        /// <summary>The addresses locked out now, and until when (AdminOps @lockouts).</summary>
        internal static List<(string key, DateTime until)> Lockouts() =>
            s_Fails.Where(kv => kv.Value.LockedUntil > DateTime.UtcNow).Select(kv => (kv.Key, kv.Value.LockedUntil)).ToList();

        /// <summary>Lift every lockout and forget the counted failures (AdminOps @lockouts clear). 2026-10-12: the
        /// owner's own address was locked out by his client retrying a refused staff login, and only a shard
        /// restart could clear it.</summary>
        internal static int ClearLockouts() { int n = Lockouts().Count; s_Fails.Clear(); return n; }

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method("ACE.Server.Network.Handlers.AuthenticationHandler:AccountSelectCallback");

        static bool Prefix(Account account, Session session, PacketInboundLoginRequest loginRequest)
        {
            try
            {
                var addr = Gateway.RealAddress(session?.EndPointC2S);   // a browser player's own, not the gateway's loopback
                if (addr == null)
                {
                    // FAIL CLOSED FOR STAFF (security audit 2026-10-20): no address means nobody can say it is local
                    if (s_GateOn && account != null && account.AccessLevel > 0)
                        return Refuse(session, $"staff login refused: account '{account.AccountName}' "
                                             + $"(level {account.AccessLevel}): its address could not be read");
                    return true;
                }
                bool local = IsLocal(addr);
                string key = addr.ToString();

                if (account == null) return true;   // ACE answers "account does not exist" itself

                if (s_GateOn && account.AccessLevel > 0 && !local && !s_Allow.Contains(key) && !InLiveAllow(key))
                {
                    Mod.Log.Warn($"[RevivalGuard] staff login refused: account '{account.AccountName}' "
                        + $"(level {account.AccessLevel}) from {key}, outside the shard's network");
                    // COUNTED ONLY WHEN THE PASSWORD WAS WRONG TOO (2026-10-12: the owner's client, with the RIGHT
                    // password, retried a refused staff login every few seconds after a deploy had blanked the allow
                    // list, and the "failures" locked his whole address out: his browser character as well). A right
                    // password from the wrong place is still refused, with the same words as a wrong one.
                    if (loginRequest != null && loginRequest.NetAuthType == NetAuthType.AccountPassword
                        && !account.PasswordMatches(loginRequest.Password))
                        Fail(key);
                    Boot(session, BAD_PASSWORD);
                    return false;
                }

                if (!local && loginRequest != null && loginRequest.NetAuthType == NetAuthType.AccountPassword
                    && !account.PasswordMatches(loginRequest.Password))
                    Fail(key);                       // ACE boots it on its own; this only counts
            }
            catch (Exception e)
            {
                Mod.Log.Error("[RevivalGuard] StaffLoginGate: " + e.Message);
                // FAIL CLOSED FOR STAFF (security audit 2026-10-20): a gate that throws must not let a staff login through
                if (s_GateOn && account != null && account.AccessLevel > 0)
                    return Refuse(session, $"staff login refused: account '{account.AccountName}' "
                                         + $"(level {account.AccessLevel}): the gate failed ({e.GetType().Name})");
            }
            return true;
        }

        /// <summary>Refuse with the same words as a wrong password, and log why.</summary>
        static bool Refuse(Session session, string why)
        {
            Mod.Log.Warn("[RevivalGuard] " + why);
            try
            {
                if (session != null) Boot(session, BAD_PASSWORD);
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] StaffLoginGate: could not end the session: " + e.Message); }
            return false;
        }

        /// <summary>Refuse a login the way ACE refuses a wrong password: its ConnectRequest first, then the boot. ACE sends
        /// the ConnectRequest before any of its own refusals (AuthenticationHandler.AccountSelectCallback), and the client
        /// can read a boot only on that connection. Without it the boot reached no one, the client resent its LoginRequest
        /// and said the server did not respond (Mastacho, 2026-11-07, locked out after 8 wrong passwords).</summary>
        static void Boot(Session session, string reason)
        {
            var net = session.Network;
            var cd = net?.ConnectionData;
            if (cd?.ServerSeed != null && cd.ClientSeed != null)
            {
                var connect = new PacketOutboundConnectRequest(Timers.PortalYearTicks, cd.ConnectionCookie, net.ClientId, cd.ServerSeed, cd.ClientSeed);
                cd.DiscardSeeds();   // ACE's order: built, seeds discarded, sent
                net.EnqueueSend(connect);
            }
            session.Terminate(SessionTerminationReason.NotAuthorizedPasswordMismatch, new GameMessageBootAccount(reason));
        }

        /// <summary>A locked-out address is refused before ACE looks the account up. Checked in AccountSelectCallback it
        /// came after DoLogin's auto-create, so a mistyped name made a new empty account even while locked out ("Mastacho",
        /// a character's name typed as the account, 2026-11-07).</summary>
        [HarmonyPatch]
        static class LockedOut
        {
            static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method("ACE.Server.Network.Handlers.AuthenticationHandler:DoLogin");

            static bool Prepare(System.Reflection.MethodBase original)
            {
                if (original != null || TargetMethod() != null) return true;
                Mod.Log.Error("[RevivalGuard] StaffLoginGate: AuthenticationHandler.DoLogin not found; login lockouts are off");
                return false;
            }

            static bool Prefix(Session session)
            {
                try
                {
                    var addr = Gateway.RealAddress(session?.EndPointC2S);
                    if (addr == null || IsLocal(addr) || !s_Fails.TryGetValue(addr.ToString(), out var f)) return true;
                    DateTime until;
                    lock (f) until = f.LockedUntil;
                    if (DateTime.UtcNow >= until) return true;
                    int min = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes));
                    Boot(session, $" because of too many wrong passwords from your connection; try again in {min} minute{(min == 1 ? "" : "s")}");
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Error("[RevivalGuard] StaffLoginGate lockout: " + e.Message);
                    return true;
                }
            }
        }

        static void Fail(string key)
        {
            var f = s_Fails.GetOrAdd(key, _ => new Fails());
            lock (f)
            {
                var now = DateTime.UtcNow;
                f.At.Enqueue(now);
                while (f.At.Count > 0 && now - f.At.Peek() > WINDOW) f.At.Dequeue();
                if (f.At.Count >= (IsSharedRelay(key) ? RELAY_FAILS_ALLOWED : FAILS_ALLOWED))
                {
                    f.LockedUntil = now + LOCKOUT;
                    f.At.Clear();
                    Mod.Log.Warn($"[RevivalGuard] {key} locked out of logins for {LOCKOUT.TotalMinutes:F0} min "
                        + $"after {(IsSharedRelay(key) ? RELAY_FAILS_ALLOWED : FAILS_ALLOWED)} failed passwords");
                }
            }
        }

        /// <summary>Register rg_shared_relays so the admin panel (@modifystring) can change it without a restart.</summary>
        internal static void Register()
        {
            try
            {
                var s = MonsterAi.Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_RELAYS)) s[P_RELAYS] = new Property<string>(DEFAULT_RELAYS,
                    "RevivalGuard: shared relays (comma-separated exact IPs, e.g. webAC's bridge) whose many players share one address: higher login-lockout and new-account limits; never staff");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {P_RELAYS}: not registered ({e.Message}); still read, default {DEFAULT_RELAYS}"); }
            try
            {
                var s = MonsterAi.Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_STAFF_ALLOW)) s[P_STAFF_ALLOW] = new Property<string>("",
                    "RevivalGuard StaffLoginGate: extra exact IPs (comma-separated) staff may log in from, beside REVIVAL_STAFF_ALLOW; empty = none");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {P_STAFF_ALLOW}: not registered ({e.Message}); still read, default empty"); }
        }

        /// <summary>Is this address in rg_staff_allow? Read live, so an added or removed IP applies at the next login.</summary>
        static bool InLiveAllow(string key)
        {
            try
            {
                foreach (var ip in (PkCommon.Str(P_STAFF_ALLOW, "") ?? "").Split(','))
                    if (ip.Trim().Length > 0 && string.Equals(ip.Trim(), key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Is this address a shared relay (rg_shared_relays)? Read live, so a changed list applies at once.</summary>
        internal static bool IsSharedRelay(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            return IsSharedRelay(a.ToString());
        }

        static bool IsSharedRelay(string key)
        {
            try
            {
                foreach (var ip in (PkCommon.Str(P_RELAYS, DEFAULT_RELAYS) ?? "").Split(','))
                    if (ip.Trim().Length > 0 && string.Equals(ip.Trim(), key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        internal static bool IsLocal(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) return true;
                byte b0 = a.GetAddressBytes()[0];
                return (b0 & 0xFE) == 0xFC;                      // fc00::/7 unique local
            }
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // Tailscale / CGNAT
        }

        static HashSet<string> ParseAllow()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = Environment.GetEnvironmentVariable("REVIVAL_STAFF_ALLOW");
            if (!string.IsNullOrWhiteSpace(raw))
                foreach (var s in raw.Split(','))
                    if (IPAddress.TryParse(s.Trim(), out var ip)) set.Add(ip.ToString());
            return set;
        }
    }
}
