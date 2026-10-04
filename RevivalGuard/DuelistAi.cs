using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Animation;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// VARROW THE UNBOWED, the Darktide duelist (weenie 901050; docs/DUELIST-BOSS.md). Ours, not retail.
    /// Owner's brief (2026-09-28): "a boss that is a bit more like a real pk duel. Cunning, smart and
    /// hard to kill." MADE, NOT ADDED: the switch `duelist_ai` defaults OFF, and with it off he is an
    /// ordinary ACE monster rolling his own spellbook (and MonsterAi treats him like any other).
    ///
    /// A UTILITY BRAIN. Two decision points, both ACE's own:
    ///   * Creature.GetNextAttackType (ACE asks it whenever a monster picks its next attack): a prefix
    ///     scores every attack he could make now -- rebuff, self-cleanse, heal spell, dispel the
    ///     target's buffs, drain, the next step of the vulnerability stack, the burst, a plain swing
    ///     or bolt -- and hands ACE the best one (Magic with CurrentSpell set, or Melee). ACE then does
    ///     the rest exactly as for any caster: spell words in chat (AiUseHumanMagicAnimations), the
    ///     human cast gestures, mana, range, facing, resist rolls, projectiles.
    ///   * Creature.Monster_Tick (every 0.2 s): a prefix scores the MOVES -- retreat, break line of
    ///     sight behind a pillar, kite, use a healing kit -- and while one runs it takes the tick
    ///     (returns false), as MonsterAi does. Otherwise ACE's tick runs: its chase is how he closes.
    /// Each score has a little noise, and his thresholds (when to heal, when to run) are re-rolled
    /// every fight and after every use, so he never repeats one fixed pattern.
    ///
    /// FAIR. He reads only what a PK could read off an opponent in retail: the stance they are in and
    /// what they hold (melee, bow, wand), their health, stamina and mana bars, the enchantments on
    /// them (their buffed values show on an assess), their armour and resistances (assess), where they
    /// are, and what his own health is doing. Never their input, never a cast before it lands.
    /// Everything he does has a tell and a counter; docs/DUELIST-BOSS.md lists them.
    ///
    ///   @duelist                     switches, tuning, counters
    ///   @duelist reset | go          a fresh Varrow at the arena (its generator re-run) | to the arena's south edge
    ///   @duelist trace [sec]         log every decision with its scores, and his position twice a
    ///                                second, to ACE_Log.txt ([Duelist] lines); tools/monsterai/duelist/duelist_summary.py reads them
    ///
    /// Registers itself from Prepare() (PatchAll calls it), so Mod.cs needs no line. Every hook
    /// catches its own exceptions; a throw falls back to ACE's own behaviour.
    /// </summary>
    static class DuelistAi
    {
        internal const uint WCID = 901050, BLADE = 901051, PILLAR = 901052;
        const uint WAND = 46397;   // retail Shadownether Isparian Wand: what he casts with (owner 2026-09-29, "casting spells with his sword in hand")
        const uint ARENA_BLOCK = 0x2832, GEN_GUID = 0x72832000, GEN_WCID = 901053;   // tools/gpubox-ace/duelist-boss.sql (not applied on live)
        // the arena's south edge, 25 m from its centre (96, 96) and outside the 24 m at which he wakes, facing north
        static readonly ACE.Entity.Position ArenaEdge = new ACE.Entity.Position(0x28320023, 96.0f, 70.0f, 77.0f, 0f, 0f, 0f, 1f);
        const string P_ON = "duelist_ai", P_LOG = "duelist_ai_log";

        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            ("duelist_kits", 4, "Duelist (RevivalGuard DuelistAi): healing kits he carries into each fight"),
            ("duelist_kit_heal", 0.15, "Duelist: health a kit restores, as a fraction of his maximum"),
            ("duelist_kit_cooldown_s", 9, "Duelist: least time between two kits (s); PK kits were on a timer too"),
            ("duelist_kit_break", 0.02, "Duelist: damage during the kit, as a fraction of max health, that spoils it"),
            ("duelist_retreats", 2, "Duelist: retreats to recover per fight"),
            ("duelist_dispel_cooldown_s", 28, "Duelist: least time between two dispels of the target's buffs (s)"),
            ("duelist_dispel_min_buffs", 5, "Duelist: he dispels only a target carrying at least this many buffs"),
            ("duelist_drain_cooldown_s", 9, "Duelist: least time between two drains (s)"),
            ("duelist_read_s", 20, "Duelist: how often he re-reads the target's armour and resistances (s)"),
            ("duelist_kite_cooldown_s", 7, "Duelist: least time between two step-backs from a melee (s)"),
            ("duelist_los_cooldown_s", 14, "Duelist: least time between two dashes behind a pillar (s)"),
            ("duelist_riposte_mult", 1.3, "Duelist: second hit of the riposte on a careless chaser, times the first"),
            ("duelist_taunt_s", 12, "Duelist: least time between two taunts (s)"),
            ("duelist_spell_damage_mult", 0.18, "Duelist: his war bolts' damage to players, times ACE's own figure (a VII bolt on a vulnerable 290-health player is ~160 unscaled)"),
            ("duelist_melee_damage_mult", 0.8, "Duelist: his sword and riposte damage to players, times ACE's own figure"),
            ("duelist_burst_gap_s", 18, "Duelist: after a burst of two, seconds before the next burst (his stack stays up; the gap is the window to cleanse it or heal)"),
            ("duelist_swing_speed", 2.8, "Duelist: his sword swing's animation speed (ACE clamps a creature at 2.0; a fast PK swings well past it)"),
        };
        static double D(int i) => PropertyManager.GetDouble(s_Doubles[i].key, s_Doubles[i].def).Item;

        // ------------------------------------------------------------------ spells (ace_world ids, level VII unless noted)

        // element -> (war bolt, vulnerability Other)
        static readonly Dictionary<DamageType, (uint bolt, uint vuln)> s_Elem = new Dictionary<DamageType, (uint, uint)>
        {
            [DamageType.Slash] = (2146, 2164),      // Evisceration, Swordsman's Gift
            [DamageType.Pierce] = (2132, 2174),     // The Spike, Archer's Gift
            [DamageType.Bludgeon] = (2144, 2166),   // Crushing Shame, Tusker's Gift
            [DamageType.Fire] = (2128, 2170),       // Ilservian's Flame, Inferno's Gift
            [DamageType.Cold] = (2136, 2168),       // Icy Torment, Gelidite's Gift
            [DamageType.Acid] = (2122, 2162),       // Disintegration, Olthoi's Gift
            [DamageType.Electric] = (2140, 2172),   // Alset's Coil, Astyrrian's Gift
        };
        // element -> the level VI bolt of the plain exchange (the VII bolt is saved for the burst)
        static readonly Dictionary<DamageType, uint> s_Bolt6 = new Dictionary<DamageType, uint>
        {
            [DamageType.Slash] = 97, [DamageType.Pierce] = 91, [DamageType.Bludgeon] = 69, [DamageType.Fire] = 85,
            [DamageType.Cold] = 74, [DamageType.Acid] = 63, [DamageType.Electric] = 80,
        };
        // element -> protection he raises against what is hitting him (the "Blessing" is the self form)
        static readonly Dictionary<DamageType, uint> s_Prot = new Dictionary<DamageType, uint>
        {
            [DamageType.Slash] = 2151, [DamageType.Pierce] = 2161, [DamageType.Bludgeon] = 2153, [DamageType.Fire] = 2157,
            [DamageType.Cold] = 2155, [DamageType.Acid] = 2149, [DamageType.Electric] = 2159,
        };
        const uint IMPERIL = 2074;          // Gossamer Flesh (Imperil Other VII)
        const uint DRAIN_MANA = 2329;       // Essence Void (Drain Mana Other VII): theirs becomes his
        const uint DRAIN_STAM = 2330;       // Vigor Siphon (Drain Stamina Other VII)
        const uint DISPEL_LIFE = 4347;      // Incantation of Nullify Life Magic Other, positive only (4-6 spells)
        const uint DISPEL_CREATURE = 4338;  // Incantation of Nullify Creature Magic Other, positive only
        const uint CLEANSE_SELF = 3180;     // Eradicate All Magic Self (VII), negative only: every curse up to level VII (the VIII form costs 320 mana)
        const uint FUTILITY = 2282;         // Futility (Magic Yield Other VII): their magic defense down, so his curses stick
        const uint STAM_TO_MANA = 1681;     // Stamina to Mana Self VI: mana management when a melee gives him none to drain
        static readonly uint[] s_HealIds = { 2073, 2072, 1161 };   // Adja's Intervention / Gift (VII), Heal Self VI: the self one wins
        static readonly uint[] s_CoreBuffs = { 2053, 2245, 2243, 2281, 2301 };   // Armor, Invulnerability, Impregnability, Magic Resistance, Sprint (VII)

        static readonly ConcurrentDictionary<uint, Spell> s_Spells = new ConcurrentDictionary<uint, Spell>();
        static Spell Sp(uint id) => s_Spells.GetOrAdd(id, i => new Spell(i));
        static uint s_Heal;

        /// <summary>Spells need the DATs, which are not loaded when PatchAll runs Prepare(): resolve on
        /// first use, and log every id's name once so a staging log shows what he will cast.</summary>
        static void ResolveSpells()
        {
            if (s_Heal != 0) return;
            s_Heal = s_HealIds.FirstOrDefault(id => { var sp = Sp(id); return !sp.NotFound && sp.IsSelfTargeted; });
            if (s_Heal == 0) s_Heal = 1161;
            var names = string.Join(", ", s_CoreBuffs.Concat(s_Prot.Values).Concat(s_Elem.Values.SelectMany(e => new[] { e.bolt, e.vuln })).Concat(s_Bolt6.Values)
                .Concat(new[] { IMPERIL, DRAIN_MANA, DRAIN_STAM, DISPEL_LIFE, DISPEL_CREATURE, CLEANSE_SELF, STAM_TO_MANA, s_Heal })
                .Select(id => { var sp = Sp(id); return sp.NotFound ? $"{id}:MISSING" : $"{id}:{sp.Name}{(sp.IsSelfTargeted ? "(self)" : "")}{(sp.IsProjectile ? " " + sp.DamageType : "")} {sp.BaseMana}mp"; }));
            Mod.Log.Info($"[Duelist] spells: {names}");
        }

        // ------------------------------------------------------------------ switches

        static volatile bool s_On, s_Log;
        static double s_CfgNext;
        static void Refresh(double rt)
        {
            if (rt < s_CfgNext) return;
            s_CfgNext = rt + 2.0;
            s_On = PropertyManager.GetBool(P_ON, false).Item;
            s_Log = PropertyManager.GetBool(P_LOG, false).Item;
        }

        /// <summary>True when this creature is the duelist AND his brain is switched on.</summary>
        internal static bool Owns(Creature c)
        {
            if (c == null || c.WeenieClassId != WCID) return false;
            Refresh(Timers.RunningTime);
            return s_On;
        }

        // ------------------------------------------------------------------ per-fight state

        enum Kind : byte { Melee, Archer, Mage }
        enum Mode : byte { None, Kite, LosBreak, Retreat, Kit }

        sealed class St
        {
            public uint Target;
            public Kind Kind;
            public double EngagedAt, ReadAt, NextThink, NextDispel, NextDrain, NextKit, NextKite, NextLos, NextTaunt, NextTrace,
                KitUntil, ModeUntil, AmbushUntil, ClosingSampleAt, NextProtCheck;
            public Mode Mode;
            public int Kits = -1, Retreats, KitHpStart, BuffsSeen, Resisted, BurstCasts, KiteFails;
            public bool Sticky;
            public float MeleeMult = 1f, BoltMult = 1f;   // the last read: how much of a sword hit, and of a bolt, gets through
            public double NextConvert, NextHealSpell, CheckAt;
            public uint Pending;
            public double CursesOffUntil;   // too many resists in a row: he stops cursing for a while and bolts raw
            public readonly Dictionary<uint, int> ResistsOf = new Dictionary<uint, int>();   // a curse he just threw: did it land? (checked a few seconds on, on the target's own enchantments)   // this target sticks to him: kiting and running get him nothing, so he fights with the blade
            public double NextBurst;
            public float HealAt, RetreatAt, ClosingSample = -1f;
            public DamageType ElemMelee = DamageType.Slash, ElemBolt = DamageType.Fire;
            public bool Prebuffed, Feinted, Announced;
            public int OpenTries;
            public WorldObject Wand, Blade;   // the weapon he is not holding right now (a PK's swap)          // the opening: curses thrown before he first goes in for damage (owner 2026-09-29: vuln you first)
            public bool Opened;
            public readonly Queue<(double t, int hp)> Hp = new Queue<(double, int)>();
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
            public string LastAttack = "";
            public bool LastWasMelee; public double TypeSince;   // stance commitment: see ChooseAttack
        }

        static readonly ConditionalWeakTable<Creature, St> s_State = new ConditionalWeakTable<Creature, St>();
        static readonly ConditionalWeakTable<Creature, St>.CreateValueCallback s_NewSt = _ => new St();
        static long s_Decisions, s_Kits, s_KitsSpoiled, s_Retreats, s_LosBreaks, s_Kites, s_Ripostes, s_Dispels, s_Kills, s_Deaths;

        static readonly Action<Creature, Spell> s_SetSpell = BindSetter();
        static readonly Action<Creature, CombatMode> s_SetCombatMode = BindCombatMode();   // Creature.CombatMode's setter is protected
        static Action<Creature, CombatMode> BindCombatMode()
        {
            try
            {
                var m = AccessTools.PropertySetter(typeof(Creature), "CombatMode");
                return m == null ? null : AccessTools.MethodDelegate<Action<Creature, CombatMode>>(m);
            }
            catch { return null; }
        }
        static Action<Creature, Spell> BindSetter()
        {
            try
            {
                var m = AccessTools.PropertySetter(typeof(Creature), "CurrentSpell");
                return m == null ? null : AccessTools.MethodDelegate<Action<Creature, Spell>>(m);
            }
            catch { return null; }
        }

        static float Rand(float a, float b) => a + (float)ThreadSafeRandom.Next(0.0f, 1.0f) * (b - a);
        static float Noise() => Rand(-0.07f, 0.07f);
        static float Frac(CreatureVital v) => v.MaxValue > 0 ? (float)v.Current / v.MaxValue : 1f;

        static void NewFight(Creature c, St st, Player p, double rt)
        {
            st.Target = p.Guid.Full; st.EngagedAt = rt; st.ReadAt = 0; st.Mode = Mode.None;
            st.Kits = (int)Math.Round(D(0)); st.Retreats = 0; st.Feinted = false; st.Announced = false;
            st.HealAt = Rand(0.38f, 0.55f); st.RetreatAt = Rand(0.20f, 0.33f);
            st.NextDispel = rt + Rand(6f, 12f); st.NextDrain = rt + Rand(2f, 5f); st.NextKit = rt; st.NextKite = rt; st.NextLos = rt;
            st.AmbushUntil = 0; st.Hp.Clear(); st.Counts.Clear(); st.Resisted = 0; st.BurstCasts = 0; st.NextBurst = rt; st.KiteFails = 0; st.Sticky = false; st.NextConvert = rt; st.NextHealSpell = rt; st.Pending = 0; st.CursesOffUntil = 0; st.ResistsOf.Clear();
            st.OpenTries = 0; st.Opened = false;
            Mod.Log.Info($"[Duelist] engage t={rt:F1} {c.Name} 0x{c.Guid.Full:X8} on {p.Name} 0x{p.Guid.Full:X8} ({Classify(p)}) at {c.Location.ToLOCString()}");
        }

        /// <summary>What a PK sees: the stance they fight in and what they hold.</summary>
        static Kind Classify(Player p)
        {
            if (p.CombatMode == CombatMode.Missile || p.GetEquippedMissileLauncher() != null) return Kind.Archer;
            if (p.CombatMode == CombatMode.Magic || p.GetEquippedWand() != null) return Kind.Mage;
            return Kind.Melee;
        }

        static readonly DamageType[] s_Types = { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire, DamageType.Cold, DamageType.Acid, DamageType.Electric };

        /// <summary>The assess: for each element, how much of a hit gets through. Melee counts their
        /// armour (ACE's own monster-vs-player armour sum over the chest and the upper legs) times their
        /// resistance; a war bolt ignores armour, so only the resistance counts.</summary>
        static void Read(Creature c, St st, Player p, double rt, bool log)
        {
            st.ReadAt = rt + D(8) * Rand(0.8f, 1.2f);
            var blade = c.GetEquippedMeleeWeapon();
            float bestM = -1f, bestB = -1f; DamageType em = st.ElemMelee, eb = st.ElemBolt;
            var parts = new List<string>();
            foreach (var t in s_Types)
            {
                float resist = p.GetResistanceMod(t, c, blade);
                float armour = 0.5f * (c.GetArmorMod(p, t, c.GetArmorLayers(p, BodyPart.Chest), blade)
                                     + c.GetArmorMod(p, t, c.GetArmorLayers(p, BodyPart.UpperLeg), blade));
                float m = resist * armour;
                if (m > bestM) { bestM = m; em = t; }
                if (resist > bestB) { bestB = resist; eb = t; }
                parts.Add($"{t}:{m:F2}/{resist:F2}");
            }
            bool changed = em != st.ElemMelee;
            st.MeleeMult = bestM; st.BoltMult = bestB;
            st.ElemMelee = em; st.ElemBolt = eb;
            if (blade != null && blade.W_DamageType != em)
            {
                blade.SetProperty(PropertyInt.DamageType, (int)em);
                c.EnqueueBroadcast(new GameMessageEmoteText(c.Guid.Full, c.Name, $"turns his Nexus blade to {ElemWord(em)}."), 40f);
                if (changed && st.Announced) Taunt(c, st, rt, $"{ElemWord(em).Substring(0, 1).ToUpper()}{ElemWord(em).Substring(1)}, is it? You should have looked after that.", force: true);
            }
            if (log || s_Log) Mod.Log.Info($"[Duelist] read t={rt:F1} {p.Name}: melee {em} ({bestM:F2}) bolt {eb} ({bestB:F2}) [{string.Join(" ", parts)}]");
        }

        static string ElemWord(DamageType t) => t switch
        {
            DamageType.Slash => "a razor edge", DamageType.Pierce => "a needle point", DamageType.Bludgeon => "a crushing weight",
            DamageType.Fire => "flame", DamageType.Cold => "frost", DamageType.Acid => "acid", DamageType.Electric => "lightning", _ => t.ToString(),
        };

        // ------------------------------------------------------------------ enchantments he can read

        static int CountBuffs(Creature who, out int life, out int creature)
        {
            life = 0; creature = 0;
            foreach (var e in who.EnchantmentManager.GetEnchantments(MagicSchool.LifeMagic))
                if ((e.StatModType & EnchantmentTypeFlags.Beneficial) != 0 && e.Duration != -1) life++;
            foreach (var e in who.EnchantmentManager.GetEnchantments(MagicSchool.CreatureEnchantment))
                if ((e.StatModType & EnchantmentTypeFlags.Beneficial) != 0 && e.Duration != -1) creature++;
            return life + creature;
        }

        static int CountDebuffs(Creature who)
        {
            int n = 0;
            foreach (var school in new[] { MagicSchool.LifeMagic, MagicSchool.CreatureEnchantment })
                foreach (var e in who.EnchantmentManager.GetEnchantments(school))
                    if ((e.StatModType & EnchantmentTypeFlags.Beneficial) == 0 && e.Duration != -1 && e.SpellId != (int)SpellId.Vitae) n++;
            return n;
        }

        static bool Has(Creature who, uint spell) => who.EnchantmentManager.HasSpell(spell);

        static uint MissingBuff(Creature c, St st)
        {
            foreach (var id in s_CoreBuffs) if (!Has(c, id)) return id;
            var prot = TopIncoming(c);
            if (prot != DamageType.Undef && s_Prot.TryGetValue(prot, out var pid) && !Has(c, pid)) return pid;
            return 0;
        }

        /// <summary>The element the target has hurt him with most, from his own damage history.</summary>
        static DamageType TopIncoming(Creature c)
        {
            try
            {
                var byType = new Dictionary<DamageType, float>();
                foreach (var d in c.DamageHistory.Log.ToArray())
                {
                    if (d.Amount >= 0 || !s_Prot.ContainsKey(d.DamageType)) continue;   // damage is logged as a negative change
                    byType.TryGetValue(d.DamageType, out var v); byType[d.DamageType] = v - d.Amount;
                }
                return byType.Count == 0 ? DamageType.Undef : byType.OrderByDescending(k => k.Value).First().Key;
            }
            catch { return DamageType.Undef; }
        }

        // ------------------------------------------------------------------ the attack brain

        /// <summary>False when the brain chose (ACE's GetNextAttackType skipped, __result set).</summary>
        internal static bool ChooseAttack(Creature c, ref CombatType __result)
        {
            if (!Owns(c) || s_SetSpell == null) return true;
            var p = c.AttackTarget as Player;
            if (p == null || p.IsDead) return true;
            var st = s_State.GetValue(c, s_NewSt);
            double rt = Timers.RunningTime;
            ResolveSpells();
            if (c.CombatTable == null) c.GetCombatTable();   // ACE loads it inside the GetNextAttackType this replaces; without it no swing lands
            if (st.Target != p.Guid.Full) NewFight(c, st, p, rt);
            if (rt >= st.ReadAt) Read(c, st, p, rt, false);

            ResolvePending(c, st, p, rt);

            float hp = Frac(c.Health), mana = Frac(c.Mana);
            float dist = c.GetDistanceToTarget();
            bool see = c.IsDirectVisible(p);
            st.Kind = Classify(p);
            // Against a melee he keeps bolt range, unless it sticks to him and his blade gets through its
            // armour about as well as a bolt does. Against an archer or a mage he closes: past 8 m he runs in
            // (a melee choice is ACE's chase), and inside it he fights with the blade or, if their armour
            // turns the blade, with bolts at arm's length.
            bool bladeWorks = st.MeleeMult >= 0.6f * st.BoltMult;
            // 2026-09-29 (owner: "his attacks aren't that aggressive"): against a melee he now trades blows too while
            // he is above ~55% health, and only falls back to bolts and step-backs once he is hurt.
            bool planMelee = bladeWorks && (st.Kind != Kind.Melee || st.Sticky || hp > 0.55f);
            bool closing = st.Kind != Kind.Melee && dist > 8f;
            var elem = planMelee ? st.ElemMelee : st.ElemBolt;
            var (bolt, vuln) = s_Elem[elem];
            bool vulned = Has(p, vuln), imperiled = Has(p, IMPERIL);
            bool stacked = vulned && (!planMelee || imperiled);

            var opts = new List<(string name, float score, uint spell)>(12);
            void Opt(string n, float s, uint spell = 0)
            {
                if (spell != 0)
                {
                    var sp = Sp(spell);
                    if (sp.NotFound || sp.BaseMana > c.Mana.Current) return;           // cannot pay: not an option
                    if (!sp.IsSelfTargeted && !see) return;                              // no line of sight, no cast
                }
                opts.Add((n, s + Noise(), spell));
            }

            // look after himself
            var missing = MissingBuff(c, st);
            int myBuffs = CountBuffs(c, out _, out _);
            if (missing != 0) Opt("rebuff", Math.Min(0.85f, 0.55f + 0.06f * Math.Max(0, st.BuffsSeen - myBuffs)), missing);
            int myDebuffs = CountDebuffs(c);
            if (myDebuffs >= 2) Opt("cleanse", 0.62f + 0.06f * myDebuffs, CLEANSE_SELF);
            if (mana < 0.25f && Frac(c.Stamina) > 0.4f && rt >= st.NextConvert) Opt("mana convert", 0.72f + (0.25f - mana), STAM_TO_MANA);
            if (hp < st.HealAt && rt >= st.NextHealSpell) Opt("heal", 0.70f + (st.HealAt - hp) * 1.5f - (dist < 3 && st.Kind == Kind.Melee ? 0.25f : 0f), s_Heal);

            // take theirs away
            int theirs = CountBuffs(p, out int tLife, out int tCreature);
            if (rt >= st.NextDispel && theirs >= (int)D(6))
                Opt("dispel", Math.Min(0.86f, 0.52f + 0.035f * theirs), tLife >= tCreature ? DISPEL_LIFE : DISPEL_CREATURE);
            if (rt >= st.NextDrain)
            {
                if (st.Kind == Kind.Mage) Opt("drain mana", 0.40f + 0.35f * Frac(p.Mana) + (mana < 0.35f ? 0.2f : 0f), DRAIN_MANA);
                else Opt("drain stamina", 0.36f + 0.32f * Frac(p.Stamina), DRAIN_STAM);
            }

            // the stack, then the burst
            // curses that keep being resisted lose their appeal; after two, he opens with Futility first
            // and after five in a row he stops cursing for half a minute and bolts raw
            bool cursing = rt >= st.CursesOffUntil;
            float Penalty(uint id) => Math.Min(0.45f, 0.15f * (st.ResistsOf.TryGetValue(id, out var k) ? k : 0));
            // THE OPENING (owner 2026-09-29: "varrow should vuln you before he goes in to do damage"): from where
            // he stands, the element's vulnerability and (for the blade) Imperil, then he goes in. Three throws
            // at most, so a well-warded opponent does not stall him; he re-curses later as the stack drops.
            if (!st.Opened && (stacked || st.OpenTries >= 3 || !cursing)) st.Opened = true;
            bool opening = !st.Opened;
            // in reach with the blade, curses lose to the swing unless the stack is down
            float inReach = planMelee && dist < 4f && st.Opened ? 0.18f : 0f;
            if (cursing)
            {
                if (st.Resisted >= 2 && !Has(p, FUTILITY)) Opt("magic yield", 0.86f - Penalty(FUTILITY) - inReach, FUTILITY);
                if (!vulned) Opt("vuln", (opening ? 0.97f : 0.74f + (hp > 0.6f ? 0.06f : 0f)) - Penalty(vuln) - (opening ? 0f : inReach * 0.5f), vuln);
                if (planMelee && !imperiled) Opt("imperil", (opening && vulned ? 0.96f : 0.66f) - Penalty(IMPERIL) - (opening ? 0f : inReach), IMPERIL);
            }
            else if (rt >= st.NextBurst && !planMelee) Opt("raw burst", 0.70f, bolt);
            if (stacked && rt >= st.NextBurst)   // bursts come in waves of two, then he works the stack again
            {
                if (planMelee) opts.Add(("burst melee", 0.90f + Noise(), 0));
                else Opt("burst bolt", 0.88f, bolt);
            }
            // the plain exchange
            if (closing) opts.Add(("close in", (opening ? 0.60f : 0.82f) + Noise(), 0));
            else if (planMelee || dist < 3.5f && bladeWorks) opts.Add(("melee", (planMelee ? (dist < 4f ? 0.80f : 0.72f) : 0.60f) + Noise(), 0));
            else Opt("bolt", 0.62f, s_Bolt6[elem]);

            if (opts.Count == 0) opts.Add(("melee", 0.1f, 0));
            opts.Sort((a, b) => b.score.CompareTo(a.score));
            var pick = opts[0];

            // STANCE COMMITMENT (owner playtest 2026-09-29: "kept going in and out of combat mode and not
            // attacking me ... his animations just seem to not be playing"). Every blade/spell flip is a
            // stance change with its own animation, so flipping on every decision left him switching
            // stances instead of swinging. Within 4 s of a switch he keeps the current kind of attack if
            // an option of that kind scores within 0.2 of the best; heals, dispels, cleanses and rebuffs
            // (the urgent ones) still switch at once.
            bool urgent = pick.name == "heal" || pick.name == "dispel" || pick.name == "cleanse" || pick.name == "rebuff";
            if (!urgent && rt - st.TypeSince < 4.0 && (pick.spell == 0) != st.LastWasMelee)
            {
                foreach (var o in opts)
                    if ((o.spell == 0) == st.LastWasMelee && pick.score - o.score <= 0.12f) { pick = o; break; }
            }
            bool meleeNow = pick.spell == 0;
            if (meleeNow != st.LastWasMelee) { st.LastWasMelee = meleeNow; st.TypeSince = rt; }

            if (pick.spell == 0)
            {
                __result = CombatType.Melee;
                // A human caster (AiUseHumanMagicAnimations) is left in the Magic stance by its last spell,
                // and ACE's MeleeAttack only raises the melee stance from NonCombat: from Magic there is no
                // combat maneuver, the swing returns 0 and nothing plays. Raise the blade stance here.
                // and since 2026-09-29 the blade comes back out of his pack first (EnterBladeStance)
                if (c.CombatMode != CombatMode.Melee || c.GetEquippedWand() != null) EnterBladeStance(c, st);
            }
            else
            {
                s_SetSpell(c, Sp(pick.spell));
                __result = CombatType.Magic;
                // A spell from the blade stance plays no gesture at all: the human motion table (0x09000001) has
                // its cast gestures only under the Magic stance, and ACE's human pre-cast then falls back to a
                // CastSpell the table does not hold in SwordCombat (the client showed his peace idle for it:
                // owner 2026-09-29, "he healed and didn't show a spellcasting movement ... fighting stance and
                // then peace mode on a loop"). ACE raises Magic only with a wand wielded; he duels with a sword,
                // so raise the stance ourselves, as a PK drops to the casting stance.
                EnterMagicStance(c, st);
                if (pick.name == "dispel") { st.NextDispel = rt + D(5) * Rand(0.85f, 1.25f); Interlocked.Increment(ref s_Dispels); }
                if (pick.name.StartsWith("drain")) st.NextDrain = rt + D(7) * Rand(0.8f, 1.3f);
                if (pick.name == "mana convert") st.NextConvert = rt + Rand(22f, 32f);
                if (pick.name == "heal") { st.HealAt = Rand(0.34f, 0.52f); st.NextHealSpell = rt + Rand(10f, 15f); }   // the next heal comes at a different mark, and not soon
                if (pick.name == "rebuff") st.BuffsSeen = Math.Max(st.BuffsSeen, myBuffs + 1);
            }
            if (pick.name == "vuln" || pick.name == "imperil" || pick.name == "magic yield")
            {
                st.Pending = pick.spell; st.CheckAt = rt + 2.5;   // the windup is ~1.5 s at ACE's PreCastSpeed 2
                if (opening) st.OpenTries++;
            }
            if ((pick.name.StartsWith("burst") || pick.name == "raw burst") && ++st.BurstCasts >= 2) { st.BurstCasts = 0; st.NextBurst = rt + D(15) * Rand(0.8f, 1.25f); }
            Interlocked.Increment(ref s_Decisions);
            st.Counts.TryGetValue(pick.name, out var n); st.Counts[pick.name] = n + 1;
            st.LastAttack = pick.name;
            if (s_Log || rt < s_TraceUntil)
                Mod.Log.Info($"[Duelist] decide t={rt:F1} tgt={p.Name} kind={st.Kind} plan={(planMelee ? "melee" : "bolt")} elem={elem} hp={hp:F2} mana={mana:F2} dist={dist:F1} see={see} theirBuffs={theirs} myDebuffs={myDebuffs} -> {pick.name}{(pick.spell != 0 ? $" ({Sp(pick.spell).Name})" : "")} {pick.score:F2} | {string.Join(", ", opts.Skip(1).Take(3).Select(o => $"{o.name} {o.score:F2}"))}");

            // a line for the moments a PK would have spoken
            if (pick.name == "dispel") Taunt(c, st, rt, Pick("Those buffs looked expensive.", "Naked again. Rebuff, if you have the mana.", "Nullified. Try to keep up."));
            else if (pick.name == "burst melee" || pick.name == "burst bolt") Taunt(c, st, rt, Pick("Stacked and open. Goodbye.", "Every hole in your armour, and I have found them all.", "That is what imperil is for."));
            else if (pick.name == "drain mana") Taunt(c, st, rt, Pick("Your mana tastes of fear.", "Out of mana, mage? Pity."));
            else if (pick.name == "drain stamina") Taunt(c, st, rt, Pick("Tired already?", "No stamina, no swing."));
            else if (pick.name == "cleanse") Taunt(c, st, rt, Pick("Your curses do not stick to me.", "Cute debuffs. Gone."));
            else if (pick.name == "rebuff" && st.BuffsSeen - myBuffs >= 2) Taunt(c, st, rt, Pick("You dispelled me? Adorable.", "Buffs are cheap. I brought spares."));
            return false;
        }

        /// <summary>Did the curse he threw land? Read off the target's own enchantments once its windup is over.
        /// Called from both brains: before 2026-09-29 only the tick read it, and each new decision pushed the
        /// check 5 s on, so a curse thrown every 3 s was never scored (Futility on repeat, 20 times running).</summary>
        static void ResolvePending(Creature c, St st, Player p, double rt)
        {
            if (st.Pending == 0 || rt < st.CheckAt) return;
            bool landed = Has(p, st.Pending);
            st.Resisted = landed ? 0 : st.Resisted + 1;
            st.ResistsOf.TryGetValue(st.Pending, out var k); st.ResistsOf[st.Pending] = landed ? 0 : k + 1;
            var sp = Sp(st.Pending);
            if (s_Log || rt < s_TraceUntil) Mod.Log.Info($"[Duelist] curse t={rt:F1} {sp.Name} on {p.Name}: {(landed ? "landed" : $"resisted ({st.Resisted} in a row)")}, his {sp.School} {c.GetCreatureSkill(sp.School).Current} vs their magic defense {p.GetEffectiveMagicDefense()}{(p.Invincible ? " (target is @invincible: ACE resists every spell on it)" : "")}");
            if (st.Resisted >= 5)
            {
                st.CursesOffUntil = rt + Rand(25f, 35f); st.Resisted = 0; st.ResistsOf.Clear();
                Mod.Log.Info($"[Duelist] adapt t={rt:F1} {p.Name} resists everything: no curses for a while, raw bolts");
                Taunt(c, st, rt, Pick("Warded to the teeth, are we? Then I will simply burn it off you.", "Resist this, then."), force: true);
            }
            st.Pending = 0;
        }

        static readonly Func<Creature, WorldObject, EquipMask, bool> s_Wield = BindWield();   // Creature.TryWieldObjectWithBroadcasting is protected
        static Func<Creature, WorldObject, EquipMask, bool> BindWield()
        {
            try
            {
                var m = AccessTools.Method(typeof(Creature), "TryWieldObjectWithBroadcasting", new[] { typeof(WorldObject), typeof(EquipMask) });
                return m == null ? null : AccessTools.MethodDelegate<Func<Creature, WorldObject, EquipMask, bool>>(m);
            }
            catch { return null; }
        }

        /// <summary>A PK's swap to the caster (owner 2026-09-29: "varrow is casting spells with his sword in hand
        /// instead of his caster"). The blade goes to his pack, the Shadownether Isparian Wand into his hand,
        /// then ACE's own SetCombatMode(Magic), which with a wand wielded is the retail stance change. ACE's
        /// monster weapon swap (Monster_Missile.SwitchToMeleeAttack) is the pattern: unwield with broadcast,
        /// inventory, wield with broadcast (ObjDesc and parent events), stance. The wand is made here, once per
        /// Varrow, and never enters his create list or death treasure, so it cannot drop.</summary>
        static void EnterMagicStance(Creature c, St st)
        {
            double rt = Timers.RunningTime;
            var from = c.CurrentMotionState?.Stance ?? MotionStance.NonCombat;
            if (c.GetEquippedWand() == null && s_Wield != null)
            {
                if (st.Wand == null || st.Wand.IsDestroyed) st.Wand = WorldObjectFactory.CreateNewWorldObject(WAND);
                if (st.Wand != null)
                {
                    var blade = c.GetEquippedMeleeWeapon(true);
                    if (blade != null && c.TryUnwieldObjectWithBroadcasting(blade.Guid, out _, out _))
                    {
                        st.Blade = blade;
                        c.TryAddToInventory(blade);
                    }
                    c.TryRemoveFromInventory(st.Wand.Guid);
                    if (!s_Wield(c, st.Wand, EquipMask.Held) && st.Blade != null)
                    {
                        c.TryRemoveFromInventory(st.Blade.Guid);   // no wand after all: the blade goes back
                        s_Wield(c, st.Blade, st.Blade.ValidLocations ?? EquipMask.MeleeWeapon);
                    }
                }
            }
            float len;
            if (c.GetEquippedWand() != null)
                len = c.SetCombatMode(CombatMode.Magic);   // HandleSwitchToMagicCombatMode: the wand's own stance change
            else
            {
                // fallback (no wand could be made): the casting stance with the sword in hand, as before
                len = MotionTable.GetAnimationLength(c.MotionTableId, from, MotionCommand.Ready, MotionCommand.Magic);
                s_SetCombatMode?.Invoke(c, CombatMode.Magic);
                if (from != MotionStance.Magic) c.ExecuteMotionPersist(new Motion(MotionStance.Magic));
            }
            if (len > 0)
            {
                c.NextMoveTime = Math.Max(c.NextMoveTime, rt) + len;
                c.NextAttackTime = Math.Max(c.NextAttackTime, rt) + len;
            }
            if (s_Log || rt < s_TraceUntil) Mod.Log.Info($"[Duelist] stance t={rt:F1} {from} -> Magic ({len:F2} s, {(c.GetEquippedWand() != null ? "wand" : "no wand")})");
        }

        /// <summary>The swap back: wand to the pack, blade to the hand, then ACE's DoAttackStance.</summary>
        static void EnterBladeStance(Creature c, St st)
        {
            var wand = c.GetEquippedWand();
            if (wand != null && c.TryUnwieldObjectWithBroadcasting(wand.Guid, out _, out _))
            {
                st.Wand = wand;
                c.TryAddToInventory(wand);
            }
            if (c.GetEquippedMeleeWeapon(true) == null && s_Wield != null)
            {
                var blade = st.Blade ?? c.Inventory.Values.FirstOrDefault(i => i.WeenieClassId == BLADE);
                if (blade != null)
                {
                    c.TryRemoveFromInventory(blade.Guid);
                    if (s_Wield(c, blade, blade.ValidLocations ?? EquipMask.MeleeWeapon)) st.Blade = null;
                }
            }
            if (c.CombatMode != CombatMode.Melee || (c.CurrentMotionState?.Stance ?? MotionStance.NonCombat) != c.GetCombatStance()) c.DoAttackStance();
        }

        // ------------------------------------------------------------------ the movement brain

        /// <summary>Returns false when this tick was taken (ACE's Monster_Tick then skipped).</summary>
        internal static bool Tick(Creature c, double now)
        {
            if (c.PhysicsObj == null || c.Location == null || !Owns(c)) return true;
            double rt = Timers.RunningTime;
            var st = s_State.GetValue(c, s_NewSt);
            if (rt < s_TraceUntil && c.IsAwake) Trace(c, st, rt);
            if (!c.IsAwake || c.IsDead || c.MonsterState == Creature.State.Return) { st.Mode = Mode.None; return true; }

            var p = c.AttackTarget as Player;
            if (p == null || p.PhysicsObj == null || p.Location == null) { if (st.Mode != Mode.None) EndMode(c, st); return true; }
            if (st.Target != p.Guid.Full) NewFight(c, st, p, rt);
            if (p.IsDead)
            {
                if (st.EngagedAt > 0)
                {
                    Interlocked.Increment(ref s_Kills);
                    Mod.Log.Info($"[Duelist] result t={rt:F1} {c.Name} 0x{c.Guid.Full:X8} DEFEATED {p.Name} ({st.Kind}) after {rt - st.EngagedAt:F1} s, his health {Frac(c.Health):P0}, kits left {st.Kits}, retreats {st.Retreats}; attacks {Counts(st)}");
                    Taunt(c, st, rt, Pick("Good fight. Check your vitae.", "Come back when your vitae is off.", "Another name for the list."), force: true);
                    st.EngagedAt = 0;
                }
                return true;
            }

            // prebuffed: a PK walks into a duel buffed (no animation; this is the state he spawned in)
            if (!st.Prebuffed)
            {
                st.Prebuffed = true;
                foreach (var id in s_CoreBuffs) { var sp = Sp(id); if (!sp.NotFound) c.TryCastSpell(sp, c, c, null, false, false, false); }
                st.BuffsSeen = CountBuffs(c, out _, out _);
            }
            if (!st.Announced && c.GetDistanceToTarget() < 30f)
            {
                st.Announced = true;
                Taunt(c, st, rt, Pick($"Another lamb for the altar. Buffed, {p.Name}? I will wait.", "The Nexus sent me a new opponent. How kind.", "Darktide rules. No healers, no mercy, no excuses."), force: true);
            }

            // health log: what the last four seconds cost him
            st.Hp.Enqueue((rt, (int)c.Health.Current));
            while (st.Hp.Count > 0 && rt - st.Hp.Peek().t > 4.0) st.Hp.Dequeue();
            float lost4 = st.Hp.Count > 0 ? Math.Max(0, st.Hp.Peek().hp - (int)c.Health.Current) / (float)Math.Max(1u, c.Health.MaxValue) : 0f;

            // a healing kit in his hands: he stands still for it
            if (st.Mode == Mode.Kit)
            {
                if (rt < st.KitUntil) { c.NextMonsterTickTime = now + 0.2; return false; }
                st.Mode = Mode.None; c.ResetAttack();
                return true;
            }
            // a move is running: keep the tick until it arrives or runs out of time
            if (st.Mode != Mode.None)
            {
                if (rt < st.ModeUntil && c.PhysicsObj.IsMovingTo()) { c.NextMonsterTickTime = now + 0.2; return false; }
                var was = st.Mode;
                EndMode(c, st);
                if (was == Mode.Kite && !st.Sticky)
                {
                    float after = c.GetDistanceToTarget();
                    if (after < 2.5f && ++st.KiteFails >= 2)
                    {
                        st.Sticky = true;
                        bool blade = st.MeleeMult >= 0.6f * st.BoltMult;
                        Mod.Log.Info($"[Duelist] adapt t={rt:F1} {p.Name} stays on him after {st.KiteFails} step-backs: no more kiting; {(blade ? "the blade" : "bolts at arm's length (the blade does not get through)")} instead");
                        Taunt(c, st, rt, blade ? Pick("You stick like a Lugian. Fine. Blades, then.", "Cannot shake you? Then stand and bleed.") : Pick("Stick to me, then. I can bolt you from here.", "Cannot shake you? Then stand and burn."), force: true);
                    }
                }
                if (was == Mode.Retreat || was == Mode.LosBreak)
                {
                    st.AmbushUntil = rt + Rand(5f, 8f);   // now wait for whoever runs round the corner
                    st.ClosingSample = -1f;
                    if (Frac(c.Health) < st.HealAt + 0.1f && st.Kits > 0 && rt >= st.NextKit) { StartKit(c, st, p, rt, "after " + was); c.NextMonsterTickTime = now + 0.2; return false; }
                }
                return true;
            }
            ResolvePending(c, st, p, rt);
            if (c.EmoteManager.IsBusy || c.IsAnimating || rt < st.NextThink) return true;
            st.NextThink = rt + 0.4;
            st.Kind = Classify(p);

            float hp = Frac(c.Health);
            float dist = c.GetDistanceToTarget();
            bool see = c.IsDirectVisible(p);

            // how fast they are closing on him, sampled over 1.5 s: a careless chase is a straight run in
            if (rt >= st.ClosingSampleAt) { st.ClosingSample = dist; st.ClosingSampleAt = rt + 1.5; }

            // THE PUNISH: a chaser who comes round the corner straight onto him eats a riposte
            if (rt < st.AmbushUntil && dist < 3.2f && st.ClosingSample > 0 && st.ClosingSample - dist > 4.0f)
            {
                st.AmbushUntil = 0;
                Riposte(c, st, p, rt);
                c.NextMonsterTickTime = now + 0.2;
                return false;
            }

            var opts = new List<(string name, float score)>(6);
            if (st.Retreats < (int)D(4) && !st.Sticky)
            {
                if (hp < st.RetreatAt) opts.Add(("retreat", 0.95f + Noise()));
                else if (!st.Feinted && hp < 0.65f && st.Kind == Kind.Melee && dist < 4f && ThreadSafeRandom.Next(0.0f, 1.0f) < 0.08f)
                    opts.Add(("feint", 0.60f + Noise()));   // a bait: run while he is still healthy, turn on the chaser
            }
            if (st.Kind != Kind.Melee && rt >= st.NextLos && see && (lost4 > 0.10f || hp < st.HealAt && st.Kits > 0))
                opts.Add(("break los", 0.78f + Math.Min(0.15f, lost4) + Noise()));
            // healthy, he stands and trades (the 2026-09-29 "press harder"); the step-back is for when he is hurt
            if (st.Kind == Kind.Melee && !st.Sticky && hp < 0.6f && dist < 3.2f && rt >= st.NextKite && st.LastAttack != "burst melee")
                opts.Add(("kite", 0.62f + (hp < 0.5f ? 0.1f : 0f) + Noise()));
            if (hp < st.HealAt && st.Kits > 0 && rt >= st.NextKit)
            {
                bool safe = !see || dist > 12f || lost4 < 0.04f;
                opts.Add(("kit", (safe ? 0.84f : 0.42f) + (st.HealAt - hp) + Noise()));
            }
            if (opts.Count == 0) return true;
            opts.Sort((a, b) => b.score.CompareTo(a.score));
            var pick = opts[0];
            if (pick.score < 0.5f) return true;

            bool took = false;
            switch (pick.name)
            {
                case "retreat":
                case "feint":
                    st.Retreats++; if (pick.name == "feint") st.Feinted = true;
                    st.RetreatAt = Rand(0.15f, 0.28f);
                    took = HidePoint(c, p, out var hideR, minFromTarget: 12f) ? MoveTo(c, st, hideR, p, Mode.Retreat, 6.0) : StepAway(c, st, p, 16f, Mode.Retreat, 6.0);
                    if (took)
                    {
                        Interlocked.Increment(ref s_Retreats);
                        Taunt(c, st, rt, pick.name == "feint" ? Pick("Catch me, if you are quick.", "Come on then. Chase me.") : Pick("Not today.", "You will have to come and get me.", "Chase me. Everyone does."), force: true);
                    }
                    break;
                case "break los":
                    st.NextLos = rt + D(10) * Rand(0.8f, 1.3f);
                    if (HidePoint(c, p, out var hide, minFromTarget: 6f)) took = MoveTo(c, st, hide, p, Mode.LosBreak, 5.0);
                    if (took) { Interlocked.Increment(ref s_LosBreaks); Taunt(c, st, rt, Pick("Shoot the pillar, then.", "No line, no damage.", "Can you see me now?")); }
                    break;
                case "kite":
                    st.NextKite = rt + D(9) * Rand(0.8f, 1.3f);
                    took = StepAway(c, st, p, Rand(7f, 10f), Mode.Kite, 4.0);
                    if (took) { Interlocked.Increment(ref s_Kites); Taunt(c, st, rt, Pick("Too slow. You swing like a Lugian.", "Reach for me. Go on.", "You cannot hit what you cannot catch.")); }
                    break;
                case "kit":
                    StartKit(c, st, p, rt, "low health"); took = true;
                    break;
            }
            if (s_Log || rt < s_TraceUntil)
                Mod.Log.Info($"[Duelist] move t={rt:F1} tgt={p.Name} kind={st.Kind} hp={hp:F2} lost4={lost4:F2} dist={dist:F1} see={see} -> {pick.name} {pick.score:F2}{(took ? "" : " (no room)")} | {string.Join(", ", opts.Skip(1).Select(o => $"{o.name} {o.score:F2}"))}");
            if (took) { st.Counts.TryGetValue(pick.name, out var n); st.Counts[pick.name] = n + 1; c.NextMonsterTickTime = now + 0.2; return false; }
            return true;
        }

        static string Counts(St st) => string.Join(", ", st.Counts.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));

        // ------------------------------------------------------------------ kit and riposte

        static void StartKit(Creature c, St st, Player p, double rt, string why)
        {
            if (c.IsMoving || c.PhysicsObj.IsMovingTo()) MonsterAi.StopMoving(c);
            st.Kits--; st.NextKit = rt + D(2) * Rand(0.85f, 1.35f);
            st.HealAt = Rand(0.34f, 0.52f);
            st.KitHpStart = (int)c.Health.Current;
            var stance = c.CurrentMotionState?.Stance ?? MotionStance.NonCombat;
            float len = MotionTable.GetAnimationLength(c.MotionTableId, stance, MotionCommand.SkillHealSelf);
            if (len <= 0.1f || len > 5f) len = 1.8f;
            c.EnqueueBroadcastMotion(new Motion(c, MotionCommand.SkillHealSelf));
            st.Mode = Mode.Kit; st.KitUntil = rt + len;
            c.NextMoveTime = c.NextAttackTime = rt + len + 0.2;
            Interlocked.Increment(ref s_Kits);
            float heal = (float)D(1), brk = (float)D(3);
            var chain = new ActionChain();
            chain.AddDelaySeconds(len);
            chain.AddAction(c, () =>
            {
                if (c.IsDead) return;
                int took = st.KitHpStart - (int)c.Health.Current;
                if (took > brk * c.Health.MaxValue)
                {
                    Interlocked.Increment(ref s_KitsSpoiled);
                    c.EnqueueBroadcast(new GameMessageEmoteText(c.Guid.Full, c.Name, "fumbles his healing kit as the blows land!"), 40f);
                    Mod.Log.Info($"[Duelist] kit spoiled t={Timers.RunningTime:F1} 0x{c.Guid.Full:X8}: took {took} during the kit");
                    return;
                }
                int amount = (int)Math.Round(heal * c.Health.MaxValue * Rand(0.85f, 1.1f));
                int before = (int)c.Health.Current;
                c.UpdateVitalDelta(c.Health, amount);
                c.EnqueueBroadcast(new GameMessageEmoteText(c.Guid.Full, c.Name, "binds his wounds with a healing kit."), 40f);
                Mod.Log.Info($"[Duelist] kit t={Timers.RunningTime:F1} 0x{c.Guid.Full:X8} ({why}): health {before} -> {c.Health.Current} of {c.Health.MaxValue}, {st.Kits} kits left");
            });
            chain.EnqueueChain();
            if (st.Kits == 1) Taunt(c, st, rt, "Last kit. Make it count, or do not.");
            else Taunt(c, st, rt, Pick("Kits are cheap. Your vitae is not.", "One moment. I am not done with you.", "Patience. I will be right with you."));
        }

        /// <summary>A double strike on whoever ran straight onto him: an ordinary ACE damage roll
        /// (dodgeable, armour applies), then a second hit riposte_mult times as hard.</summary>
        static void Riposte(Creature c, St st, Player p, double rt)
        {
            Interlocked.Increment(ref s_Ripostes);
            if (c.IsMoving || c.PhysicsObj.IsMovingTo()) MonsterAi.StopMoving(c);
            c.Rotate(p);
            Taunt(c, st, rt, Pick("Ran straight onto my blade. They always do.", "Never chase a duelist round a corner.", "Careless."), force: true);
            var blade = c.GetEquippedMeleeWeapon();
            float mult = (float)D(11);
            c.EnqueueBroadcastMotion(new Motion(c, MotionCommand.DoubleSlashHigh));
            DamageEvent first = null;
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.3);
            chain.AddAction(c, () =>
            {
                if (c.IsDead || p.IsDead) return;
                first = DamageEvent.CalculateDamage(c, p, blade, MotionCommand.SlashHigh);
                if (!first.HasDamage) { p.OnEvade(c, CombatType.Melee); first = null; return; }
                p.TakeDamage(c, first);
            });
            chain.AddDelaySeconds(0.45);
            chain.AddAction(c, () =>
            {
                if (first == null || c.IsDead || p.IsDead || c.GetDistance(p) > 4f) return;
                p.TakeDamage(c, first.DamageType, first.Damage * mult, first.BodyPart, first.IsCritical);
                Mod.Log.Info($"[Duelist] riposte t={Timers.RunningTime:F1} 0x{c.Guid.Full:X8} on {p.Name}: {first.Damage:F0} + {first.Damage * mult:F0} {first.DamageType}");
            });
            chain.EnqueueChain();
            c.PrevAttackTime = rt;
            c.NextMoveTime = rt + 0.9; c.NextAttackTime = rt + 0.8;   // was 1.3 / 1.2 (owner 2026-09-29: faster)
        }

        // ------------------------------------------------------------------ movement

        static void EndMode(Creature c, St st)
        {
            if (c.PhysicsObj.IsMovingTo()) MonsterAi.StopMoving(c);
            st.Mode = Mode.None;
            c.ResetAttack();   // ACE asks GetNextAttackType again, and turns to the target
        }

        static bool StepAway(Creature c, St st, Creature target, float metres, Mode mode, double seconds)
        {
            if (!MonsterAi.Frame(c, target, out var me, out var tg)) return false;
            var away = me - tg; away.Z = 0;
            if (away.LengthSquared() < 0.01f) away = new Vector3(0, 1, 0);
            away = Vector3.Normalize(away);
            var home = c.GetPosition(PositionType.Home);   // lean back towards home past half his leash
            if (home != null && (!c.Location.Indoors || home.Landblock == c.Location.Landblock))
            {
                var h = c.Location.Indoors ? home.Pos : home.ToGlobal();
                var toHome = h - me; toHome.Z = 0;
                float hd = toHome.Length(), leash = (float)(c.HomeRadius ?? 60.0);
                if (hd > leash * 0.5f) away = Vector3.Normalize(away + toHome / hd);
            }
            foreach (var a in new[] { 0f, 35f, -35f, 70f, -70f })
            {
                var dest = me + MonsterAi.Rotate(away, a) * metres; dest.Z = me.Z;
                if (MoveTo(c, st, dest, target, mode, seconds)) return true;
            }
            return false;
        }

        /// <summary>A spot behind the nearest arena pillar as seen from the target: the pillar between
        /// them, and the spot out of the target's line of sight (tested, not assumed).</summary>
        static bool HidePoint(Creature c, Player p, out Vector3 dest, float minFromTarget)
        {
            dest = default;
            if (c.PhysicsObj.CurCell == null || !MonsterAi.Frame(c, p, out var me, out var tg)) return false;
            float best = float.MaxValue;
            foreach (var o in c.PhysicsObj.ObjMaint.GetVisibleObjects(c.PhysicsObj.CurCell))
            {
                var w = o?.WeenieObj?.WorldObject;
                if (w == null || w is Creature && w.WeenieClassId != PILLAR || w.Location == null) continue;
                if (w.WeenieClassId != PILLAR && !(w.Name ?? "").Contains("Pillar") && !(w.Name ?? "").Contains("Monolith") && !(w.Name ?? "").Contains("Column")) continue;
                if (!MonsterAi.Frame(c, w, out _, out var pp)) continue;
                var fromT = pp - tg; fromT.Z = 0;
                float ft = fromT.Length();
                if (ft < 1f || ft > 40f) continue;
                float r = 1.2f * (w.ObjScale ?? 1f) + 1.6f;
                var spot = pp + fromT / ft * r; spot.Z = me.Z;
                float fromTarget = Vector2.Distance(new Vector2(spot.X, spot.Y), new Vector2(tg.X, tg.Y));
                if (fromTarget < minFromTarget) continue;
                float travel = Vector2.Distance(new Vector2(spot.X, spot.Y), new Vector2(me.X, me.Y));
                if (travel > 30f || travel >= best) continue;
                var pos = c.Location.FromGlobal(spot);
                if (pos == null || p.IsDirectVisible(pos)) continue;   // they would still see him there
                best = travel; dest = spot;
            }
            return best < float.MaxValue;
        }

        static bool MoveTo(Creature c, St st, Vector3 dest, Creature face, Mode mode, double seconds)
        {
            var pos = c.Location.FromGlobal(dest);
            if (pos == null || pos.LandblockId.Raw == 0) return false;
            if (pos.Indoors && ((pos.LandblockId.Raw & 0xFFFF) < 0x100 || !c.IsDirectVisible(pos))) return false;
            if (MonsterAi.Frame(c, face, out _, out var tg)) { var f = tg - dest; f.Z = 0; if (f.LengthSquared() > 0.01f) pos.Rotate(f); }
            if (c.IsMoving || c.PhysicsObj.IsMovingTo()) MonsterAi.StopMoving(c);
            c.RunRate = c.GetRunRate();   // re-read: a Leaden Feet on him slows the retreat, as it should
            c.MoveTo(pos, c.RunRate > 0 ? c.RunRate : 1f, true, 1.0f);
            st.Mode = mode;
            st.ModeUntil = Timers.RunningTime + seconds;
            return true;
        }

        // ------------------------------------------------------------------ voice

        static string Pick(params string[] lines) => lines[ThreadSafeRandom.Next(0, lines.Length - 1)];

        static void Taunt(Creature c, St st, double rt, string text, bool force = false)
        {
            if (!force && rt < st.NextTaunt) return;
            st.NextTaunt = rt + D(12) * Rand(0.8f, 1.5f);
            c.EnqueueBroadcast(new GameMessageHearSpeech(text, c.Name, c.Guid.Full, ChatMessageType.Speech), 60f);
            if (s_Log || rt < s_TraceUntil) Mod.Log.Info($"[Duelist] says t={rt:F1}: {text}");
        }

        internal static void OnDeath(Creature c, DamageHistoryInfo killer)
        {
            if (c?.WeenieClassId != WCID || !s_State.TryGetValue(c, out var st)) return;
            double rt = Timers.RunningTime;
            Interlocked.Increment(ref s_Deaths);
            var who = killer?.TryGetPetOwnerOrAttacker();
            Mod.Log.Info($"[Duelist] result t={rt:F1} {c.Name} 0x{c.Guid.Full:X8} SLAIN by {who?.Name ?? "?"} ({(who is Player pl ? Classify(pl).ToString() : "-")}) after {(st.EngagedAt > 0 ? rt - st.EngagedAt : 0):F1} s; kits left {st.Kits}, retreats {st.Retreats}; attacks {Counts(st)}");
            c.EnqueueBroadcast(new GameMessageHearSpeech("The Nexus... remembers. I will be waiting.", c.Name, c.Guid.Full, ChatMessageType.Speech), 60f);
            st.EngagedAt = 0;
        }

        // ------------------------------------------------------------------ trace and @duelist

        static double s_TraceUntil;

        static void Trace(Creature c, St st, double rt)
        {
            if (rt < st.NextTrace) return;
            st.NextTrace = rt + 0.5;
            var p = c.AttackTarget as Creature;
            var g = c.Location.Indoors ? c.Location.Pos : c.Location.ToGlobal();
            string d = p != null && !p.IsDead ? c.GetDistanceToTarget().ToString("F1") : "-";
            Mod.Log.Info($"[Duelist] trace t={rt:F1} {c.Name} state={c.MonsterState} mode={st.Mode} atk={c.CurrentAttack} last={st.LastAttack} dist={d} hp={100f * Frac(c.Health):F0} mana={100f * Frac(c.Mana):F0} kits={st.Kits} x={g.X:F1} y={g.Y:F1} tgt={p?.Name ?? "-"} tgthp={(p != null ? 100f * Frac(p.Health) : 0):F0}");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Mod.Log.Info(text);
        }

        static void Handle(Session session, params string[] p)
        {
            double rt = Timers.RunningTime;
            s_CfgNext = 0; Refresh(rt);
            ResolveSpells();
            if (p.Length > 0 && p[0].Equals("trace", StringComparison.OrdinalIgnoreCase))
            {
                if (p.Length > 1 && p[1].Equals("off", StringComparison.OrdinalIgnoreCase)) { s_TraceUntil = 0; Say(session, "[Duelist] trace off"); return; }
                double secs = p.Length > 1 && double.TryParse(p[1], out var sv) ? Math.Clamp(sv, 5, 1800) : 120;
                s_TraceUntil = rt + secs;
                Mod.Log.Info($"[Duelist] trace start t={rt:F1} for {secs:F0} s");
                Say(session, $"[Duelist] tracing his decisions and position for {secs:F0} s into ACE_Log.txt (ours, not retail)");
                return;
            }
            string verb = p.Length > 0 ? p[0].ToLowerInvariant() : "status";
            if (verb == "go")
            {
                if (session?.Player == null) { Say(session, "[Duelist] needs a player"); return; }
                session.Player.Teleport(new ACE.Entity.Position(ArenaEdge));
                Say(session, "[Duelist] to the arena's south edge; walk north to meet him (ours, not retail)");
                return;
            }
            if (verb == "reset")
            {
                var lb = LandblockManager.GetLandblock(new ACE.Entity.LandblockId(ARENA_BLOCK << 16 | 0xFFFF), false);
                var gen = lb?.GetObject(GEN_GUID) ?? lb?.GetAllWorldObjectsForDiagnostics().FirstOrDefault(o => o.WeenieClassId == GEN_WCID);
                if (gen == null) { Say(session, $"[Duelist] generator 0x{GEN_GUID:X8} not found in landblock {ARENA_BLOCK:X4} ({(lb == null ? "not loaded" : lb.GetAllWorldObjectsForDiagnostics().Count + " objects")}): is duelist-boss.sql applied?"); return; }
                foreach (var old in lb.GetAllWorldObjectsForDiagnostics().OfType<Creature>().Where(o => o.WeenieClassId == WCID).ToList()) old.Destroy();
                gen.ResetGenerator();
                gen.GeneratorEnteredWorld = false;
                gen.GeneratorRegeneration(Time.GetUnixTime());
                Say(session, "[Duelist] reset: a fresh Varrow the Unbowed is back in the arena (ours, not retail)");
                Mod.Log.Info($"[Duelist] reset by {session?.Player?.Name ?? "console"}");
                return;
            }
            Say(session, $"[Duelist] (ours, not retail) brain {(s_On ? "ON" : "OFF")} ({P_ON}), log {(s_Log ? "on" : "off")}; heal spell {Sp(s_Heal).Name}; " +
                string.Join(", ", s_Doubles.Select(d => $"{d.key.Replace("duelist_", "")} {PropertyManager.GetDouble(d.key, d.def).Item:0.##}")));
            Say(session, $"[Duelist] since load: decisions {Interlocked.Read(ref s_Decisions)}, kits {s_Kits} (spoiled {s_KitsSpoiled}), retreats {s_Retreats}, pillar dashes {s_LosBreaks}, kites {s_Kites}, ripostes {s_Ripostes}, dispels {s_Dispels}, wins {s_Kills}, deaths {s_Deaths}");
        }

        // ------------------------------------------------------------------ registration

        static bool s_Registered;
        static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard DuelistAi: Varrow the Unbowed (901050) fights like a PK duelist. Off = an ordinary ACE monster");
                if (!b.ContainsKey(P_LOG)) b[P_LOG] = new Property<bool>(false, "RevivalGuard DuelistAi: log every decision ([Duelist] lines in ACE_Log.txt)");
                var d = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DuelistAi: switches not registered with @modify* ({e.Message}); defaults apply"); }
            if (s_SetSpell == null) Mod.Log.Error("[RevivalGuard] DuelistAi: Creature.CurrentSpell setter not found; the attack brain is off");
            CommandManager.TryAddCommand(Handle, "duelist", AccessLevel.Admin, CommandHandlerFlag.None,
                "Varrow the Unbowed, the duelist boss (RevivalGuard DuelistAi): switches and counters; 'trace [sec]' logs his decisions; 'reset' respawns him fresh; 'go' takes you to the arena.",
                "[trace [seconds] | trace off | reset | go]");
            Mod.Log.Info($"[RevivalGuard] DuelistAi: Varrow the Unbowed ({WCID}) utility brain, {(PropertyManager.GetBool(P_ON, false).Item ? "ON" : "off")} ({P_ON}), @duelist");
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        // ------------------------------------------------------------------ the patches (declared on Creature)

        [HarmonyPatch(typeof(Creature), nameof(Creature.GetNextAttackType))]
        static class AttackChoice
        {
            static int s_Errors;
            static bool Prepare() { Register(); return true; }
            static bool Prefix(Creature __instance, ref CombatType __result)
            {
                try { return ChooseAttack(__instance, ref __result); }
                catch (Exception e)
                {
                    if (++s_Errors <= 5) Mod.Log.Error($"[RevivalGuard] DuelistAi attack choice threw for {__instance?.Name}; ACE's roll runs", e);
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
        [HarmonyPriority(Priority.High)]
        static class MoveTick
        {
            static int s_Errors;
            static bool Prefix(Creature __instance, double currentUnixTime)
            {
                if (__instance == null || __instance.WeenieClassId != WCID) return true;   // everyone else: one int compare
                try { return Tick(__instance, currentUnixTime); }
                catch (Exception e)
                {
                    if (++s_Errors <= 5) Mod.Log.Error($"[RevivalGuard] DuelistAi tick threw for {__instance?.Name}; ACE's tick runs", e);
                    return true;
                }
            }
        }

        /// <summary>His swing speed (owner 2026-09-29: "his attack speed needs to be way faster"). ACE clamps a
        /// creature's animation speed at 2.0 (Creature_Combat.MaxAttackSpeed); his is duelist_swing_speed (2.8).
        /// Only while his brain is on, only for him.</summary>
        [HarmonyPatch(typeof(Creature), nameof(Creature.GetAnimSpeed))]
        static class SwingSpeed
        {
            static void Postfix(Creature __instance, ref float __result)
            {
                try
                {
                    if (__instance?.WeenieClassId == WCID && s_On && __instance.CurrentAttack == CombatType.Melee)
                        __result = Math.Max(__result, (float)Math.Clamp(PropertyManager.GetDouble("duelist_swing_speed", 2.8).Item, 1.0, 4.0));
                }
                catch { }
            }
        }

        /// <summary>After a swing ACE holds a monster still for the swing + 0.5 s and waits a random
        /// 0..PowerupTime before the next (Monster_Melee.cs:151-156). His weenie's PowerupTime is 0.1 now; this
        /// trims the move hold to the swing + 0.1 s, so he follows a backing opponent straight away.</summary>
        [HarmonyPatch(typeof(Creature), nameof(Creature.MeleeAttack))]
        static class AfterSwing
        {
            static void Postfix(Creature __instance, float __result)
            {
                try
                {
                    if (__instance?.WeenieClassId == WCID && s_On && __result > 0)
                        __instance.NextMoveTime = Math.Min(__instance.NextMoveTime, __instance.PrevAttackTime + __result + 0.1);
                }
                catch { }
            }
        }

        /// <summary>His bolts hit at duelist_spell_damage_mult of ACE's figure: a level VII bolt on a
        /// vulnerable player is ~160, which ends a 290-health player in two. Tuned on staging.</summary>
        [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.CalculateDamage))]
        static class BoltDamage
        {
            static void Postfix(WorldObject source, Creature target, ref float? __result)
            {
                try
                {
                    if (__result.HasValue && source?.WeenieClassId == WCID && target is Player && s_On)
                        __result = __result.Value * (float)PropertyManager.GetDouble("duelist_spell_damage_mult", 0.18).Item;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(BodyPart), typeof(bool), typeof(AttackConditions) })]
        static class MeleeDamage
        {
            static void Prefix(WorldObject source, ref float _amount)
            {
                try
                {
                    if (source?.WeenieClassId == WCID && s_On)
                        _amount *= (float)PropertyManager.GetDouble("duelist_melee_damage_mult", 0.8).Item;
                }
                catch { }
            }
        }

        [HarmonyPatch]
        static class Death
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(Creature __instance, DamageHistoryInfo killer)
            {
                try { if (__instance?.WeenieClassId == WCID) OnDeath(__instance, killer); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DuelistAi death: {e.GetType().Name}: {e.Message}"); }
            }
        }
    }
}
