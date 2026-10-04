using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using ACE.Database.Models.Auth;
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
    ///     REVIVAL_STAFF_ALLOW (comma-separated, exact IPs). Anywhere else gets the wrong-password
    ///     boot, word for word, so the gate does not confirm that a staff account exists.
    ///   * Any address that fails a password FAILS_ALLOWED times inside WINDOW is refused for LOCKOUT
    ///     (ACE's own TO-DO at the mismatch: "temporary lockout ... preventing brute force").
    ///     Loopback and private ranges are exempt, so the fleet cannot lock itself out.
    ///
    /// REVIVAL_STAFF_GATE=0 turns the staff rule off (the throttle stays).
    /// </summary>
    [HarmonyPatch]
    static class StaffLoginGate
    {
        const int FAILS_ALLOWED = 8;
        static readonly TimeSpan WINDOW = TimeSpan.FromMinutes(10);
        static readonly TimeSpan LOCKOUT = TimeSpan.FromMinutes(15);
        const string BAD_PASSWORD = " because the password entered for this account was not correct";

        static readonly bool s_GateOn = Environment.GetEnvironmentVariable("REVIVAL_STAFF_GATE") != "0";
        static readonly HashSet<string> s_Allow = ParseAllow();

        sealed class Fails { public readonly Queue<DateTime> At = new Queue<DateTime>(); public DateTime LockedUntil; }
        static readonly ConcurrentDictionary<string, Fails> s_Fails = new ConcurrentDictionary<string, Fails>();

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method("ACE.Server.Network.Handlers.AuthenticationHandler:AccountSelectCallback");

        static bool Prefix(Account account, Session session, PacketInboundLoginRequest loginRequest)
        {
            try
            {
                var addr = session?.EndPointC2S?.Address;
                if (addr == null) return true;
                bool local = IsLocal(addr);
                string key = addr.ToString();

                if (!local && s_Fails.TryGetValue(key, out var f))
                    lock (f)
                        if (DateTime.UtcNow < f.LockedUntil)
                        {
                            session.Terminate(SessionTerminationReason.NotAuthorizedPasswordMismatch,
                                new GameMessageBootAccount(" because of too many failed logins; try again in a few minutes"));
                            return false;
                        }

                if (account == null) return true;   // ACE answers "account does not exist" itself

                if (s_GateOn && account.AccessLevel > 0 && !local && !s_Allow.Contains(key))
                {
                    Mod.Log.Warn($"[RevivalGuard] staff login refused: account '{account.AccountName}' "
                        + $"(level {account.AccessLevel}) from {key}, outside the shard's network");
                    Fail(key);
                    session.Terminate(SessionTerminationReason.NotAuthorizedPasswordMismatch,
                        new GameMessageBootAccount(BAD_PASSWORD));
                    return false;
                }

                if (!local && loginRequest != null && loginRequest.NetAuthType == NetAuthType.AccountPassword
                    && !account.PasswordMatches(loginRequest.Password))
                    Fail(key);                       // ACE boots it on its own; this only counts
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] StaffLoginGate: " + e.Message); }
            return true;
        }

        static void Fail(string key)
        {
            var f = s_Fails.GetOrAdd(key, _ => new Fails());
            lock (f)
            {
                var now = DateTime.UtcNow;
                f.At.Enqueue(now);
                while (f.At.Count > 0 && now - f.At.Peek() > WINDOW) f.At.Dequeue();
                if (f.At.Count >= FAILS_ALLOWED)
                {
                    f.LockedUntil = now + LOCKOUT;
                    f.At.Clear();
                    Mod.Log.Warn($"[RevivalGuard] {key} locked out of logins for {LOCKOUT.TotalMinutes:F0} min "
                        + $"after {FAILS_ALLOWED} failed passwords");
                }
            }
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
