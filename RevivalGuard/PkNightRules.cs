using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// PK NIGHT, THE PURE HALF (no ACE types; tools/nettest compiles this file by path, PkNightCase). The schedule
    /// (every other Sunday 7 PM Pacific for ROUND_MINUTES, first run 2026-11-22), the spot rotation, the hold scoring, the tie
    /// rule, which announcement is due when, and every line a player reads. PkNight.cs runs it on the world loop.
    /// Design: docs/proposals/pk-night.md; the owner's decisions: docs/proposals/pk-night-decisions.md.
    /// </summary>
    public static partial class PkNight
    {
        // ---------------------------------------------------------------- the schedule

        /// <summary>The first run (owner 2026-11-09): Sunday 2026-11-22. Nothing is announced or run before it.</summary>
        public static readonly DateTime FIRST_SUNDAY = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Unspecified);
        public const int START_HOUR = 19;          // 7 PM Pacific
        /// <summary>The round's length (owner 2026-11-22 after PK Night #1, "too long, could be 30-45 mins"; v2 plan: 40).
        /// One number to change: the player lines say it through RoundWords().</summary>
        public const int ROUND_MINUTES = 40;
        public static string RoundWords(int minutes = ROUND_MINUTES) => minutes == 60 ? "the hour" : $"{minutes} minutes";
        /// <summary>The moving hill in a sentence (PkNightHillRules), for the notices, the login line and the board.</summary>
        public static string HillWords() => $"The hill moves every {HILL_MOVE_MINUTES} minutes and torches mark its edge.";
        /// <summary>Every other Sunday (owner 2026-11-23: "Let's make pk night every other week. That was allot of work."):
        /// rounds fall only on Sundays a multiple of INTERVAL_DAYS after FIRST_SUNDAY (Nov 22, Dec 6, Dec 20, ...).</summary>
        public const int INTERVAL_DAYS = 14;
        public const string SCHEDULE_WORDS = "every other Sunday at 7 PM Pacific";

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
            public int Round;               // 0 = the first run (Nov 22), 1 = Dec 6, ...: one per held night, not per week
            public DateTime StartUtc, EndUtc;
            public Spot Spot;
            public bool Running(DateTime utc) => utc >= StartUtc && utc < EndUtc;
            /// <summary>"week0" for the first run (its saved record), "round1" on: the day before's notices and Played() key on it.</summary>
            public string Key => Round == 0 ? "week0" : $"round{Round}";
        }

        /// <summary>A PK Night Sunday: on or after the first run, a whole number of INTERVAL_DAYS after it (a Pacific date).</summary>
        public static bool IsNightSunday(DateTime localDate)
        {
            int days = (int)Math.Round((localDate.Date - FIRST_SUNDAY).TotalDays);
            return localDate.DayOfWeek == DayOfWeek.Sunday && days >= 0 && days % INTERVAL_DAYS == 0;
        }

        /// <summary>The round running now, or the next one: the first PK Night Sunday on or after today whose round
        /// has not ended yet. The off Sundays between have nothing.</summary>
        public static Scheduled Next(DateTime utcNow)
        {
            var local = PacificLocal(utcNow).Date;
            int days = (int)Math.Round((local - FIRST_SUNDAY).TotalDays);
            int round = days <= 0 ? 0 : (days + INTERVAL_DAYS - 1) / INTERVAL_DAYS;
            for (int i = 0; i < 3; i++, round++)
            {
                var s = OfRound(round);
                if (utcNow < s.EndUtc) return s;
            }
            return null;
        }

        public static Scheduled After(Scheduled s) => OfRound(s.Round + 1);

        /// <summary>Round n: the Sunday n * INTERVAL_DAYS after the first run, at the nth spot of the rotation.</summary>
        public static Scheduled OfRound(int round)
        {
            var sunday = FIRST_SUNDAY.AddDays(INTERVAL_DAYS * round);
            var start = StartUtcOf(sunday);
            return new Scheduled { Round = round, StartUtc = start, EndUtc = start.AddMinutes(ROUND_MINUTES), Spot = SpotOfRound(round) };
        }

        public static Spot SpotOfRound(int round) => SPOTS[((round % SPOTS.Length) + SPOTS.Length) % SPOTS.Length];

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
            /// <summary>How to walk there, said in the prep (30 minutes before) and start lines (owner 2026-11-18, after the Borg walk test:
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

        /// <summary>The rotation, in order from the first run (round 0 = the first). Every one is outdoors and far
        /// from Holtburg (42.1N 33.6E), the arrival town (owner: never near the Holtburg arrival area). Owner 2026-11-20
        /// ("Yes, this Sunday"): the Dark Isle takes round 0 and the Mite Maze moves to the end. Owner 2026-11-23 (every
        /// other Sunday): the rotation moves one spot per held round, so Dec 6 is Zombie Castle and the Mite Maze round 7 (Feb 28).</summary>
        public static readonly Spot[] SPOTS =
        {
            // the owner's 2017 Darktide footage "PVP at 150 Island": the open sand of the south beach by the lifestone
            // (84.6N 58.6E), landblock C8E9 (atlas: walkable sand at sea level, shallows to the north). Retail reached it
            // only by the Dark Isle Flagging quest or the far east of Vissidal, so PK Night opens a portal from Holtburg.
            new Spot { Key = "darkisle", Name = "the Dark Isle", Where = "84.7N 58.4E", Ns = 84.72, Ew = 58.42,
                       Near = "the sand of the Dark Isle's south beach by its lifestone, north of Vissidal", Levels = "any level",
                       Way = $"The way in is a portal in Holtburg's lower town, open from {LeadWords()} before the start, and a portal home waits on the beach.",
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
        /// <summary>The prep time (owner 2026-11-22: "1 hr for people to prep is a long time. make it 30 mins"): the portals
        /// open this long before the start (and close at the end), and the prep notice goes out then. One number to change:
        /// the player lines say it through LeadWords().</summary>
        public const int GATE_LEAD_MINUTES = 30;
        /// <summary>A method, not a static field: SPOTS (above, initialised first) uses it.</summary>
        public static string LeadWords() => GATE_LEAD_MINUTES % 60 == 0
            ? (GATE_LEAD_MINUTES == 60 ? "one hour" : $"{GATE_LEAD_MINUTES / 60} hours") : $"{GATE_LEAD_MINUTES} minutes";

        /// <summary>The spot whose portals should stand now (and whose creatures are paused), or null. A running round:
        /// until its end (a staff round opens them at its start). No round running: the next scheduled one from
        /// GATE_LEAD_MINUTES before its start, unless that round was already played (ended early by staff).</summary>
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

        /// <summary>A global point inside the spot's paused zone: within ClearMetres of the landing or of a hill point (the centre first).</summary>
        public static bool InClearZone(Spot s, double gx, double gy, double metres)
        {
            if (s == null || metres <= 0) return false;
            var l = GlobalOfMap(s.LandNs, s.LandEw);
            if ((gx - l.x) * (gx - l.x) + (gy - l.y) * (gy - l.y) <= metres * metres) return true;
            foreach (var h in HillPoints(s))   // the centre is the first; v2: every point the hill moves to
            {
                var c = GlobalOfMap(h.Ns, h.Ew);
                if ((gx - c.x) * (gx - c.x) + (gy - c.y) * (gy - c.y) <= metres * metres) return true;
            }
            return false;
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
            public Dictionary<uint, int> Ticks { get; set; } = new Dictionary<uint, int>();   // scoring ticks each stood there (RuneSkinsRule.PkWinner)
            public uint Leader { get; set; }          // fellowship mode: its leader at the last scoring tick (a side of one: the player)
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
            /// <summary>"fellowship" or "allegiance", fixed when the round opens (pk_night_sides); null in a file saved before
            /// fellowship mode existed, whose round was scored by allegiance.</summary>
            public string SideRule { get; set; }
            /// <summary>Fellowship mode, the anti-hop rule (owner 2026-11-22): each player's first side this round, by guid,
            /// and the players who changed side since and count for nobody until the end. Saved with the round.</summary>
            public Dictionary<uint, uint> Locked { get; set; } = new Dictionary<uint, uint>();
            public HashSet<uint> Benched { get; set; } = new HashSet<uint>();
            [System.Text.Json.Serialization.JsonIgnore] public bool ByFellowship => SideRule == SIDES_FELLOWSHIP;
        }

        /// <summary>A player the tick looked at. Away: online but outside the circle (or unable to hold it), looked at only
        /// for the anti-hop rule. Regrouping: after a restart mid-round, locked to a fellowship the restart dissolved and
        /// alone now: counts for nobody but is not benched (they are forming it again).</summary>
        public struct Presence { public uint Side; public string Label; public uint Guid; public uint Leader; public bool Away; public bool Regrouping; }

        // ---------------------------------------------------------------- the sides (owner 2026-11-22, docs/proposals/pk-night-fellowship.md)

        /// <summary>Server property pk_night_sides: "fellowship" (the default) or "allegiance". Anything but "allegiance",
        /// an unset value included, is fellowship mode.</summary>
        public const string SIDES_FELLOWSHIP = "fellowship", SIDES_ALLEGIANCE = "allegiance";
        public static bool IsFellowshipMode(string value) =>
            !string.Equals((value ?? "").Trim(), SIDES_ALLEGIANCE, StringComparison.OrdinalIgnoreCase);
        public static string SidesOf(string value) => IsFellowshipMode(value) ? SIDES_FELLOWSHIP : SIDES_ALLEGIANCE;

        /// <summary>A fellowship's side key, for its lifetime (the Fellowship object: ACE keeps it through a leader change and
        /// drops it at a disband). Keys sit below player guids (0x50000000 up), so a side of one never meets a fellowship.</summary>
        public const uint FELLOW_KEY_BASE = 0x20000000, FELLOW_KEY_END = 0x50000000;
        public static bool IsFellowKey(uint key) => key >= FELLOW_KEY_BASE && key < FELLOW_KEY_END;

        /// <summary>The next fellowship key to hand out: past every key already used (saved rounds and the live one), so a
        /// restart, which forgets which object had which key, never gives an old key to a new fellowship.</summary>
        public static uint NextFellowKey(IEnumerable<uint> used, uint counter)
        {
            uint k = Math.Max(counter, FELLOW_KEY_BASE);
            foreach (var u in used) if (IsFellowKey(u) && u >= k) k = u + 1;
            return k;
        }

        /// <summary>Every key a round holds (its sides and its locks).</summary>
        public static IEnumerable<uint> KeysOf(Live live) =>
            live == null ? Enumerable.Empty<uint>() : live.Sides.Keys.Concat(live.Locked.Values);

        /// <summary>After a restart mid-round: a fellowship formed again takes back the key its members were locked to
        /// (the most of them; a key issued before this process started, below firstKey, not yet taken back). 0: none.</summary>
        public static uint AdoptKey(Live live, IEnumerable<uint> members, ICollection<uint> adopted, uint firstKey)
        {
            if (live == null || !live.ByFellowship) return 0;
            return members.Select(m => live.Locked.TryGetValue(m, out var k) ? k : 0)
                .Where(k => IsFellowKey(k) && k < firstKey && !adopted.Contains(k))
                .GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
        }

        /// <summary>A player locked to a fellowship a restart dissolved (its key is from before firstKey).</summary>
        public static bool LockedToGone(Live live, uint guid, uint firstKey) =>
            live != null && live.ByFellowship && live.Locked.TryGetValue(guid, out var k) && IsFellowKey(k) && k < firstKey;

        /// <summary>The anti-hop rule (fellowship mode only): a player's side is locked to the first side they were counted
        /// for this round; once they are seen on another (another fellowship, or alone after leaving one) they count for
        /// nobody until the round ends. Returns who counts in the circle; `benched` gets the players benched just now.</summary>
        public static List<Presence> Admit(Live live, IEnumerable<Presence> seen, List<uint> benched = null)
        {
            var counted = new List<Presence>();
            foreach (var p in seen)
            {
                if (!live.ByFellowship) { if (!p.Away) counted.Add(p); continue; }
                if (live.Benched.Contains(p.Guid) || p.Regrouping) continue;
                if (live.Locked.TryGetValue(p.Guid, out var first))
                {
                    if (first != p.Side) { live.Benched.Add(p.Guid); benched?.Add(p.Guid); continue; }
                    if (!p.Away) counted.Add(p);
                    continue;
                }
                if (p.Away) continue;
                live.Locked[p.Guid] = p.Side;
                counted.Add(p);
            }
            return counted;
        }

        /// <summary>Who gets the Banner of the Held Ground for this winner, or 0. Allegiance mode: the side (its monarch, or
        /// the lone winner), once per allegiance ever (`sides`). Fellowship mode: the fellowship's leader at the end
        /// (leaderNow; else the last one seen holding), once per player ever: not one already given a banner either way.</summary>
        public static uint BannerTo(bool byFellowship, Standing w, uint leaderNow, ICollection<uint> sides, ICollection<uint> players)
        {
            if (w == null) return 0;
            if (!byFellowship) return w.Side != 0 && !sides.Contains(w.Side) ? w.Side : 0;
            uint who = leaderNow != 0 ? leaderNow : w.Leader != 0 ? w.Leader : (IsFellowKey(w.Side) ? 0 : w.Side);
            return who != 0 && !players.Contains(who) && !sides.Contains(who) ? who : 0;
        }

        static string Word(bool fs) => fs ? "fellowship" : "allegiance";

        /// <summary>One scoring tick. `present` is every eligible player killer within the radius. "Outnumber to score"
        /// (owner 2026-11-25): the side with more of its members there than every other side present holds and earns
        /// the tick, so a big allegiance's extra fellowships no longer block a bigger side; a tie for most is a contest
        /// and nobody earns it; nobody there, nobody earns it. Returns the side that took the lead on this tick, or 0.</summary>
        public static uint Tick(Live live, IEnumerable<Presence> present, double now, double dt, List<uint> benched = null)
        {
            var here = Admit(live, present, benched);
            var groups = here.GroupBy(p => p.Side).OrderByDescending(g => g.Count()).ToList();
            bool outnumbers = groups.Count == 1 || (groups.Count > 1 && groups[0].Count() > groups[1].Count());
            var holders = outnumbers ? groups[0].ToList() : null;
            live.PresentNow = here.Count;
            live.Contested = groups.Count > 1 && !outnumbers;
            live.Holding = holders != null ? holders[0].Side : 0;
            live.LastTickAt = now;
            uint before = Leader(live)?.Side ?? 0;
            if (holders != null)
            {
                var side = holders[0].Side;
                if (!live.Sides.TryGetValue(side, out var st))
                    live.Sides[side] = st = new Standing { Side = side, Name = holders[0].Label };
                st.Name = holders[0].Label;
                if (holders[0].Leader != 0) st.Leader = holders[0].Leader;
                st.Held += dt;
                st.LastHeldAt = now;
                foreach (var p in holders) { st.Present.Add(p.Guid); st.Ticks[p.Guid] = st.Ticks.GetValueOrDefault(p.Guid) + 1; }
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

        public enum Notice { DayBefore, PrepBefore, TenMinutes, Start, Standings, End }

        /// <summary>Which pre-round notice is due now for a scheduled round, given what was already said for it.
        /// Each has a window: a notice the server slept through (down at the moment) is skipped, never said late.</summary>
        public static Notice? DueBefore(Scheduled s, DateTime utcNow, ISet<string> said)
        {
            if (Due(s.StartUtc.AddHours(-24), 20, utcNow) && said.Add("day")) return Notice.DayBefore;
            // (key "prep", was "hour" before 2026-11-22: a pknight.json saved mid-week with "hour" only means this notice
            // may still go once at its new time, never twice)
            if (Due(s.StartUtc.AddMinutes(-GATE_LEAD_MINUTES), 15, utcNow) && said.Add("prep")) return Notice.PrepBefore;
            if (Due(s.StartUtc.AddMinutes(-10), 5, utcNow) && said.Add("ten")) return Notice.TenMinutes;
            return null;
        }

        static bool Due(DateTime at, int windowMinutes, DateTime now) => now >= at && now < at.AddMinutes(windowMinutes);

        /// <summary>The live standings line goes out every 10 minutes of the round (10, 20, 30 in 40 minutes), once each.</summary>
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

        public static string DayBeforeLine(Spot spot, bool fs = true) =>
            $"PK Night is tomorrow, Sunday at 7 PM Pacific: hold the hill at {spot.Name} ({spot.Where}) for {RoundWords()}. The {Word(fs)} that holds it longest takes the night. {HillWords()}" + GateTail(spot);

        public static string PrepBeforeLine(Spot spot, bool fs = true) =>
            $"PK Night begins in {LeadWords()} at {spot.Name} ({spot.Where}). Gather your {Word(fs)}. {HillWords()}" + WayTail(spot);

        public static string TenMinutesLine(Spot spot) =>
            $"PK Night begins in ten minutes at {spot.Name} ({spot.Where}).";

        public static string StartLine(Spot spot, int minutes, bool fs = true) =>
            $"PK Night has begun! Hold the hill at {spot.Name} ({spot.Where}). The {Word(fs)} that holds it longest in the next {(minutes == 60 ? "hour" : minutes + " minutes")} takes the night. {HillWords()}"
            + (fs ? " " + HopRule : "") + WayTail(spot);

        /// <summary>Fellowship mode's rule about changing sides, said at the start and in the text board (@pknight).</summary>
        public const string HopRule = "Not in a fellowship, you hold alone; change fellowships and you count for no one until the night is over.";

        /// <summary>To a player benched by the anti-hop rule, once.</summary>
        public static string BenchedLine(Spot spot) =>
            $"You changed fellowships during PK Night, so you count for no one at {spot.Name} until the night is over.";

        /// <summary>The @pknight text board's lines that name the side (the rest are in PkNight.cs).</summary>
        public static string NoneHeldYetLine(bool fs = true) => $"No {Word(fs)} has held it yet.";
        public static string HowToHoldLine(double radius, bool fs = true) =>
            $"Stand within {radius:0} metres of the hill with more of your {Word(fs)} there than any other {Word(fs)} to hold it; a tie scores for no one. {HillWords()} Dying there counts as it always does."
            + (fs ? " " + HopRule : "");

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
            if (next != null) line += $" Next is {WhenWords(next.StartUtc)}: {next.Spot.Name} ({next.Spot.Where}).";
            return line;
        }

        /// <summary>The Town Crier's line for the week (the crier on branch claude/town-crier reads CrierLine()).</summary>
        public static string CrierLine(string winner, Spot spot, double held, Scheduled next, bool fs = true) =>
            winner == null
                ? $"Hear ye! No {Word(fs)} held {spot.Name} on PK Night. {NextWords(next)}"
                : $"Hear ye! {winner} held {spot.Name} for {Clock(held)} and took PK Night. {NextWords(next)}";

        static string NextWords(Scheduled next) => next == null
            ? $"PK Night is {SCHEDULE_WORDS}, at a new spot."
            : $"Next is {WhenWords(next.StartUtc)}: {next.Spot.Name}.";

        /// <summary>The @pknight text board's line about the next round (between rounds, the off Sundays too).</summary>
        public static string NextRoundLine(Scheduled next, bool fs = true) =>
            $"PK Night is {SCHEDULE_WORDS}. The next is {WhenWords(next.StartUtc)}: hold the hill at {next.Spot.Name} ({next.Spot.Where}) for {RoundWords()}. The {Word(fs)} that holds it longest takes the night. {HillWords()}";

        public static string LoginLiveLine(Spot spot, double secondsLeft) =>
            $"PK Night is on now: hold the hill at {spot.Name} ({spot.Where}). {HillWords()} {Clock(secondsLeft)} left.";

        public static string LoginSoonLine(Scheduled s, bool fs = true) =>
            $"PK Night is {WhenWords(s.StartUtc)}: hold the hill at {s.Spot.Name} ({s.Spot.Where}) for {RoundWords()}. The {Word(fs)} that holds it longest takes the night. {HillWords()}" + GateTail(s.Spot);

        public static string DiscordStartLine(Spot spot, bool fs = true) =>
            $"**PK Night** has begun on Hollowtide: hold the hill at {spot.Name} ({spot.Where}) for {RoundWords()}. The {Word(fs)} that holds it longest takes the night. The hill moves every {HILL_MOVE_MINUTES} minutes.";

        public static string DiscordEndLine(Live live, Spot spot)
        {
            var w = Leader(live);
            return w == null ? $"**PK Night** is over: no one held {spot.Name} tonight."
                             : $"**PK Night** is over: **{w.Name}** held {spot.Name} for {Clock(w.Held)} and took the night.";
        }

        /// <summary>Every line a player can read, for the style checks (no em dashes, nothing about staff tools).</summary>
        public static IEnumerable<string> AllLines(bool fs = true)
        {
            var spot = SPOTS[0];
            var live = new Live { StartedAt = 0, EndsAt = ROUND_MINUTES * 60 };
            live.Sides[1] = new Standing { Side = 1, Name = "The Borg", Held = 1230, LastHeldAt = 100 };
            live.Sides[2] = new Standing { Side = 2, Name = "Hollow Crown", Held = 600, LastHeldAt = 200 };
            var next = OfRound(1);
            yield return DayBeforeLine(spot, fs);
            yield return PrepBeforeLine(spot, fs);
            yield return TenMinutesLine(spot);
            yield return StartLine(spot, ROUND_MINUTES, fs);
            yield return StandingsLine(live, spot, 1200);
            yield return StandingsLine(new Live { StartedAt = 0, EndsAt = ROUND_MINUTES * 60 }, spot, 1200);
            yield return LeadLine("The Borg", spot);
            yield return EndLine(live, spot, next);
            yield return EndLine(new Live { StartedAt = 0, EndsAt = ROUND_MINUTES * 60 }, spot, next);
            yield return CrierLine("The Borg", spot, 1230, next, fs);
            yield return CrierLine(null, spot, 0, next, fs);
            yield return LoginLiveLine(spot, 1500);
            yield return LoginSoonLine(next, fs);
            yield return DiscordStartLine(spot, fs);
            yield return DiscordEndLine(live, spot);
            yield return GateOpenLine(spot);
            yield return LoginSoonLine(OfRound(0), fs);
            yield return NextRoundLine(next, fs);
            yield return CrierLine(null, spot, 0, null, fs);
            yield return NoneHeldYetLine(fs);
            yield return HowToHoldLine(40, fs);
            if (fs) yield return BenchedLine(spot);
            foreach (var l in HillLines()) yield return l;
        }
    }
}
