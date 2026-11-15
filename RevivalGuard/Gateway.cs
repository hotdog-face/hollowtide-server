using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using ACE.Database;
using ACE.Entity.Enum;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE BROWSER PLAYER'S REAL ADDRESS (docs/BROWSER-CLIENT-PLAN.md, "Blocker before any public hosting").
    /// A browser cannot send UDP, so browser players reach ACE through the WebSocket gateway on this machine
    /// (tools/public-shard/ws-gateway), one UDP socket each, and ACE sees every one of them at 127.0.0.1. This
    /// shard TRUSTS its own network: StaffLoginGate lets admin logins in only from it, and the new-account cap
    /// and the account query's rate limit exempt it. Through the gateway that trust would extend to anyone.
    ///
    /// So a loopback endpoint is asked about: the gateway answers GET /who?port=N (loopback only) with the
    /// address of the player behind its UDP port N, and every check here uses that address instead. The decision
    /// table is GatewayAddressRule (pure, unit-tested in tools/nettest): it FAILS CLOSED (security review
    /// 2026-11-14, SEC-001): a loopback endpoint stays local only when no gateway runs here or a version-2 gateway
    /// is sure the port is not its own; anything else that is not a positive address is UNKNOWN (not trusted).
    ///
    /// No answer is cached for trust decisions. <see cref="RealAddress"/> always asks; the packet-thread rate
    /// limits use <see cref="RealAddressForLimits"/>, whose port-keyed cache holds only positive OUTSIDE answers
    /// for a few seconds, so a port the gateway hands to the next browser session never inherits local trust.
    ///
    /// The gateway is asked at http://127.0.0.1:9080 (REVIVAL_GATEWAY to move it, "off" to never ask).
    /// </summary>
    static class Gateway
    {
        static readonly string s_Url = GatewayUrl();
        static string GatewayUrl()
        {
            var v = Environment.GetEnvironmentVariable("REVIVAL_GATEWAY");
            if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return null;
            return string.IsNullOrWhiteSpace(v) ? "http://127.0.0.1:9080" : v.TrimEnd('/');
        }
        static readonly HttpClient s_Http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        static readonly ConcurrentDictionary<int, (IPAddress addr, DateTime at)> s_Cache = new ConcurrentDictionary<int, (IPAddress, DateTime)>();
        const int CACHE_MAX = 4096;

        /// <summary>Is this the "gateway could not say" marker?</summary>
        internal static bool IsUnknown(IPAddress a) => a != null && a.Equals(GatewayAddressRule.UNKNOWN);

        /// <summary>The address to judge this endpoint by, asked now (never cached): every trust decision.</summary>
        internal static IPAddress RealAddress(IPEndPoint ep)
        {
            if (ep == null) return null;
            if (!GatewayAddressRule.NeedsAsking(ep.Address, s_Url != null)) return ep.Address;
            return Ask(ep);
        }

        /// <summary>The same, for the unauthenticated packet-thread rate limits (AccountQuery, PasswordRecovery):
        /// a positive outside answer is reused for a few seconds. Never use it to grant trust.</summary>
        internal static IPAddress RealAddressForLimits(IPEndPoint ep)
        {
            if (ep == null) return null;
            if (!GatewayAddressRule.NeedsAsking(ep.Address, s_Url != null)) return ep.Address;
            var now = DateTime.UtcNow;
            if (s_Cache.TryGetValue(ep.Port, out var hit) && GatewayAddressRule.Fresh(hit.at, now)) return hit.addr;
            var real = Ask(ep);
            if (GatewayAddressRule.Cacheable(ep.Address, real, StaffLoginGate.IsLocal(real)))
            {
                if (s_Cache.Count >= CACHE_MAX)
                    foreach (var kv in s_Cache)
                        if (!GatewayAddressRule.Fresh(kv.Value.at, now)) s_Cache.TryRemove(kv.Key, out _);
                if (s_Cache.Count < CACHE_MAX) s_Cache[ep.Port] = (real, now);
            }
            else s_Cache.TryRemove(ep.Port, out _);
            return real;
        }

        static IPAddress Ask(IPEndPoint ep)
        {
            try
            {
                using var r = s_Http.GetAsync($"{s_Url}/who?port={ep.Port}").GetAwaiter().GetResult();
                string version = r.Headers.TryGetValues(GatewayAddressRule.VERSION_HEADER, out var vs) ? string.Join(",", vs) : null;
                string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var real = GatewayAddressRule.Decide(ep.Address, GatewayAddressRule.Reply.Answered, (int)r.StatusCode, version, body);
                if (IsUnknown(real))
                    Mod.Log.Warn($"[RevivalGuard] gateway could not say who is on port {ep.Port} (HTTP {(int)r.StatusCode}, who-version {version ?? "none"}); not trusting it");
                return real;
            }
            catch (Exception e) when (Refused(e))
            {
                return GatewayAddressRule.Decide(ep.Address, GatewayAddressRule.Reply.Refused);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] gateway did not say who is on port {ep.Port} ({e.Message}); not trusting it");
                return GatewayAddressRule.Decide(ep.Address, GatewayAddressRule.Reply.Failed);
            }
        }

        static bool Refused(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
                if (x is System.Net.Sockets.SocketException se && se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused) return true;
            return false;
        }

        // THE ADDRESS AN ACCOUNT IS CREATED FROM. AccountCreationCap counts accounts by the creation address ACE
        // stores, and ACE stores the session's (127.0.0.1 for every browser player). DoLogin runs CreateAccount
        // on its own thread with no port in the call, so the login prefix leaves the real address here first.
        [ThreadStatic] internal static IPAddress t_LoginReal;

        [HarmonyPatch(typeof(AuthenticationDatabase), nameof(AuthenticationDatabase.CreateAccount))]
        static class CreateFromRealAddress
        {
            static void Prefix(ref IPAddress address)
            {
                if (t_LoginReal != null && address != null && IPAddress.IsLoopback(address)) address = t_LoginReal;
            }
        }
    }
}
