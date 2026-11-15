using System;
using System.Net;

namespace RevivalGuard
{
    /// <summary>
    /// Gateway.cs's decision, pure (no ACE or HTTP types), so tools/nettest compiles and checks it by path.
    /// What address a loopback endpoint stands for, given what the browser gateway's GET /who?port=N said.
    ///
    /// FAIL CLOSED (security review 2026-11-14, SEC-001/SEC-015): only two answers keep a loopback endpoint local:
    /// no gateway at all (none configured, or the connection refused: nobody can have come through one), and a
    /// gateway that says, in its version-2 protocol, that it is sure the port is not one of its players. Everything
    /// else that is not a positive address (an old gateway's bare 404, which a close/rebind race could produce for a
    /// live browser session; a timeout; a 5xx; the gateway's own "unknown" for a recently closed port; a body that
    /// is not an address) is UNKNOWN: an outside address that nothing trusts.
    ///
    /// The cache is for the packet-thread rate limits only, and holds only positive OUTSIDE answers: a port that the
    /// gateway hands to the next browser session can then at most inherit another outside rate-limit bucket, never
    /// local or staff trust. Trust decisions (staff gate, logins, PK) always ask fresh.
    /// </summary>
    public static class GatewayAddressRule
    {
        /// <summary>The header a version-2 gateway puts on every /who answer (tools/public-shard/ws-gateway).</summary>
        public const string VERSION_HEADER = "X-Who-Version";
        public const int VERSION = 2;

        /// <summary>192.0.2.1 (TEST-NET-1): "a browser player whose address the gateway could not give". Not local,
        /// so nothing trusts it; shared, so the per-address limits fall on all such sessions together.</summary>
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

        /// <summary>The address to judge <paramref name="endpoint"/> by. <paramref name="version"/> is the
        /// X-Who-Version header's value (null when absent).</summary>
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

        /// <summary>A 404 means "not my port" only from a gateway that keeps closed ports as "unknown" for a while
        /// (version 2). An older gateway's 404 can be a live session lost in its close/rebind race (SEC-015).</summary>
        public static bool SureNotOurs(string version) =>
            int.TryParse((version ?? "").Trim(), out var v) && v >= VERSION;

        /// <summary>May this answer go in the port-keyed cache? Only a positive address from the gateway that is not
        /// local (<paramref name="resolvedIsLocal"/>: StaffLoginGate.IsLocal of it), so a reused port never inherits
        /// trust.</summary>
        public static bool Cacheable(IPAddress endpoint, IPAddress resolved, bool resolvedIsLocal) =>
            endpoint != null && resolved != null && !resolved.Equals(UNKNOWN) && !resolved.Equals(endpoint) && !resolvedIsLocal;

        /// <summary>Is a cache entry made at <paramref name="at"/> still good at <paramref name="now"/>?</summary>
        public static bool Fresh(DateTime at, DateTime now) => now >= at && now - at < CACHE;
    }
}
