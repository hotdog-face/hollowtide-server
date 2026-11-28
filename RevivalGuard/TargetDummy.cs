using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Retail's pyreal target drudge as a damage meter.</summary>
    public static class TargetDummy
    {
        internal const string P_ON = "rg_target_dummy";
        internal const uint PYREAL_TARGET_DRUDGE = 24888;
        const double QUIET_S = 5.0;

        internal static bool On
        {
            get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } }
        }

        internal static bool Is(WorldObject wo) => wo is Creature c && !(wo is Player) && c.WeenieClassId == PYREAL_TARGET_DRUDGE && On;

        sealed class Run
        {
            public DateTime First, Last;
            public long Total;
            public int Hits, Crits, Over, Misses, Resists;
            public uint Biggest;
            public bool Pending;
            public readonly Dictionary<string, long> Src = new Dictionary<string, long>();
            public readonly Dictionary<string, uint> SkillAt = new Dictionary<string, uint>();   // buffed skill seen per build
        }

        static readonly object s_Lock = new object();
        // (dummy guid, player guid) -> that player's run on that dummy
        static readonly Dictionary<(uint, uint), Run> s_Sessions = new Dictionary<(uint, uint), Run>();

        // Set by the hit that is about to reach DamageHistory.Add on this thread; an Add with neither is damage over time or a harm spell.
        [ThreadStatic] static bool t_Crit;
        [ThreadStatic] static bool t_Direct;
        [ThreadStatic] static Skill t_Skill;   // a projectile's school (its caster may have left Magic mode by the time it lands)

        static void Refill(Creature c)
        {
            var max = c.Health.MaxValue;
            if (c.Health.Current < max) c.Health.Current = max;
        }

        static Skill SkillOf(MagicSchool? school) => school switch
        {
            MagicSchool.WarMagic => Skill.WarMagic, MagicSchool.VoidMagic => Skill.VoidMagic, MagicSchool.LifeMagic => Skill.LifeMagic,
            MagicSchool.CreatureEnchantment => Skill.CreatureEnchantment, MagicSchool.ItemEnchantment => Skill.ItemEnchantment,
            _ => Skill.None,
        };

        /// <summary>The build a hit counts for and the buffed skill behind it.</summary>
        static (string kind, uint skill) KindOf(Player p, Skill skill, bool pet)
        {
            try
            {
                if (pet) return (TargetDummyRule.SUMMON, p.GetCreatureSkill(Skill.Summoning)?.Current ?? 0);
                if (skill == Skill.None) skill = p.GetCurrentAttackSkill();
                return (TargetDummyRule.KindOf(skill.ToString()), p.GetCreatureSkill(skill)?.Current ?? 0);
            }
            catch { return (TargetDummyRule.OTHER, 0); }
        }

        static void Record(Creature dummy, Player p, uint amount, bool crit, bool direct, bool miss = false, bool resist = false, Skill skill = Skill.None, bool pet = false)
        {
            var key = (dummy.Guid.Full, p.Guid.Full);
            var now = DateTime.UtcNow;
            var (kind, level) = KindOf(p, skill, pet);
            bool schedule = false;
            lock (s_Lock)
            {
                if (!s_Sessions.TryGetValue(key, out var s)) s_Sessions[key] = s = new Run { First = now };
                s.Last = now;
                s.Total += amount;
                if (resist) s.Resists++; else if (miss) s.Misses++; else if (direct) s.Hits++; else s.Over++;
                if (crit) s.Crits++;
                if (amount > s.Biggest) s.Biggest = amount;
                s.Src[kind] = (s.Src.TryGetValue(kind, out var had) ? had : 0) + amount;
                if (!s.SkillAt.TryGetValue(kind, out var lv) || level > lv) s.SkillAt[kind] = level;
                if (!s.Pending) { s.Pending = true; schedule = true; }
            }
            if (schedule) Check(dummy, p, QUIET_S);
        }

        static void Check(Creature dummy, Player p, double delay)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(p, () =>
            {
                try { Due(dummy, p); } catch (Exception e) { TickGuard.Report("TargetDummy.Due", e); }
            });
            chain.EnqueueChain();
        }

        static void Due(Creature dummy, Player p)
        {
            var key = (dummy.Guid.Full, p.Guid.Full);
            Run s;
            bool lastOne;
            lock (s_Lock)
            {
                if (!s_Sessions.TryGetValue(key, out s)) return;
                var quiet = (DateTime.UtcNow - s.Last).TotalSeconds;
                if (quiet < QUIET_S - 0.05)
                {
                    Check(dummy, p, QUIET_S - quiet);
                    return;
                }
                s_Sessions.Remove(key);
                lastOne = true;
                foreach (var k in s_Sessions.Keys) if (k.Item1 == key.Item1) { lastOne = false; break; }
            }
            if (lastOne && !dummy.IsDestroyed)
            {
                try { dummy.EnchantmentManager.RemoveAllEnchantments(); Refill(dummy); }
                catch (Exception e) { TickGuard.Report("TargetDummy.Reset", e); }
            }
            try { Log(p, s); } catch (Exception e) { TickGuard.Report("TargetDummy.Log", e); }
            if (p.Session == null || p.IsDestroyed) return;
            foreach (var line in Lines(dummy.Name, s))
                p.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>The run, to the event log (dummy_dps), for the weekly table.</summary>
        static void Log(Player p, Run s)
        {
            if (s.Total <= 0 && s.Misses == 0 && s.Resists == 0) return;
            var secs = (s.Last - s.First).TotalSeconds;
            string build = TargetDummyRule.Dominant(s.Src);
            var spec = new List<string>(); var trained = new List<string>();
            try
            {
                foreach (var kv in p.Skills)
                {
                    if (kv.Value.AdvancementClass == SkillAdvancementClass.Specialized) spec.Add(kv.Key.ToString());
                    else if (kv.Value.AdvancementClass == SkillAdvancementClass.Trained) trained.Add(kv.Key.ToString());
                }
            }
            catch { }
            var sb = new StringBuilder();
            sb.Append(",\"build\":\"").Append(build).Append("\",\"tmpl\":\"").Append(TargetDummyRule.Template(spec, trained)).Append('"')
              .Append(",\"dmg\":").Append(s.Total).Append(",\"secs\":").Append(F(secs)).Append(",\"dps\":").Append(F(TargetDummyRule.Dps(s.Total, secs)))
              .Append(",\"hits\":").Append(s.Hits).Append(",\"crits\":").Append(s.Crits).Append(",\"over\":").Append(s.Over)
              .Append(",\"miss\":").Append(s.Misses).Append(",\"res\":").Append(s.Resists).Append(",\"big\":").Append(s.Biggest)
              .Append(",\"skill\":").Append(s.SkillAt.TryGetValue(build, out var lv) ? lv : 0)
              .Append(",\"src\":{").Append(string.Join(",", s.Src.Select(kv => $"\"{kv.Key}\":{kv.Value}"))).Append('}');
            Telemetry.Emit("dummy_dps", p, sb.ToString());
        }

        static string[] Lines(string name, Run s)
        {
            var secs = (s.Last - s.First).TotalSeconds;
            int n = s.Hits + s.Over;
            string head = $"Your damage to the {name}: {s.Total:N0}";
            head += secs >= 1 ? $" over {secs:0.0} seconds, {s.Total / secs:N1} per second." : (n == 1 ? " in a single blow." : " in an instant.");
            string tail = $"{s.Hits:N0} {(s.Hits == 1 ? "hit" : "hits")}, biggest {s.Biggest:N0}, {s.Crits:N0} critical";
            if (s.Over > 0) tail += $", {s.Over:N0} over time";
            if (s.Misses > 0) tail += $", {s.Misses:N0} evaded";
            if (s.Resists > 0) tail += $", {s.Resists:N0} resisted";
            return new[] { head, tail + "." };
        }

        static void Fail(string where, Exception e) => TickGuard.Report("TargetDummy." + where, e);

        /// <summary>Every damage a creature records.</summary>
        [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
        static class Damage
        {
            static void Postfix(DamageHistory __instance, WorldObject attacker, uint amount)
            {
                bool crit = t_Crit, direct = t_Direct;
                t_Crit = t_Direct = false;
                try
                {
                    var c = __instance.Creature;
                    if (!Is(c)) return;
                    Refill(c);
                    var skill = t_Skill;
                    t_Skill = Skill.None;
                    if (amount == 0) return;
                    if (attacker is Player p) Record(c, p, amount, crit, direct, skill: skill);
                    else if (attacker is CombatPet pet && pet.P_PetOwner is Player owner) Record(c, owner, amount, crit, direct, pet: true);
                }
                catch (Exception e) { Fail("Add", e); }
            }
        }

        [HarmonyPatch(typeof(Creature), nameof(Creature.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(bool) })]
        static class Hit
        {
            static void Prefix(Creature __instance, WorldObject source, bool crit)
            {
                try
                {
                    if (!Is(__instance)) return;
                    Refill(__instance);
                    t_Crit = crit;
                    t_Direct = source != null;
                }
                catch (Exception e) { Fail("TakeDamage", e); }
            }

            static void Postfix(Creature __instance)
            {
                t_Crit = t_Direct = false;
                try { if (Is(__instance)) Refill(__instance); } catch (Exception e) { Fail("TakeDamage.Post", e); }
            }
        }

        /// <summary>A melee or missile swing it evaded: counted, so the report shows how often the attacks land.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.DamageTarget), new[] { typeof(Creature), typeof(WorldObject) })]
        static class Evaded
        {
            static void Postfix(Player __instance, Creature target, DamageEvent __result)
            {
                try
                {
                    if (__result == null || __result.HasDamage || !Is(target)) return;
                    Record(target, __instance, 0, false, true, miss: true);
                }
                catch (Exception e) { Fail("Evaded", e); }
            }
        }

        /// <summary>A spell it resisted: counted, and it keeps a caster's run going.</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.TryResistSpell), new[] { typeof(WorldObject), typeof(Spell), typeof(WorldObject), typeof(bool) })]
        static class Resisted
        {
            static void Postfix(WorldObject __instance, WorldObject target, Spell spell, bool __result)
            {
                try
                {
                    if (!__result || !(__instance is Player p) || !Is(target)) return;
                    Record((Creature)target, p, 0, false, true, resist: true, skill: SkillOf(spell?.School));
                }
                catch (Exception e) { Fail("Resisted", e); }
            }
        }

        /// <summary>War and life projectiles.</summary>
        [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.DamageTarget))]
        static class Bolt
        {
            static void Prefix(SpellProjectile __instance, Creature target, bool critical)
            {
                try
                {
                    if (!Is(target)) return;
                    Refill(target);
                    t_Crit = critical;
                    t_Direct = true;
                    t_Skill = SkillOf(__instance.Spell?.School);
                }
                catch (Exception e) { Fail("DamageTarget", e); }
            }

            static void Postfix() { t_Crit = t_Direct = false; t_Skill = Skill.None; }
        }

        /// <summary>The last guard: whatever got its health to 0 (a single blow over 10,000), it does not die.</summary>
        [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
        static class NoDeath
        {
            static bool Prefix(Creature __instance, ref DeathMessage __result)
            {
                try
                {
                    if (!Is(__instance)) return true;
                    Refill(__instance);
                    __result = null;
                    return false;
                }
                catch (Exception e) { Fail("OnDeath", e); return true; }
            }
        }

        /// <summary>Die() and @smite both end here.</summary>
        [HarmonyPatch(typeof(Creature), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
        static class NoDie
        {
            static bool Prefix(Creature __instance)
            {
                try
                {
                    if (!Is(__instance)) return true;
                    Refill(__instance);
                    return false;
                }
                catch (Exception e) { Fail("Die", e); return true; }
            }
        }

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard TargetDummy: a player hitting a Pyreal Target Drudge gets a damage report (total, hits, per second, biggest, crits) 5 s after they stop; the drudge never dies (false: ACE, no report, it can be killed)");
        }
    }
}
