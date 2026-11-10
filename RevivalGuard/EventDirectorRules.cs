using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// The Event Director's pure scheduling rules: no ACE types, so tools/event-director-test can run them on any
    /// machine (docs/EVENT-DIRECTOR.md, "Festival events"). EventDirector.cs feeds them the live facts.
    /// </summary>
    public static class EventDirectorRules
    {
        /// <summary>A festival entry runs only while one of its festivals is on (SeasonalCalendar.IsFestivalActive, so
        /// an admin's @festival on/off counts). An entry with no festivals is year-round.</summary>
        public static bool FestivalOpen(string[] festivals, Func<string, bool> isActive) =>
            festivals == null || festivals.Length == 0 || festivals.Any(isActive);

        /// <summary>
        /// FROSTFELL RUNS ITS OWN EVENTS (owner 2026-10-29: "Event director on from Dec 4"). While Frostfell is active the
        /// director runs its Frostfell entries (the Present Raids, the Holtburg snowmen, the Stonehold Scold) even with
        /// rg_event_director off; rg_event_director_frostfell (default on) lets staff switch that off. rg_event_director on
        /// runs the whole rotation as before. No datafix: the calendar decides, so nothing can start early.
        /// </summary>
        public const string FROSTFELL = "frostfell";

        /// <summary>RETIRED EVENTS RUN THEIR OWN TOO (owner 2026-11-01: every retired live event on a yearly automated
        /// calendar, docs/EVENTS-RETIRED.md). The Bur Portal Summoning Ritual's assault is a director entry like the Present
        /// Raids; while its festival is on it runs with rg_event_director off, behind rg_event_director_retired (default on).</summary>
        public const string BUR_RITUAL = "burritual";

        /// <summary>Aerbax's Prodigal Children (retail Jan 2008): its three waves near Yaraq are a director entry too
        /// (docs/EVENT-TESTS/prodigal.md).</summary>
        public const string PRODIGAL = "prodigal";

        /// <summary>The House Mhoire Risings (retail Feb 2012): Lady Tairla near Dryreach and the Corrupted Pyre Oak near
        /// Ayan Baqur, two director entries (docs/EVENT-TESTS/mhoire.md).</summary>
        public const string MHOIRE = "mhoire";

        /// <summary>The 2009 Hero Token raids (retail Mar and Apr 2009): two director entries at Hebian-To
        /// (docs/EVENT-TESTS/hero-raids.md).</summary>
        public const string HERO_RAIDS = "heroraids";

        /// <summary>Captain Burnja Attacks the Windmill (retail Apr 2007): one director entry (docs/EVENT-TESTS/burnja.md).</summary>
        public const string BURNJA = "burnja";

        /// <summary>The Burun Raids (retail May 2004): five director entries, one per raided town (docs/EVENT-TESTS/burun-raids.md).</summary>
        public const string BURUN_RAIDS = "burunraids";

        /// <summary>The Viridian incursion at Baishi (retail Apr 2015): one director entry (docs/EVENT-TESTS/viridian-baishi.md).</summary>
        public const string VIRIDIAN_BAISHI = "viridianbaishi";

        /// <summary>Martine Attacks (retail May to Jun 2002): two director entries (docs/EVENT-TESTS/martine-attacks.md).</summary>
        public const string MARTINE_ATTACKS = "martineattacks";

        /// <summary>The revived retired events whose own entries run with rg_event_director off (rg_event_director_retired).</summary>
        public static readonly string[] RETIRED = { BUR_RITUAL, PRODIGAL, MHOIRE, HERO_RAIDS, BURNJA, BURUN_RAIDS, VIRIDIAN_BAISHI, MARTINE_ATTACKS };

        /// <summary>
        /// FESTIVALS THAT RUN THEIR OWN EVENTS. Frostfell (above) and T'thuun's Marauders (2026-11-01, docs/EVENT-TESTS/
        /// tthuun-marauders.md: the town raids ARE that festival, so its entries run while it is on even with the rotation
        /// off). Each has its own staff switch, default on: rg_event_director_frostfell, rg_event_director_tthuun.
        /// </summary>
        public const string TTHUUN = "tthuun";

        /// <summary>The festivals whose own director entries run with rg_event_director off (each behind its switch:
        /// Frostfell's, T'thuun's, and rg_event_director_retired for every revived retired event).</summary>
        public static readonly string[] SELF_RUN = new[] { FROSTFELL, TTHUUN }.Concat(RETIRED).ToArray();

        /// <summary>The director runs: the rotation is on, or a self-running festival is on with its switch on.
        /// selfRunning(key) = that festival is active and its switch is on.</summary>
        public static bool DirectorOn(bool enabled, Func<string, bool> selfRunning) =>
            enabled || (selfRunning != null && SELF_RUN.Any(selfRunning));

        /// <summary>May this entry run? Everything with the rotation on; else only an entry of a self-running festival
        /// whose switch is on (switchOn(key)).</summary>
        public static bool EntryAllowed(bool enabled, string[] festivals, Func<string, bool> switchOn) =>
            enabled || (festivals != null && festivals.Any(f => SELF_RUN.Contains(f, StringComparer.OrdinalIgnoreCase) && (switchOn == null || switchOn(f))));

        /// <summary>A boss lair inside the event's landblock box, widened by `margin` landblocks each way.</summary>
        public static bool LairNear((int x0, int y0, int x1, int y1) box, ushort lair, int margin)
        {
            int x = lair >> 8, y = lair & 0xFF;
            return x >= box.x0 - margin && x <= box.x1 + margin && y >= box.y0 - margin && y <= box.y1 + margin;
        }

        /// <summary>RETAIL'S RAID CADENCE: the Holiday Events Stopgap Gen (ace80018) respawns the Present Raids Stopgap
        /// (73224) every 14,400 s and it starts one raid, Tufa or Yaraq, so retail ran one Present Raid every 4 hours.
        /// The director keeps that: a raid may not start within this many seconds of the last raid's start.</summary>
        public const double RAID_APART_S = 14400;
        public const string RAID_REGION = "presentraids";

        /// <summary>Did any entry of this group start less than `apart` seconds before `now`? (lastStarts: unix seconds,
        /// 0 or missing = never.)</summary>
        public static bool TooSoon(double now, IEnumerable<double> lastStarts, double apart) =>
            lastStarts != null && lastStarts.Any(t => t > 0 && now - t < apart);

        /// <summary>Do [from, to) and the boss's warned-or-open window [winFrom, winTo) overlap?</summary>
        public static bool Overlaps(double from, double to, double winFrom, double winTo) => winFrom < to && winTo > from;

        /// <summary>The result of a pick: the catalogue index (-1: nothing can run) and the rotation state after it.</summary>
        public struct Pick
        {
            public int Index;
            public int Rotation, FestRotation;
            public int SinceFest;   // year-round events started since the last festival one
        }

        /// <summary>
        /// Two round robins over one catalogue: the year-round entries from `rotation`, the festival entries from
        /// `festRotation`. A festival entry gets the slot once `every` year-round events have run since the last one
        /// (event_director_festival_every: 1 alternates, 3 is the default, about 2 festival events a day); with
        /// nothing seasonal eligible the year-round rotation runs alone, and the other way round.
        /// </summary>
        public static Pick Choose(IReadOnlyList<bool> isFestival, IReadOnlyList<bool> eligible, int rotation, int festRotation, int sinceFest, int every)
        {
            int n = isFestival.Count;
            bool festTurn = sinceFest >= Math.Max(1, every);
            var r = new Pick { Index = -1, Rotation = rotation, FestRotation = festRotation, SinceFest = sinceFest };
            if (n == 0) return r;
            int Scan(int from, bool fest)
            {
                for (int i = 0; i < n; i++)
                {
                    int idx = ((from % n) + n + i) % n;
                    if (isFestival[idx] == fest && eligible[idx]) return idx;
                }
                return -1;
            }
            int first = festTurn ? Scan(festRotation, true) : Scan(rotation, false);
            bool pickedFest = festTurn;
            if (first < 0) { first = festTurn ? Scan(rotation, false) : Scan(festRotation, true); pickedFest = !festTurn; }
            if (first < 0) return r;
            r.Index = first;
            Advance(ref r, first, pickedFest, n);
            return r;
        }

        /// <summary>Move the rotation past an entry that has just started, however it was started: a festival entry
        /// restarts the count of year-round events, a year-round one adds to it.</summary>
        public static void Advance(ref Pick p, int index, bool festival, int count)
        {
            if (count <= 0) return;
            if (festival) { p.FestRotation = (index + 1) % count; p.SinceFest = 0; }
            else { p.Rotation = (index + 1) % count; p.SinceFest = Math.Min(p.SinceFest + 1, 1000); }
        }
    }
}
