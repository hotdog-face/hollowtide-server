using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Common;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// MONSTER FUN (owner, 2026-11-03: monsters that add fun, not frustration; docs/MONSTER-FUN.md). Ours, not retail.
    /// Three parts, each on its own switch, all default OFF:
    ///
    ///   rg_champions           a rare generator spawn becomes a named champion: more health, a little more damage,
    ///                          one family trick, an extra loot roll a tier up, a named trophy, a server line on death.
    ///                          One per landblock, none where one player has been farming (the busy rule).
    ///   rg_boss_phases         per-boss table: at 50 % and 25 % a boss changes swing speed, spells, weakness, or
    ///                          calls helpers. Beside the world data's own phase lines and WorldBoss.cs's extras.
    ///   rg_boss_heavy_attacks  one telegraphed area hit per boss: line + boss_shadowfall, 3.5 s wind-up, then a hit
    ///                          round where the boss stood. Never lethal on its own; walk out and it misses.
    ///
    /// Hooks: WorldObject.EnterWorld prefix (champion roll, before the client first sees the name), Creature.Monster_Tick
    /// postfix (tricks, phases, heavy attacks), Creature.GenerateTreasure postfix (champion loot), Creature.OnDeath
    /// postfix (server line, busy-rule kill log). Every target is declared where patched; misses are logged, not thrown.
    /// </summary>
    static class MonsterFun
    {
        public const string P_CHAMP = "rg_champions", P_PHASES = "rg_boss_phases", P_HEAVY = "rg_boss_heavy_attacks";
        const string P_SKIP = "rg_champion_skip_landblocks";
        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            ("rg_champion_chance", 0.005, "MonsterFun: chance a generator spawn becomes a champion"),
            ("rg_champion_min_level", 20, "MonsterFun: lowest creature level that can be a champion"),
            ("rg_champion_health_mult", 4.0, "MonsterFun: champion max health multiplier"),
            ("rg_champion_damage_rating", 15, "MonsterFun: Damage Rating added to a champion"),
            ("rg_champion_xp_mult", 3.0, "MonsterFun: champion XP multiplier"),
            ("rg_champion_busy_min", 20, "MonsterFun: minutes of the last 30 one player must have killed on a landblock to block champions there"),
            ("rg_champion_max", 10, "MonsterFun: champions alive on the server at once"),
        };
        static double D(string k) => PropertyManager.GetDouble(k, s_Doubles.First(x => x.key == k).def).Item;
        public static bool ChampOn => PropertyManager.GetBool(P_CHAMP, true).Item;
        public static bool PhasesOn => PropertyManager.GetBool(P_PHASES, true).Item;
        public static bool HeavyOn => PropertyManager.GetBool(P_HEAVY, true).Item;

        // ------------------------------------------------------------------ data

        internal enum Trick { Frenzy, Rally, Mending, Slam }

        internal sealed class Heavy
        {
            public string Name, Warn, Hit;          // Warn: "{0}" = the boss's name. Hit: "{0}" = name, "{1}" = damage
            public float Radius = 6f, Graze = 3f, Frac = 0.35f, GrazeFrac = 0.08f;
            public double WindUp = 3.5, First = 20, Every = 25, EveryLast = 18;
        }

        internal sealed class Phase
        {
            public float At;                        // health fraction
            public string Line;                     // nearby players' chat
            public float Swing = 1f;                // < 1 = faster swings (share of the gap between swings kept)
            public double? MagicDelay;              // AiUseMagicDelay, seconds between spells
            public (int spell, float factor)[] SpellWeights;
            public (DamageType type, double mult)[] Weak;   // multiplies ResistX: > 1 takes more of that type
            public uint HelperWcid; public int Helpers;
            public bool LastHeavy;                  // heavy attack on its faster rhythm from here
        }

        internal sealed class BossSpec { public string Name; public Phase[] Phases; public Heavy Heavy; }

        /// <summary>The first set (docs/MONSTER-FUN.md). Adding a boss is one more entry.</summary>
        internal static readonly Dictionary<uint, BossSpec> Bosses = new Dictionary<uint, BossSpec>
        {
            [WorldBoss.BAEL] = new BossSpec
            {
                Name = "Bael'Zharon",
                Phases = new[]
                {
                    new Phase { At = 0.50f, Line = "Living shadow wraps Bael'Zharon. Blades slide off him, but fire bites deep!",
                        Weak = new[] { (DamageType.Fire, 1.3), (DamageType.Slash, 0.7) } },
                    new Phase { At = 0.25f, Line = "Bael'Zharon calls his Shadows and strikes faster!",
                        Swing = 0.75f, HelperWcid = 900201, Helpers = 3, LastHeavy = true },
                },
                Heavy = new Heavy { Name = "Hopeslayer's Wrath", Radius = 6f,
                    Warn = "{0} gathers the Shadow around him. Get away from him!",
                    Hit = "{0}'s wrath tears through you for {1} points of damage!" },
            },
            [AlligatorBoss_Wcid] = new BossSpec
            {
                Name = "Old Snapjaw",
                Phases = new[]
                {
                    new Phase { At = 0.50f, Line = "Old Snapjaw thrashes, and things stir in the shallows!",
                        HelperWcid = 7181, Helpers = 3 },   // Desolation Moarsman, retail, level 115
                    new Phase { At = 0.25f, Line = "Old Snapjaw rolls, showing his soft belly. He snaps faster!",
                        Swing = 0.65f, Weak = new[] { (DamageType.Pierce, 1.4) }, LastHeavy = true },
                },
                Heavy = new Heavy { Name = "Tail Sweep", Radius = 5f, Frac = 0.30f,
                    Warn = "{0} coils his tail. Get clear!",
                    Hit = "{0}'s tail sweeps into you for {1} points of damage!" },
            },
            [WorldBoss.BITTERFROST] = new BossSpec
            {
                Name = "Old Bitterfrost",
                Phases = new[]
                {
                    new Phase { At = 0.50f, Line = "Old Bitterfrost's ice cracks. Fire will melt him, blades will not!",
                        Weak = new[] { (DamageType.Fire, 1.3), (DamageType.Slash, 0.7) } },
                    new Phase { At = 0.25f, Line = "Old Bitterfrost howls, and the blizzard comes faster!",
                        Swing = 0.75f, SpellWeights = new[] { (2138, 2f), (2731, 2f) }, LastHeavy = true },   // Blizzard, Frost Arc VII
                },
                Heavy = new Heavy { Name = "Avalanche", Radius = 7f,
                    Warn = "{0} heaves up a mountain of snow. Run!",
                    Hit = "The avalanche buries you for {1} points of damage!" },
            },
        };
        const uint AlligatorBoss_Wcid = 900400;

        static readonly string[] s_FirstNames =
        {
            "Grimtooth", "Bloodmaw", "Ashclaw", "Old Gnarl", "Rotfang", "Skullsplitter", "Blackhide", "Ironjaw", "Mirefoot",
            "Stonebelly", "Gorehorn", "Nightstalker", "Bonecrusher", "Duskwing", "Rimeback", "Scarbrow", "Thornback", "Wretchgut",
            "Cinderhide", "Gloomtusk", "Ravenmaw", "Splinterclaw", "Mossback", "Hollowgaze", "Vilefang", "Grimscale", "Ashenmane",
            "Bleakhorn", "Cragjaw", "Dreadpaw", "Fellstride", "Gutripper", "Hookclaw", "Ironhide", "Marrowgnaw", "Redeye",
            "Saltfang", "Shiverback", "Sludgemaw", "Wartskin",
        };

        static Trick TrickFor(Creature c)
        {
            switch (c.CreatureType)
            {
                case CreatureType.Drudge: case CreatureType.Banderling: case CreatureType.Mosswart: case CreatureType.Tusker:
                case CreatureType.Gromnie: case CreatureType.Reedshark: case CreatureType.Rat:
                    return HasHealSelf(c) ? Trick.Mending : Trick.Rally;
                case CreatureType.Shadow: case CreatureType.Virindi:
                    return Trick.Mending;
                case CreatureType.Golem: case CreatureType.Lugian: case CreatureType.Olthoi: case CreatureType.Tumerok:
                    return Trick.Slam;
                default:
                    return HasHealSelf(c) ? Trick.Mending : Trick.Frenzy;
            }
        }

        static readonly int[] HEAL_SELF = { 6, 1157, 1158, 1159, 1160, 1161 };
        static bool HasHealSelf(Creature c)
        {
            try { var sb = c.Biota.PropertiesSpellBook; return sb != null && HEAL_SELF.Any(sb.ContainsKey); }
            catch { return false; }
        }

        static readonly Heavy SLAM = new Heavy { Name = "Ground Slam", Radius = 5f, Graze = 0f, Frac = 0.15f, WindUp = 2.5, First = 8, Every = 25, EveryLast = 25,
            Warn = "{0} rears up to slam the ground. Step back!", Hit = "{0} slams the ground, hitting you for {1} points of damage!" };

        // ------------------------------------------------------------------ state

        sealed class Champ
        {
            public string Name; public Trick Trick; public ushort Lb; public bool TrickDone;
            public uint BaseWcid; public DateTime Born = DateTime.UtcNow;
        }
        sealed class Fight
        {
            public double EngagedAt, LastTick, NextHeavy, BusyUntil, SwungFrom;
            public bool LastHeavy;
            public float Swing = 1f;
            public readonly HashSet<float> PhasesDone = new HashSet<float>();
            public readonly List<(double due, Action act)> Pending = new List<(double, Action)>();
        }
        static readonly ConditionalWeakTable<Creature, Champ> s_Champs = new ConditionalWeakTable<Creature, Champ>();
        static readonly ConditionalWeakTable<Creature, Fight> s_Fights = new ConditionalWeakTable<Creature, Fight>();
        static readonly ConcurrentDictionary<ushort, WeakReference<Creature>> s_ChampByLb = new ConcurrentDictionary<ushort, WeakReference<Creature>>();
        static int s_Registered, s_Made, s_Slain, s_Phases, s_Heavies, s_HeavyHits, s_SkippedBusy, s_SkippedCap;

        // busy rule: per (landblock, player) a bit per minute with a kill, last 64 minutes
        static readonly Dictionary<(ushort, uint), (long min, ulong mask)> s_Kills = new Dictionary<(ushort, uint), (long, ulong)>();
        static readonly object s_KillLock = new object();
        static long NowMin => (long)(Time.GetUnixTime() / 60);

        internal static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_CHAMP)) b[P_CHAMP] = new Property<bool>(true, "MonsterFun: rare named champions (ours, docs/MONSTER-FUN.md)");
                if (!b.ContainsKey(P_PHASES)) b[P_PHASES] = new Property<bool>(true, "MonsterFun: boss phases at 50% and 25% (ours)");
                if (!b.ContainsKey(P_HEAVY)) b[P_HEAVY] = new Property<bool>(true, "MonsterFun: bosses' warned heavy area attack (ours)");
                var d = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
                var s = Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_SKIP)) s[P_SKIP] = new Property<string>("2111", "MonsterFun: landblocks (hex, comma separated) that never get champions; 2111 = the Withered Atoll");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MonsterFun: switches not registered with @modify* ({e.Message}); defaults apply"); }
            CommandManager.TryAddCommand(Handle, "monsterfun", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Champions, boss phases, heavy attacks (ours): status; champ <wcid> makes a champion in front of you; busy shows the busy rule here; hurt <pct> sets the selected (or nearest) monster's health.", "[champ <wcid> | hurt <pct> | busy]");
            Mod.Log.Info($"[RevivalGuard] MonsterFun: champions {(ChampOn ? "ON" : "off")}, boss phases {(PhasesOn ? "ON" : "off")}, heavy attacks {(HeavyOn ? "ON" : "off")}; {Bosses.Count} bosses");
        }

        // ------------------------------------------------------------------ champions

        internal static void OnEnterWorld(WorldObject wo)
        {
            if (!(wo is Creature c) || c is Player || c.Generator == null || c.CurrentLandblock != null || c.Location == null) return;
            if (!ChampOn || ThreadSafeRandom.Next(0.0f, 1.0f) >= D("rg_champion_chance")) return;
            if (!Eligible(c)) return;
            ushort lb = (ushort)(c.Location.Cell >> 16);
            if (Skipped(lb)) return;
            if (HasChampion(lb) || AliveCount() >= D("rg_champion_max")) { s_SkippedCap++; return; }
            if (Busy(lb)) { s_SkippedBusy++; return; }
            MakeChampion(c, lb);
        }

        static bool Eligible(Creature c) =>
            c.WeenieClassId < 900000                              // never our own creatures (bosses, event and staff content)
            && c.Attackable && !(c is CombatPet)
            && (c.Tolerance & (Tolerance.NoAttack | Tolerance.Retaliate)) == 0
            && (c.Level ?? 0) >= D("rg_champion_min_level")
            && c.DeathTreasureType.HasValue
            && !s_Champs.TryGetValue(c, out _);

        static bool Skipped(ushort lb)
        {
            var list = PropertyManager.GetString(P_SKIP, "2111").Item ?? "";
            foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (ushort.TryParse(part.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var v) && v == lb) return true;
            return false;
        }

        static bool Alive(Creature c) => c != null && !c.IsDead && !c.IsDestroyed;
        static bool HasChampion(ushort lb) => s_ChampByLb.TryGetValue(lb, out var w) && w.TryGetTarget(out var c) && Alive(c);
        static int AliveCount() => s_ChampByLb.Values.Count(w => w.TryGetTarget(out var c) && Alive(c));

        internal static void MakeChampion(Creature c, ushort lb)
        {
            var name = $"{s_FirstNames[ThreadSafeRandom.Next(0, s_FirstNames.Length - 1)]} the Champion {c.Name}";
            var ch = new Champ { Name = name, Trick = TrickFor(c), Lb = lb, BaseWcid = c.WeenieClassId };
            s_Champs.AddOrUpdate(c, ch);
            s_ChampByLb[lb] = new WeakReference<Creature>(c);
            c.Name = name;
            c.ObjScale = (c.ObjScale ?? 1f) * 1.2f;
            uint max = c.Health.MaxValue;
            c.Health.StartingValue += (uint)Math.Max(0, max * (D("rg_champion_health_mult") - 1));
            c.SetMaxVitals();
            c.SetProperty(PropertyInt.DamageRating, (c.GetProperty(PropertyInt.DamageRating) ?? 0) + (int)D("rg_champion_damage_rating"));
            if (c.XpOverride.HasValue) c.XpOverride = (int)Math.Min(int.MaxValue, c.XpOverride.Value * D("rg_champion_xp_mult"));
            s_Made++;
            Mod.Log.Info($"[RevivalGuard] MonsterFun: champion {name} (wcid {c.WeenieClassId}, level {c.Level}) in 0x{lb:X4}, trick {ch.Trick}, health {max} -> {c.Health.MaxValue}");
        }

        internal static bool IsChampion(Creature c) => c != null && s_Champs.TryGetValue(c, out _);

        /// <summary>Champion loot: one more roll from its own profile, a tier up, always magic; and the trophy.</summary>
        internal static void ChampionLoot(Creature c, DamageHistoryInfo killer, Corpse corpse, List<WorldObject> dropped)
        {
            if (!s_Champs.TryGetValue(c, out var ch) || c.DeathTreasure == null) return;
            var t = c.DeathTreasure;
            var up = new TreasureDeath
            {
                Id = t.Id, TreasureType = t.TreasureType, Tier = Math.Min(8, t.Tier + 1), LootQualityMod = t.LootQualityMod + 0.1f,
                UnknownChances = t.UnknownChances, ItemChance = 0, ItemMinAmount = 0, ItemMaxAmount = 0,
                ItemTreasureTypeSelectionChances = t.ItemTreasureTypeSelectionChances,
                MagicItemChance = 100, MagicItemMinAmount = 1, MagicItemMaxAmount = 1,
                MagicItemTreasureTypeSelectionChances = t.MagicItemTreasureTypeSelectionChances,
                MundaneItemChance = 0, MundaneItemMinAmount = 0, MundaneItemMaxAmount = 0,
                MundaneItemTypeSelectionChances = t.MundaneItemTypeSelectionChances,
            };
            var items = new List<WorldObject>();
            try { items.AddRange(LootGenerationFactory.CreateRandomLootObjects(up)); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MonsterFun: champion loot roll for {ch.Name}: {e.Message}"); }
            var player = killer?.TryGetPetOwnerOrAttacker() as Player;
            var trophy = WorldObjectFactory.CreateNewWorldObject(TROPHY);
            if (trophy != null)
            {
                trophy.Name = $"Skull of {ch.Name}";
                trophy.SetProperty(PropertyString.LongDesc, TrophyMounts.Inscription(ch.Name, player?.Name ?? "an unknown hand", DateTime.UtcNow));
                trophy.Value = 500;
                items.Add(trophy);
            }
            foreach (var wo in items)
            {
                if (corpse != null) { if (!corpse.TryAddToInventory(wo)) wo.Destroy(); }
                else dropped?.Add(wo);
            }
            Mod.Log.Info($"[RevivalGuard] MonsterFun: {ch.Name} loot: {string.Join(", ", items.Select(i => i.Name))} (tier {up.Tier})");
        }
        const uint TROPHY = 34204;   // Tremendous Monouga Skull: retail, wall hook, no quest or emote uses it (ace_world 2026-11-03)

        internal static void OnDeath(Creature c)
        {
            if (c == null || c is Player) return;
            var lb = c.Location != null ? (ushort)(c.Location.Cell >> 16) : (ushort)0;
            Player killer = null;
            try { killer = c.DamageHistory?.LastDamager?.TryGetPetOwnerOrAttacker() as Player; } catch { }
            if (killer != null && lb != 0) LogKill(lb, killer.Guid.Full);
            if (!s_Champs.TryGetValue(c, out var ch)) return;
            s_Slain++;
            s_ChampByLb.TryRemove(ch.Lb, out _);
            if (killer == null) { Mod.Log.Info($"[RevivalGuard] MonsterFun: champion {ch.Name} died, no player killer"); return; }
            var fs = killer.Fellowship?.FellowshipName;
            string line = string.IsNullOrWhiteSpace(fs)
                ? $"{ch.Name} has been slain by {killer.Name}!"
                : $"{ch.Name} has been slain by {killer.Name} and the fellowship {fs}!";
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(line, ChatMessageType.WorldBroadcast));
            Mod.Log.Info($"[RevivalGuard] MonsterFun: {line}");
        }

        static void LogKill(ushort lb, uint player)
        {
            long now = NowMin;
            lock (s_KillLock)
            {
                s_Kills.TryGetValue((lb, player), out var e);
                ulong mask = Shift(e.mask, now - e.min) | 1UL;
                s_Kills[(lb, player)] = (now, mask);
                if (s_Kills.Count > 4000)
                    foreach (var k in s_Kills.Where(kv => now - kv.Value.min > 64).Select(kv => kv.Key).ToList()) s_Kills.Remove(k);
            }
        }

        static ulong Shift(ulong mask, long by) => by <= 0 ? mask : by >= 64 ? 0 : mask << (int)by;

        /// <summary>Minutes with a kill in the last 30, for the busiest single player on this landblock.</summary>
        internal static int BusyMinutes(ushort lb)
        {
            long now = NowMin; int best = 0;
            lock (s_KillLock)
                foreach (var kv in s_Kills)
                    if (kv.Key.Item1 == lb)
                        best = Math.Max(best, BitOperations.PopCount(Shift(kv.Value.mask, now - kv.Value.min) & ((1UL << 30) - 1)));
            return best;
        }
        static bool Busy(ushort lb) => BusyMinutes(lb) >= D("rg_champion_busy_min");

        // ------------------------------------------------------------------ the tick: tricks, phases, heavy attacks

        internal static void Tick(Creature c)
        {
            bool champ = s_Champs.TryGetValue(c, out var ch);
            Bosses.TryGetValue(c.WeenieClassId, out var boss);
            if (!champ && boss == null) return;
            if (c.IsDead || c.Location == null) return;
            if (champ && !ChampOn) champ = false;
            bool phases = boss != null && PhasesOn, heavy = boss != null && HeavyOn;
            if (!champ && !phases && !heavy) return;

            var f = s_Fights.GetOrCreateValue(c);
            double now = Time.GetUnixTime();
            if (now - f.LastTick > 15 || f.EngagedAt == 0) { f.EngagedAt = now; f.NextHeavy = 0; }   // a new fight (or the first)
            f.LastTick = now;
            for (int i = f.Pending.Count - 1; i >= 0; i--)
                if (now >= f.Pending[i].due) { var a = f.Pending[i].act; f.Pending.RemoveAt(i); try { a(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MonsterFun: pending on {c.Name}: {e.Message}"); } }

            float hp = c.Health.MaxValue == 0 ? 1f : (float)c.Health.Current / c.Health.MaxValue;
            var target = c.AttackTarget as Creature;

            if (champ) ChampTrick(c, ch, f, hp, target, now);
            if (phases)
                foreach (var ph in boss.Phases)
                    if (hp <= ph.At && f.PhasesDone.Add(ph.At)) ApplyPhase(c, boss, ph, f, target);
            if (f.Swing < 1f) FasterSwing(c, f);
            if (heavy) HeavyRhythm(c, boss.Heavy, f, target, now, f.LastHeavy);
        }

        static void ChampTrick(Creature c, Champ ch, Fight f, float hp, Creature target, double now)
        {
            switch (ch.Trick)
            {
                case Trick.Rally when !ch.TrickDone && hp <= 0.5f:
                    ch.TrickDone = true;
                    Say(c, $"{ch.Name} howls for its kin!");
                    Helpers(c, ch.BaseWcid, 2, target);
                    break;
                case Trick.Mending when !ch.TrickDone && hp <= 0.3f:
                    ch.TrickDone = true;
                    c.UpdateVital(c.Health, Math.Min(c.Health.MaxValue, c.Health.Current + c.Health.MaxValue / 4));
                    c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.HealthUpRed));
                    Say(c, $"{ch.Name} binds its wounds!");
                    break;
                case Trick.Frenzy when !ch.TrickDone && hp <= 0.4f:
                    ch.TrickDone = true;
                    f.Swing = 0.7f;
                    Say(c, $"{ch.Name} flies into a frenzy!");
                    break;
                case Trick.Slam:
                    HeavyRhythm(c, SLAM, f, target, now, false, ch.Name);
                    break;
            }
        }

        static void ApplyPhase(Creature c, BossSpec boss, Phase ph, Fight f, Creature target)
        {
            s_Phases++;
            if (!string.IsNullOrEmpty(ph.Line)) Say(c, ph.Line);
            if (ph.Swing < 1f) f.Swing = Math.Min(f.Swing, ph.Swing);
            if (ph.MagicDelay.HasValue) c.SetProperty(PropertyFloat.AiUseMagicDelay, ph.MagicDelay.Value);
            if (ph.SpellWeights != null)
                foreach (var (spell, factor) in ph.SpellWeights)
                {
                    var sb = c.Biota.PropertiesSpellBook;
                    if (sb == null || !sb.TryGetValue(spell, out var p)) continue;
                    // ACE spellbook weights over 2.0 are "2 + chance"; under 2 they are a plain chance
                    float np = p > 2f ? 2f + (p - 2f) * factor : p * factor;
                    c.Biota.TryRemoveKnownSpell(spell, c.BiotaDatabaseLock);
                    c.Biota.GetOrAddKnownSpell(spell, c.BiotaDatabaseLock, out _, np);
                }
            if (ph.Weak != null)
                foreach (var (type, mult) in ph.Weak)
                {
                    var prop = ResistProp(type);
                    if (prop != 0) c.SetProperty(prop, (c.GetProperty(prop) ?? 1.0) * mult);
                }
            if (ph.Helpers > 0) Helpers(c, ph.HelperWcid, ph.Helpers, target);
            if (ph.LastHeavy) f.LastHeavy = true;
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.EnchantUpRed));
            Mod.Log.Info($"[RevivalGuard] MonsterFun: {boss.Name} 0x{c.Guid.Full:X8} phase at {ph.At:P0} (health {c.Health.Current}/{c.Health.MaxValue})");
        }

        static PropertyFloat ResistProp(DamageType t) => t switch
        {
            DamageType.Slash => PropertyFloat.ResistSlash, DamageType.Pierce => PropertyFloat.ResistPierce,
            DamageType.Bludgeon => PropertyFloat.ResistBludgeon, DamageType.Fire => PropertyFloat.ResistFire,
            DamageType.Cold => PropertyFloat.ResistCold, DamageType.Acid => PropertyFloat.ResistAcid,
            DamageType.Electric => PropertyFloat.ResistElectric, _ => 0,
        };

        /// <summary>Faster swings: once per swing, shorten the wait ACE set (Monster_Melee: animation + a random 0..PowerupTime).</summary>
        static void FasterSwing(Creature c, Fight f)
        {
            if (c.PrevAttackTime == f.SwungFrom || c.NextAttackTime >= double.MaxValue / 2 || c.NextAttackTime <= c.PrevAttackTime) return;
            f.SwungFrom = c.PrevAttackTime;
            c.NextAttackTime = c.PrevAttackTime + (c.NextAttackTime - c.PrevAttackTime) * f.Swing;
        }

        static void Helpers(Creature c, uint wcid, int n, Creature target)
        {
            int made = 0;
            for (int i = 0; i < n; i++)
            {
                if (!(WorldObjectFactory.CreateNewWorldObject(wcid) is Creature h)) { Mod.Log.Warn($"[RevivalGuard] MonsterFun: helper weenie {wcid} missing"); return; }
                double ang = 2 * Math.PI * i / n + ThreadSafeRandom.Next(0.0f, 0.6f);
                var p = c.Location.Pos + new Vector3((float)Math.Cos(ang) * 4f, (float)Math.Sin(ang) * 4f, 0.5f);
                h.Location = new ACE.Entity.Position(c.Location.Cell, p.X, p.Y, p.Z, 0, 0, 0, 1);
                h.Lifespan = 600;   // helpers fade after ten minutes, never pile up at a spawn
                if (!h.EnterWorld()) { h.Destroy(); continue; }
                made++;
                h.EnqueueBroadcast(new GameMessageScript(h.Guid, PlayScript.Create));
                if (target != null && !target.IsDead) { h.AttackTarget = target; h.WakeUp(false); }
            }
            Mod.Log.Info($"[RevivalGuard] MonsterFun: {c.Name} called {made} x {wcid}");
        }

        // ------------------------------------------------------------------ heavy attacks

        static void HeavyRhythm(Creature c, Heavy h, Fight f, Creature target, double now, bool last, string name = null)
        {
            if (!(target is Player tp) || tp.IsDead || tp.Location == null) return;
            if (f.NextHeavy == 0) { f.NextHeavy = f.EngagedAt + h.First; return; }
            if (now < f.NextHeavy || now < f.BusyUntil) return;
            if (tp.CurrentLandblock != c.CurrentLandblock || c.Location.DistanceTo(tp.Location) > 25f) return;
            f.NextHeavy = now + (last ? h.EveryLast : h.Every);
            StartHeavy(c, h, f, now, name ?? c.Name, tp);
        }

        static void StartHeavy(Creature c, Heavy h, Fight f, double now, string name, Player faced)
        {
            s_Heavies++;
            if (c.IsMoving) c.CancelMoveTo();
            c.TurnTo(faced);
            c.EmoteManager.IsBusy = true;
            f.BusyUntil = now + h.WindUp + 1.0;
            var at = new ACE.Entity.Position(c.Location);   // fixed now: walking away from here is the dodge
            BossWarning.WarnAlways(c);
            Say(c, string.Format(h.Warn, name));
            f.Pending.Add((now + h.WindUp, () => HeavyHit(c, h, at, name)));
            f.Pending.Add((now + h.WindUp + 1.0, () => { if (!c.IsDestroyed) c.EmoteManager.IsBusy = false; }));
        }

        static void HeavyHit(Creature c, Heavy h, ACE.Entity.Position at, string name)
        {
            if (c.IsDead || c.Location == null) return;
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.BaelZharonSmite));
            int hit = 0, grazed = 0;
            var who = new List<string>();
            foreach (var p in Near(c, 40f))
            {
                if (p.IsDead) continue;
                float dist = p.Location.Distance2D(at);   // across landblock edges too
                float frac = dist <= h.Radius ? h.Frac : dist <= h.Radius + h.Graze ? h.GrazeFrac : 0f;
                who.Add($"{p.Name} {dist:0.0}m");
                if (frac <= 0f) continue;
                // never lethal on its own: it stops at 1 health
                uint amount = (uint)Math.Min(frac * p.Health.MaxValue, Math.Max(0, (int)p.Health.Current - 1));
                if (amount == 0) continue;
                int dealt = p.TakeDamage(c, DamageType.Bludgeon, amount, BodyPart.Chest);
                who[who.Count - 1] += $" -{dealt}";
                if (dealt > 0) p.SendMessage(string.Format(h.Hit, name, dealt), ChatMessageType.Combat);   // an invincible admin takes 0: no line
                if (dist <= h.Radius)
                {
                    // the push point in the player's own landblock frame
                    var away = new Vector3((p.Location.LandblockId.LandblockX - at.LandblockId.LandblockX) * 192 + p.Location.PositionX - at.PositionX,
                                           (p.Location.LandblockId.LandblockY - at.LandblockId.LandblockY) * 192 + p.Location.PositionY - at.PositionY, 0);
                    Knockback.PushFrom(p, p.Location.Pos - away, 7f, 3f); hit++;
                }
                else grazed++;
            }
            s_HeavyHits += hit;
            Mod.Log.Info($"[RevivalGuard] MonsterFun: {name} {h.Name}: {hit} hit, {grazed} grazed ({string.Join(", ", who)})");
        }

        // ------------------------------------------------------------------ helpers

        static IEnumerable<Player> Near(Creature c, float m) =>
            PlayerManager.GetAllOnline().Where(p => p.Location != null && p.Location.DistanceTo(c.Location) <= m);

        static void Say(Creature c, string text)
        {
            foreach (var p in Near(c, 60f))
                p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        static void Reply(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static void Handle(Session session, params string[] args)
        {
            var me = session?.Player;
            if (args.Length >= 2 && args[0].Equals("champ", StringComparison.OrdinalIgnoreCase) && uint.TryParse(args[1], out var wcid) && me?.Location != null)
            {
                if (!(WorldObjectFactory.CreateNewWorldObject(wcid) is Creature c)) { Reply(session, $"[MonsterFun] weenie {wcid} is not a creature"); return; }
                var fwd = Vector3.Transform(Vector3.UnitY, me.Location.Rotation) * 6f;
                var p = me.Location.Pos + fwd;
                c.Location = new ACE.Entity.Position(me.Location.Cell, p.X, p.Y, p.Z + 0.5f, 0, 0, 0, 1);
                ushort lb = (ushort)(me.Location.Cell >> 16);
                MakeChampion(c, lb);
                c.Lifespan = 1800;   // no generator, so ACE would save it: a test one goes away in 30 min
                if (!c.EnterWorld()) { c.Destroy(); Reply(session, "[MonsterFun] could not place it there"); return; }
                Reply(session, $"[MonsterFun] {c.Name} 0x{c.Guid.Full:X8}, health {c.Health.MaxValue}, trick {(s_Champs.TryGetValue(c, out var ch) ? ch.Trick.ToString() : "?")}");
                return;
            }
            // hurt <pct>: set the nearest monster within 20 m to that percent of its health (testing phases without a long fight)
            if (args.Length >= 2 && args[0].Equals("hurt", StringComparison.OrdinalIgnoreCase) && float.TryParse(args[1], out var pct) && me?.CurrentLandblock != null)
            {
                Creature near = null; float best = 20f;
                // the selected (appraised) monster first, else the nearest
                if (me.RequestedAppraisalTarget is uint sel && me.CurrentLandblock.GetObject(sel) is Creature picked && !(picked is Player) && picked.IsAlive)
                    near = picked;
                else foreach (var wo in me.CurrentLandblock.GetAllWorldObjectsForDiagnostics())
                    if (wo is Creature cr && !(cr is Player) && cr.IsAlive && cr.Attackable && cr.Location != null)
                    {
                        float d = me.Location.DistanceTo(cr.Location);
                        if (d < best) { best = d; near = cr; }
                    }
                if (near == null) { Reply(session, "[MonsterFun] no monster within 20 m"); return; }
                near.Health.Current = (uint)Math.Clamp(near.Health.MaxValue * pct / 100f, 1, near.Health.MaxValue);
                Reply(session, $"[MonsterFun] {near.Name} now {near.Health.Current}/{near.Health.MaxValue}");
                return;
            }
            if (args.Length >= 1 && args[0].Equals("busy", StringComparison.OrdinalIgnoreCase) && me?.Location != null)
            {
                ushort lb = (ushort)(me.Location.Cell >> 16);
                Reply(session, $"[MonsterFun] 0x{lb:X4}: busiest player killed in {BusyMinutes(lb)} of the last 30 minutes (blocks at {D("rg_champion_busy_min")}); skip list {(Skipped(lb) ? "yes" : "no")}; champion here {(HasChampion(lb) ? "yes" : "no")}");
                return;
            }
            Reply(session, $"[MonsterFun] champions {(ChampOn ? "ON" : "off")} (chance {D("rg_champion_chance")}, alive {AliveCount()}, made {s_Made}, slain {s_Slain}, skipped busy {s_SkippedBusy}, cap {s_SkippedCap}); phases {(PhasesOn ? "ON" : "off")} ({s_Phases} run); heavy {(HeavyOn ? "ON" : "off")} ({s_Heavies} started, {s_HeavyHits} players hit)");
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        internal static int Errors;
        internal static void Err(string where, Exception e)
        {
            if (Interlocked.Increment(ref Errors) <= 5 || Errors % 1000 == 0)
                Mod.Log.Error($"[RevivalGuard] MonsterFun {where} threw (error {Errors})", e);
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.EnterWorld))]
    static class MonsterFunEnterWorld
    {
        static bool Prepare() { MonsterFun.Register(); return true; }
        static void Prefix(WorldObject __instance)
        {
            try { MonsterFun.OnEnterWorld(__instance); } catch (Exception e) { MonsterFun.Err("champion roll", e); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class MonsterFunTick
    {
        static void Postfix(Creature __instance)
        {
            try { MonsterFun.Tick(__instance); } catch (Exception e) { MonsterFun.Err("tick", e); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class MonsterFunDeath
    {
        static void Postfix(Creature __instance)
        {
            try { MonsterFun.OnDeath(__instance); } catch (Exception e) { MonsterFun.Err("death", e); }
        }
    }

    [HarmonyPatch]
    static class MonsterFunLoot
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            bool ok = TargetMethod() != null;
            if (!ok) Mod.Log.Error("[RevivalGuard] MonsterFun: Creature.GenerateTreasure not found; champions drop no extra loot");
            return ok;
        }
        static void Postfix(Creature __instance, DamageHistoryInfo killer, Corpse corpse, List<WorldObject> __result)
        {
            try { MonsterFun.ChampionLoot(__instance, killer, corpse, __result); } catch (Exception e) { MonsterFun.Err("loot", e); }
        }
    }
}
