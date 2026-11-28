namespace RevivalGuard
{
    /// <summary>MotionQueueWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class MotionQueueRule
    {
        public static bool Stale(int count, bool sameHead, bool sameCounter, double age, double stale)
            => count > 0 && sameHead && sameCounter && age >= stale;

        public static bool Backlog(int linked, bool contact, bool substateFalling, double fallingFor, double deepFor,
                                   double fallingSeconds, int deepLinked, double deepSeconds)
            => linked > 0 && contact
               && ((substateFalling && fallingFor >= fallingSeconds) || (linked >= deepLinked && deepFor >= deepSeconds));

        /// <summary>After the drain, should the body be put back to Ready in its stance?</summary>
        public static bool BackToReady(bool forwardIsAction, bool substateFalling, bool contact)
            => forwardIsAction || (substateFalling && contact);
    }
}
