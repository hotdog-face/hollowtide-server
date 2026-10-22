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

        /// <summary>A boss lair inside the event's landblock box, widened by `margin` landblocks each way.</summary>
        public static bool LairNear((int x0, int y0, int x1, int y1) box, ushort lair, int margin)
        {
            int x = lair >> 8, y = lair & 0xFF;
            return x >= box.x0 - margin && x <= box.x1 + margin && y >= box.y0 - margin && y <= box.y1 + margin;
        }

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
