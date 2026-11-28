namespace RevivalGuard
{
    /// <summary>ChugWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class ChugWatchdogRule
    {
        /// <summary>The age of the chug phase seen now.</summary>
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

        /// <summary>A drink or a gem whose phase has waited at least limit seconds is stuck: a drink takes 1-3 s, its Ready half under 1 s.</summary>
        public static bool Stuck(double ageSeconds, double limit) => ageSeconds >= limit;

        /// <summary>Stuck, or sooner when the server body is off walkable ground</summary>
        public static bool Stuck(double ageSeconds, double limit, bool walkable, double animSeconds, double grace)
            => ageSeconds >= limit || (!walkable && ageSeconds >= animSeconds + grace);

        /// <summary>As above, and on walkable ground past the animation time plus footedGrace.</summary>
        public static bool Stuck(double ageSeconds, double limit, bool walkable, double animSeconds, double grace, double footedGrace)
            => Stuck(ageSeconds, limit, walkable, animSeconds, grace) || ageSeconds >= animSeconds + footedGrace;
    }
}
