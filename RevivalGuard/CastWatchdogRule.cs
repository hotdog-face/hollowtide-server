namespace RevivalGuard
{
    /// <summary>CastWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class CastWatchdogRule
    {
        /// <summary>A cast running at least <paramref name="limit"/> seconds is stuck, unless ACE is holding it on
        /// purpose for a turn key still down (MagicState.PendingTurnRelease with a turn in the last MoveToState).</summary>
        public static bool Stuck(double ageSeconds, bool pendingTurnRelease, uint heldTurnCommand, double limit) =>
            ageSeconds >= limit && !(pendingTurnRelease && heldTurnCommand != 0);
    }
}
