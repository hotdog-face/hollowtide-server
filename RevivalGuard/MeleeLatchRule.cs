namespace RevivalGuard
{
    /// <summary>MeleeLatchGuard's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class MeleeLatchRule
    {
        /// <summary>rg_melee_latch_mode: 0 off, 1 log the latch only, 2 log and finish it.</summary>
        public const long OFF = 0, LOG = 1, FIX = 2;
        public static long Mode(long v) => v < OFF || v > FIX ? LOG : v;

        /// <summary>The melee loop is latched</summary>
        public static bool Latched(bool liveTarget, bool attacking, bool moveToPending, bool bodyMoving)
            => liveTarget && !attacking && moveToPending && !bodyMoving;

        /// <summary>How long the same latch (same attack sequence) has held; 0 when not latched or a new attack began.</summary>
        public static double Age(bool latched, int attackSequence, ref int lastSequence, ref double since, double now)
        {
            if (!latched) { lastSequence = -1; since = now; return 0; }
            if (attackSequence != lastSequence) { lastSequence = attackSequence; since = now; return 0; }
            return now - since;
        }

        /// <summary>A turn or step to a target in reach takes about a second; a latch held this long is finished.</summary>
        public static bool Stuck(double age, double limit) => age >= limit;

        /// <summary>Drain the body's motion queue as well?</summary>
        public static bool DrainQueue(int queueCount, bool sameHeadSinceLatch, bool sameCounterSinceLatch)
            => queueCount > 0 && sameHeadSinceLatch && sameCounterSinceLatch;

        /// <summary>MeleeRestart waits this long on an attack that has not swung before a new press replaces it</summary>
        public const double OTHER_TARGET_WAIT = 0.5, SAME_TARGET_WAIT = 3.0;

        public static bool Restart(bool liveTarget, bool attacking, bool moveToPending, bool sameTarget, double secondsSinceAccepted)
            => liveTarget && !attacking && moveToPending
               && secondsSinceAccepted >= (sameTarget ? SAME_TARGET_WAIT : OTHER_TARGET_WAIT);
    }
}
