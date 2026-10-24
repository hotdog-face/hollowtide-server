namespace RevivalGuard
{
    /// <summary>CastWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class CastWatchdogRule
    {
        /// <summary>A cast running at least <paramref name="limit"/> seconds is stuck, unless ACE is holding it on
        /// purpose for a turn key still down (MagicState.PendingTurnRelease with a turn in the last MoveToState).</summary>
        public static bool Stuck(double ageSeconds, bool pendingTurnRelease, uint heldTurnCommand, double limit) =>
            ageSeconds >= limit && !(pendingTurnRelease && heldTurnCommand != 0);

        /// <summary>As above, or sooner once the server body has been off walkable ground for <paramref name="unfootedLimit"/>
        /// seconds: ACE queues neither the turn to the target nor the cast gesture for it (contact_allows_move), so the cast
        /// cannot finish until it lands (findings use-stuck-2026-10-23). A jump lands well inside 2 s (ACE fails a cast
        /// on a jump itself).</summary>
        public static bool Stuck(double ageSeconds, bool pendingTurnRelease, uint heldTurnCommand, double limit,
                                 double unfootedSeconds, double unfootedLimit) =>
            (ageSeconds >= limit || unfootedSeconds >= unfootedLimit) && !(pendingTurnRelease && heldTurnCommand != 0);

        /// <summary>A cast whose gesture has played past its own length plus <paramref name="grace"/> on walkable ground with no
        /// motion-done is finished as ACE finishes it on that callback (HandleMotionDone_Magic: CastMotionDone, DoCastSpell),
        /// not held to the 10 s fizzle (261024-0003: eight Revitalize casts held 10 s and fizzled, each answering every drink,
        /// cast and swing meanwhile with "You're too busy!"). Offline (ACE's physics on the box) the gesture is done in 0.3-2.5 s
        /// whatever the client or the server sends during it. Not while the cast turns to its target, and not before the
        /// gesture has started (sinceGesture &lt; 0).</summary>
        public static bool FinishNow(double sinceGesture, double gestureSeconds, double grace, bool motionDone, bool turning, bool walkable) =>
            sinceGesture >= 0 && walkable && !motionDone && !turning && sinceGesture >= gestureSeconds + grace;

        /// <summary>How long the body has been off walkable ground, from the first tick it was seen so (since = NaN: footed).</summary>
        public static double Unfooted(bool walkable, ref double since, double now)
        {
            if (walkable) { since = double.NaN; return 0; }
            if (double.IsNaN(since)) since = now;
            return now - since;
        }
    }
}
