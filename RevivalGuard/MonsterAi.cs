using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// MONSTER TACTICS (docs/AI-POPULATION.md Layer 1, docs/MONSTER-AI.md). Owner-approved 2026-09-27;
    /// NOT RETAIL, recorded in docs/DIVERGENCES.md. A small layer ON TOP of ACE's monster AI
    /// (Monster_Tick.cs and friends): a Harmony prefix on Creature.Monster_Tick that either lets ACE's
    /// tick run as it always has (returns true), or takes this one 0.2 s tick itself (returns false)
    /// while a tactic is moving the creature. Nothing here replaces ACE's targeting, attacking or
    /// casting code; the tactics steer WHERE the creature stands and WHICH attack it rolls.
    ///
    ///   casters keep range   A creature whose harmful spells make up enough of its spellbook
    ///                        (monster_ai_caster_min_cast_chance) does not melee-chase: when ACE rolls
    ///                        a melee attack at a target out of melee reach, it re-rolls its spellbook
    ///                        (ACE's own TryRollSpell) instead and holds position between tries, for
    ///                        at most monster_ai_caster_hold_s; a target that closes inside
    ///                        monster_ai_caster_close_m makes it back off to monster_ai_caster_range_m.
    ///   archers kite         A creature with a missile weapon wielded steps back
    ///                        monster_ai_kite_step_m when its target is inside monster_ai_kite_close_m,
    ///                        at most once per monster_ai_kite_cooldown_s, then ACE turns and shoots.
    ///   pack call            Once engaged (hurt, or within 20 m of its target), a creature wakes idle creatures of its own CreatureType (or its
    ///                        FriendType) within monster_ai_pack_range_m AND in line of sight, onto its
    ///                        target (ACE's AlertFriendly only runs at wake-up and uses aural range).
    ///                        Kin with Tolerance Retaliate (training targets) are never called.
    ///   pack spread          Melee chasers of one target that are lined up behind each other take
    ///                        slots around the target instead of stacking on one side.
    ///   morale               Families in monster_ai_morale_families flee (towards home) once per fight
    ///                        at monster_ai_morale_health for monster_ai_morale_flee_s, then return.
    ///                        Undead, golems, skeletons, ghosts, shadows, wisps, elementals and virindi
    ///                        never do, whatever the list says.
    ///   healers              A creature with Heal Self N in its spellbook casts Heal Other N on the
    ///                        most hurt awake ally of its family under monster_ai_heal_ally_health
    ///                        within monster_ai_heal_range_m (no monster in ace_world has Heal Other).
    ///   leash                An awake creature more than monster_ai_leash_m from home goes home (ACE's
    ///                        own is 192 m); one that finishes a return home is healed to full.
    ///
    /// COST. Every switch and number is an ACE server property (@modifybool / @modifydouble /
    /// @modifystring), read into one snapshot every 2 s, not per tick. Per creature per tick: a few
    /// field reads and one ConditionalWeakTable lookup, only for awake creatures; no allocation.
    /// Scans of nearby objects (pack call, spread, healer) are throttled per creature (once per fight,
    /// 2.5 s, 1.5 s), line-of-sight tests are capped at 8 per pack call and 1 per heal, and a movement
    /// plan (a MoveTo) allocates once per tactic START, never per tick.
    ///
    ///   @monsterai                       switches, tuning and how often each tactic has fired
    ///   @monsterai trace [sec] [metres]  log every monster near you twice a second to ACE_Log.txt
    ///                                    ("[MonsterAI] trace"), for measuring positions over time
    ///   @monsterai trace off
    /// </summary>
    public static class MonsterAi
    {
        // ------------------------------------------------------------------ switches and tuning

        internal const string P_CASTER = "monster_ai_casters_keep_range", P_KITE = "monster_ai_archers_kite",
            P_PACK = "monster_ai_pack_call", P_SPREAD = "monster_ai_pack_spread", P_MORALE = "monster_ai_morale",
            P_HEAL = "monster_ai_healers", P_LEASH = "monster_ai_leash", P_LOG = "monster_ai_log";

        static readonly (string key, bool def, string desc)[] s_Bools =
        {
            (P_CASTER, true, "RevivalGuard MonsterAi: casters re-roll spells instead of melee-chasing, and back off when crowded"),
            (P_KITE, true, "RevivalGuard MonsterAi: missile users step back when their target closes, then shoot"),
            (P_PACK, true, "RevivalGuard MonsterAi: a hurt monster wakes same-family monsters in range and line of sight"),
            (P_SPREAD, true, "RevivalGuard MonsterAi: melee chasers of one target take slots around it instead of stacking"),
            (P_MORALE, true, "RevivalGuard MonsterAi: listed families flee once per fight at low health, then return"),
            (P_HEAL, true, "RevivalGuard MonsterAi: monsters with Heal Self heal hurt awake allies of their family"),
            (P_LEASH, true, "RevivalGuard MonsterAi: monsters go home past monster_ai_leash_m and heal on arrival"),
            (P_LOG, false, "RevivalGuard MonsterAi: log each tactic as it fires ([MonsterAI] lines in ACE_Log.txt)"),
        };

        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            ("monster_ai_caster_min_cast_chance", 0.20, "MonsterAi: chance per attack roll of a harmful spell for a creature to count as a caster"),
            ("monster_ai_caster_close_m", 2.5, "MonsterAi: a caster backs off when its target is closer than this (m)"),
            ("monster_ai_caster_range_m", 10.0, "MonsterAi: distance a caster backs off to (m)"),
            ("monster_ai_caster_hold_s", 6.0, "MonsterAi: longest a caster holds position re-rolling spells before it melee-chases (s)"),
            ("monster_ai_kite_close_m", 3.0, "MonsterAi: an archer steps back when its target is closer than this (m)"),
            ("monster_ai_kite_step_m", 8.0, "MonsterAi: how far an archer or caster steps back (m)"),
            ("monster_ai_kite_cooldown_s", 8.0, "MonsterAi: least time between two step-backs of one creature (s)"),
            ("monster_ai_pack_range_m", 30.0, "MonsterAi: pack-call radius (m)"),
            ("monster_ai_morale_health", 0.25, "MonsterAi: health fraction at which a morale family flees"),
            ("monster_ai_morale_flee_s", 5.0, "MonsterAi: how long a flee lasts at most (s)"),
            ("monster_ai_morale_flee_m", 14.0, "MonsterAi: how far a flee runs (m)"),
            ("monster_ai_heal_ally_health", 0.5, "MonsterAi: an ally below this health fraction gets healed"),
            ("monster_ai_heal_range_m", 20.0, "MonsterAi: healer reach (m)"),
            ("monster_ai_heal_cooldown_s", 8.0, "MonsterAi: least time between two ally heals by one healer (s)"),
            ("monster_ai_leash_m", 80.0, "MonsterAi: an awake monster further than this from home goes home (m; ACE's own is 192)"),
        };

        const string P_FAMILIES = "monster_ai_morale_families";
        const string FAMILIES_DEFAULT = "Drudge,Banderling,Mosswart,Rat,Gromnie,Reedshark";

        /// <summary>Families that never flee, whatever monster_ai_morale_families says.</summary>
        static readonly CreatureType[] s_NeverFlee =
        {
            CreatureType.Undead, CreatureType.Golem, CreatureType.Skeleton, CreatureType.Ghost, CreatureType.Shadow,
            CreatureType.Wisp, CreatureType.Elemental, CreatureType.FireElemental, CreatureType.FrostElemental,
            CreatureType.LightningElemental, CreatureType.AcidElemental, CreatureType.Virindi,
        };

        /// <summary>One read of every switch and number; replaced (never mutated) every 2 s.</summary>
        sealed class Cfg
        {
            public bool Caster, Kite, Pack, Spread, Morale, Heal, Leash, Log, Any;
            public float CasterMinChance, CasterClose, CasterRange, CasterHold, KiteClose, KiteStep, KiteCooldown,
                PackRange, MoraleHealth, FleeSeconds, FleeMetres, HealAlly, HealRange, HealCooldown, LeashM;
            public string FamiliesRaw;
            public bool[] FleeFamily;   // indexed by (int)CreatureType
        }

        static volatile Cfg s_Cfg = new Cfg();
        static double s_CfgNext;
        static readonly object s_CfgLock = new object();

        static Cfg Config(double rt)
        {
            var cfg = s_Cfg;
            if (rt < s_CfgNext) return cfg;
            lock (s_CfgLock)
            {
                if (rt < s_CfgNext) return s_Cfg;
                var n = new Cfg();
                n.Caster = PropertyManager.GetBool(P_CASTER, true).Item;
                n.Kite = PropertyManager.GetBool(P_KITE, true).Item;
                n.Pack = PropertyManager.GetBool(P_PACK, true).Item;
                n.Spread = PropertyManager.GetBool(P_SPREAD, true).Item;
                n.Morale = PropertyManager.GetBool(P_MORALE, true).Item;
                n.Heal = PropertyManager.GetBool(P_HEAL, true).Item;
                n.Leash = PropertyManager.GetBool(P_LEASH, true).Item;
                n.Log = PropertyManager.GetBool(P_LOG, false).Item;
                n.Any = n.Caster || n.Kite || n.Pack || n.Spread || n.Morale || n.Heal || n.Leash;
                float D(int i) => (float)PropertyManager.GetDouble(s_Doubles[i].key, s_Doubles[i].def).Item;
                n.CasterMinChance = D(0); n.CasterClose = D(1); n.CasterRange = D(2); n.CasterHold = D(3);
                n.KiteClose = D(4); n.KiteStep = D(5); n.KiteCooldown = D(6); n.PackRange = D(7);
                n.MoraleHealth = D(8); n.FleeSeconds = D(9); n.FleeMetres = D(10); n.HealAlly = D(11);
                n.HealRange = D(12); n.HealCooldown = D(13); n.LeashM = D(14);
                n.FamiliesRaw = PropertyManager.GetString(P_FAMILIES, FAMILIES_DEFAULT).Item ?? "";
                n.FleeFamily = n.FamiliesRaw == cfg.FamiliesRaw && cfg.FleeFamily != null ? cfg.FleeFamily : ParseFamilies(n.FamiliesRaw);
                s_Cfg = n;
                s_CfgNext = rt + 2.0;
                return n;
            }
        }

        static bool[] ParseFamilies(string raw)
        {
            int max = 0;
            foreach (CreatureType t in Enum.GetValues(typeof(CreatureType))) max = Math.Max(max, (int)t);
            var flags = new bool[max + 1];
            foreach (var part in raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse<CreatureType>(part.Trim(), true, out var t) && (int)t >= 0 && (int)t <= max) flags[(int)t] = true;
            foreach (var t in s_NeverFlee) if ((int)t <= max) flags[(int)t] = false;
            return flags;
        }

        // ------------------------------------------------------------------ per-weenie profile

        /// <summary>What a creature IS, from its weenie; computed once per weenie class.</summary>
        sealed class Profile
        {
            public float HarmfulCastChance;   // chance per ACE attack roll of a harmful, other-targeted spell
            public Spell HealOther;           // Heal Other at the level of its best Heal Self, or null
            public bool Immobile;             // AiImmobile: never moved by a tactic
            public uint MinHarmfulMana;       // the cheapest harmful spell: below this the caster is out of mana
        }

        static readonly ConcurrentDictionary<uint, Profile> s_Profiles = new ConcurrentDictionary<uint, Profile>();

        // Heal Self I..VI -> Heal Other I..VI (ace_world.spell; no creature weenie carries Heal Other).
        static readonly (uint self, uint other)[] s_HealMap = { (6, 5), (1157, 1162), (1158, 1163), (1159, 1164), (1160, 1165), (1161, 1166) };

        static Profile GetProfile(Creature c)
        {
            if (s_Profiles.TryGetValue(c.WeenieClassId, out var p)) return p;
            p = new Profile();
            p.Immobile = c.AiImmobile;   // NOT Stuck: every creature weenie carries Stuck (it only means "cannot be picked up")
            try
            {
                var book = c.Biota.PropertiesSpellBook;
                if (book != null)
                {
                    double miss = 1.0;
                    int bestHeal = -1;
                    uint minMana = uint.MaxValue;
                    foreach (var kv in book.ToArray())
                    {
                        var prob = kv.Value > 2.0f ? kv.Value - 2.0f : kv.Value / 100.0f;
                        for (int i = 0; i < s_HealMap.Length; i++)
                            if (s_HealMap[i].self == (uint)kv.Key && i > bestHeal) bestHeal = i;
                        var spell = new Spell((uint)kv.Key);
                        if (spell.NotFound || !spell.IsHarmful || spell.IsSelfTargeted) continue;
                        miss *= 1.0 - Math.Min(0.99, Math.Max(0.0, prob));
                        minMana = Math.Min(minMana, spell.BaseMana);
                    }
                    p.MinHarmfulMana = minMana == uint.MaxValue ? 0 : minMana;
                    p.HarmfulCastChance = (float)(1.0 - miss);
                    if (bestHeal >= 0)
                    {
                        var h = new Spell(s_HealMap[bestHeal].other);
                        if (!h.NotFound) p.HealOther = h;
                    }
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MonsterAi: profile of {c.Name} ({c.WeenieClassId}) failed: {e.Message}"); }
            if (s_Profiles.TryAdd(c.WeenieClassId, p) && s_Cfg.Log)
                Mod.Log.Info($"[MonsterAI] profile: {c.Name} wcid={c.WeenieClassId} type={c.CreatureType} harmful cast chance {p.HarmfulCastChance:F2}, ally heal {p.HealOther?.Name ?? "none"}, immobile {p.Immobile}");
            return p;
        }

        // ------------------------------------------------------------------ per-creature state

        enum Mode : byte { None, Backoff, Kite, Flee, Flank }

        sealed class St
        {
            public Mode Mode;
            public double ModeUntil, StepReadyAt, HoldSince, HoldRollAt, NextSpread, NextLeash, HealReadyAt, NextHealScan,
                RangedAt = -1, PackAt, NextTrace;
            public uint PackFor, LastTarget;
            public int HoldRolls, Flanks;
            public bool Fled, Ranged;
        }

        static readonly ConditionalWeakTable<Creature, St> s_State = new ConditionalWeakTable<Creature, St>();
        static readonly ConditionalWeakTable<Creature, St>.CreateValueCallback s_NewSt = _ => new St();

        // ACE's private spell roll (Monster_Magic.cs TryRollSpell): sets CurrentSpell and says whether a
        // spell came up. Bound once; missing in a future ACE build turns the caster re-roll off, not the mod.
        static readonly Func<Creature, bool> s_TryRollSpell = Bind();
        static Func<Creature, bool> Bind()
        {
            try
            {
                var m = AccessTools.Method(typeof(Creature), "TryRollSpell");
                return m == null ? null : AccessTools.MethodDelegate<Func<Creature, bool>>(m);
            }
            catch { return null; }
        }

        static readonly Func<Creature, Spell> s_CurrentSpell = BindSpell();
        static Func<Creature, Spell> BindSpell()
        {
            try
            {
                var g = AccessTools.PropertyGetter(typeof(Creature), "CurrentSpell");
                return g == null ? null : AccessTools.MethodDelegate<Func<Creature, Spell>>(g);
            }
            catch { return null; }
        }

        enum Tactic { CasterHold, CasterCast, CasterBackoff, Kite, PackCall, PackWoken, Spread, Flee, AllyHeal, Leash, HomeHeal, Count }
        static readonly long[] s_Fired = new long[(int)Tactic.Count];
        static void Fired(Tactic t) => Interlocked.Increment(ref s_Fired[(int)t]);

        // ------------------------------------------------------------------ the tick

        /// <summary>Returns false when this tick was taken (ACE's Monster_Tick is then skipped).</summary>
        internal static bool Tick(Creature c, double now)
        {
            if (c is Player || c is Pet || c.IsChessPiece || c.PhysicsObj == null || c.Location == null) return true;
            if (DuelistAi.Owns(c)) return true;   // the duelist boss has his own brain (DuelistAi.cs); nothing here moves him
            double rt = Timers.RunningTime;
            var cfg = Config(rt);

            if (s_TraceUntil > rt) Trace(c, rt);

            if (!cfg.Any || !c.IsAwake || c.IsDead) return true;
            var st = s_State.GetValue(c, s_NewSt);

            if (c.MonsterState == Creature.State.Return) { if (st.Mode != Mode.None) st.Mode = Mode.None; return true; }

            var target = c.AttackTarget as Creature;
            if (target == null || target.IsDead || target.PhysicsObj == null || target.Location == null)
            {
                if (st.Mode != Mode.None) EndMode(c, st);
                return true;
            }
            if (target.Guid.Full != st.LastTarget) { st.LastTarget = target.Guid.Full; st.HoldSince = 0; st.Flanks = 0; }

            // a tactic is moving the creature: keep the tick until it arrives or runs out of time
            if (st.Mode != Mode.None)
            {
                if (rt < st.ModeUntil && c.PhysicsObj.IsMovingTo()) { c.NextMonsterTickTime = now + 0.2; return false; }
                EndMode(c, st);
                return true;
            }

            if (c.EmoteManager.IsBusy || !c.IsVisibleTarget(target)) return true;   // ACE finds a new target

            // leash: once a second
            if (cfg.Leash && rt >= st.NextLeash)
            {
                st.NextLeash = rt + 1.0;
                if (BeyondLeash(c, cfg))
                {
                    Fired(Tactic.Leash);
                    if (cfg.Log) Mod.Log.Info($"[MonsterAI] leash: {c.Name} 0x{c.Guid.Full:X8} past {cfg.LeashM:F0} m from home, going home");
                    c.MoveToHome();
                    c.NextMonsterTickTime = now + 0.2;
                    return false;
                }
            }

            var prof = GetProfile(c);
            float hp = c.Health.MaxValue > 0 ? (float)c.Health.Current / c.Health.MaxValue : 1f;
            bool mobile = !prof.Immobile;

            if (rt - st.RangedAt > 3.0) { st.Ranged = c.IsRanged; st.RangedAt = rt; }
            float dist = c.GetDistanceToTarget();

            // pack call: once per target per 30 s, once engaged (hurt, or within 20 m of its target)
            if (cfg.Pack && target is Player && (hp < 1f || dist < 20f) && (st.PackFor != target.Guid.Full || rt - st.PackAt > 30.0))
            {
                st.PackFor = target.Guid.Full; st.PackAt = rt;
                PackCall(c, target, cfg);
            }

            // morale: once per fight
            if (cfg.Morale && mobile && !st.Fled && hp <= cfg.MoraleHealth && FleeFamily(c, cfg) && !c.IsAnimating)
            {
                st.Fled = true;
                if (StepAway(c, st, target, cfg.FleeMetres, Mode.Flee, cfg.FleeSeconds, cfg, towardHome: true))
                {
                    Fired(Tactic.Flee);
                    if (cfg.Log) Mod.Log.Info($"[MonsterAI] flee: {c.Name} 0x{c.Guid.Full:X8} at {hp:P0} health from {target.Name}");
                    c.NextMonsterTickTime = now + 0.2;
                    return false;
                }
            }

            // healer
            if (cfg.Heal && prof.HealOther != null && rt >= st.NextHealScan)
            {
                st.NextHealScan = rt + 1.5;
                if (rt >= st.HealReadyAt && rt >= c.NextMagicAttackTime && !c.IsAnimating && TryHealAlly(c, st, prof.HealOther, cfg, rt))
                {
                    c.NextMonsterTickTime = now + 0.2;
                    return false;
                }
            }

            // archers kite
            if (st.Ranged)
            {
                if (cfg.Kite && mobile && dist < cfg.KiteClose && rt >= st.StepReadyAt && !c.IsAnimating)
                {
                    st.StepReadyAt = rt + cfg.KiteCooldown;
                    if (StepAway(c, st, target, cfg.KiteStep, Mode.Kite, 4.0, cfg, towardHome: false))
                    {
                        Fired(Tactic.Kite);
                        if (cfg.Log) Mod.Log.Info($"[MonsterAI] kite: {c.Name} 0x{c.Guid.Full:X8} target {target.Name} at {dist:F1} m, stepping back {cfg.KiteStep:F0} m");
                        c.NextMonsterTickTime = now + 0.2;
                        return false;
                    }
                }
                return true;
            }

            // casters keep range
            if (cfg.Caster && mobile && prof.HarmfulCastChance >= cfg.CasterMinChance && prof.HarmfulCastChance > 0f)
            {
                if (dist < cfg.CasterClose && rt >= st.StepReadyAt && !c.IsAnimating && ManaOk(c, prof))
                {
                    st.StepReadyAt = rt + cfg.KiteCooldown;
                    if (StepAway(c, st, target, Math.Max(2f, cfg.CasterRange - dist), Mode.Backoff, 4.0, cfg, towardHome: false))
                    {
                        Fired(Tactic.CasterBackoff);
                        if (cfg.Log) Mod.Log.Info($"[MonsterAI] backoff: {c.Name} 0x{c.Guid.Full:X8} target {target.Name} at {dist:F1} m");
                        c.NextMonsterTickTime = now + 0.2;
                        return false;
                    }
                }
                if (dist <= 3f || c.CurrentAttack == CombatType.Magic) st.HoldSince = 0;   // in melee anyway, or casting already
                else if (c.CurrentAttack == CombatType.Melee && dist < 40f && ManaOk(c, prof) && s_TryRollSpell != null)
                {
                    if (st.HoldSince == 0)
                    {
                        st.HoldSince = rt; st.HoldRolls = 0;
                        Fired(Tactic.CasterHold);
                        if (cfg.Log) Mod.Log.Info($"[MonsterAI] hold: {c.Name} 0x{c.Guid.Full:X8} rolled melee at {dist:F1} m, holding to cast (chance {prof.HarmfulCastChance:F2}, mana {c.Mana.Current})");
                    }
                    if (rt - st.HoldSince < cfg.CasterHold)
                    {
                        if (rt >= st.HoldRollAt)
                        {
                            st.HoldRollAt = rt + 1.0;
                            for (int k = 0; k < 3; k++)
                            {
                                st.HoldRolls++;
                                if (!s_TryRollSpell(c)) continue;
                                var rolled = s_CurrentSpell?.Invoke(c);
                                if (rolled != null && UsesMana(c) && rolled.BaseMana > c.Mana.Current) continue;   // could not pay for it
                                if (c.IsMoving || c.PhysicsObj.IsMovingTo()) StopMoving(c);
                                c.CurrentAttack = CombatType.Magic;
                                c.MaxRange = c.GetMaxRange();   // ACE's own: re-rolls to melee if a bolt has no line of sight
                                // no line of sight: end the hold for this approach so ACE walks it round, rather
                                // than stopping and starting it every second; the next cast re-arms the hold
                                st.HoldSince = c.CurrentAttack == CombatType.Magic ? 0 : rt - cfg.CasterHold;
                                Fired(Tactic.CasterCast);
                                if (cfg.Log) Mod.Log.Info($"[MonsterAI] hold: {c.Name} 0x{c.Guid.Full:X8} cast after {st.HoldRolls} rolls, attack {c.CurrentAttack}, range {c.MaxRange:F1}");
                                return true;
                            }
                        }
                        if (c.IsMoving || c.PhysicsObj.IsMovingTo()) StopMoving(c);
                        c.NextMonsterTickTime = now + 0.2;
                        return false;
                    }
                }
            }

            // pack spread: melee chasers take slots around the target
            if (cfg.Spread && mobile && st.Flanks < 2 && c.CurrentAttack == CombatType.Melee && dist > 3f && dist < 15f && rt >= st.NextSpread && c.IsMoving)
            {
                st.NextSpread = rt + 2.5;
                if (TrySpread(c, st, target, dist, cfg, rt))
                {
                    c.NextMonsterTickTime = now + 0.2;
                    return false;
                }
            }
            return true;
        }

        static bool UsesMana(Creature c) => c.GetProperty(PropertyBool.AiUsesMana) ?? true;

        /// <summary>An out-of-mana caster melees, as ACE would have it: ACE's MagicAttack silently does
        /// nothing when the spell costs more than the monster has (UseMana), so holding one at range
        /// would leave it standing there.</summary>
        static bool ManaOk(Creature c, Profile prof) => !UsesMana(c) || c.Mana.Current >= Math.Max(1u, prof.MinHarmfulMana);

        static bool FleeFamily(Creature c, Cfg cfg)
        {
            var t = c.CreatureType;
            if (t == null) return false;
            int i = (int)t.Value;
            return i >= 0 && i < cfg.FleeFamily.Length && cfg.FleeFamily[i];
        }

        static bool BeyondLeash(Creature c, Cfg cfg)
        {
            var home = c.GetPosition(PositionType.Home);
            if (home == null) return false;
            var hr = c.HomeRadius;
            if (hr.HasValue && hr.Value <= cfg.LeashM) return false;   // ACE's own CheckMissHome already holds it tighter
            return Vector3.DistanceSquared(home.ToGlobal(), c.Location.ToGlobal()) > cfg.LeashM * cfg.LeashM;
        }

        // ------------------------------------------------------------------ movement

        /// <summary>Stops ACE's own chase (a sticky MoveToObject) the way Creature.CancelMoveTo does,
        /// without its FindNextTarget.</summary>
        internal static void StopMoving(Creature c)
        {
            var mtm = c.PhysicsObj.MovementManager?.MoveToManager;
            if (mtm != null) { mtm.CancelMoveTo(WeenieError.ActionCancelled); mtm.FailProgressCount = 0; }
            c.PhysicsObj.unstick_from_object();
            if (c.CurrentMotionState != null)
                c.EnqueueBroadcastMotion(new Motion(c.CurrentMotionState.Stance, MotionCommand.Ready));
            c.IsMoving = false;
        }

        static void EndMode(Creature c, St st)
        {
            if (c.PhysicsObj.IsMovingTo()) StopMoving(c);
            st.Mode = Mode.None;
            c.ResetAttack();   // ACE re-rolls its next attack and turns to the target
        }

        /// <summary>The frame tactics measure in: landblock-local for a dungeon (FromGlobal treats its
        /// argument as local there), global outdoors.</summary>
        internal static bool Frame(Creature c, WorldObject other, out Vector3 me, out Vector3 them)
        {
            if (c.Location.Indoors)
            {
                me = c.Location.Pos; them = other.Location.Pos;
                return other.Location.Landblock == c.Location.Landblock;
            }
            me = c.Location.ToGlobal(); them = other.Location.ToGlobal();
            return true;
        }

        internal static Vector3 Rotate(Vector3 v, float deg)
        {
            double a = deg * Math.PI / 180.0, cs = Math.Cos(a), sn = Math.Sin(a);
            return new Vector3((float)(v.X * cs - v.Y * sn), (float)(v.X * sn + v.Y * cs), 0f);
        }

        static readonly float[] s_TryAngles = { 0f, 35f, -35f, 70f, -70f };

        /// <summary>Runs directly away from the target (blended towards home for a flee, and whenever the
        /// creature is past half its leash), trying five headings for one that lands in a cell.</summary>
        static bool StepAway(Creature c, St st, Creature target, float metres, Mode mode, double seconds, Cfg cfg, bool towardHome)
        {
            if (!Frame(c, target, out var me, out var tg)) return false;
            var away = me - tg; away.Z = 0;
            if (away.LengthSquared() < 0.01f) away = new Vector3(0, 1, 0);
            away = Vector3.Normalize(away);

            var home = c.GetPosition(PositionType.Home);
            if (home != null && home.Landblock == c.Location.Landblock || home != null && !c.Location.Indoors)
            {
                var h = c.Location.Indoors ? home.Pos : home.ToGlobal();
                var toHome = h - me; toHome.Z = 0;
                float hd = toHome.Length();
                if (hd > 1f && (towardHome || hd > cfg.LeashM * 0.5f))
                    away = Vector3.Normalize(away + toHome / hd * (towardHome ? 0.75f : Math.Min(1f, hd / cfg.LeashM)));
            }

            foreach (var a in s_TryAngles)
                if (MoveToPoint(c, st, me + Rotate(away, a) * metres, me.Z, tg, mode, seconds)) return true;
            if (cfg.Log) Mod.Log.Info($"[MonsterAI] {mode.ToString().ToLowerInvariant()}: {c.Name} 0x{c.Guid.Full:X8} no room to step back (5 headings, {(c.Location.Indoors ? "indoors" : "outdoors")})");
            return false;
        }

        static bool MoveToPoint(Creature c, St st, Vector3 dest, float z, Vector3 face, Mode mode, double seconds)
        {
            dest.Z = z;
            var pos = c.Location.FromGlobal(dest);
            if (pos == null || pos.LandblockId.Raw == 0) return false;
            if (pos.Indoors && ((pos.LandblockId.Raw & 0xFFFF) < 0x100 || !c.IsDirectVisible(pos))) return false;
            var f = face - dest; f.Z = 0;
            if (f.LengthSquared() > 0.01f) pos.Rotate(f);

            if (c.MoveSpeed == 0f) c.GetMovementSpeed();
            if (c.IsMoving || c.PhysicsObj.IsMovingTo()) StopMoving(c);
            c.MoveTo(pos, c.RunRate > 0 ? c.RunRate : 1f, true, 1.0f);
            st.Mode = mode;
            st.ModeUntil = Timers.RunningTime + seconds;
            return true;
        }

        // ------------------------------------------------------------------ pack

        static bool Kin(Creature c, Creature n) =>
            c.CreatureType != null && (n.CreatureType == c.CreatureType || c.FriendType != null && n.CreatureType == c.FriendType);

        static bool Eligible(Creature c, Creature n) =>
            n != null && n != c && !(n is Player) && !(n is Pet) && n.IsMonster && !n.IsDead && n.PhysicsObj != null
            && n.CurrentLandblock != null && c.CurrentLandblock != null
            && n.CurrentLandblock.CurrentLandblockGroup == c.CurrentLandblock.CurrentLandblockGroup;

        static void PackCall(Creature c, Creature target, Cfg cfg)
        {
            if (c.CreatureType == null || c.PhysicsObj.CurCell == null) return;
            var objs = c.PhysicsObj.ObjMaint.GetVisibleObjects(c.PhysicsObj.CurCell);
            double r2 = cfg.PackRange * cfg.PackRange;
            int looks = 0, woken = 0, kin = 0, blind = 0;
            foreach (var o in objs)
            {
                var n = o?.WeenieObj?.WorldObject as Creature;
                if (!Eligible(c, n) || n.IsAwake || !Kin(c, n) || n == target) continue;
                // Retaliate kin fight only when struck themselves (the Academy's Sparring Golems and
                // Carpenter Wasps: ACE's AlertFriendly reaches them only inside their 0.1 m aural range).
                // Owner 2026-10-01: "the trees all aggrod me".
                if ((n.Tolerance & (Tolerance.NoAttack | Tolerance.Provoke | Tolerance.Retaliate)) != 0) continue;
                kin++;
                if (c.PhysicsObj.get_distance_sq_to_object(n.PhysicsObj, true) > r2) continue;
                if (looks++ >= 8) break;
                if (!c.IsDirectVisible(n)) { blind++; continue; }
                // ACE's AlertFriendly, the same faction rules
                if (n.SameFaction(target)) n.AddRetaliateTarget(target);
                if (c.PotentialFoe(target)) { if (n.PotentialFoe(target)) n.AddRetaliateTarget(target); else continue; }
                n.AttackTarget = target;
                n.WakeUp(false);
                woken++;
            }
            if (woken > 0)
            {
                Fired(Tactic.PackCall);
                Interlocked.Add(ref s_Fired[(int)Tactic.PackWoken], woken);
                if (cfg.Log) Mod.Log.Info($"[MonsterAI] pack: {c.Name} 0x{c.Guid.Full:X8} woke {woken} onto {target.Name}");
            }
            else if (cfg.Log && kin > 0)
                Mod.Log.Info($"[MonsterAI] pack: {c.Name} 0x{c.Guid.Full:X8} woke none: {kin} idle kin in view, {looks} within {cfg.PackRange:F0} m, {blind} of those out of line of sight");
        }

        static float Bearing(Vector3 from, Vector3 to) => (float)(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI);
        static float AngleDiff(float a, float b) { float d = (a - b) % 360f; if (d > 180f) d -= 360f; if (d < -180f) d += 360f; return d; }

        /// <summary>Slots around the target, by guid order among the melee chasers of the same target:
        /// the lowest guid goes straight in, the others take alternate sides at an even spacing (40-90
        /// degrees). Only when a chaser is lined up behind another (within 30 degrees and further out).</summary>
        static bool TrySpread(Creature c, St st, Creature target, float dist, Cfg cfg, double rt)
        {
            if (c.PhysicsObj.CurCell == null || !Frame(c, target, out var me, out var tg)) return false;
            float myBearing = Bearing(tg, me);
            var objs = c.PhysicsObj.ObjMaint.GetVisibleObjects(c.PhysicsObj.CurCell);
            int n = 1, lower = 0;
            bool crowded = false;
            uint leaderGuid = c.Guid.Full;
            float leaderBearing = myBearing;
            foreach (var o in objs)
            {
                var a = o?.WeenieObj?.WorldObject as Creature;
                if (!Eligible(c, a) || a.AttackTarget != target || a.IsRanged) continue;
                if (!Frame(a, target, out var am, out _)) continue;
                if (c.Location.Indoors != a.Location.Indoors) continue;
                var ad = Vector2.Distance(new Vector2(am.X, am.Y), new Vector2(tg.X, tg.Y));
                if (ad > 15f) continue;
                n++;
                float b = Bearing(tg, am);
                if (a.Guid.Full < c.Guid.Full) lower++;
                if (a.Guid.Full < leaderGuid) { leaderGuid = a.Guid.Full; leaderBearing = b; }
                if (Math.Abs(AngleDiff(b, myBearing)) < 30f && ad < dist) crowded = true;
            }
            // a mob of more than 8 has no free side to go to: ACE's straight chase (live 2026-09-28, 21 chasers
            // round one bot, re-flanked every 2.5 s and never swung)
            if (n < 2 || n > 8 || !crowded || lower == 0) return false;
            float spacing = Math.Clamp(360f / n, 40f, 90f);
            float offset = ((lower + 1) / 2) * spacing * ((lower & 1) == 1 ? 1f : -1f);
            float want = leaderBearing + offset;
            if (Math.Abs(AngleDiff(myBearing, want)) < 25f) return false;
            float r = Math.Max(2.5f, Math.Min(dist * 0.6f, 5f));
            double w = want * Math.PI / 180.0;
            var dest = tg + new Vector3((float)Math.Cos(w), (float)Math.Sin(w), 0f) * r;
            if (!MoveToPoint(c, st, dest, me.Z, tg, Mode.Flank, 3.0)) return false;
            st.Flanks++;   // at most two flanks per creature per target, then it goes straight in
            Fired(Tactic.Spread);
            if (cfg.Log) Mod.Log.Info($"[MonsterAI] spread: {c.Name} 0x{c.Guid.Full:X8} slot {lower} of {n}, bearing {myBearing:F0} -> {want:F0}");
            return true;
        }

        // ------------------------------------------------------------------ healer

        static bool TryHealAlly(Creature c, St st, Spell spell, Cfg cfg, double rt)
        {
            if (c.PhysicsObj.CurCell == null) return false;
            bool usesMana = c.GetProperty(PropertyBool.AiUsesMana) ?? true;
            if (usesMana && c.Mana.Current < spell.BaseMana) return false;
            var objs = c.PhysicsObj.ObjMaint.GetVisibleObjects(c.PhysicsObj.CurCell);
            double r2 = cfg.HealRange * cfg.HealRange;
            Creature best = null; float bestHp = cfg.HealAlly;
            foreach (var o in objs)
            {
                var a = o?.WeenieObj?.WorldObject as Creature;
                if (!Eligible(c, a) || !a.IsAwake || !Kin(c, a) || a.Health.MaxValue == 0) continue;
                float ahp = (float)a.Health.Current / a.Health.MaxValue;
                if (ahp >= bestHp) continue;
                if (c.PhysicsObj.get_distance_sq_to_object(a.PhysicsObj, true) > r2) continue;
                best = a; bestHp = ahp;
            }
            if (best == null || !c.IsDirectVisible(best)) return false;

            st.HealReadyAt = rt + cfg.HealCooldown;
            if (c.IsMoving || c.PhysicsObj.IsMovingTo()) StopMoving(c);
            if (usesMana) c.Mana.Current -= spell.BaseMana;

            var ally = best;
            float pre = c.PreCastMotion(ally, true);   // the generic monster cast motion
            var chain = new ACE.Server.Entity.Actions.ActionChain();
            chain.AddDelaySeconds(pre);
            chain.AddAction(c, () =>
            {
                if (c.IsDead || ally.IsDead) return;
                var before = ally.Health.Current;
                c.TryCastSpell(spell, ally, null, null, false, false, false);
                c.PostCastMotion();
                if (s_Cfg.Log) Mod.Log.Info($"[MonsterAI] heal: {ally.Name} 0x{ally.Guid.Full:X8} health {before} -> {ally.Health.Current} of {ally.Health.MaxValue}");
            });
            chain.EnqueueChain();
            float post = c.GetPostCastTime(spell, true);
            c.PrevAttackTime = rt + pre;
            c.NextMoveTime = c.NextAttackTime = c.PrevAttackTime + post + 0.5;
            c.ResetAttack();
            Fired(Tactic.AllyHeal);
            if (cfg.Log) Mod.Log.Info($"[MonsterAI] heal: {c.Name} 0x{c.Guid.Full:X8} casts {spell.Name} on {ally.Name} 0x{ally.Guid.Full:X8} at {bestHp:P0}");
            return true;
        }

        // ------------------------------------------------------------------ return home

        internal static void OnSleep(Creature c, bool wasReturning)
        {
            if (c is Player || c is Pet) return;
            if (s_State.TryGetValue(c, out var st))
            {
                st.Mode = Mode.None; st.Fled = false; st.HoldSince = 0; st.PackFor = 0; st.LastTarget = 0;
            }
            if (!wasReturning || c.IsDead) return;
            var cfg = Config(Timers.RunningTime);
            if (!cfg.Leash || c.Health.Current >= c.Health.MaxValue) return;
            c.SetMaxVitals();
            Fired(Tactic.HomeHeal);
            if (cfg.Log) Mod.Log.Info($"[MonsterAI] home: {c.Name} 0x{c.Guid.Full:X8} back home, healed to full");
        }

        // ------------------------------------------------------------------ trace and @monsterai

        static double s_TraceUntil;
        static Vector3 s_TraceAt;
        static uint s_TraceBlock;
        static float s_TraceRadius = 60f;

        static void Trace(Creature c, double rt)
        {
            if (!c.IsMonster || c.PhysicsObj == null) return;
            var st = s_State.GetValue(c, s_NewSt);
            if (rt < st.NextTrace) return;
            st.NextTrace = rt + 0.5;
            Vector3 p;
            if (c.Location.Indoors) { if (c.Location.Landblock != s_TraceBlock) return; p = c.Location.Pos; }
            else p = c.Location.ToGlobal();
            if (Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(s_TraceAt.X, s_TraceAt.Y)) > s_TraceRadius) return;
            var t = c.AttackTarget as Creature;
            string d = t != null && !t.IsDead ? c.GetDistanceToTarget().ToString("F1") : "-";
            float hp = c.Health.MaxValue > 0 ? 100f * c.Health.Current / c.Health.MaxValue : 0f;
            Mod.Log.Info($"[MonsterAI] trace t={rt:F1} {c.Name} 0x{c.Guid.Full:X8} wcid={c.WeenieClassId} type={c.CreatureType} state={c.MonsterState} mode={st.Mode} atk={c.CurrentAttack} dist={d} hp={hp:F0} x={p.X:F1} y={p.Y:F1} tgt={t?.Name ?? "-"}");
        }

        internal static void Register()
        {
            RegisterProperties();
            if (s_TryRollSpell == null) Mod.Log.Error("[RevivalGuard] MonsterAi: Creature.TryRollSpell not found; casters will not re-roll");
            CommandManager.TryAddCommand(Handle, "monsterai", AccessLevel.Admin, CommandHandlerFlag.None,
                "Monster tactics (RevivalGuard): switches, tuning and counts; 'trace [sec] [m]' logs nearby monsters to ACE_Log.txt.",
                "[trace [seconds] [metres] | trace off]");
            Mod.Log.Info("[RevivalGuard] MonsterAi: casters keep range, archers kite, pack call and spread, morale, healers, leash (monster_ai_* server properties, @monsterai)");
        }

        /// <summary>Makes the switches ordinary ACE properties, so @modifybool / @modifydouble /
        /// @modifystring accept them (they refuse a key missing from the Default*Properties maps,
        /// ReadOnlyDictionaries over private dictionaries; the same route as MansionPk). If this fails
        /// the switches are still read, at their defaults, and only the @modify verbs refuse them.</summary>
        static void RegisterProperties()
        {
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                foreach (var (key, def, desc) in s_Bools) if (!b.ContainsKey(key)) b[key] = new Property<bool>(def, desc);
                var d = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
                var s = Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_FAMILIES)) s[P_FAMILIES] = new Property<string>(FAMILIES_DEFAULT, "MonsterAi: CreatureType names that flee at low health, comma separated");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MonsterAi: switches not registered with @modify* ({e.Message}); they are still read"); }
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Mod.Log.Info(text);
        }

        static void Handle(Session session, params string[] parameters)
        {
            var rt = Timers.RunningTime;
            if (parameters.Length > 0 && parameters[0].Equals("trace", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters.Length > 1 && parameters[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                { s_TraceUntil = 0; Say(session, "[MonsterAI] trace off (ours, not retail)"); return; }
                var p = session?.Player;
                if (p?.Location == null) { Say(session, "[MonsterAI] trace needs a player in the world"); return; }
                double secs = parameters.Length > 1 && double.TryParse(parameters[1], out var sv) ? Math.Clamp(sv, 5, 600) : 60;
                s_TraceRadius = parameters.Length > 2 && float.TryParse(parameters[2], out var rv) ? Math.Clamp(rv, 5f, 200f) : 60f;
                s_TraceBlock = p.Location.Landblock;
                s_TraceAt = p.Location.Indoors ? p.Location.Pos : p.Location.ToGlobal();
                s_TraceUntil = rt + secs;
                Mod.Log.Info($"[MonsterAI] trace start t={rt:F1} by {p.Name} at {p.Location.ToLOCString()} for {secs:F0} s within {s_TraceRadius:F0} m");
                Say(session, $"[MonsterAI] tracing monsters within {s_TraceRadius:F0} m of here for {secs:F0} s into ACE_Log.txt (ours, not retail)");
                return;
            }
            s_CfgNext = 0;
            var cfg = Config(rt);
            Say(session, $"[MonsterAI] (ours, not retail) casters {On(cfg.Caster)}, kite {On(cfg.Kite)}, pack call {On(cfg.Pack)}, spread {On(cfg.Spread)}, morale {On(cfg.Morale)}, healers {On(cfg.Heal)}, leash {On(cfg.Leash)}, log {On(cfg.Log)}");
            Say(session, $"[MonsterAI] caster cast chance >= {cfg.CasterMinChance:F2}, back off inside {cfg.CasterClose:F1} m to {cfg.CasterRange:F0} m, hold {cfg.CasterHold:F0} s; kite inside {cfg.KiteClose:F1} m by {cfg.KiteStep:F0} m every {cfg.KiteCooldown:F0} s; pack {cfg.PackRange:F0} m; flee at {cfg.MoraleHealth:P0} for {cfg.FleeSeconds:F0} s ({cfg.FamiliesRaw}); heal allies under {cfg.HealAlly:P0} within {cfg.HealRange:F0} m every {cfg.HealCooldown:F0} s; leash {cfg.LeashM:F0} m");
            var parts = new List<string>();
            for (int i = 0; i < (int)Tactic.Count; i++) parts.Add($"{(Tactic)i} {Interlocked.Read(ref s_Fired[i])}");
            Say(session, "[MonsterAI] fired since load: " + string.Join(", ", parts));
        }

        static string On(bool b) => b ? "on" : "off";
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class MonsterAiTick
    {
        static int s_Errors;
        static bool Prefix(Creature __instance, double currentUnixTime)
        {
            try { return MonsterAi.Tick(__instance, currentUnixTime); }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref s_Errors) <= 5 || s_Errors % 1000 == 0)
                    Mod.Log.Error($"[RevivalGuard] MonsterAi tick threw for {__instance?.Name} (error {s_Errors}); ACE's own tick runs", e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.Sleep))]
    static class MonsterAiSleep
    {
        static void Prefix(Creature __instance, out bool __state) => __state = __instance.MonsterState == Creature.State.Return;
        static void Postfix(Creature __instance, bool __state)
        {
            try { MonsterAi.OnSleep(__instance, __state); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MonsterAi sleep hook: {e.Message}"); }
        }
    }
}
