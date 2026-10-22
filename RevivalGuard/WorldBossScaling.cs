using System;

namespace RevivalGuard
{
    /// <summary>
    /// WORLD BOSS HEALTH SCALES WITH THE FIGHTERS (owner, 2026-10-21: "Health scales with fighters"). Pure maths,
    /// no ACE types, so tools/worldboss-scaling-test compiles this file by path and checks it. Ours, not retail.
    ///
    ///   multiplier(N) = min(cap, 1 + perFighter * (N - 1))          N = distinct players who have damaged him
    ///   max(N)        = round(baseMax * multiplier(N))               baseMax = his weenie's health (the solo size)
    ///
    /// A boss's health is sized for ONE strong fighter (a level 275 at ~80-100 damage a second: 15-20 minutes on
    /// 90,000). With d damage a second per fighter the kill takes baseMax * (1 + k(N-1)) / (N d), which falls towards
    /// baseMax * k / d as N grows instead of towards zero: with k = 0.15 a big group still needs about a
    /// seventh of a lone fighter's time, so 5-10 minutes for groups of 5-10 level 200s (~50 a
    /// second each). The cap (4x, reached at 21 fighters) keeps a zerg from making him unkillable inside the window.
    ///
    /// Raising never heals him down and never undoes work: the new health is old current + (new max - old max), so
    /// damage already dealt stays dealt and his health fraction only rises a little. The max never shrinks (a fighter
    /// who leaves or dies still counts). Window open = baseMax (one fighter).
    /// </summary>
    static class WorldBossScaling
    {
        public const double DEFAULT_PER_FIGHTER = 0.15, DEFAULT_CAP = 4.0;

        public static double Multiplier(int fighters, double perFighter = DEFAULT_PER_FIGHTER, double cap = DEFAULT_CAP)
        {
            if (fighters < 1) fighters = 1;
            if (perFighter < 0) perFighter = 0;
            if (cap < 1) cap = 1;
            return Math.Min(cap, 1 + perFighter * (fighters - 1));
        }

        /// <summary>The max for N fighters, never below the current max.</summary>
        public static uint TargetMax(uint baseMax, uint currentMax, int fighters, double perFighter = DEFAULT_PER_FIGHTER, double cap = DEFAULT_CAP)
        {
            double want = Math.Round(baseMax * Multiplier(fighters, perFighter, cap));
            if (want > uint.MaxValue / 2) want = uint.MaxValue / 2;
            return Math.Max(currentMax, (uint)want);
        }

        /// <summary>(new max, new current) when the max goes from oldMax to newMax: current rises by the same amount.</summary>
        public static (uint max, uint current) Rescale(uint oldMax, uint current, uint newMax)
        {
            if (newMax <= oldMax) return (oldMax, current);
            ulong cur = (ulong)current + (newMax - oldMax);
            return (newMax, (uint)Math.Min(cur, newMax));
        }

        /// <summary>Expected kill time in seconds for N fighters doing dps each (the design check; see the tests).</summary>
        public static double KillSeconds(uint baseMax, int fighters, double dpsEach, double perFighter = DEFAULT_PER_FIGHTER, double cap = DEFAULT_CAP) =>
            baseMax * Multiplier(fighters, perFighter, cap) / (Math.Max(1, fighters) * dpsEach);
    }
}
