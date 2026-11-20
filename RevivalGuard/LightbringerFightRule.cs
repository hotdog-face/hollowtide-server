namespace RevivalGuard
{
    /// <summary>
    /// HOW HARD THE CRAGSTONE FIGHT IS, scaled to who is fighting (owner 2026-10-29: "when we run the quest it should
    /// dynamically scale in its difficulty to players playing it. i dont want to just make it into a flop fest that
    /// frustrates everyone"). Pure: no ACE types, so tools/nettest checks it (LightbringerSceneCase.cs).
    ///
    /// n is the number of players fighting: alive, within the scene's engage range of Bael, off the rock, the most seen
    /// in the last minute (a death does not make the fight easier for the rest). Levels count through each target's own
    /// max health: every blow is a share of the victim's max health, so a level 20 and a level 200 feel the same threat,
    /// and WINDOW_FRAC per WINDOW_S bounds what all of Bael's attacks together can take, so nobody falls from full health
    /// in one blow or one bad second. Phase 2 (the second half) hits PHASE2 times harder, with more shadows and meteors.
    /// </summary>
    public static class LightbringerFightRule
    {
        public const double WINDOW_FRAC = 0.40, WINDOW_S = 3.0, PHASE2 = 1.3, MAX_BLOW = 0.30;
        public const int N_MAX = 8;

        static int N(int n) => Math.Clamp(n, 1, N_MAX);
        static double Phase(int phase) => phase >= 2 ? PHASE2 : 1.0;

        /// <summary>A sword hit, as a share of the victim's max health: 9% solo, a little more for each extra fighter
        /// (more hands to heal and carry shards), never above MAX_BLOW. (11% solo killed a still, unhealed level 25 bot
        /// about every 40 s on the box, 2026-10-29: too much for one player.)</summary>
        public static double SwingFrac(int n, int phase) => Math.Min(MAX_BLOW, (0.09 + 0.015 * (N(n) - 1)) * Phase(phase));
        public static double SlamFrac(int n, int phase) => SwingFrac(n, phase);
        public static double SpellFrac(int n, int phase) => SwingFrac(n, phase) * 0.8;
        /// <summary>A shadow's blow or spell: retail's Tenebrous Shadow and Shadow Wraith hit a level 25 for most of their
        /// health (box test 2026-10-29); in the scene they hit for half a sword blow's share, counted in the same window.</summary>
        public static double AddFrac(int n, int phase) => SwingFrac(n, phase) * 0.5;
        /// <summary>A meteor hits harder than a blow; it is telegraphed and can be stepped out of.</summary>
        public static double MeteorFrac(int n, int phase) => Math.Min(MAX_BLOW, SwingFrac(n, phase) * 1.4);

        /// <summary>Shards of Light to drive him off: 6 for one player, 2 more for each extra fighter. With ShardEvery a
        /// focused solo player or group wins around the halfway mark, so phase 2 is met (box test 2026-10-29: at 3 + 2n
        /// and a shard every 10 s a solo bot won 50 s into the fight).</summary>
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
