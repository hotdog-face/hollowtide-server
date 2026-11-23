using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevivalGuard
{
    /// <summary>
    /// DARKTIDE SEASON 1, THE PURE HALF (no ACE types; tools/nettest compiles it by path with PkNightRules.cs, PkSeasonCase).
    /// Owner 2026-11-21 ("yes all 1-5", docs/findings/pk-server-plan-2026-11-21.md move 1): a dated PK season. It opens
    /// with PK Night on a Sunday at 7 PM Pacific with a fresh renown ladder, runs a whole number of weeks with each
    /// PK Night as its weekly finale, and ends when the last PK Night's hour ends: the leader is Warlord of Dereth,
    /// second and third Gladiator Champion (PkRenown.EndSeason). PkSeason.cs runs it on the world loop.
    /// </summary>
    public static class PkSeasonRules
    {
        /// <summary>The proposal (owner confirms date and name): opens with PK Night on Sunday 2026-12-06, 7 PM PST
        /// (Mon 12-07 03:00 UTC), eight weeks, the last PK Night Sunday 2027-01-31.</summary>
        public static readonly DateTime PROPOSED_SUNDAY = new DateTime(2026, 12, 6, 0, 0, 0, DateTimeKind.Unspecified);
        public const int PROPOSED_WEEKS = 8;
        public const string PROPOSED_NAME = "Darktide Season 1", PROPOSED_NEXT = "Darktide Season 2";
        public const string PRESEASON = "Preseason";
        const long WEEK = 7 * 24 * 3600, DAY = 24 * 3600, HOUR = 3600;

        public sealed class Schedule
        {
            public string Name { get; set; }
            public string NextName { get; set; }
            public long StartAt { get; set; }        // unix UTC: the opening PK Night's start
            public long EndAt { get; set; }          // unix UTC: the last PK Night's end
            public bool Started { get; set; }
            public bool Ended { get; set; }
            public HashSet<string> Said { get; set; } = new HashSet<string>();
        }

        public static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        public static DateTime Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        /// <summary>A season opening with PK Night on `sunday` (a Pacific date) and ending when the PK Night `weeks`
        /// weeks later is over (so it holds `weeks` + 1 PK Nights, the first and last included).</summary>
        public static Schedule Plan(DateTime sunday, int weeks, string name, string next)
        {
            if (sunday.DayOfWeek != DayOfWeek.Sunday) throw new ArgumentException("the season opens on a Sunday (PK Night)");
            if (weeks < 1 || weeks > 52) throw new ArgumentException("a season is 1 to 52 weeks");
            var start = PkNight.StartUtcOf(sunday);
            var lastNight = PkNight.StartUtcOf(sunday.Date.AddDays(7 * weeks));
            return new Schedule
            {
                Name = name, NextName = next, StartAt = Unix(start),
                EndAt = Unix(lastNight.AddMinutes(PkNight.ROUND_MINUTES)),
            };
        }

        public enum Step { None, Start, End, Notice }

        /// <summary>What is due now: the opening, the end, or one announcement (its key in `notice`, to be added to
        /// Said once told). A notice missed while the server was down is skipped, never told late.</summary>
        public static Step Due(Schedule s, long now, out string notice)
        {
            notice = null;
            if (s == null || s.Ended || s.StartAt <= 0) return Step.None;
            if (!s.Started)
            {
                if (now >= s.StartAt) return Step.Start;
                if (Window(s, "open-day", now, s.StartAt - DAY, s.StartAt - HOUR)) { notice = "open-day"; return Step.Notice; }
                if (Window(s, "open-hour", now, s.StartAt - HOUR, s.StartAt)) { notice = "open-hour"; return Step.Notice; }
                return Step.None;
            }
            if (s.EndAt <= 0) return Step.None;
            if (now >= s.EndAt) return Step.End;
            if (Window(s, "end-day", now, s.EndAt - DAY - HOUR, s.EndAt - 2 * HOUR)) { notice = "end-day"; return Step.Notice; }
            return Step.None;
        }

        static bool Window(Schedule s, string key, long now, long from, long until) => now >= from && now < until && !s.Said.Contains(key);

        public static int Weeks(Schedule s) => (int)Math.Round((s.EndAt - s.StartAt) / (double)WEEK);
        /// <summary>Which week a moment falls in, 1-based (the opening PK Night starts week 1).</summary>
        public static int WeekOf(Schedule s, long now) => (int)Math.Max(1, Math.Min(Weeks(s), (now - s.StartAt) / WEEK + 1));

        /// <summary>"Sunday, Dec 6 at 7 PM Pacific" for the PK Night that opens or closes the season.</summary>
        public static string NightWords(long startOrEnd, bool isEnd)
        {
            var utc = isEnd ? Utc(startOrEnd).AddMinutes(-PkNight.ROUND_MINUTES) : Utc(startOrEnd);
            var l = PkNight.PacificLocal(utc);
            if (l.Hour == PkNight.START_HOUR && l.Minute == 0) return PkNight.WhenWords(utc);
            // a season opened by hand, off the PK Night hour
            return $"{l:dddd}, {l.ToString("MMM d", CultureInfo.InvariantCulture)} at {l.ToString("h:mm tt", CultureInfo.InvariantCulture)} Pacific";
        }

        static string Day(long endAt)
        {
            var l = PkNight.PacificLocal(Utc(endAt).AddMinutes(-PkNight.ROUND_MINUTES));
            return $"{l:dddd}, {l.ToString("MMM d", CultureInfo.InvariantCulture)}";
        }

        // ---------------------------------------------------------------- the words (no em dashes)

        public static string OpenDayLine(Schedule s) =>
            $"{s.Name} opens in a day, with PK Night on {NightWords(s.StartAt, false)}. The renown ladder starts fresh, and when the season "
            + $"ends after PK Night on {Day(s.EndAt)}, the top killer is crowned Warlord of Dereth and the next two Gladiator Champion.";

        public static string OpenHourLine(Schedule s) =>
            $"{s.Name} opens in an hour, with PK Night. Every honourable kill from then on counts toward the season.";

        public static string OpenLine(Schedule s, bool trophies) =>
            $"{s.Name} has begun. The renown ladder is fresh: honourable kills earn renown{(trophies ? " and a Darktide Trophy" : "")}. "
            + $"PK Night, every Sunday at 7 PM Pacific, is the weekly finale. The season ends after PK Night on {Day(s.EndAt)}.";

        public static string EndDayLine(Schedule s, string leader, long renown) =>
            $"{s.Name} ends in a day, after PK Night on {NightWords(s.EndAt, true)}. "
            + (leader != null ? $"{leader} leads with {renown:N0} renown." : "Nobody has earned renown yet.");

        /// <summary>After each PK Night round while the season runs: the week and the top three.</summary>
        public static string FinaleLine(Schedule s, long now, IList<(string name, long renown)> top)
        {
            var head = $"{s.Name}, week {WeekOf(s, now)} of {Weeks(s)}";
            if (top == null || top.Count == 0) return head + ": nobody has earned renown yet.";
            var parts = new List<string>();
            for (int i = 0; i < top.Count && i < 3; i++) parts.Add($"{i + 1}. {top[i].name} ({top[i].renown:N0})");
            return head + ": " + string.Join(", ", parts) + ".";
        }

        /// <summary>The season's name as the PK board shows it, with its end while it runs.</summary>
        public static string Label(string season, Schedule s, long now)
        {
            if (s == null || !s.Started || s.Ended || s.Name != season || s.EndAt <= 0) return season;
            return $"{season} (ends {Day(s.EndAt)})";
        }
    }
}
