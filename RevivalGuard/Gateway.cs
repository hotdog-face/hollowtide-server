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
    /// address of the player behind its UDP port N, and every check here uses that address instead. A loopback
    /// endpoint the gateway does not know (a client on this machine, the fleet) stays loopback. Answers are
    /// cached for the life of the port; a gateway that does not answer within 500 ms leaves the address as it
    /// was, which for a browser session means NOT trusted: <see cref="RealAddress"/> returns a marker address
    /// outside every local range when the gateway is configured and silent, so a failure fails closed.
    ///
    /// The gateway is asked at http://127.0.0.1:9080 (REVIVAL_GATEWAY to move it, "off" to never ask). No
    /// gateway running (the connection refused) means nobody can be on one, so a loopback client stays local;
    /// a gateway that answers 404 for a port says that client is not one of its players; only a gateway that
    /// is up and fails to answer makes the session UNKNOWN (not trusted).
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
        static readonly TimeSpan CACHE = TimeSpan.FromMinutes(10);

        /// <summary>192.0.2.1 (TEST-NET-1): "a browser player whose address the gateway could not give". Not
        /// local, so nothing trusts it; shared, so the per-address limits fall on all such sessions together.</summary>
        static readonly IPAddress UNKNOWN = IPAddress.Parse("192.0.2.1");

        /// <summary>Is this the "gateway could not say" marker?</summary>
        internal static bool IsUnknown(IPAddress a) => a != null && a.Equals(UNKNOWN);

        /// <summary>The address to judge this endpoint by.</summary>
        internal static IPAddress RealAddress(IPEndPoint ep)
        {
            if (ep == null) return null;
            var a = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            if (s_Url == null || !IPAddress.IsLoopback(a)) return ep.Address;
            if (s_Cache.TryGetValue(ep.Port, out var hit) && DateTime.UtcNow - hit.at < CACHE) return hit.addr;
            IPAddress real;
            try
            {
                var r = s_Http.GetAsync($"{s_Url}/who?port={ep.Port}").GetAwaiter().GetResult();
                if (r.StatusCode == HttpStatusCode.NotFound) real = ep.Address;   // not a gateway port: a local client
                else
                {
                    string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                    real = r.IsSuccessStatusCode && IPAddress.TryParse(body, out var p) ? p : UNKNOWN;
                }
            }
            catch (Exception e) when (Refused(e))
            {
                real = ep.Address;   // no gateway on this machine: nobody can have come through one
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] gateway did not say who is on port {ep.Port} ({e.Message}); not trusting it");
                real = UNKNOWN;
            }
            s_Cache[ep.Port] = (real, DateTime.UtcNow);
            return real;
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
