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

        /// <summary>How long the body has been off walkable ground, from the first tick it was seen so (since = NaN: footed).</summary>
        public static double Unfooted(bool walkable, ref double since, double now)
        {
            if (walkable) { since = double.NaN; return 0; }
            if (double.IsNaN(since)) since = now;
            return now - since;
        }
    }
}
