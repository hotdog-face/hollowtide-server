using System;

namespace RevivalGuard
{
    /// <summary>World boss health scales with the fighters.</summary>
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
