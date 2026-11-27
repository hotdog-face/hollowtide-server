using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// PK NIGHT v2, THE MOVING HILL (owner 2026-11-22, docs/proposals/pk-night-v2.md), the pure half (tools/nettest compiles it
    /// by path, PkNightCase). Within the night's spot the hill moves every HILL_MOVE_MINUTES between the spot's points (the
    /// first is the spot's own centre), with a warning a minute before; its edge is marked by event objects (PkNightHill.cs)
    /// and every BOARD_LINE_SECONDS each player near it is told who holds it. Scoring is unchanged (PkNightRules.Tick).
    /// The hill's place is read from the clock (minutes since the start), so a restart mid-round puts it back where it was.
    /// </summary>
    public static partial class PkNight
    {
        public const int HILL_MOVE_MINUTES = 10;
        public const double HILL_WARN_SECONDS = 60;
        /// <summary>The edge markers stand about this far apart around the circle (pk_night_radius).</summary>
        public const double MARKER_SPACING = 8;
        /// <summary>The battlefield line: this often, to each player within the radius plus BOARD_LINE_NEAR metres.</summary>
        public const double BOARD_LINE_SECONDS = 15, BOARD_LINE_NEAR = 30;

        /// <summary>Each spot's hill points as map coordinates (north, east), the first being the spot's centre (SPOTS), the
        /// others 60-120 m on from the one before. Picked 2026-11-22 from the client's walk grid
        /// (Assets/StreamingAssets/walkgrid.bin, the atlas's 4 m ground squares): the centre and the 20 m around each point
        /// walkable (no water, building or steep ground) and most of the 40 m circle too. The Dark Isle's also stay 48 m or
        /// more from the event portal's landing and the way-home portal. A spot not listed has one point, its centre.</summary>
        public static readonly Dictionary<string, (double Ns, double Ew)[]> HILLS = new Dictionary<string, (double, double)[]>
        {
            ["darkisle"] = new[] { (84.72, 58.42), (84.47, 58.4367), (84.77, 58.07), (84.87, 57.8367) },
            ["zombiecastle"] = new[] { (-45.2, 39.1), (-45.2833, 38.6167), (-45.4833, 38.8167), (-45.35, 39.2833) },
            // 2026-11-27, same walk grid: the hilltop is a shelf of about 150 x 90 m (z 340-360), so its points loop round
            // it and their 40 m circles are 84-99% walkable (the west one 49 m from the castle portal's landing; every
            // point 87 m or more from the Tower Guardian)
            ["asheronscastle"] = new[] { (21.4667, 69.28), (21.3, 69.48), (21.4, 69.08), (21.5333, 69.48) },
            ["colosseum"] = new[] { (-11.24, 37.54), (-11.3233, 37.0567), (-11.0567, 37.0567), (-11.0567, 37.39) },
            ["empyreanruin"] = new[] { (81.4, -32.9), (81.3167, -33.3833), (81.5833, -33.3833), (81.3167, -33.1167) },
            ["beachfort"] = new[] { (76.0, -49.05), (75.5833, -49.2667), (75.5167, -49.0), (75.85, -48.8667) },
            ["gaerlan"] = new[] { (57.01, 44.79), (56.9267, 44.3067), (57.1933, 44.3067), (56.9267, 44.5733) },
            ["tumerokfort"] = new[] { (-50.7, -81.8), (-50.25, -81.75), (-50.3833, -81.4833), (-50.65, -81.55) },
            ["mitemaze"] = new[] { (41.08, 57.03), (40.9967, 56.5467), (40.73, 56.68), (40.8633, 56.9467) },
            // 2026-11-27: open grass east of the walls, all four 67 m or more from the Risen Shelf portal and 84 m or more
            // from the east wall (the south-east one 38 m from the hand-started Omens raid generator)
            ["arwiclawn"] = new[] { (33.4, 57.3), (33.7333, 57.3667), (33.4667, 57.7), (33.1667, 57.5333) },
            // the Darktide recall spots (owner 2026-11-27), same walk grid: every 20 m round a point walkable, the 40 m
            // circle 99-100%, every point 70 m or more from anything placed
            ["aphuslassel"] = new[] { (2.2747, 94.7403), (2.3413, 94.2737), (2.008, 94.0737), (2.2747, 94.0737) },
            ["sclavavania"] = new[] { (-22.321, 65.519), (-22.4543, 65.0523), (-22.2543, 64.8523), (-22.521, 64.8523) },
            ["sanctuary"] = new[] { (-82.0833, 92.9), (-82.0167, 92.5), (-82.4833, 92.5), (-82.2167, 92.6333) },
            ["infestedarea"] = new[] { (43.709, 54.495), (43.2423, 54.3617), (43.509, 54.3617), (43.309, 54.5617) },
        };

        public static (double Ns, double Ew)[] HillPoints(Spot s) =>
            s != null && HILLS.TryGetValue(s.Key, out var p) && p.Length > 0 ? p : new[] { (s?.Ns ?? 0, s?.Ew ?? 0) };

        static double MoveSeconds => HILL_MOVE_MINUTES * 60.0;

        /// <summary>Which point the hill stands on now: one step every HILL_MOVE_MINUTES from the start, round the list.</summary>
        public static int HillIndex(Live live, Spot s, double now)
        {
            int n = HillPoints(s).Length;
            if (live == null || n <= 1) return 0;
            int step = (int)Math.Floor(Math.Max(0, now - live.StartedAt) / MoveSeconds);
            return step % n;
        }

        public static (double Ns, double Ew) HillAt(Live live, Spot s, double now) => HillPoints(s)[HillIndex(live, s, now)];

        /// <summary>When the hill next moves (unix), or 0: no point to move to, or the round ends first.</summary>
        public static double NextMoveAt(Live live, Spot s, double now)
        {
            if (live == null || HillPoints(s).Length <= 1) return 0;
            int step = (int)Math.Floor(Math.Max(0, now - live.StartedAt) / MoveSeconds);
            double at = live.StartedAt + (step + 1) * MoveSeconds;
            return at < live.EndsAt ? at : 0;
        }

        public enum HillNote { Warn, Move }

        /// <summary>The move line or its one-minute warning, if one is due now (each once, in a short window: a line the server
        /// slept through is skipped, never said late). Step k (1, 2, ...) is the k-th move. Uses live.Said.</summary>
        public static (HillNote note, int step)? HillDue(Live live, Spot s, double now)
        {
            if (live == null || HillPoints(s).Length <= 1) return null;
            for (int k = 1; live.StartedAt + k * MoveSeconds < live.EndsAt; k++)
            {
                double at = live.StartedAt + k * MoveSeconds;
                if (now >= at - HILL_WARN_SECONDS && now < at - HILL_WARN_SECONDS + 30 && live.Said.Add("hillwarn" + k)) return (HillNote.Warn, k);
                if (now >= at && now < at + 60 && live.Said.Add("hillmove" + k)) return (HillNote.Move, k);
            }
            return null;
        }

        /// <summary>The point of step k (0 = the start).</summary>
        public static (double Ns, double Ew) HillOfStep(Spot s, int step)
        {
            var p = HillPoints(s);
            return p[((step % p.Length) + p.Length) % p.Length];
        }

        /// <summary>Metres and compass word from one map point to another (240 m a map unit).</summary>
        public static (double metres, string dir) HillStep((double Ns, double Ew) from, (double Ns, double Ew) to)
        {
            double dn = (to.Ns - from.Ns) * 240, de = (to.Ew - from.Ew) * 240;
            double m = Math.Sqrt(dn * dn + de * de);
            double deg = (Math.Atan2(de, dn) * 180 / Math.PI + 360) % 360;   // 0 north, 90 east
            string[] w = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return (m, w[(int)Math.Round(deg / 45) % 8]);
        }

        /// <summary>"84.5N 58.4E": a map point the way the spots' Where reads (one decimal).</summary>
        public static string MapWords((double Ns, double Ew) p) =>
            $"{Math.Abs(p.Ns).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}{(p.Ns >= 0 ? "N" : "S")} "
            + $"{Math.Abs(p.Ew).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}{(p.Ew >= 0 ? "E" : "W")}";

        static string Metres(double m) => $"{(int)(Math.Round(m / 10) * 10)} metres";

        public static string HillWarnLine(Spot spot, int step)
        {
            var (m, dir) = HillStep(HillOfStep(spot, step - 1), HillOfStep(spot, step));
            return $"PK Night: the hill at {spot.Name} moves in one minute, about {Metres(m)} {dir} to {MapWords(HillOfStep(spot, step))}.";
        }

        public static string HillMoveLine(Spot spot, int step)
        {
            var (m, dir) = HillStep(HillOfStep(spot, step - 1), HillOfStep(spot, step));
            return $"PK Night: the hill has moved {Metres(m)} {dir}, to {MapWords(HillOfStep(spot, step))}. The torches mark the new circle.";
        }

        /// <summary>"2:10", "0:45": times on the battlefield line, always minutes and seconds.</summary>
        public static string MinSec(double seconds)
        {
            int s = Math.Max(0, (int)Math.Round(seconds));
            return $"{s / 60}:{s % 60:00}";
        }

        /// <summary>The battlefield line (every BOARD_LINE_SECONDS to each player near the hill): who holds it, their time,
        /// the leader, the time left and the next move. mySide 0: the reader counts for no side (not a player killer, or
        /// benched).</summary>
        public static string HillLine(Live live, Spot spot, uint mySide, double now)
        {
            var parts = new List<string>();
            var leader = Leader(live);
            live.Sides.TryGetValue(live.Holding, out var holder);
            live.Sides.TryGetValue(mySide, out var mine);
            bool iHold = !live.Contested && holder != null && mySide != 0 && holder.Side == mySide;
            bool iLead = leader != null && mySide != 0 && leader.Side == mySide;
            if (live.Contested) parts.Add("The hill is contested: no one scores.");
            else if (holder == null) parts.Add("No one holds the hill.");
            else if (iHold) parts.Add(iLead ? $"You hold the hill and lead: {MinSec(holder.Held)}." : $"You hold the hill: {MinSec(holder.Held)}.");
            else parts.Add($"{holder.Name} holds the hill: {MinSec(holder.Held)}.");
            if (leader == null) parts.Add("No one has held it yet.");
            else if (iLead) { if (!iHold) parts.Add($"You lead with {MinSec(leader.Held)}."); }
            else parts.Add($"Leader: {leader.Name} {MinSec(leader.Held)}." + (!iHold && mine != null && mine.Held > 0 ? $" You have {MinSec(mine.Held)}." : ""));
            double left = Math.Max(0, live.EndsAt - now);
            string leftWords = left < 60 ? "Under a minute left" : $"{(int)Math.Ceiling(left / 60)} min left";
            double move = NextMoveAt(live, spot, now);
            parts.Add(move > 0 ? $"{leftWords}; the hill moves in {MinSec(move - now)}." : $"{leftWords}; the hill stays here to the end.");
            return string.Join(" ", parts);
        }

        /// <summary>The edge markers' map points: about every MARKER_SPACING metres round a circle of `radius` metres.</summary>
        public static List<(double Ns, double Ew)> RingPoints((double Ns, double Ew) c, double radius, double spacing = MARKER_SPACING)
        {
            int n = Math.Max(8, (int)Math.Round(2 * Math.PI * radius / spacing));
            var list = new List<(double, double)>(n);
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                list.Add((c.Ns + radius * Math.Sin(t) / 240, c.Ew + radius * Math.Cos(t) / 240));
            }
            return list;
        }

        /// <summary>Which set of markers should stand now: null when none (no round, or its time is up), else a key that
        /// changes when the hill moves (or the radius or the marker weenies change), so the old ring is removed.</summary>
        public static string MarkerKey(Live live, Spot s, double now, double radius, string weenies = "") =>
            live == null || s == null || now >= live.EndsAt ? null : $"{s.Key}/{HillIndex(live, s, now)}/{radius:0}/{weenies}";

        /// <summary>The markers of the hill now: the centre first, then the edge ring.</summary>
        public static List<(double Ns, double Ew, bool Centre)> MarkerPoints(Live live, Spot s, double now, double radius)
        {
            var at = HillAt(live, s, now);
            var list = new List<(double, double, bool)> { (at.Ns, at.Ew, true) };
            foreach (var r in RingPoints(at, radius)) list.Add((r.Ns, r.Ew, false));
            return list;
        }

        public enum MarkerDo { Keep, Make, Wait }

        /// <summary>One marker on a tick (the Gateway rule, PkNightGate.Gone): never made, make it (this loads its landblock
        /// once); destroyed, or out of the world past `remakeAfter` seconds (a landblock unload takes an unsaved object
        /// with it), make it again only where its landblock is loaded (a player came back), so an empty field is not
        /// kept awake; otherwise keep it.</summary>
        public static MarkerDo MarkerCheck(bool made, bool destroyed, bool inWorld, double age, bool landblockLoaded, double remakeAfter = 30)
        {
            if (!made) return MarkerDo.Make;
            bool gone = destroyed || (!inWorld && age > remakeAfter);
            if (!gone) return MarkerDo.Keep;
            return landblockLoaded ? MarkerDo.Make : MarkerDo.Wait;
        }

        /// <summary>The hill's lines, for the style checks (AllLines and the nettest).</summary>
        public static IEnumerable<string> HillLines()
        {
            var spot = SPOTS[0];
            var live = new Live { StartedAt = 0, EndsAt = ROUND_MINUTES * 60, Holding = 1 };
            live.Sides[1] = new Standing { Side = 1, Name = "Iron Wolves", Held = 340, LastHeldAt = 100 };
            live.Sides[2] = new Standing { Side = 2, Name = "Hollow Crown", Held = 130, LastHeldAt = 90 };
            yield return HillWarnLine(spot, 1);
            yield return HillMoveLine(spot, 1);
            yield return HillLine(live, spot, 1, 1020);
            yield return HillLine(live, spot, 2, 1020);
            yield return HillLine(live, spot, 0, 2350);
            live.Holding = 0; live.Contested = true;
            yield return HillLine(live, spot, 2, 1020);
            yield return HillLine(new Live { StartedAt = 0, EndsAt = ROUND_MINUTES * 60 }, spot, 5, 30);
        }
    }
}
