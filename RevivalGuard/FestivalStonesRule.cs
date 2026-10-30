using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// FESTIVAL STONES, THE ANNIVERSARY GOAL (owner 2026-10-29: "Visit all 20 Festival Stones"). Retail's twenty Festival
    /// Stones (5376-5395, the AnniversaryFestivalStones event) stand across Dereth from Nov 2 to 5. Standing at each one
    /// counts it, "Festival Stones: 7 of 20."; the twentieth gives Pack Levistras (29920) and Pack Martine (29921), retail's
    /// 5th Anniversary gift dolls (their own book pages: "This doll is a gift, celebrating five years of Isparian freedom
    /// on Dereth"). The dolls come once per character ever. Pure, so tools/nettest checks it by path; FestivalStones.cs does
    /// the counting and the giving. No em dashes (player text).
    /// </summary>
    public static class FestivalStonesRule
    {
        public const string Festival = "anniversary";
        public const int N = 20;
        public const double RANGE_M = 15;           // standing at a stone (they cannot be used): a teleport to its map spot lands 10-11 m off
        public const double STALE_DAYS = 60;        // a visit from an earlier year's Anniversary no longer counts
        public static readonly uint[] Stones = Enumerable.Range(5376, N).Select(i => (uint)i).ToArray();
        public static readonly uint[] Dolls = { 29920, 29921 };

        // The record: three quest flags, no world data. COUNTER's solves are the stones visited this year (the Journal
        // reads it), SEEN's are a bit per stone, DOLLS' a bit per doll given (once ever, never erased).
        public const string COUNTER = "FestivalStones", SEEN = "FestivalStonesSeen", DOLLS = "FestivalStonesDolls";

        public static bool IsStone(uint wcid) => wcid >= 5376 && wcid <= 5395;
        public static int Bit(uint stone) => 1 << (int)(stone - 5376);
        public static int Count(int seen) { int n = 0; for (int b = seen & ((1 << N) - 1); b != 0; b &= b - 1) n++; return n; }

        /// <summary>Last year's visits no longer count: the record was last moved STALE_DAYS or more ago (no Anniversary is longer than 4 days).</summary>
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
