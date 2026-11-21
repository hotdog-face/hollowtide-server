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
            public WaveDef Waves { get; set; } = new WaveDef();
            public RewardDef Rewards { get; set; } = new RewardDef();
            public List<Theme> Themes { get; set; } = new List<Theme>();
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
            // never mid-fight (owner 2026-11-21: big raids must not vanish while people fight them): past MaxMinutes, or
            // with the town empty, a raid withdraws only once no raider has been hit for this long
            public double FightGraceSeconds { get; set; } = 120;
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
            public uint[] Adds { get; set; } = Array.Empty<uint>();  // the weaker pack that runs in with each wave (same family)
        }

        /// <summary>A festival's monsters make the raid while that festival runs (owner 2026-11-20: "add spawns of turkeys
        /// and similar themed monsters to the raids for every event"; then "for now turkeys should be what raids are made
        /// of. and christams is snowmans"). Retail weenies only, unchanged; a tier the theme has nothing for gets the
        /// ordinary raid. Replace (the default): the whole raid is the theme's (Themed); else Share of each group mixed in.</summary>
        public sealed class Theme
        {
            public string Festival { get; set; }               // SeasonalCalendar key ("fowlplay")
            public string Group { get; set; }                  // "turkeys", for the lines and the log
            public double Weight { get; set; } = 1;            // when two festivals with themes overlap
            public double Share { get; set; } = 0.4;
            public bool Replace { get; set; } = true;
            public List<ThemeTier> Tiers { get; set; } = new List<ThemeTier>();
            public ThemeTier TierAt(int tier) => Tiers.FirstOrDefault(t => t.Tier == tier);
        }

        public sealed class ThemeTier
        {
            public int Tier { get; set; }
            public uint[] Wave { get; set; } = Array.Empty<uint>();
            public uint[] Adds { get; set; } = Array.Empty<uint>();   // empty: the adds come from Wave
            public uint Leader { get; set; }                           // 0: no leader (and no big war band) for this theme
            public string Group { get; set; }                          // this tier's name in the lines, else the theme's
            public uint[] Escort { get; set; } = Array.Empty<uint>();
            public uint[] AddsOrWave => Adds != null && Adds.Length > 0 ? Adds : Wave;
        }

        /// <summary>A raid comes in waves (owner 2026-11-16: "need more smaller adds in addition to the group that comes
        /// and a few waves. there were only 4 drudges"): the main group each wave, a pack of weaker adds with it, the
        /// next wave once most of the last is down (or after a while, if someone is fighting), the leader with the last.</summary>
        public sealed class WaveDef
        {
            public int Count { get; set; } = 3;
            public int BigCount { get; set; } = 4;
            public double LaterFactor { get; set; } = 0.8;      // later waves' main group against the first's
            public double AddsBase { get; set; } = 2;
            public double AddsPerPlayer { get; set; } = 1;
            public int AddsMax { get; set; } = 8;
            public double NextAtShare { get; set; } = 0.7;     // of the last wave killed
            public double NextAfterSeconds { get; set; } = 150; // ... or this long after it, if someone hit a raider since
            public double MinGapSeconds { get; set; } = 40;
            public double LeaderChance { get; set; } = 0.3;    // a raid that is not big still brings its leader with the last wave
            public int MaxAlive { get; set; } = 30;            // the next wave waits while this many stand (landblock health)
            public int MaxTotal { get; set; } = 60;            // all waves of one raid, adds and leader included
        }

        public sealed class BigDef
        {
            public double WaveFactor { get; set; } = 1.5;
            public int Escorts { get; set; } = 3;              // with the leader in a big raid's last wave
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
            foreach (var t in c.Themes ?? new List<Theme>())
            {
                if (string.IsNullOrWhiteSpace(t.Festival) || string.IsNullOrWhiteSpace(t.Group)) p.Add("theme: festival and group");
                if (t.Share <= 0 || t.Share >= 1) p.Add($"theme {t.Festival}: share between 0 and 1");
                foreach (var tt in t.Tiers) if (tt.Wave == null || tt.Wave.Length == 0) p.Add($"theme {t.Festival} tier {tt.Tier}: empty wave");
                foreach (var tt in t.Tiers) if (tt.Leader == 0 && tt.Escort != null && tt.Escort.Length > 0) p.Add($"theme {t.Festival} tier {tt.Tier}: escorts without a leader");
                if (c.Themes.Count(x => string.Equals(x.Festival, t.Festival, StringComparison.OrdinalIgnoreCase)) > 1) p.Add($"theme {t.Festival}: twice");
            }
            if (c.Waves == null || c.Waves.Count < 1 || c.Waves.BigCount < 1 || c.Waves.MaxAlive < 1 || c.Waves.MaxTotal < 1) p.Add("waves: counts and caps must be 1 or more");
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

        public static int WaveCount(WaveDef w, bool big) => Math.Max(1, big ? w.BigCount : w.Count);

        /// <summary>One wave: the main group (the first full size, later ones LaterFactor of it; a big raid's all half again),
        /// the adds (base plus so many per player, up to AddsMax; none when the tier has no adds), the leader with the last
        /// wave (always for a big raid, else when rolled), and a big raid's escorts with it. MaxTotal, shared among the
        /// waves, trims the adds first.</summary>
        public static (int main, int adds, bool leader, int escorts) PlanWave(Config c, TierDef t, int players, bool big, int wave, int waves, bool leaderRolled, int spawnedSoFar)
        {
            var w = c.Waves;
            int main = WaveSize(t, players, big, c.Big.WaveFactor);
            if (wave > 1) main = Math.Max(1, (int)Math.Round(main * w.LaterFactor, MidpointRounding.AwayFromZero));
            int adds = t.Adds == null || t.Adds.Length == 0 ? 0
                : (int)Math.Round(Math.Min(w.AddsBase + w.AddsPerPlayer * Math.Max(1, players), w.AddsMax), MidpointRounding.AwayFromZero);
            bool comes = t.Leader != 0 && (big || leaderRolled);
            bool leader = comes && wave >= waves;
            int escorts = leader && big ? c.Big.Escorts : 0;
            // the leader (and escorts) are kept room for from the start; the rest shared evenly among the waves
            int reserve = comes ? 1 + (big ? c.Big.Escorts : 0) : 0;
            int share = Math.Max(1, (w.MaxTotal - reserve) / Math.Max(1, waves));
            int room = Math.Min(share, Math.Max(0, w.MaxTotal - reserve - spawnedSoFar));
            if (main + adds > room) adds = Math.Max(0, room - main);
            if (main > room) main = room;
            return (main, adds, leader, escorts);
        }

        /// <summary>The festival theme for a raid at this tier: weighted among the themes whose festival is on and that have
        /// something for the tier; null when none. Staff may force one by festival key (on or not); "none" forces none.</summary>
        public static (Theme theme, ThemeTier tier) PickTheme(Config c, int tier, Func<string, bool> festivalOn, double r, string force = null)
        {
            if (c.Themes == null || c.Themes.Count == 0 || string.Equals(force, "none", StringComparison.OrdinalIgnoreCase)) return (null, null);
            var options = c.Themes
                .Where(t => force != null ? string.Equals(t.Festival, force, StringComparison.OrdinalIgnoreCase) : festivalOn != null && festivalOn(t.Festival))
                .Select(t => (t, tt: t.TierAt(tier)))
                .Where(o => o.tt != null && o.tt.Wave != null && o.tt.Wave.Length > 0)
                .ToList();
            if (options.Count == 0) return (null, null);
            double total = options.Sum(o => Math.Max(0, o.t.Weight));
            if (total <= 0) return options[0];
            double x = r * total;
            foreach (var o in options) { x -= Math.Max(0, o.t.Weight); if (x < 0) return o; }
            return options[options.Count - 1];
        }

        /// <summary>A raid made of the theme's monsters: the tier's sizes, treasure and newcomer guard, the theme's weenies
        /// (its leader or none).</summary>
        public static TierDef Themed(TierDef def, ThemeTier tt) => new TierDef
        {
            Tier = def.Tier, Base = def.Base, PerPlayer = def.PerPlayer, Max = def.Max, Treasure = def.Treasure,
            ProtectBelow = def.ProtectBelow, Wave = tt.Wave, Adds = tt.AddsOrWave, Leader = tt.Leader,
            Escort = tt.Escort ?? Array.Empty<uint>(),
        };

        /// <summary>How many of n become the theme's: the share rounded, at least one of any group, never all of a group
        /// of two or more (the raid's own kind still leads it).</summary>
        public static int ThemedCount(int n, double share)
        {
            if (n <= 0 || share <= 0) return 0;
            int k = (int)Math.Round(n * share, MidpointRounding.AwayFromZero);
            return Math.Max(1, Math.Min(k, n >= 2 ? n - 1 : 1));
        }

        /// <summary>A wave's weenies: the leader and escorts as the kind's; ThemedCount of the main group and of the adds
        /// from the theme. pick(n) is a random index below n.</summary>
        public static List<(uint wcid, bool add, bool themed)> WaveWeenies(TierDef def, ThemeTier theme, double share,
            int main, int adds, bool leader, int escorts, Func<int, int> pick)
        {
            var list = new List<(uint, bool, bool)>();
            if (leader) list.Add((def.Leader, false, false));
            var esc = def.Escort.Length > 0 ? def.Escort : def.Wave;
            for (int i = 0; i < escorts; i++) list.Add((esc[pick(esc.Length)], false, false));
            int tm = theme == null ? 0 : ThemedCount(main, share), ta = theme == null ? 0 : ThemedCount(adds, share);
            for (int i = 0; i < main; i++)
                list.Add(i < tm ? (theme.Wave[pick(theme.Wave.Length)], false, true) : (def.Wave[pick(def.Wave.Length)], false, false));
            var tpool = theme?.AddsOrWave;
            for (int i = 0; i < adds; i++)
                list.Add(i < ta ? (tpool[pick(tpool.Length)], true, true) : (def.Adds[pick(def.Adds.Length)], true, false));
            return list;
        }

        /// <summary>The next wave comes once NextAtShare of the last one is down, or NextAfterSeconds after it if a raider
        /// was hit since it came (an idle town is not piled on); never sooner than MinGapSeconds, never when it would put
        /// more than MaxAlive on the field (unless none stand).</summary>
        public static bool NextWaveDue(int lastWave, int lastWaveAlive, double sinceWave, bool hitSince, int aliveNow, int nextSize, WaveDef w)
        {
            if (sinceWave < w.MinGapSeconds) return false;
            if (aliveNow > 0 && aliveNow + nextSize > w.MaxAlive) return false;
            if (lastWave <= 0 || lastWave - lastWaveAlive >= Math.Ceiling(lastWave * w.NextAtShare)) return true;
            return hitSince && sinceWave >= w.NextAfterSeconds;
        }

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
        /// running; the idle clock restarts with each wave); faded when raiders are gone without dying (a stop, a block taken down); withdrawn when nobody has
        /// hit a raider for the idle timeout, the town has stood empty for EmptyTownMinutes, or the raid passed its
        /// longest time. (Box 2026-11-16: the town emptied, the raiders dropped off the live list and a raid read as held.)</summary>
        public static Outcome Judge(int spawned, int alive, int killed, bool moreToCome, double sinceSpawn, double sinceLastHit, ScheduleDef s, double emptyFor = 0)
        {
            if (spawned == 0) return Outcome.Running;
            if (alive == 0 && !moreToCome) return killed >= HeldAt(spawned, s) ? Outcome.Held : Outcome.Faded;
            if (alive == 0) return Outcome.Running;
            if (sinceLastHit >= s.IdleTimeoutMinutes * 60) return Outcome.Withdrew;
            bool fighting = sinceLastHit < s.FightGraceSeconds;
            if (!fighting && sinceSpawn >= s.MaxMinutes * 60) return Outcome.Withdrew;
            if (!fighting && emptyFor >= s.EmptyTownMinutes * 60) return Outcome.Withdrew;
            return Outcome.Running;
        }

        /// <summary>AdminSpawn's cleanup: a creature the admin made stays until killed, or until nobody has been within
        /// AdminSpawnNearMetres of it for an hour.</summary>
        public const float AdminSpawnNearMetres = 100f;
        public static bool AdminSpawnStale(double secondsSinceSomeoneNear) => secondsSinceSomeoneNear >= 3600;

        /// <summary>A raider standing outside town is stuck only when it is not fighting (one in a fight stands still).</summary>
        public static bool StuckOutside(double stillFor, bool fighting, ScheduleDef s) => !fighting && stillFor >= s.StuckOutsideSeconds;

        /// <summary>Kills that hold the town once no raider is left: HeldShare of those spawned (a raider stuck outside
        /// town is taken away, box 2026-11-16: 11 of 12 killed read as faded).</summary>
        public static int HeldAt(int spawned, ScheduleDef s) => Math.Max(1, (int)Math.Ceiling(spawned * s.HeldShare));

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
