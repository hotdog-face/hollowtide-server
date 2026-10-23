namespace RevivalGuard
{
    /// <summary>UseWalkWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class UseWalkRule
    {
        public enum Act { None, Settle, Fail }

        /// <summary>
        /// What to do with a use whose server walk/turn (Player.MoveToParams) has waited <paramref name="age"/> seconds.
        /// Settle: ACE cancelled the move and waits "until the player is stationary" (OnMoveComplete_MoveTo2), but it only
        /// looks on a physics tick that was moving, so a body already still never answers: ask now, as ACE meant to.
        /// Fail: the walk cannot end. Off walkable ground MoveToManager's RunForward is never applied (no progress, no
        /// cancel: FailProgressCount is never read); otherwise ACE's own move-to timeout for the timed path (15 s,
        /// Player_Move.cs defaultMoveToTimeout) is the limit. Failing calls the use back with false: UseDone, not busy.
        /// </summary>
        public static Act Decide(double age, bool movingTo, bool bodyMoving, bool walkable,
                                 double settleAfter, double unfootedAfter, double limit)
        {
            if (age >= limit) return Act.Fail;
            if (!movingTo && !bodyMoving && age >= settleAfter) return Act.Settle;
            if (movingTo && !walkable && age >= unfootedAfter) return Act.Fail;
            return Act.None;
        }
    }
}
