using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using ACE.Database;
using ACE.Entity.Enum;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The browser player's real address (docs/BROWSER-CLIENT-PLAN.md, "Blocker before any public hosting").</summary>
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

        internal static IPAddress RealAddress(IPEndPoint ep)
        {
            if (ep == null) return null;
            if (!GatewayAddressRule.NeedsAsking(ep.Address, s_Url != null)) return ep.Address;
            return Ask(ep);
        }

        /// <summary>The same, for the unauthenticated packet-thread rate limits: a positive outside answer is reused for a few seconds.</summary>
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

        // The address an account is created from.
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
