namespace RevivalGuard
{
    /// <summary>MotionQueueWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class MotionQueueRule
    {
        /// <summary>
        /// A server body's motion queue (MotionTableManager.PendingAnimations) is stale when it holds something and neither
        /// its head nor its counter of finished links has moved for <paramref name="stale"/> seconds. Every link in the
        /// motion table is shorter than that, so a live queue always moves first.
        /// </summary>
        public static bool Stale(int count, bool sameHead, bool sameCounter, double age, double stale)
            => count > 0 && sameHead && sameCounter && age >= stale;

        /// <summary>
        /// After the drain, should the body be put back to Ready in its stance? Only when its state is one a standing
        /// body cannot be in: the interpreted forward command is an action (ACE plays a swing as the forward command, and
        /// HitGround plays it again on landing), or the table still says Falling with the body on the ground. A body the
        /// client is running or turning is left alone; its next MoveToState sets it.
        /// </summary>
        public static bool BackToReady(bool forwardIsAction, bool substateFalling, bool contact)
            => forwardIsAction || (substateFalling && contact);
    }
}
