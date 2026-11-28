using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevivalGuard
{
    /// <summary>Darktide season 1, the pure half (no ACE types; tools/nettest compiles it by path with PkNightRules.cs, PkSeasonCase).</summary>
    public static class PkSeasonRules
    {
        public static readonly DateTime PROPOSED_SUNDAY = new DateTime(2026, 12, 6, 0, 0, 0, DateTimeKind.Unspecified);
        public const int PROPOSED_ROUNDS = 5;
        public const string PROPOSED_NAME = "Darktide Season 1", PROPOSED_NEXT = "Darktide Season 2";
        public const string PRESEASON = "Preseason";
        const long DAY = 24 * 3600, HOUR = 3600, INTERVAL = PkNight.INTERVAL_DAYS * DAY;

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

        /// <summary>A season opening with PK Night on `sunday` and ending when its `rounds`th PK Night is over.</summary>
        public static Schedule Plan(DateTime sunday, int rounds, string name, string next)
        {
            if (!PkNight.IsNightSunday(sunday)) throw new ArgumentException("the season opens on a PK Night Sunday (every other Sunday from Nov 22)");
            if (rounds < 2 || rounds > 26) throw new ArgumentException("a season is 2 to 26 PK Nights");
            var start = PkNight.StartUtcOf(sunday);
            var lastNight = PkNight.StartUtcOf(sunday.Date.AddDays(PkNight.INTERVAL_DAYS * (rounds - 1)));
            return new Schedule
            {
                Name = name, NextName = next, StartAt = Unix(start),
                EndAt = Unix(lastNight.AddMinutes(PkNight.ROUND_MINUTES)),
            };
        }

        public enum Step { None, Start, End, Notice }

        /// <summary>What is due now: the opening, the end, or one announcement (its key in `notice`, to be added to Said once told).</summary>
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

        /// <summary>The season's PK Nights, the first and last included.</summary>
        public static int Rounds(Schedule s) => (int)Math.Round((s.EndAt - s.StartAt) / (double)INTERVAL) + 1;
        /// <summary>Which PK Night of the season a moment falls in or after, 1-based.</summary>
        public static int RoundOf(Schedule s, long now) => (int)Math.Max(1, Math.Min(Rounds(s), (now - s.StartAt) / INTERVAL + 1));

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
            + $"It runs over {Rounds(s)} PK Nights, {PkNight.SCHEDULE_WORDS}. The season ends after PK Night on {Day(s.EndAt)}.";

        public static string EndDayLine(Schedule s, string leader, long renown) =>
            $"{s.Name} ends in a day, after PK Night on {NightWords(s.EndAt, true)}. "
            + (leader != null ? $"{leader} leads with {renown:N0} renown." : "Nobody has earned renown yet.");

        /// <summary>After each PK Night round while the season runs: which PK Night of the season it was and the top three.</summary>
        public static string FinaleLine(Schedule s, long now, IList<(string name, long renown)> top)
        {
            var head = $"{s.Name} after PK Night {RoundOf(s, now)} of {Rounds(s)}";
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
