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

        /// <summary>Stuck, or sooner when the server body is off walkable ground: ACE then queues no animation for the
        /// phase (contact_allows_move), so its motion-done cannot come while the body stays there. Past the phase's own
        /// animation time plus <paramref name="grace"/> (a landing re-plays it: findings use-stuck-2026-10-23) it is
        /// finished at once instead of after the full limit.</summary>
        public static bool Stuck(double ageSeconds, double limit, bool walkable, double animSeconds, double grace)
            => ageSeconds >= limit || (!walkable && ageSeconds >= animSeconds + grace);

        /// <summary>As above, and on walkable ground past the animation time plus <paramref name="footedGrace"/>. Offline
        /// (ACE's physics on the box) every footed phase ends within its animation plus 2 s: stance swap, the client's
        /// turn/run/stance MoveToStates, a jump. Only getting up from a sit or sleep runs longer, and finishing that early
        /// is harmless. Report 261023-0014: a MimeEat (dispel potion) on flat ground held 10 s.</summary>
        public static bool Stuck(double ageSeconds, double limit, bool walkable, double animSeconds, double grace, double footedGrace)
            => Stuck(ageSeconds, limit, walkable, animSeconds, grace) || ageSeconds >= animSeconds + footedGrace;
    }
}
