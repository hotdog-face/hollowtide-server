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
        /// A queue that moves but never catches up (261117 lag, Mystic Warrior: "queue 53 (AttackMed1,HandCombat,...)",
        /// every use and attack then waited 2-4 s behind it). Two shapes, both reproduced offline (tools/ace-physprobe
        /// `backlog`): the table says Falling with the body on the ground (after a jump, a fall or a body left off the
        /// ground mid-swing every later swing plays as 5 links, so the queue grows without end), or several swings still
        /// waiting to play, held (a stance swap, an eat or a jump in a fight leaves a backlog that auto-repeat swings at full
        /// speed never drain; box shard4: 5-9 entries with a Ready table held a food use 5.0-5.5 s against 2.75 s).
        /// <paramref name="linked"/> counts entries that still hold links (a swing); 0-link entries (stance, Ready, a turn
        /// the client sent) cost no time and are not counted, so a client's turn spam never trips it. A steady fight keeps
        /// 1-2 (the swing playing). Only on the ground: a body in the air is left to land.
        /// </summary>
        public static bool Backlog(int linked, bool contact, bool substateFalling, double fallingFor, double deepFor,
                                   double fallingSeconds, int deepLinked, double deepSeconds)
            => linked > 0 && contact
               && ((substateFalling && fallingFor >= fallingSeconds) || (linked >= deepLinked && deepFor >= deepSeconds));

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
