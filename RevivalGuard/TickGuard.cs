using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// The catch every world-tick patch needs. ACE's UpdateWorld loop (WorldManager.cs:524) calls
    /// PlayerManager.Tick with no try/catch, and Harmony does not catch a postfix's exception: one
    /// throw ends the world thread, so the world freezes while the process stays up. Each tick
    /// postfix wraps its body and reports here. A persistent throw is said once a minute per site,
    /// with how many were swallowed in between, so it cannot flood the log.
    /// </summary>
    static class TickGuard
    {
        sealed class Site { public double Last = double.MinValue; public int Suppressed; }

        static readonly Dictionary<string, Site> s_Sites = new Dictionary<string, Site>();
        const double EVERY_SECONDS = 60.0;

        internal static void Report(string site, Exception e)
        {
            try
            {
                var now = ACE.Common.Time.GetUnixTime();
                int suppressed;
                lock (s_Sites)
                {
                    if (!s_Sites.TryGetValue(site, out var s)) s_Sites[site] = s = new Site();
                    if (now - s.Last < EVERY_SECONDS) { s.Suppressed++; return; }
                    s.Last = now;
                    suppressed = s.Suppressed;
                    s.Suppressed = 0;
                }
                Mod.Log.Error($"[RevivalGuard] {site} tick threw; the world loop carries on"
                    + (suppressed > 0 ? $" ({suppressed} more since the last report)" : ""), e);
            }
            catch { }   // the report itself must never reach the world loop
        }
    }
}
