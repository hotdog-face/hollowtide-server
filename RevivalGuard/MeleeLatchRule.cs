namespace RevivalGuard
{
    /// <summary>MeleeLatchGuard's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class MeleeLatchRule
    {
        /// <summary>rg_melee_latch_mode: 0 off, 1 log the latch only (default: the latch is inferred from ACE's code, not yet
        /// seen on a box), 2 log and finish it.</summary>
        public const long OFF = 0, LOG = 1, FIX = 2;
        public static long Mode(long v) => v < OFF || v > FIX ? LOG : v;

        /// <summary>
        /// The melee loop is latched: ACE holds a live MeleeTarget, no swing is in flight (Attacking false), the swing waits
        /// on a move-to or turn-to (IsPlayerMovingTo2, or its callback still pending), and the body is not moving. ACE's
        /// HandleActionTargetedMeleeAttack then drops every new attack request at "already in melee loop?" with no
        /// AttackDone, and the FastTick move-to has no timeout. A real charge moves the body, so it never counts.
        /// </summary>
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

        /// <summary>
        /// Drain the body's motion queue as well? Only when it holds something and neither its head nor its counter of
        /// finished links moved during the whole latch: that queue is why the turn never began (MoveToManager waits while
        /// IsAnimating), and the next swing would latch behind it again. A jump does the same (HandleActionJump:
        /// RemoveLinkAnimations, IsAnimating false), which is why players found that jumping frees the attack.
        /// </summary>
        public static bool DrainQueue(int queueCount, bool sameHeadSinceLatch, bool sameCounterSinceLatch)
            => queueCount > 0 && sameHeadSinceLatch && sameCounterSinceLatch;

        /// <summary>MeleeRestart waits this long on an attack that has not swung before a NEW press replaces it: a press on
        /// another monster at once (past a turn already begun), the same monster only after a run to it had its chance.</summary>
        public const double OTHER_TARGET_WAIT = 0.5, SAME_TARGET_WAIT = 3.0;

        /// <summary>
        /// A new attack request REPLACES the one still waiting on its walk or turn (MeleeRestart, F7 261117-0010 Mystic
        /// Warrior: "Occasionally I cannot attack until I tap spacebar. Especially with olthoi"). ACE keeps a live MeleeTarget
        /// while CreateMoveToChain polls for the player to come within 0.6 m (15 s for a non-PK player), and drops every
        /// later request at "already in melee loop?" with no AttackDone. Never while a swing is in flight, never between
        /// auto-repeat swings (no walk pending), never on a dead target (ACE ends that itself).
        /// </summary>
        public static bool Restart(bool liveTarget, bool attacking, bool moveToPending, bool sameTarget, double secondsSinceAccepted)
            => liveTarget && !attacking && moveToPending
               && secondsSinceAccepted >= (sameTarget ? SAME_TARGET_WAIT : OTHER_TARGET_WAIT);
    }
}
