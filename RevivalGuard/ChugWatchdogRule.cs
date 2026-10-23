namespace RevivalGuard
{
    /// <summary>ChugWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class ChugWatchdogRule
    {
        /// <summary>The age of the chug phase seen now. A phase is ACE's FoodState as (callback, motion): a new drink brings a
        /// new callback, its Ready half a null one, so the same pair seen again is the same phase still waiting. Returns 0
        /// when the phase changed (the clock restarts) or nothing is chugging.</summary>
        public static double Age(bool chugging, object callback, uint motion, ref object lastCallback, ref uint lastMotion,
                                 ref double since, double now)
        {
            if (!chugging) { lastCallback = null; lastMotion = 0; since = now; return 0; }
            if (!ReferenceEquals(callback, lastCallback) || motion != lastMotion)
            {
                lastCallback = callback; lastMotion = motion; since = now;
                return 0;
            }
            return now - since;
        }

        /// <summary>A drink or a gem whose phase has waited at least <paramref name="limit"/> seconds is stuck: a drink takes
        /// 1-3 s, its Ready half under 1 s.</summary>
        public static bool Stuck(double ageSeconds, double limit) => ageSeconds >= limit;
    }
}
