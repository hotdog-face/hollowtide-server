using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>Festival stones, the anniversary goal.</summary>
    public static class FestivalStonesRule
    {
        public const string Festival = "anniversary";
        public const int N = 20;
        public const double RANGE_M = 15;           // standing at a stone (they cannot be used): a teleport to its map spot lands 10-11 m off
        public const double STALE_DAYS = 60;
        public static readonly uint[] Stones = Enumerable.Range(5376, N).Select(i => (uint)i).ToArray();
        public static readonly uint[] Dolls = { 29920, 29921 };

        // The record: three quest flags, no world data.
        public const string COUNTER = "FestivalStones", SEEN = "FestivalStonesSeen", DOLLS = "FestivalStonesDolls";

        public static bool IsStone(uint wcid) => wcid >= 5376 && wcid <= 5395;
        public static int Bit(uint stone) => 1 << (int)(stone - 5376);
        public static int Count(int seen) { int n = 0; for (int b = seen & ((1 << N) - 1); b != 0; b &= b - 1) n++; return n; }

        public static bool Stale(double lastMoved, double now) => now - lastMoved >= STALE_DAYS * 86400;

        /// <summary>The dolls this character still has coming, from DOLLS' bits.</summary>
        public static List<uint> Owed(int dollBits) => Dolls.Where((d, i) => (dollBits & (1 << i)) == 0).ToList();
        public static int DollBit(uint doll) => 1 << System.Array.IndexOf(Dolls, doll);

        /// <summary>The one line a visit says.</summary>
        public static string VisitLine(int n) =>
            n == 1 ? $"Festival Stones: 1 of {N}. Visit all {N} during the Anniversary for two pack dolls."
                   : $"Festival Stones: {n} of {N}.";

        /// <summary>The twentieth stone's line, with the dolls as made.</summary>
        public static string DoneLine(IList<string> names)
        {
            if (names == null || names.Count == 0) return null;
            string list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
            return $"Festival Stones: {N} of {N}! You have visited every Festival Stone. {list} {(names.Count == 1 ? "is" : "are")} in your pack, "
                + "pack dolls from Dereth's fifth anniversary. Hook one in your house, or use it to read its story.";
        }

        /// <summary>All twenty visited, but the dolls did not fit (slots or burden).</summary>
        public static string FullLine(int slots) =>
            $"Festival Stones: {N} of {N}! Your pack needs room for {slots} {(slots == 1 ? "item" : "items")}. Make room, then stand by any Festival Stone or log in again for your prize.";
    }
}
