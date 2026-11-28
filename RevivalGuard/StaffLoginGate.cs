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
    /// <summary>Staff accounts log in only from the shard's own network, and passwords cannot be guessed in bulk.</summary>
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
                    // Fail closed for staff: no address means nobody can say it is local
                    if (s_GateOn && account != null && account.AccessLevel > 0)
                        return Refuse(session, $"staff login refused: account '{account.AccountName}' "
                                             + $"(level {account.AccessLevel}): its address could not be read");
                    return true;
                }
                bool local = IsLocal(addr);
                string key = addr.ToString();

                if (account == null) return true;   // ACE answers "account does not exist" itself

                if (s_GateOn && account.AccessLevel > 0 && Gateway.IsUnknown(addr))
                    return Refuse(session, $"staff login refused: account '{account.AccountName}' "
                                         + $"(level {account.AccessLevel}): the browser gateway could not say where it comes from");

                if (s_GateOn && account.AccessLevel > 0 && !local && !s_Allow.Contains(key) && !InLiveAllow(key))
                {
                    Mod.Log.Warn($"[RevivalGuard] staff login refused: account '{account.AccountName}' "
                        + $"(level {account.AccessLevel}) from {key}, outside the shard's network");
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

        /// <summary>Refuse a login the way ACE refuses a wrong password: its ConnectRequest first, then the boot.</summary>
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

        /// <summary>A locked-out address is refused before ACE looks the account up.</summary>
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

        /// <summary>Is this address in rg_staff_allow?</summary>
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

        /// <summary>Is this address a shared relay (rg_shared_relays)?</summary>
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
