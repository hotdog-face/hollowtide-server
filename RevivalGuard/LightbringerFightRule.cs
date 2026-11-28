namespace RevivalGuard
{
    /// <summary>How hard the cragstone fight is, scaled to who is fighting.</summary>
    public static class LightbringerFightRule
    {
        public const double WINDOW_FRAC = 0.40, WINDOW_S = 3.0, PHASE2 = 1.3, MAX_BLOW = 0.30;
        public const int N_MAX = 8;

        static int N(int n) => Math.Clamp(n, 1, N_MAX);
        static double Phase(int phase) => phase >= 2 ? PHASE2 : 1.0;

        public static double SwingFrac(int n, int phase) => Math.Min(MAX_BLOW, (0.09 + 0.015 * (N(n) - 1)) * Phase(phase));
        public static double SlamFrac(int n, int phase) => SwingFrac(n, phase);
        public static double SpellFrac(int n, int phase) => SwingFrac(n, phase) * 0.8;
        /// <summary>A shadow's blow or spell: retail's Tenebrous Shadow and Shadow Wraith hit a level 25 for most of their health</summary>
        public static double AddFrac(int n, int phase) => SwingFrac(n, phase) * 0.5;
        /// <summary>A meteor hits harder than a blow; it is telegraphed and can be stepped out of.</summary>
        public static double MeteorFrac(int n, int phase) => Math.Min(MAX_BLOW, SwingFrac(n, phase) * 1.4);

        /// <summary>Shards of Light to drive him off: 6 for one player, 2 more for each extra fighter.</summary>
        public static int ShardsNeeded(int n) => 4 + 2 * N(n);
        /// <summary>How many shards lie in the water at once.</summary>
        public static int ShardsOut(int n) => Math.Clamp(1 + N(n), 2, 6);
        /// <summary>Seconds between new shards while short: 28 solo, 4 less for each extra fighter, 10 at least.</summary>
        public static double ShardEvery(int n) => Math.Max(10.0, 28.0 - 4.0 * (N(n) - 1));

        /// <summary>Seconds between meteors: 14 solo, faster with more fighters, faster again in phase 2.</summary>
        public static double MeteorEvery(int n, int phase) => Math.Max(5.0, 14.0 - 1.5 * (N(n) - 1)) / Phase(phase);

        /// <summary>Shadows summoned at a call.</summary>
        public static int Shadows(int n, int phase) => Math.Clamp(1 + 2 * N(n), 3, 10) + (phase >= 2 ? 2 : 0);

        /// <summary>How weakened he is: 0 (whole) to 3 (driven off).</summary>
        public static int Stage(int delivered, int needed) => needed <= 0 ? 3 : Math.Clamp(delivered * 3 / needed, 0, 3);

        /// <summary>The share of his missing health his self-heal restores at each stage: whole, then less, then none.</summary>
        public static double HealScale(int stage) => stage <= 0 ? 1.0 : stage == 1 ? 0.5 : 0.0;
    }
}
