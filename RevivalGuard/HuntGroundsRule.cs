using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>Under-used dungeons.</summary>
    public static partial class HuntGroundsRule
    {
        public enum Kind { Busy, Quiet }

        public sealed class Ground
        {
            public readonly ushort Lb; public readonly string Name; public readonly int Lo, Mid, Hi; public readonly Kind Kind;
            public readonly double Hours; public readonly int Chars; public readonly string Entrance;
            public Ground(int lb, string name, int lo, int mid, int hi, Kind kind, double hours, int chars, string entrance)
            {
                Lb = (ushort)lb; Name = name; Lo = lo; Mid = mid; Hi = hi; Kind = kind; Hours = hours; Chars = chars; Entrance = entrance;
            }
        }

        /// <summary>The champion hunt's level bands, taken in turn (the champions' own floor is 20).</summary>
        public static readonly (int lo, int hi)[] BANDS = { (20, 34), (35, 49), (50, 74), (75, 99), (100, 149), (150, 199) };
        public const int CEILING = 199;

        // defaults of the server properties (HuntGrounds registers them)
        public const double BUSY_MULT = 0.25, QUIET_MULT = 2.0, HUNT_CHANCE = 0.05;

        static readonly Lazy<Dictionary<ushort, Ground>> s_ByLb = new Lazy<Dictionary<ushort, Ground>>(
            () => Grounds.GroupBy(g => g.Lb).ToDictionary(x => x.Key, x => x.First()));
        static Dictionary<ushort, Ground> ByLb => s_ByLb.Value;

        public static Ground At(ushort lb) => ByLb.TryGetValue(lb, out var g) ? g : null;

        /// <summary>Hoshino, Viridian Rise, or anything whose monsters sit past the ceiling.</summary>
        public static bool EndGame(Ground g) =>
            g == null || g.Mid > CEILING || g.Hi > 250
            || (g.Name ?? "").IndexOf("Hoshino", StringComparison.OrdinalIgnoreCase) >= 0
            || (g.Name ?? "").IndexOf("Viridian", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The quiet dungeons whose middle monster level is in [lo, hi], least hunted first.</summary>
        public static List<Ground> QuietIn(int lo, int hi) =>
            Grounds.Where(g => g.Kind == Kind.Quiet && !EndGame(g) && g.Mid >= lo && g.Mid <= hi)
                .OrderBy(g => g.Hours).ThenBy(g => g.Chars).ThenBy(g => g.Lb).ToList();

        /// <summary>The hunt for turn turn: the band is turn mod the bands, the dungeon is the band's-th, round and round.</summary>
        public static (Ground g, int lo, int hi)? Plan(int turn)
        {
            if (turn < 0) turn = 0;
            for (int i = 0; i < BANDS.Length; i++)
            {
                int bi = (turn + i) % BANDS.Length;
                var list = QuietIn(BANDS[bi].lo, BANDS[bi].hi);
                if (list.Count == 0) continue;
                return (list[(turn / BANDS.Length) % list.Count], BANDS[bi].lo, BANDS[bi].hi);
            }
            return null;
        }

        /// <summary>A generator spawn's champion chance on this landblock</summary>
        public static double ChampionChance(double baseChance, ushort lb, ushort huntLb, double busyMult, double quietMult, double huntChance)
        {
            if (double.IsNaN(baseChance) || double.IsInfinity(baseChance) || baseChance <= 0) baseChance = 0;
            double c = baseChance;
            var g = At(lb);
            double multiplier = g != null && g.Kind == Kind.Busy ? busyMult : quietMult;
            if (double.IsNaN(multiplier) || multiplier < 0) multiplier = 0;
            if (g != null && !EndGame(g)) c = baseChance * multiplier;
            if (huntLb != 0 && lb == huntLb && !EndGame(g))
            {
                if (double.IsNaN(huntChance) || huntChance < 0) huntChance = 0;
                c = Math.Max(c, huntChance);
            }
            return Math.Min(1.0, Math.Max(0, c));
        }

        // ------------------------------------------------------------------ the hunt's words (no em dashes)

        public static string Title(Ground g) => $"Champions Gather in {g.Name}";
        public static string Levels(int lo, int hi) => $"{lo} to {hi}";
        public static string Announce(Ground g, int lo, int hi) =>
            $"Attention Citizens of Dereth! Champions have gathered in {g.Name}, a place few have hunted of late. Its entrance is near {g.Entrance ?? "its portal"}. Hunters of levels {lo} to {hi}, the hour is yours!";
        public static string Ended(Ground g, int slain) =>
            slain <= 0 ? $"The champions of {g.Name} have gone back into the dark."
            : $"The hunt in {g.Name} is over: {slain} {(slain == 1 ? "champion was" : "champions were")} slain. Well hunted!";

        public static IEnumerable<string> AllLines(Ground g)
        {
            yield return Title(g); yield return Announce(g, 20, 34); yield return Ended(g, 0); yield return Ended(g, 1); yield return Ended(g, 3);
        }
    }
}
