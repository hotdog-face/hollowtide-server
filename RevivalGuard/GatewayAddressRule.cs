using System;
using System.Net;

namespace RevivalGuard
{
    public static class GatewayAddressRule
    {
        /// <summary>The header a version-2 gateway puts on every /who answer (tools/public-shard/ws-gateway).</summary>
        public const string VERSION_HEADER = "X-Who-Version";
        public const int VERSION = 2;

        public static readonly IPAddress UNKNOWN = IPAddress.Parse("192.0.2.1");

        /// <summary>How long a positive outside answer serves the packet-thread rate limits.</summary>
        public static readonly TimeSpan CACHE = TimeSpan.FromSeconds(10);

        public enum Reply
        {
            Off,        // no gateway configured (REVIVAL_GATEWAY=off)
            Refused,    // nothing listening where the gateway should be
            Answered,   // an HTTP answer: see status, version, body
            Failed,     // timeout or any other error
        }

        /// <summary>The address to judge endpoint by.</summary>
        public static IPAddress Decide(IPAddress endpoint, Reply reply, int status = 0, string version = null, string body = null)
        {
            if (endpoint == null) return null;
            if (!NeedsAsking(endpoint, reply != Reply.Off)) return endpoint;
            switch (reply)
            {
                case Reply.Refused: return endpoint;   // no gateway on this machine: a local client
                case Reply.Failed: return UNKNOWN;
            }
            if (status == 200 && IPAddress.TryParse((body ?? "").Trim(), out var p)) return p;
            if (status == 404 && SureNotOurs(version)) return endpoint;   // the gateway is sure: a local client
            return UNKNOWN;
        }

        /// <summary>Only a loopback endpoint can be a gateway session, and only when a gateway is configured.</summary>
        public static bool NeedsAsking(IPAddress endpoint, bool gatewayConfigured)
        {
            if (endpoint == null || !gatewayConfigured) return false;
            var a = endpoint.IsIPv4MappedToIPv6 ? endpoint.MapToIPv4() : endpoint;
            return IPAddress.IsLoopback(a);
        }

        /// <summary>A 404 means "not my port" only from a gateway that keeps closed ports as "unknown" for a while (version 2).</summary>
        public static bool SureNotOurs(string version) =>
            int.TryParse((version ?? "").Trim(), out var v) && v >= VERSION;

        /// <summary>May this answer go in the port-keyed cache?</summary>
        public static bool Cacheable(IPAddress endpoint, IPAddress resolved, bool resolvedIsLocal) =>
            endpoint != null && resolved != null && !resolved.Equals(UNKNOWN) && !resolved.Equals(endpoint) && !resolvedIsLocal;

        /// <summary>Is a cache entry made at at still good at now?</summary>
        public static bool Fresh(DateTime at, DateTime now) => now >= at && now - at < CACHE;
    }
}
