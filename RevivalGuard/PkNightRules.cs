using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// PK NIGHT, THE PURE HALF (no ACE types; tools/nettest compiles this file by path, PkNightCase). The schedule
    /// (every Sunday 7 PM Pacific for an hour, first run 2026-11-22), the spot rotation, the hold scoring, the tie
    /// rule, which announcement is due when, and every line a player reads. PkNight.cs runs it on the world loop.
    /// Design: docs/proposals/pk-night.md; the owner's decisions: docs/proposals/pk-night-decisions.md.
    /// </summary>
    public static partial class PkNight
    {
        // ---------------------------------------------------------------- the schedule

        /// <summary>The first run (owner 2026-11-09): Sunday 2026-11-22. Nothing is announced or run before it.</summary>
        public static readonly DateTime FIRST_SUNDAY = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Unspecified);
        public const int START_HOUR = 19;          // 7 PM Pacific
        public const int ROUND_MINUTES = 60;
        public const string WHEN_WORDS = "Sunday at 7 PM Pacific";

        /// <summary>US daylight saving (since 2007): from the second Sunday of March at 2:00 to the first Sunday of
        /// November at 2:00, local time. Pacific is UTC-8 in winter and UTC-7 in summer.</summary>
        public static int PacificOffsetHours(DateTime utc)
        {
            int y = utc.Year;
            var dstStart = NthSunday(y, 3, 2).AddHours(2 + 8);    // 2:00 PST = 10:00 UTC
            var dstEnd = NthSunday(y, 11, 1).AddHours(2 + 7);     // 2:00 PDT = 09:00 UTC
            return utc >= dstStart && utc < dstEnd ? -7 : -8;
        }

        static DateTime NthSunday(int year, int month, int n)
        {
            var d = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            int add = ((int)DayOfWeek.Sunday - (int)d.DayOfWeek + 7) % 7;
            return d.AddDays(add + 7 * (n - 1));
        }

        public static DateTime PacificLocal(DateTime utc) => DateTime.SpecifyKind(utc.AddHours(PacificOffsetHours(utc)), DateTimeKind.Unspecified);

        /// <summary>7 PM Pacific on the given local date, as UTC. The offset is read at that moment, so the Sunday
        /// of a clock change still starts at 7 PM on the wall.</summary>
        public static DateTime StartUtcOf(DateTime localDate)
        {
            var guess = DateTime.SpecifyKind(localDate.Date.AddHours(START_HOUR + 8), DateTimeKind.Utc);   // as if PST
            int off = PacificOffsetHours(guess);
            return DateTime.SpecifyKind(localDate.Date.AddHours(START_HOUR - off), DateTimeKind.Utc);
        }

        public sealed class Scheduled
        {
            public int Week;                // 0 = the first run
            public DateTime StartUtc, EndUtc;
            public Spot Spot;
            public bool Running(DateTime utc) => utc >= StartUtc && utc < EndUtc;
            public string Key => $"week{Week}";
        }

        /// <summary>The round running now, or the next one: the first Sunday on or after the first run whose hour
        /// has not ended yet.</summary>
        public static Scheduled Next(DateTime utcNow)
        {
            var local = PacificLocal(utcNow).Date;
            var sunday = local.AddDays(((int)DayOfWeek.Sunday - (int)local.DayOfWeek + 7) % 7);
            if (sunday < FIRST_SUNDAY) sunday = FIRST_SUNDAY;
            for (int i = 0; i < 3; i++, sunday = sunday.AddDays(7))
            {
                var start = StartUtcOf(sunday);
                var end = start.AddMinutes(ROUND_MINUTES);
                if (utcNow < end) return Make(sunday, start, end);
            }
            return null;
        }

        public static Scheduled After(Scheduled s)
        {
            var sunday = PacificLocal(s.StartUtc).Date.AddDays(7);
            var start = StartUtcOf(sunday);
            return Make(sunday, start, start.AddMinutes(ROUND_MINUTES));
        }

        static Scheduled Make(DateTime sunday, DateTime start, DateTime end)
        {
            int week = (int)Math.Round((sunday - FIRST_SUNDAY).TotalDays / 7.0);
            return new Scheduled { Week = week, StartUtc = start, EndUtc = end, Spot = SpotOfWeek(week) };
        }

        public static Spot SpotOfWeek(int week) => SPOTS[((week % SPOTS.Length) + SPOTS.Length) % SPOTS.Length];

        /// <summary>"Sunday, Nov 22 at 7 PM Pacific", for a start time.</summary>
        public static string WhenWords(DateTime startUtc)
        {
            var l = PacificLocal(startUtc);
            return $"{l:dddd}, {l.ToString("MMM d", CultureInfo.InvariantCulture)} at 7 PM Pacific";
        }

        // ---------------------------------------------------------------- the spots

        /// <summary>A landmark the night is fought over. Ns/Ew are map coordinates (north and east positive); the
        /// server makes the position and reads the terrain height when the round opens. Three (Zombie Castle, the
        /// Empyrean Ruin, the Tumerok Fort) are the Shadow Shrine stops a bot landed on 2026-09-28 (docs/PK-EXPANSION.md);
        /// the rest are read from the gazetteer (Assets/StreamingAssets/place-names.json: ace_world portal instances
        /// and the AC wiki), to be walked before the first run.</summary>
        public sealed class Spot
        {
            public string Key, Name, Where, Near, Levels;
            /// <summary>How to walk there, said in the hour-before and start lines (owner 2026-11-18, after the Borg walk test:
            /// Arwic has no north gate). Null: no directions line.</summary>
            public string Way;
            public double Ns, Ew;
            /// <summary>An event portal for the night (owner 2026-11-20, the Dark Isle: retail reached it only by a flagging
            /// quest): where it lands players (map coordinates; 0 = no event portal), where the way-home portal stands at the
            /// spot, and the metres around the centre and the landing in which creature generators pause for the window.</summary>
            public double LandNs, LandEw, HomeNs, HomeEw, ClearMetres;
            public bool HasGate => LandNs != 0 || LandEw != 0;
            public override string ToString() => $"{Name} ({Where})";
        }

        /// <summary>The rotation, in order from the first run (week 0 = the first). Every one is outdoors and far
        /// from Holtburg (42.1N 33.6E), the arrival town (owner: never near the Holtburg arrival area). Owner 2026-11-20
        /// ("Yes, this Sunday"): the Dark Isle takes week 0 and the Mite Maze moves to the end, so weeks 1 to 6 keep their
        /// dates and the Mite Maze still falls on week 7 (Jan 10); only the second time round (week 8 on) shifts.</summary>
        public static readonly Spot[] SPOTS =
        {
            // the owner's 2017 Darktide footage "PVP at 150 Island": the open sand of the south beach by the lifestone
            // (84.6N 58.6E), landblock C8E9 (atlas: walkable sand at sea level, shallows to the north). Retail reached it
            // only by the Dark Isle Flagging quest or the far east of Vissidal, so PK Night opens a portal from Holtburg.
            new Spot { Key = "darkisle", Name = "the Dark Isle", Where = "84.7N 58.4E", Ns = 84.72, Ew = 58.42,
                       Near = "the sand of the Dark Isle's south beach by its lifestone, north of Vissidal", Levels = "any level",
                       Way = "The way in is a portal in Holtburg's lower town, open from an hour before the start, and a portal home waits on the beach.",
                       LandNs = 84.5667, LandEw = 58.25, HomeNs = 84.5833, HomeEw = 58.1667, ClearMetres = 300 },
            new Spot { Key = "zombiecastle", Name = "Zombie Castle", Where = "45.2S 39.1E", Ns = -45.2, Ew = 39.1,
                       Near = "the castle ruin in southern Osteth, by the Zombie Castle estates", Levels = "20 and up" },
            new Spot { Key = "colosseum", Name = "the Colosseum", Where = "11.2S 37.5E", Ns = -11.24, Ew = 37.54,
                       Near = "the old arena on the plains south-east of Tufa", Levels = "any level" },
            new Spot { Key = "empyreanruin", Name = "the Empyrean Ruin", Where = "81.4N 32.9W", Ns = 81.4, Ew = -32.9,
                       Near = "the quiet ruin in the far north-west, north of Stonehold", Levels = "any level" },
            new Spot { Key = "beachfort", Name = "the Beach Fort", Where = "76.0N 49.1W", Ns = 76.0, Ew = -49.05,
                       Near = "the fort on the north-west shore, a portal lands beside it", Levels = "30 and up" },
            new Spot { Key = "gaerlan", Name = "Gaerlan's Citadel", Where = "57.0N 44.8E", Ns = 57.01, Ew = 44.79,
                       Near = "the citadel valley in the north-east, a portal lands at its foot", Levels = "40 and up" },
            new Spot { Key = "tumerokfort", Name = "the Tumerok Fort", Where = "50.7S 81.8W", Ns = -50.7, Ew = -81.8,
                       Near = "the fort in the south-western Direlands", Levels = "50 and up" },
            new Spot { Key = "mitemaze", Name = "the Mite Maze", Where = "41.1N 57.0E", Ns = 41.08, Ew = 57.03,
                       Near = "the snowy valley north of Arwic, by the Mite Maze portal", Levels = "any level",
                       Way = "From Arwic, leave by the west gate and head north." },
        };

        // ---------------------------------------------------------------- the event portals (a spot with HasGate)

        /// <summary>The Holtburg end: the portal stands on the open grass of the lower town west of the well (A9B4 76, 168),
        /// about 155 m from the lifestone square where newcomers and the dead arrive (owner: never near the Holtburg arrival
        /// area); not by the cow pen east of the sign (box test 2026-11-20: a portal there stood inside the fence), nor on the
        /// bank down to the shore at 80, 176. The way-home portal lands players 13 m south-east of it, on the road.</summary>
        public const double HOLTBURG_GATE_NS = 42.7, HOLTBURG_GATE_EW = 33.5167, HOLTBURG_LAND_NS = 42.6708, HOLTBURG_LAND_EW = 33.5583;
        public const int GATE_LEAD_MINUTES = 60;      // the portals open an hour before the start and close at the end

        /// <summary>The spot whose portals should stand now (and whose creatures are paused), or null. A running round:
        /// until its end (a staff round opens them at its start). No round running: the next scheduled one from an hour
        /// before its start, unless that hour's round was already played (ended early by staff).</summary>
        public static Spot GateSpot(Live live, Scheduled next, bool nextPlayed, DateTime utcNow)
        {
            if (live != null)
            {
                var s = SpotNamed(live.SpotKey);
                double now = (utcNow - DateTime.UnixEpoch).TotalSeconds;
                return s != null && s.HasGate && now < live.EndsAt ? s : null;
            }
            if (next == null || !next.Spot.HasGate || nextPlayed) return null;
            return utcNow >= next.StartUtc.AddMinutes(-GATE_LEAD_MINUTES) && utcNow < next.EndUtc ? next.Spot : null;
        }

        /// <summary>Map coordinates to an outdoor cell and the landblock-local metres (ACE: 240 m a map unit, 192 m a
        /// landblock, 24 m a cell, -102 at the origin).</summary>
        public static (uint cell, float x, float y) CellOfMap(double ns, double ew)
        {
            double gx = (ew + 102) * 240, gy = (ns + 102) * 240;
            int bx = (int)Math.Floor(gx / 192), by = (int)Math.Floor(gy / 192);
            double x = gx - bx * 192, y = gy - by * 192;
            uint cell = ((uint)bx << 24) | ((uint)by << 16) | (uint)((int)(x / 24) * 8 + (int)(y / 24) + 1);
            return (cell, (float)x, (float)y);
        }

        /// <summary>Global metres (ACE ToGlobal) of a map point.</summary>
        public static (double x, double y) GlobalOfMap(double ns, double ew) => ((ew + 102) * 240, (ns + 102) * 240);

        /// <summary>A global point inside the spot's paused zone: within ClearMetres of the centre or of the landing.</summary>
        public static bool InClearZone(Spot s, double gx, double gy, double metres)
        {
            if (s == null || metres <= 0) return false;
            var c = GlobalOfMap(s.Ns, s.Ew); var l = GlobalOfMap(s.LandNs, s.LandEw);
            return (gx - c.x) * (gx - c.x) + (gy - c.y) * (gy - c.y) <= metres * metres
                || (gx - l.x) * (gx - l.x) + (gy - l.y) * (gy - l.y) <= metres * metres;
        }

        public const string GATE_NAME = "Dark Isle Portal", HOME_NAME = "Holtburg Portal";
        public static string GateName(Spot s) => s.Key == "darkisle" ? GATE_NAME : $"{Cap(s.Name.Replace("the ", ""))} Portal";
        static string Cap(string t) => string.IsNullOrEmpty(t) ? t : char.ToUpperInvariant(t[0]) + t.Substring(1);

        public static string GateOpenLine(Spot spot) =>
            $"A portal to {spot.Name} has opened in Holtburg's lower town for PK Night. It closes when the night is over.";

        public static Spot SpotNamed(string keyOrNumber)
        {
            if (string.IsNullOrWhiteSpace(keyOrNumber)) return null;
            var k = keyOrNumber.Trim().ToLowerInvariant();
            if (int.TryParse(k, out int n) && n >= 1 && n <= SPOTS.Length) return SPOTS[n - 1];
            return SPOTS.FirstOrDefault(s => s.Key == k || s.Name.ToLowerInvariant().Replace("the ", "") == k.Replace("the ", ""));
        }

        // ---------------------------------------------------------------- the hold

        /// <summary>Metres from the spot's centre within which a player holds it (server property pk_night_radius).</summary>
        public const int DEFAULT_RADIUS = 40;
        public const double TICK_SECONDS = 5;
        public const double LEAD_NOTE_GAP = 60;    // a lead-change line at most once a minute

        public sealed class Standing
        {
            public uint Side { get; set; }
            public string Name { get; set; }
            public double Held { get; set; }          // seconds held, uncontested
            public double LastHeldAt { get; set; }    // unix; the tie rule
            public HashSet<uint> Present { get; set; } = new HashSet<uint>();   // who stood there for the side, by guid
        }

        /// <summary>A round in progress: what the file keeps between ticks and across a restart.</summary>
        public sealed class Live
        {
            public string Key { get; set; }           // "week3", or "forced-<unix>"
            public string SpotKey { get; set; }
            public double StartedAt { get; set; }     // unix
            public double EndsAt { get; set; }
            public bool Forced { get; set; }          // a staff-started round (admin command); counts only when Record
            public bool Record { get; set; }
            public Dictionary<uint, Standing> Sides { get; set; } = new Dictionary<uint, Standing>();
            public uint Holding { get; set; }         // the side that held on the last tick, 0 none
            public bool Contested { get; set; }
            public int PresentNow { get; set; }
            public uint Leader { get; set; }
            public double LeadSaidAt { get; set; }
            public HashSet<string> Said { get; set; } = new HashSet<string>();   // "standings10", "standings20", ...
            public double LastTickAt { get; set; }
        }

        public struct Presence { public uint Side; public string Label; public uint Guid; }

        /// <summary>One scoring tick. `present` is every eligible player killer within the radius. One side alone
        /// holds and earns the tick; two or more sides are a contest and nobody earns it; nobody there, nobody
        /// earns it. Returns the side that took the lead on this tick, or 0.</summary>
        public static uint Tick(Live live, IEnumerable<Presence> present, double now, double dt)
        {
            var here = present.ToList();
            var sides = here.Select(p => p.Side).Distinct().ToList();
            live.PresentNow = here.Count;
            live.Contested = sides.Count > 1;
            live.Holding = sides.Count == 1 ? sides[0] : 0;
            live.LastTickAt = now;
            uint before = Leader(live)?.Side ?? 0;
            if (sides.Count == 1)
            {
                var side = sides[0];
                if (!live.Sides.TryGetValue(side, out var st))
                    live.Sides[side] = st = new Standing { Side = side, Name = here[0].Label };
                st.Name = here[0].Label;
                st.Held += dt;
                st.LastHeldAt = now;
                foreach (var p in here) st.Present.Add(p.Guid);
            }
            uint after = Leader(live)?.Side ?? 0;
            if (after != 0 && after != before) { live.Leader = after; return after; }
            return 0;
        }

        /// <summary>Best first: most time held; a tie goes to the side that reached its time first (the earlier
        /// last scoring tick), then the name.</summary>
        public static List<Standing> Ranked(Live live) =>
            live.Sides.Values.Where(s => s.Held > 0)
                .OrderByDescending(s => Math.Round(s.Held)).ThenBy(s => s.LastHeldAt).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();

        public static Standing Leader(Live live) => Ranked(live).FirstOrDefault();

        // ---------------------------------------------------------------- the announcements

        public enum Notice { DayBefore, HourBefore, TenMinutes, Start, Standings, End }

        /// <summary>Which pre-round notice is due now for a scheduled round, given what was already said for it.
        /// Each has a window: a notice the server slept through (down at the moment) is skipped, never said late.</summary>
        public static Notice? DueBefore(Scheduled s, DateTime utcNow, ISet<string> said)
        {
            if (Due(s.StartUtc.AddHours(-24), 20, utcNow) && said.Add("day")) return Notice.DayBefore;
            if (Due(s.StartUtc.AddHours(-1), 15, utcNow) && said.Add("hour")) return Notice.HourBefore;
            if (Due(s.StartUtc.AddMinutes(-10), 5, utcNow) && said.Add("ten")) return Notice.TenMinutes;
            return null;
        }

        static bool Due(DateTime at, int windowMinutes, DateTime now) => now >= at && now < at.AddMinutes(windowMinutes);

        /// <summary>The live standings line goes out every 10 minutes of the hour (10, 20, ... 50), once each.</summary>
        public static bool StandingsDue(Live live, double now)
        {
            double mins = (now - live.StartedAt) / 60.0;
            double total = (live.EndsAt - live.StartedAt) / 60.0;
            for (int m = 10; m < total; m += 10)
                if (mins >= m && mins < m + 2 && live.Said.Add("standings" + m)) return true;
            return false;
        }

        // ---------------------------------------------------------------- the words

        public static string Clock(double seconds)
        {
            int s = (int)Math.Round(seconds);
            int m = s / 60; s %= 60;
            if (m == 0) return s == 1 ? "1 second" : $"{s} seconds";
            if (s == 0) return m == 1 ? "1 minute" : $"{m} minutes";
            return $"{m}:{s:00}";
        }

        public static string DayBeforeLine(Spot spot) =>
            $"PK Night is tomorrow, {WHEN_WORDS}: hold {spot.Name} ({spot.Where}) for the hour. The allegiance that holds it longest takes the night." + GateTail(spot);

        public static string HourBeforeLine(Spot spot) =>
            $"PK Night begins in one hour at {spot.Name} ({spot.Where}). Gather your allegiance." + WayTail(spot);

        public static string TenMinutesLine(Spot spot) =>
            $"PK Night begins in ten minutes at {spot.Name} ({spot.Where}).";

        public static string StartLine(Spot spot, int minutes) =>
            $"PK Night has begun! Hold {spot.Name} ({spot.Where}). The allegiance that holds it longest in the next {(minutes == 60 ? "hour" : minutes + " minutes")} takes the night." + WayTail(spot);

        static string WayTail(Spot spot) => string.IsNullOrEmpty(spot.Way) ? "" : " " + spot.Way;
        /// <summary>The way, in the day-before and login lines too, only for a spot reached by the event portal.</summary>
        static string GateTail(Spot spot) => spot.HasGate ? WayTail(spot) : "";

        public static string StandingsLine(Live live, Spot spot, double now)
        {
            int left = Math.Max(0, (int)Math.Round((live.EndsAt - now) / 60.0));
            var ranked = Ranked(live);
            if (ranked.Count == 0) return $"PK Night, {left} minutes left: no one has held {spot.Name} yet.";
            var line = $"PK Night, {left} minutes left: {ranked[0].Name} leads with {Clock(ranked[0].Held)} at {spot.Name}.";
            if (ranked.Count > 1) line += $" {ranked[1].Name} has {Clock(ranked[1].Held)}.";
            return line;
        }

        public static string LeadLine(string side, Spot spot) => $"{side} takes the lead at {spot.Name}.";

        public static string EndLine(Live live, Spot spot, Scheduled next)
        {
            var w = Leader(live);
            string line = w == null
                ? $"PK Night is over: no one held {spot.Name}. The night goes unclaimed."
                : $"PK Night is over: {w.Name} held {spot.Name} for {Clock(w.Held)} and takes the night!";
            if (next != null) line += $" Next {WHEN_WORDS}: {next.Spot.Name} ({next.Spot.Where}).";
            return line;
        }

        /// <summary>The Town Crier's line for the week (the crier on branch claude/town-crier reads CrierLine()).</summary>
        public static string CrierLine(string winner, Spot spot, double held, Scheduled next) =>
            winner == null
                ? $"Hear ye! No allegiance held {spot.Name} on PK Night. Next {WHEN_WORDS}: {next?.Spot.Name ?? "a new spot"}."
                : $"Hear ye! {winner} held {spot.Name} for {Clock(held)} and took PK Night. Next {WHEN_WORDS}: {next?.Spot.Name ?? "a new spot"}.";

        public static string LoginLiveLine(Spot spot, double secondsLeft) =>
            $"PK Night is on now: hold {spot.Name} ({spot.Where}). {Clock(secondsLeft)} left.";

        public static string LoginSoonLine(Scheduled s) =>
            $"PK Night is {WhenWords(s.StartUtc)}: hold {s.Spot.Name} ({s.Spot.Where}) for the hour. The allegiance that holds it longest takes the night." + GateTail(s.Spot);

        public static string DiscordStartLine(Spot spot) =>
            $"**PK Night** has begun on Hollowtide: hold {spot.Name} ({spot.Where}) for the hour. The allegiance that holds it longest takes the night.";

        public static string DiscordEndLine(Live live, Spot spot)
        {
            var w = Leader(live);
            return w == null ? $"**PK Night** is over: no one held {spot.Name} tonight."
                             : $"**PK Night** is over: **{w.Name}** held {spot.Name} for {Clock(w.Held)} and took the night.";
        }

        /// <summary>Every line a player can read, for the style checks (no em dashes, nothing about staff tools).</summary>
        public static IEnumerable<string> AllLines()
        {
            var spot = SPOTS[0];
            var live = new Live { StartedAt = 0, EndsAt = 3600 };
            live.Sides[1] = new Standing { Side = 1, Name = "The Borg", Held = 1230, LastHeldAt = 100 };
            live.Sides[2] = new Standing { Side = 2, Name = "Hollow Crown", Held = 600, LastHeldAt = 200 };
            var next = new Scheduled { Week = 1, StartUtc = StartUtcOf(FIRST_SUNDAY.AddDays(7)), Spot = SPOTS[1] };
            next.EndUtc = next.StartUtc.AddMinutes(ROUND_MINUTES);
            yield return DayBeforeLine(spot);
            yield return HourBeforeLine(spot);
            yield return TenMinutesLine(spot);
            yield return StartLine(spot, 60);
            yield return StandingsLine(live, spot, 1800);
            yield return StandingsLine(new Live { StartedAt = 0, EndsAt = 3600 }, spot, 1800);
            yield return LeadLine("The Borg", spot);
            yield return EndLine(live, spot, next);
            yield return EndLine(new Live { StartedAt = 0, EndsAt = 3600 }, spot, next);
            yield return CrierLine("The Borg", spot, 1230, next);
            yield return CrierLine(null, spot, 0, next);
            yield return LoginLiveLine(spot, 1500);
            yield return LoginSoonLine(next);
            yield return DiscordStartLine(spot);
            yield return DiscordEndLine(live, spot);
            yield return GateOpenLine(spot);
            yield return LoginSoonLine(new Scheduled { Week = 0, StartUtc = StartUtcOf(FIRST_SUNDAY), Spot = spot });
        }
    }
}
