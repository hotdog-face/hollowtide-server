using System;

namespace RevivalGuard
{
    /// <summary>
    /// FROSTFELL'S NUMBERS AND WORDS, PURE (no ACE types): Frostfell.cs runs them, tools/nettest compiles this file by
    /// path (Frostfell.cs there). Ours, not retail (docs/DIVERGENCES.md "Frostfell revival"; docs/EVENT-TESTS/frostfell.md).
    ///
    /// THE ABOMINABLE SNOWMAN SCALES WITH HIS FIGHTERS. Retail sized him for a crowd (level 135, 399,820 health): one
    /// level 135 at about 120 damage a second needs nearly an hour. Now his health starts at SOLO for the first player
    /// who hurts him and grows by PER for each new one, never past retail's own size:
    ///     max(N) = min(retailMax, SOLO + PER * (N - 1))          N = distinct players who have hurt him this life
    /// With d damage a second each the kill takes max(N) / (N d): about 6 minutes for one, 5 for five, 5 for ten
    /// (box measure in docs/EVENT-TESTS/frostfell.md). Raising never heals work away: current health rises by what the
    /// max rises (as WorldBossScaling.Rescale).
    ///
    /// FROSTFELL CHEER, the buff for the Snowman's fall and for the Costumer's carols: more creature XP for an hour,
    /// BASE alone, PER_FRIEND for each other player on it, at most CAP (the Night Club party buff's sizes).
    /// </summary>
    static class FrostfellRule
    {
        public const uint SOLO = 45000, PER = 36000;
        public const double CHEER_BASE = 0.05, CHEER_PER = 0.01, CHEER_CAP = 0.10, CHEER_S = 3600;

        /// <summary>The Snowman's max health for N fighters, given his weenie's (retail) max.</summary>
        public static uint SnowmanMax(int fighters, uint retailMax)
        {
            if (fighters < 1) fighters = 1;
            double want = SOLO + (double)PER * (fighters - 1);
            return (uint)Math.Min(retailMax, want);
        }

        /// <summary>(new max, new current) when his max goes from oldMax to newMax. Shrinking (the first fighter) keeps
        /// the damage already dealt; growing adds the rise to current. Never below 1 while alive.</summary>
        public static (uint max, uint current) Resize(uint oldMax, uint current, uint newMax)
        {
            long dealt = (long)oldMax - current;
            long cur = newMax >= oldMax ? (long)current + (newMax - oldMax) : newMax - dealt;
            if (cur < 1) cur = 1;
            if (cur > newMax) cur = newMax;
            return (newMax, (uint)cur);
        }

        /// <summary>
        /// THE PRESENT RAIDS SCALE WITH THEIR DEFENDERS (box 2026-10-29: nine level 220 Gurog Grumps broke Tufa's present
        /// in under 3 minutes). Each raider spawns with this share of its health for the defenders (distinct accounts)
        /// within RAID_R: 40% for one, full from six. The present gets PresentShare: 5x its 50,000 alone, 4.4x for two,
        /// at least 1.5x. Box 2026-10-29, two fighters (275 and 243) killing Grumps all along: the three waves (9, 9, then
        /// 7 with the Mastermind and Max) took 122,000 off it in 13 minutes, so 130,000 fell; 220,000 leaves a margin
        /// only while both keep killing. Seen at spawn, never changed later.
        /// </summary>
        public const float RAID_R = 120f;
        public static double RaiderShare(int defenders) => Math.Min(1.0, 0.4 + 0.12 * (Math.Max(1, defenders) - 1));
        public static double PresentShare(int defenders) => Math.Max(1.5, 5.0 - 0.6 * (Math.Max(1, defenders) - 1));

        /// <summary>
        /// THE RAID'S PACING (owner's ticket 261030-0013: "measures intensity per player and as it peaks it drops with a
        /// 45 second calm down then hits them with a new wave"). Intensity = raiders alive per defender. While building,
        /// a new wave may come (the wave generators' spawns are let through). Once a peak has been seen (at least PEAK_MIN
        /// a defender) and intensity falls to RELAX_AT of it, the defenders have broken the wave: CALM_S seconds with no
        /// new raiders, then the next wave. Retail's waves (9, 9, then 7 with the Mastermind and Max) are the same; only
        /// when they come changes.
        /// </summary>
        public sealed class RaidPacing
        {
            public const double CALM_S = 45, RELAX_AT = 0.25, PEAK_MIN = 1.0;
            public double Peak, CalmUntil;
            public bool Calm(double now) => now < CalmUntil;

            /// <summary>One look (every couple of seconds). Returns "calm" when a calm has just begun, "wave" when one has
            /// just ended (the next wave may come), else null.</summary>
            public string Update(double now, int raiders, int defenders)
            {
                double i = raiders / (double)Math.Max(1, defenders);
                if (Calm(now)) return null;
                if (CalmUntil > 0 && now >= CalmUntil) { CalmUntil = 0; Peak = i; return "wave"; }
                if (i > Peak) Peak = i;
                if (Peak >= PEAK_MIN && i <= Peak * RELAX_AT) { CalmUntil = now + CALM_S; Peak = 0; return "calm"; }
                return null;
            }
        }

        /// <summary>The raid's countdown (ticket 261030-0011: "players may not know that they are expected to do anything
        /// for 30 minutes"): a line to the defenders at these minutes left, with the present's health.</summary>
        public static readonly int[] TIMER_MARKS = { 25, 20, 15, 10, 5, 2, 1 };

        public static string TimerLine(int minsLeft, double presentFrac) =>
            $"Hold the Holiday Present for {minsLeft} more minute{(minsLeft == 1 ? "" : "s")}! It has {Math.Round(presentFrac * 100):0}% left.";

        /// <summary>Frostfell Cheer for a group of this many players (distinct accounts).</summary>
        public static double Cheer(int players) => players <= 0 ? 0 : Math.Min(CHEER_CAP, CHEER_BASE + CHEER_PER * (players - 1));

        /// <summary>
        /// ONE LINE FOR A GIFT BOX (owner's guide 2026-10-28: "1 of 12" and "Present Hunt: 1 of 6" read as two hunts).
        /// boxLine is retail's own count with its numbers in ("3 of 12 gift boxes found."); hunt is the Present Hunt count
        /// after this box, need its goal (6), prize this year's trophy name.
        /// </summary>
        public static string BoxLine(string boxLine, int hunt, int need, string prize)
        {
            string box = (boxLine ?? "").Trim();
            if (box.Length > 0 && !box.EndsWith(".")) box += ".";
            string tail = hunt < need
                ? $"Present Hunt: {hunt} of {need} for a {prize} from the Frostfell Costumer in Holtburg."
                : $"Present Hunt complete! The Frostfell Costumer in Holtburg has your {prize}. Find all 12 boxes for a title and a pet.";
            return box.Length > 0 ? box + " " + tail : tail;
        }
    }
}
