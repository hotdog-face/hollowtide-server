using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// THE FESTIVAL TABLE AND ITS RULES, PURE (no ACE types): SeasonalCalendar.cs runs them on the world loop, tools/nettest
    /// compiles this file by path and steps it through whole years (the yearly buffer swap, owner 2026-10-25), and
    /// tools/seasons/export_calendar.py, tools/seasons/preview.py and tools/journal/seasons.py parse the table's literals.
    /// Every window is a month and day, never a year: the table repeats every year by itself.
    /// </summary>
    public static partial class SeasonalCalendar
    {
        /// <summary>
        /// Retail's windows are stored as 05:01 and 04:59 UTC, which is 00:01 and 23:59 at UTC-5,
        /// in October as well as in December. That is US Eastern with daylight saving ignored, which
        /// is what a hand-built live-ops table looks like. Evaluating the calendar at a fixed -5
        /// reproduces the stored windows exactly instead of drifting an hour twice a year.
        /// (The two New Year rows, 80222 and 80224, were authored in plain UTC by someone else. The
        /// one-minute margins at each end are dropped here; a festival that starts at midnight
        /// rather than 00:01 is not a fidelity question anyone can observe.)
        /// </summary>
        internal const int RETAIL_UTC_OFFSET_HOURS = -5;

        /// <summary>A row of the table. tools/seasons/export_calendar.py parses these literals into
        /// Assets/StreamingAssets/seasons.json for the client's calendar: keep each field a plain literal.</summary>
        internal sealed class Festival
        {
            public string Key;              // what @festival takes
            public string Name;             // what a player is told
            public int StartMonth, StartDay, EndMonth, EndDay;   // inclusive both ends, local to UTC-5
            public string[] On = Array.Empty<string>();          // ace_world.event rows to start
            public string[] Off = Array.Empty<string>();         // rows to stop for the duration
            public string Announce;         // the world broadcast when it opens (seasons.json "about")
            public string Hint = "";        // where to find it, on the login line (FestivalNotices); placements checked on OVH 2026-10-21
            public string Evidence;         // the weenie this was read off, shown by @festival list

            public string Window => $"{StartMonth:00}-{StartDay:00} to {EndMonth:00}-{EndDay:00}";
        }

        /// <summary>
        /// THE CALENDAR. Every window and every bundle below is transcribed from a retail stopgap
        /// weenie; the id in Evidence is the `...stopgapgen` that carries the dates, and its
        /// `...stopgap!` child carries the StartEvent/StopEvent list. Nothing here was invented.
        ///
        /// Two rows are worth pausing on:
        ///  - Festival Season is the only festival that turns something OFF. `normaltowncrier` gates
        ///    `aljalimasign` (weenie 4658) in the three Al-Jalima landblocks, and Al-Jalima is where
        ///    the zombie incursion happens, so retail takes the ordinary town furniture down for the
        ///    month and puts a festival crier up in its place. It must go back On afterwards, which
        ///    is why Off lists are restored rather than merely stopped.
        ///  - Fireworks appears twice, from two different stopgaps (69999 for the anniversary week,
        ///    80223 under the New Year's Day gen). That is why desired state is computed as a union
        ///    over active festivals rather than per festival: two windows can want the same row.
        /// </summary>
        internal static readonly Festival[] Calendar =
        {
            new Festival
            {
                Key = "festivalseason", Name = "Festival Season",
                StartMonth = 10, StartDay = 1, EndMonth = 11, EndDay = 1,
                On  = new[] { "EventFallFestival" },
                // AzimarYearRound (ours, azimar-seasons.sql, owner 2026-10-25): Azimar stands in the Pumpkin Buffers'
                // spots the rest of the year; the pumpkins have them for the month
                Off = new[] { "normaltowncrier", "AzimarYearRound" },
                Announce = "The harvest has turned. Pumpkin Lords and Harvest Reapers walk the "
                         + "landscape, the dead are stirring in Al-Jalima, and the Majestic Pumpkin "
                         + "is waiting in Glenden Wood.",
                Hint = "Pumpkins line Holtburg, the Festival Costumer is in Glenden Wood, and the dead walk in Al-Jalima.",
                Evidence = "ace80069-fallfestivaleventsstopgapgen, Oct 1 to Nov 1",
            },
            new Festival
            {
                Key = "frostfell", Name = "Frostfell",
                StartMonth = 12, StartDay = 4, EndMonth = 1, EndDay = 1,
                On  = new[] { "assaultonfrosthavenevent", "GiftGopherEvent", "hiddenpresentsevent",
                              "HollyJollyHelperEvent", "SclavusSantaEvent" },
                Announce = "Winter has come to Dereth. Presents are hidden across the towns, Frost "
                         + "Haven is under assault, and something wearing a red suit has been seen "
                         + "in the desert.",
                Hint = "Look for hidden presents in the towns, and the Frostfell Costumer in Holtburg.",
                Evidence = "ace80018-holidayeventsstopgapgen, Dec 4 to Jan 1",
            },
            new Festival
            {
                Key = "spring", Name = "the Spring Festival",
                StartMonth = 4, StartDay = 1, EndMonth = 5, EndDay = 1,
                On  = new[] { "springbabies", "springbunnyevent", "SpringEasterEggs", "IHOPopen" },
                Announce = "Spring has come to Dereth. Young creatures are on the landscape, "
                         + "coloured eggs are turning up, and the House of Pancakes has opened.",
                Hint = "Look for eggs and young creatures on the landscape, the House of Pancakes in Shoushi, and the Spring Costumer in Holtburg.",
                Evidence = "ace87093-springeventsstopgapgen, Apr 1 to May 1",
            },
            new Festival
            {
                // Not a stopgap bundle: the April 2003 Raining Mad Cows Gen (23631, placed 22 times)
                // carried its own RealTime window, 2026-04-01 00:01 to 23:59 at UTC-5, and no event row.
                // Moved onto the calendar 2026-09-28 (owner-approved) on AprilFoolsMadCows, a row this
                // project added: tools/gpubox-ace/stones-madcow-calendar.sql. Inside the Spring Festival's
                // window, which is fine: desired state is a union over active festivals.
                Key = "aprilfools", Name = "April Fools' Day",
                StartMonth = 4, StartDay = 1, EndMonth = 4, EndDay = 1,
                On  = new[] { "AprilFoolsMadCows" },
                Announce = "It is raining cows. They are not happy about it.",
                Hint = "Look up, and mind the cows.",
                Evidence = "ace23631 April 2003 Raining Mad Cows Gen's own window, Apr 1 only",
            },
            new Festival
            {
                Key = "wedding", Name = "the Royal Wedding Anniversary",
                StartMonth = 5, StartDay = 1, EndMonth = 6, EndDay = 1,
                On  = new[] { "ReceptionGames" },
                Announce = "The Royal Wedding anniversary is being kept. The reception games have "
                         + "begun again.",
                Hint = "",
                Evidence = "ace80228-royalweddinganniversarystopgapgen, May 1 to Jun 1",
            },
            new Festival
            {
                // AnniversaryFestivalStones is a row this project added (2026-09-28, owner-approved): the
                // twenty Festival Stones (5376-5395, "Rejoice! The Hopeslayer has been defeated") carried
                // their own RealTime window, 2026-11-02 to 11-05 at UTC-5, the same four days as this
                // stopgap, and would never have come back. tools/gpubox-ace/stones-madcow-calendar.sql.
                Key = "anniversary", Name = "the Anniversary",
                StartMonth = 11, StartDay = 2, EndMonth = 11, EndDay = 5,
                On  = new[] { "Fireworks", "AnniversaryFestivalStones" },
                Announce = "Another year of Dereth. There are fireworks over the towns, and the Festival "
                         + "Stones have risen again.",
                Hint = "Look for fireworks over the towns, the Festival Stones across Dereth, and the Anniversary Costumer in Cragstone.",
                Evidence = "ace69999-fireworksstopgapgen, Nov 2 to Nov 5 (and the Festival Stones' own window)",
            },
            new Festival
            {
                // Not a stopgap bundle: the Night Club Attendant Generator (69997, placed at 0x33D9,
                // 0x8090, 0xBC9F and 0xE74E) carried its OWN RealTime window, 1793595660-1796187540, and
                // no event row, so it would have opened in November 2026 and never again. Moved onto
                // this calendar 2026-09-28 (owner-approved): tools/gpubox-ace/nightclub-calendar.sql makes
                // it an Event generator gated on AnniversaryNightClub, a row this project added. The
                // window is its own, Nov 2 00:01 to Dec 1 23:59 at UTC-5, the Night Club's retail month.
                Key = "nightclub", Name = "the Anniversary Night Club",
                StartMonth = 11, StartDay = 2, EndMonth = 12, EndDay = 1,
                On  = new[] { "AnniversaryNightClub" },
                Announce = "The Night Club is open for the anniversary. Its attendants are waiting in "
                         + "Cragstone, Hebian-To, Sanamar and Zaikhal.",
                Hint = "Its attendants are in Cragstone, Hebian-To, Sanamar and Zaikhal.",
                Evidence = "ace69997-nightclubattendantgenerator's own window, Nov 2 to Dec 1",
            },
            new Festival
            {
                Key = "newyear", Name = "the New Year",
                StartMonth = 1, StartDay = 1, EndMonth = 2, EndDay = 1,
                On  = new[] { "EventNewYear" },
                Announce = "A new year begins in Dereth.",
                Hint = "",
                Evidence = "ace80222-newyeareventsstopgapgen, Jan 1 to Feb 1",
            },
            new Festival
            {
                Key = "newyearsday", Name = "New Year's Day",
                StartMonth = 1, StartDay = 1, EndMonth = 1, EndDay = 1,
                On  = new[] { "Fireworks" },
                Announce = "Fireworks over Dereth for the new year.",
                Hint = "Look up for fireworks over the towns.",
                Evidence = "ace80224-newyearsdayeventsstopgapgen, Jan 1 only",
            },
        };

        /// <summary>
        /// Is this (month, day) inside the festival's window? Both ends are inclusive days, and the
        /// comparison is on MMDD so that Frostfell, which starts in December and ends in January,
        /// works without knowing which year either end is in.
        /// </summary>
        internal static bool InWindow(Festival f, DateTime local)
        {
            int cur = local.Month * 100 + local.Day;
            int start = f.StartMonth * 100 + f.StartDay;
            int end = f.EndMonth * 100 + f.EndDay;
            return start <= end ? (cur >= start && cur <= end) : (cur >= start || cur <= end);
        }

        /// <summary>
        /// What every event row the table names should be, given which festivals are active: ON beats OFF; a row some
        /// festival SUPPRESSES (its Off list) is part of the world's ordinary state, so outside that festival it belongs
        /// On (normaltowncrier, AzimarYearRound). Nothing ends up in both sets. Apply moves the rows that differ.
        /// </summary>
        internal static (HashSet<string> on, HashSet<string> off) Desired(ICollection<Festival> active)
        {
            var wantOn = new HashSet<string>(active.SelectMany(f => f.On), StringComparer.OrdinalIgnoreCase);
            var wantOff = new HashSet<string>(active.SelectMany(f => f.Off), StringComparer.OrdinalIgnoreCase);
            wantOff.ExceptWith(wantOn);
            foreach (var f in Calendar)
            {
                foreach (var name in f.On)
                    if (!wantOn.Contains(name)) wantOff.Add(name);
                foreach (var name in f.Off)
                    if (!wantOff.Contains(name)) wantOn.Add(name);
            }
            wantOff.ExceptWith(wantOn);
            return (wantOn, wantOff);
        }

        /// <summary>A forced festival hands itself back to the calendar this many hours after it was set (owner
        /// 2026-10-25: nothing may leave the yearly buffer swap stuck; the Sep 22 override ran Festival Season for weeks).</summary>
        internal const int OVERRIDE_HOURS = 72;

        /// <summary>Has an override set at <paramref name="at"/> (UTC; null = unknown) run out? "keep" ones never do.</summary>
        internal static bool OverrideExpired(DateTime? at, bool keep, DateTime nowUtc) =>
            !keep && (at == null || nowUtc >= at.Value.AddHours(OVERRIDE_HOURS));

        /// <summary>The festivals whose natural window holds this local date (no overrides).</summary>
        internal static List<Festival> NaturalOn(DateTime local) => Calendar.Where(f => InWindow(f, local)).ToList();
    }
}
