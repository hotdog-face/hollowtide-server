namespace RevivalGuard
{
    /// <summary>MonsterAi's archer step-back limit, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class MonsterKiteRule
    {
        /// <summary>monster_ai_kite_per_target default: one step-back per target, then the archer stands and shoots as ACE
        /// does. Unlimited (every 8 s) sent melee players chasing Withered Atoll Tumeroks all fight (Discord 2026-11-25).</summary>
        public const double DEFAULT_PER_TARGET = 1;

        /// <summary>May the archer step back again from this target? <paramref name="perTarget"/> below 0 is the old
        /// behaviour (no limit, only the cooldown), 0 never, N at most N times while it keeps the same target.</summary>
        public static bool Allowed(int stepsAtThisTarget, double perTarget)
            => double.IsNaN(perTarget) ? stepsAtThisTarget < DEFAULT_PER_TARGET
               : perTarget < 0 || stepsAtThisTarget < perTarget;
    }
}
