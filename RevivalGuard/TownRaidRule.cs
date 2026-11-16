using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RevivalGuard
{
    /// <summary>
    /// HOLTBURG RAIDS, THE PURE HALF (no ACE types; tools/nettest compiles it by path, TownRaidCase). Ours, not retail
    /// (owner 2026-11-15; docs/DIVERGENCES.md). The data (TownRaid.json), the schedule roll, the tier and wave size, the
    /// kind and side, the end of a raid, and the reward rolls. TownRaid.cs runs it on the server.
    /// </summary>
    public static class TownRaidRule
    {
        // ---- the data file --------------------------------------------------------------------------------------------

        public sealed class Config
        {
            public string Town { get; set; } = "Holtburg";
            public ScheduleDef Schedule { get; set; } = new ScheduleDef();
            public int[] TierFloors { get; set; } = { 1, 20, 60, 110 };
            public float[] TownBox { get; set; } = { -12, -12, 204, 180 };
            public float[] Lifestone { get; set; } = { 81.3f, 11.8f };
            public float NoSpawnNearLifestone { get; set; } = 60;
            public float HomeRadius { get; set; } = 60;
            public List<Side> Sides { get; set; } = new List<Side>();
            public List<Kind> Kinds { get; set; } = new List<Kind>();
            public BigDef Big { get; set; } = new BigDef();
            public RewardDef Rewards { get; set; } = new RewardDef();
            public Dictionary<string, string[]> Lines { get; set; } = new Dictionary<string, string[]>();

            public Side SideBy(string key) => Sides.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            public Kind KindBy(string key) => Kinds.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        public sealed class ScheduleDef
        {
            public double MinGapMinutes { get; set; } = 75;
            public double MeanExtraMinutes { get; set; } = 50;
            public int[] DeadHoursPacific { get; set; } = { 1, 2, 3, 4, 5, 6, 7 };
            public double DeadHourWeight { get; set; } = 0.35;
            public double OnlineFactorPer { get; set; } = 0.08;
            public double OnlineFactorMin { get; set; } = 0.6;
            public double OnlineFactorMax { get; set; } = 1.4;
            public int MinPlayersInTown { get; set; } = 1;
            public double WarningSeconds { get; set; } = 60;
            public double IdleTimeoutMinutes { get; set; } = 10;
            public double MaxMinutes { get; set; } = 25;
            public double EmptyTownMinutes { get; set; } = 3;
            public double HeldShare { get; set; } = 0.75;
            public double StuckOutsideSeconds { get; set; } = 60;
            public string RestartUtc { get; set; } = "09:30";
            public double QuietBeforeRestartMinutes { get; set; } = 45;
            public double QuietAfterRestartMinutes { get; set; } = 20;
            public string[] PauseForEvents { get; set; } = { "holtburg" };
        }

        public sealed class Side
        {
            public string Key { get; set; }
            public string Name { get; set; }
            public float[] From { get; set; }
            public float[] To { get; set; }
            public float[] GuardsFrom { get; set; }
            public List<float[]> GuardsAt { get; set; } = new List<float[]>();
            public string Muster { get; set; }                 // a Royal Guard's route name brought up on a big wave
            public List<float[]> MusterPath { get; set; } = new List<float[]>();
        }

        public sealed class Kind
        {
            public string Key { get; set; }
            public string Group { get; set; }                  // "drudges"
            public double Weight { get; set; } = 1;
            public string[] Sides { get; set; } = Array.Empty<string>();
            public double BigChance { get; set; }              // only kinds with a leader at the tier can be big
            public List<TierDef> Tiers { get; set; } = new List<TierDef>();
            public TierDef TierAt(int tier) => Tiers.FirstOrDefault(t => t.Tier == tier);
        }

        public sealed class TierDef
        {
            public int Tier { get; set; }
            public uint[] Wave { get; set; } = Array.Empty<uint>();
            public uint Leader { get; set; }
            public uint[] Escort { get; set; } = Array.Empty<uint>();
            public double Base { get; set; } = 3;
            public double PerPlayer { get; set; } = 2;
            public int Max { get; set; } = 12;
            public uint Treasure { get; set; }                 // a retail death treasure (TreasureDeath) id: the reward roll's table
            public int ProtectBelow { get; set; }              // raiders never pick a player under this level who has not hit them
        }

        public sealed class BigDef
        {
            public double WaveFactor { get; set; } = 1.5;
            public int Escorts { get; set; } = 3;
            public double LeaderAfterShare { get; set; } = 0.6;
            public double LeaderAfterSeconds { get; set; } = 180;
            public int Defenders { get; set; } = 3;
            public uint DefenderWcid { get; set; } = 37518;   // Royal Guard
        }

        public sealed class RewardDef
        {
            public int[] Rolls { get; set; } = { 1, 2, 3 };
            public double ShareFor2 { get; set; } = 0.25;
            public double ShareFor3 { get; set; } = 0.5;
            public double MinDamage { get; set; } = 10;
            public string Counter { get; set; } = "rg_townraid_defended";
            public string Title { get; set; } = "";
            public int TitleAfter { get; set; } = 25;
            public uint[] StripItems { get; set; } = Array.Empty<uint>();
        }

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };

        public static Config Parse(string text) => JsonSerializer.Deserialize<Config>(text, Json) ?? throw new FormatException("empty");

        /// <summary>What is wrong with the data (empty = fine): unknown sides, missing points, empty waves, no tier 1.</summary>
        public static List<string> Problems(Config c)
        {
            var p = new List<string>();
            if (c.TierFloors == null || c.TierFloors.Length == 0) p.Add("no tier floors");
            foreach (var s in c.Sides)
            {
                if (s.From == null || s.From.Length < 2 || s.To == null || s.To.Length < 2) p.Add($"side {s.Key}: from/to");
                if (s.From != null && s.From.Length >= 2 && InBox(c, s.From[0], s.From[1], 0)) p.Add($"side {s.Key}: spawns inside the town");
                if (s.From != null && s.From.Length >= 2 && Dist(s.From, c.Lifestone) < c.NoSpawnNearLifestone) p.Add($"side {s.Key}: spawns near the lifestone");
                if (s.To != null && s.To.Length >= 2 && !InBox(c, s.To[0], s.To[1], 0)) p.Add($"side {s.Key}: goes nowhere in town");
            }
            foreach (var k in c.Kinds)
            {
                if (k.Sides.Length == 0) p.Add($"kind {k.Key}: no side");
                foreach (var sk in k.Sides) if (c.SideBy(sk) == null) p.Add($"kind {k.Key}: unknown side {sk}");
                foreach (var t in k.Tiers) if (t.Wave.Length == 0) p.Add($"kind {k.Key} tier {t.Tier}: empty wave");
            }
            if (!c.Kinds.Any(k => k.TierAt(1) != null)) p.Add("no kind at tier 1");
            return p;
        }

        public static bool InBox(Config c, float x, float y, float margin) =>
            x >= c.TownBox[0] - margin && x <= c.TownBox[2] + margin && y >= c.TownBox[1] - margin && y <= c.TownBox[3] + margin;

        public static float Dist(float[] a, float[] b) => (float)Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

        // ---- who is in town, and the tier -----------------------------------------------------------------------------

        /// <summary>The tier the town's players call for: the lower median of their levels against the floors (1..n).
        /// A median, not the top: one high character among newcomers does not bring the hardest raiders.</summary>
        public static int TierFor(IList<int> levels, int[] floors)
        {
            if (levels == null || levels.Count == 0 || floors == null || floors.Length == 0) return 1;
            var sorted = levels.OrderBy(l => l).ToList();
            int median = sorted[(sorted.Count - 1) / 2];
            int tier = 1;
            for (int i = 0; i < floors.Length; i++) if (median >= floors[i]) tier = i + 1;
            return tier;
        }

        /// <summary>How many raiders: base plus so many per player in town, up to the tier's max (a big wave half again).</summary>
        public static int WaveSize(TierDef t, int players, bool big, double bigFactor)
        {
            double n = t.Base + t.PerPlayer * Math.Max(1, players);
            int size = (int)Math.Round(Math.Min(n, t.Max));
            if (big) size = (int)Math.Round(size * bigFactor);
            return Math.Max(1, size);
        }

        /// <summary>The kind of raid: weighted among the kinds that have this tier (or the nearest lower one they have);
        /// drudges when nothing else fits.</summary>
        public static (Kind kind, TierDef tier) PickKind(Config c, int tier, double r, string forceKind = null)
        {
            var options = new List<(Kind k, TierDef t)>();
            foreach (var k in c.Kinds)
            {
                if (forceKind != null && !string.Equals(k.Key, forceKind, StringComparison.OrdinalIgnoreCase)) continue;
                var t = k.TierAt(tier);
                if (t != null) options.Add((k, t));
            }
            if (options.Count == 0 && forceKind != null)
            {
                var k = c.KindBy(forceKind);
                var t = k?.Tiers.Where(x => x.Tier <= tier).OrderByDescending(x => x.Tier).FirstOrDefault() ?? k?.Tiers.OrderBy(x => x.Tier).FirstOrDefault();
                if (t != null) return (k, t);
            }
            if (options.Count == 0)
            {
                foreach (var k in c.Kinds)
                {
                    var t = k.Tiers.Where(x => x.Tier <= tier).OrderByDescending(x => x.Tier).FirstOrDefault();
                    if (t != null) { options.Add((k, t)); break; }
                }
                if (options.Count == 0) return (null, null);
            }
            double total = options.Sum(o => Math.Max(0, o.k.Weight));
            if (total <= 0) return options[0];
            double x = r * total;
            foreach (var o in options) { x -= Math.Max(0, o.k.Weight); if (x < 0) return o; }
            return options[options.Count - 1];
        }

        public static Side PickSide(Config c, Kind k, double r, string forceSide = null)
        {
            if (forceSide != null) return c.SideBy(forceSide);
            var sides = k.Sides.Select(c.SideBy).Where(s => s != null).ToList();
            if (sides.Count == 0) return c.Sides.FirstOrDefault();
            return sides[Math.Min(sides.Count - 1, (int)(r * sides.Count))];
        }

        public static bool CanBeBig(TierDef t) => t != null && t.Leader != 0;

        // ---- the schedule ---------------------------------------------------------------------------------------------

        /// <summary>The chance a raid starts in this minute: none within the minimum gap of the last one; after it, a
        /// steady chance (mean MeanExtraMinutes at full weight), lower in the dead hours (Pacific), higher with more
        /// players online. With the defaults, about 11 a day when people are on.</summary>
        public static double ChancePerMinute(ScheduleDef s, double minutesSinceLast, int pacificHour, int online)
        {
            if (minutesSinceLast < s.MinGapMinutes) return 0;
            double p = 1.0 / Math.Max(1, s.MeanExtraMinutes);
            if (s.DeadHoursPacific != null && Array.IndexOf(s.DeadHoursPacific, pacificHour) >= 0) p *= s.DeadHourWeight;
            p *= Math.Clamp(s.OnlineFactorMin + s.OnlineFactorPer * online, s.OnlineFactorMin, s.OnlineFactorMax);
            return Math.Clamp(p, 0, 1);
        }

        /// <summary>No raid may begin when it could still be running at the nightly restart: from QuietBeforeRestart
        /// minutes before it to QuietAfterRestart after.</summary>
        public static bool QuietForRestart(DateTime utc, ScheduleDef s)
        {
            if (string.IsNullOrWhiteSpace(s.RestartUtc) || !TimeSpan.TryParse(s.RestartUtc, out var at)) return false;
            var restart = utc.Date + at;
            foreach (var r in new[] { restart.AddDays(-1), restart, restart.AddDays(1) })
            {
                double m = (utc - r).TotalMinutes;
                if (m >= -s.QuietBeforeRestartMinutes && m <= s.QuietAfterRestartMinutes) return true;
            }
            return false;
        }

        public static bool PausedForEvent(string currentEventKey, ScheduleDef s) =>
            !string.IsNullOrEmpty(currentEventKey) && s.PauseForEvents != null
            && s.PauseForEvents.Any(w => !string.IsNullOrEmpty(w) && currentEventKey.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        // ---- a running raid -------------------------------------------------------------------------------------------

        public enum Outcome { Running, Held, Withdrew, Faded }

        /// <summary>How a raid stands: held only when every raider spawned died (a later wave still to come keeps it
        /// running); faded when raiders are gone without dying (a stop, a block taken down); withdrawn when nobody has
        /// hit a raider for the idle timeout, the town has stood empty for EmptyTownMinutes, or the raid passed its
        /// longest time. (Box 2026-11-16: the town emptied, the raiders dropped off the live list and a raid read as held.)</summary>
        public static Outcome Judge(int spawned, int alive, int killed, bool moreToCome, double sinceSpawn, double sinceLastHit, ScheduleDef s, double emptyFor = 0)
        {
            if (spawned == 0) return Outcome.Running;
            if (alive == 0 && !moreToCome) return killed >= HeldAt(spawned, s) ? Outcome.Held : Outcome.Faded;
            if (alive == 0) return Outcome.Running;
            if (sinceSpawn >= s.MaxMinutes * 60) return Outcome.Withdrew;
            if (sinceLastHit >= s.IdleTimeoutMinutes * 60) return Outcome.Withdrew;
            if (emptyFor >= s.EmptyTownMinutes * 60) return Outcome.Withdrew;
            return Outcome.Running;
        }

        /// <summary>Kills that hold the town once no raider is left: HeldShare of those spawned (a raider stuck outside
        /// town is taken away, box 2026-11-16: 11 of 12 killed read as faded).</summary>
        public static int HeldAt(int spawned, ScheduleDef s) => Math.Max(1, (int)Math.Ceiling(spawned * s.HeldShare));

        /// <summary>The big wave's leader comes once the first wave is mostly down, or after a while regardless.</summary>
        public static bool LeaderDue(int firstWave, int firstWaveAlive, double sinceSpawn, BigDef b) =>
            firstWave > 0 && ((firstWave - firstWaveAlive) >= Math.Ceiling(firstWave * b.LeaderAfterShare) || sinceSpawn >= b.LeaderAfterSeconds);

        /// <summary>Raiders ignore a player below the tier's protection level unless that player has hurt them.</summary>
        public static bool MayTarget(int playerLevel, bool hurtMe, int protectBelow) => hurtMe || playerLevel >= protectBelow;

        // ---- rewards --------------------------------------------------------------------------------------------------

        /// <summary>Each defender's reward rolls on a held town, by their share of the damage dealt to the raiders: one
        /// for taking part (at least MinDamage), two from ShareFor2 of it, three from ShareFor3.</summary>
        public static Dictionary<uint, int> Rolls(IDictionary<uint, double> damage, RewardDef r)
        {
            var res = new Dictionary<uint, int>();
            if (damage == null || damage.Count == 0) return res;
            double total = damage.Values.Where(v => v > 0).Sum();
            if (total <= 0) return res;
            foreach (var kv in damage)
            {
                if (kv.Value < r.MinDamage) continue;
                double share = kv.Value / total;
                int idx = share >= r.ShareFor3 ? 2 : share >= r.ShareFor2 ? 1 : 0;
                idx = Math.Min(idx, r.Rolls.Length - 1);
                res[kv.Key] = r.Rolls[idx];
            }
            return res;
        }

        public static bool TitleDue(int defendedBefore, int defendedNow, RewardDef r) =>
            !string.IsNullOrWhiteSpace(r.Title) && r.TitleAfter > 0 && defendedBefore < r.TitleAfter && defendedNow >= r.TitleAfter;

        // ---- lines ----------------------------------------------------------------------------------------------------

        public static string Line(Config c, string key, double r, IDictionary<string, string> tokens = null)
        {
            if (c?.Lines == null || !c.Lines.TryGetValue(key, out var pool) || pool == null || pool.Length == 0) return null;
            var s = pool[Math.Min(pool.Length - 1, (int)(r * pool.Length))];
            return Fill(s, tokens);
        }

        public static string Fill(string s, IDictionary<string, string> tokens)
        {
            if (s == null || tokens == null) return s;
            foreach (var kv in tokens)
            {
                s = s.Replace("{" + kv.Key + "}", kv.Value ?? "");
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    s = s.Replace("{" + char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1) + "}", char.ToUpperInvariant(kv.Value[0]) + kv.Value.Substring(1));
            }
            return s;
        }

        /// <summary>Every line a player can read (tests: plain ASCII, no em dash, no stray token).</summary>
        public static IEnumerable<string> AllLines(Config c) => c.Lines.Values.SelectMany(v => v);

    }
}
