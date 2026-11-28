namespace RevivalGuard
{
    /// <summary>CastWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class CastWatchdogRule
    {
        public const bool WatchdogFailSpends = false;

        /// <summary>A cast running at least limit seconds is stuck, unless ACE is holding it on purpose for a turn key still down.</summary>
        public static bool Stuck(double ageSeconds, bool pendingTurnRelease, uint heldTurnCommand, double limit) =>
            ageSeconds >= limit && !(pendingTurnRelease && heldTurnCommand != 0);

        /// <summary>As above, or sooner once the server body has been off walkable ground for unfootedLimit seconds</summary>
        public static bool Stuck(double ageSeconds, bool pendingTurnRelease, uint heldTurnCommand, double limit,
                                 double unfootedSeconds, double unfootedLimit) =>
            (ageSeconds >= limit || unfootedSeconds >= unfootedLimit) && !(pendingTurnRelease && heldTurnCommand != 0);

        public static bool FinishNow(double sinceGesture, double gestureSeconds, double grace, bool motionDone, bool turning, bool walkable) =>
            sinceGesture >= 0 && walkable && !motionDone && !turning && sinceGesture >= gestureSeconds + grace;

        /// <summary>How long the body has been off walkable ground, from the first tick it was seen so.</summary>
        public static double Unfooted(bool walkable, ref double since, double now)
        {
            if (walkable) { since = double.NaN; return 0; }
            if (double.IsNaN(since)) since = now;
            return now - since;
        }
    }
}
