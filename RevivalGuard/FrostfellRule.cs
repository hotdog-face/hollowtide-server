using System;

namespace RevivalGuard
{
    /// <summary>Frostfell's numbers and words, PURE: Frostfell.cs runs them, tools/nettest compiles this file by path.</summary>
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

        /// <summary>(new max, new current) when his max goes from oldMax to newMax.</summary>
        public static (uint max, uint current) Resize(uint oldMax, uint current, uint newMax)
        {
            long dealt = (long)oldMax - current;
            long cur = newMax >= oldMax ? (long)current + (newMax - oldMax) : newMax - dealt;
            if (cur < 1) cur = 1;
            if (cur > newMax) cur = newMax;
            return (newMax, (uint)cur);
        }

        /// <summary>The present raids scale with their defenders.</summary>
        public const float RAID_R = 120f;
        public static double RaiderShare(int defenders) => Math.Min(1.0, 0.4 + 0.12 * (Math.Max(1, defenders) - 1));
        public static double PresentShare(int defenders) => Math.Max(1.5, 5.0 - 0.6 * (Math.Max(1, defenders) - 1));

        /// <summary>The raid's pacing.</summary>
        public sealed class RaidPacing
        {
            public const double CALM_S = 45, RELAX_AT = 0.25, PEAK_MIN = 1.0;
            public double Peak, CalmUntil;
            public bool Calm(double now) => now < CalmUntil;

            /// <summary>One look (every couple of seconds).</summary>
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

        /// <summary>The raid's countdown: a line to the defenders at these minutes left, with the present's health.</summary>
        public static readonly int[] TIMER_MARKS = { 25, 20, 15, 10, 5, 2, 1 };

        public static string TimerLine(int minsLeft, double presentFrac) =>
            $"Hold the Holiday Present for {minsLeft} more minute{(minsLeft == 1 ? "" : "s")}! It has {Math.Round(presentFrac * 100):0}% left.";

        /// <summary>Frostfell Cheer for a group of this many players (distinct accounts).</summary>
        public static double Cheer(int players) => players <= 0 ? 0 : Math.Min(CHEER_CAP, CHEER_BASE + CHEER_PER * (players - 1));

        /// <summary>One line for a gift BOX. boxLine is retail's own count with its numbers in</summary>
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
