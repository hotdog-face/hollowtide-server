using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>The pure part of the allegiance directory and the small-allegiance bonus.</summary>
    public static class AllegianceBalanceRule
    {
        // the small-allegiance bonus
        public const long FULL_UP_TO = 10, ZERO_AT = 40;
        public const double PASSUP_MAX = 0.25, LUM_MAX = 0.12;

        /// <summary>The bonus fraction for an allegiance of members</summary>
        public static double Bonus(int members, long fullUpTo, long zeroAt, double max)
        {
            if (members < 2 || max <= 0 || double.IsNaN(max) || zeroAt <= fullUpTo || fullUpTo < 1) return 0;
            if (members <= fullUpTo) return max;
            if (members >= zeroAt) return 0;
            return max * (zeroAt - members) / (double)(zeroAt - fullUpTo);
        }

        public static double PassupBonus(int members) => Bonus(members, FULL_UP_TO, ZERO_AT, PASSUP_MAX);
        public static double LumBonus(int members) => Bonus(members, FULL_UP_TO, ZERO_AT, LUM_MAX);

        public static ulong Apply(ulong amount, double bonus)
        {
            if (bonus <= 0 || amount == 0) return amount;
            double v = Math.Floor(amount * (1.0 + bonus));
            return v >= ulong.MaxValue ? ulong.MaxValue : Math.Max(amount, (ulong)v);
        }

        public static long Apply(long amount, double bonus) =>
            amount <= 0 ? amount : (long)Math.Min(long.MaxValue, Apply((ulong)amount, bonus));

        // accounts, not characters

        /// <summary>One member for the count: its account and whether it played this week.</summary>
        public struct Who { public uint Guid, Account; public bool Active; }

        /// <summary>(distinct accounts, distinct accounts with a member active this week).</summary>
        public static (int accounts, int active) Accounts(IEnumerable<Who> members)
        {
            var all = new HashSet<ulong>();
            var act = new HashSet<ulong>();
            foreach (var m in members ?? Enumerable.Empty<Who>())
            {
                ulong key = m.Account != 0 ? m.Account : (1UL << 32) | m.Guid;
                all.Add(key);
                if (m.Active) act.Add(key);
            }
            return (all.Count, act.Count);
        }

        /// <summary>Two characters of the same known account.</summary>
        public static bool SameAccount(uint a, uint b) => a != 0 && a == b;

        /// <summary>The pass-up bonus for one vassal-to-patron pass: none between characters of one account.</summary>
        public static double PassupFor(double bonus, uint vassalAccount, uint patronAccount) =>
            SameAccount(vassalAccount, patronAccount) ? 0 : bonus;

        /// <summary>The whole percent the panel shows ("+50%").</summary>
        public static int Percent(double bonus) => (int)Math.Round(bonus * 100, MidpointRounding.AwayFromZero);

        // ------------------------------------------------------------------ the directory's order

        public sealed class Row
        {
            public uint Monarch; public string Name; public int Members, Active, Online; public double Fit = 1;
        }

        /// <summary>How small an allegiance is, for its place in the list: 1 up to FULL_UP_TO members, falling to 0.2 at 50 and beyond.</summary>
        public static double Smallness(int members)
        {
            if (members <= FULL_UP_TO) return 1.0;
            if (members >= 50) return 0.2;
            return 1.0 - 0.8 * (members - FULL_UP_TO) / (50.0 - FULL_UP_TO);
        }

        /// <summary>Busy: members seen this week (up to 10) and on now (up to 5, counted again). 0 = nobody this week.</summary>
        public static int Activity(int activeWeek, int online) => Math.Min(Math.Max(activeWeek, 0), 10) + Math.Min(Math.Max(online, 0), 5);

        /// <summary>Activity counts up to this in the score: past a handful of active members, size decides.</summary>
        public const int SCORE_ACT_CAP = 5;

        /// <summary>The order's score: activity (up to SCORE_ACT_CAP) weighted by smallness and the reader's level fit.</summary>
        public static double Score(Row r)
        {
            int act = Activity(r.Active, r.Online);
            if (act == 0) return 0;
            return (1 + Math.Min(act, SCORE_ACT_CAP)) * Smallness(r.Members) * Math.Max(0.05, r.Fit);
        }

        /// <summary>The directory as a reader sees it: score, then on now, then the SMALLER first, then name.</summary>
        public static List<Row> Sort(IEnumerable<Row> rows) =>
            (rows ?? Enumerable.Empty<Row>()).Where(r => r != null)
                .OrderByDescending(Score).ThenByDescending(r => r.Online).ThenBy(r => r.Members)
                .ThenBy(r => r.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Monarch).ToList();

        /// <summary>The one suggestion: the first row that is active this week, under ZERO_AT members, and not the reader's own</summary>
        public static uint Suggested(IList<Row> sorted, uint readersMonarch)
        {
            foreach (var r in sorted ?? new List<Row>())
                if (r.Monarch != readersMonarch && Activity(r.Active, r.Online) > 0 && r.Members < ZERO_AT && r.Fit >= 0.5) return r.Monarch;
            return 0;
        }

        // ------------------------------------------------------------------ who is asked

        public const int PROMPT_MAX = 6, MAX_VASSALS = 11;
        /// <summary>ACE's officer rank that may take vassals while the allegiance is locked (Player.IsPledgable).</summary>
        public const int CASTELLAN = 3;

        public sealed class Member
        {
            public uint Guid; public bool Online, BotOrStaff, IgnoresRequests, IsMonarch, IsContact; public int Level, Vassals, OfficerRank;
        }

        /// <summary>Can this member take the asker as a vassal now?</summary>
        public static bool CanTake(Member m, uint asker, int askerLevel, bool locked) =>
            m != null && m.Online && !m.BotOrStaff && m.Guid != asker && m.Vassals < MAX_VASSALS && !m.IgnoresRequests
            && m.Level >= Math.Max(1, askerLevel) && (!locked || m.OfficerRank >= CASTELLAN);

        /// <summary>Who is asked, in order, at most PROMPT_MAX</summary>
        public static List<uint> Prompted(IEnumerable<Member> members, uint asker, int askerLevel, bool locked) =>
            (members ?? Enumerable.Empty<Member>()).Where(m => CanTake(m, asker, askerLevel, locked))
                .OrderByDescending(m => m.IsContact).ThenByDescending(m => m.IsMonarch).ThenByDescending(m => m.OfficerRank)
                .ThenBy(m => m.Vassals).ThenByDescending(m => m.Level).ThenBy(m => m.Guid)
                .Take(PROMPT_MAX).Select(m => m.Guid).ToList();

        // what a member may do from the panel

        public const int SPEAKER = 1, SENESCHAL = 2, MONARCH = 4;

        public static bool MaySetMotd(int perm) => perm >= SPEAKER;
        public static bool MayLock(int perm) => perm >= SENESCHAL;
        public static bool MayName(int perm) => perm >= CASTELLAN;

        // ------------------------------------------------------------------ lines

        public static string BonusLine(int passupPct, int lumPct) =>
            $"Small allegiance bonus: +{passupPct}% XP passed up, +{lumPct}% luminance.";
        public const string NOBODY_TO_ASK = "Nobody from that allegiance who can take a vassal is on. Your request waits; they are asked when they log in.";

        public static IEnumerable<string> AllLines()
        {
            yield return BonusLine(25, 12); yield return NOBODY_TO_ASK;
        }
    }
}
