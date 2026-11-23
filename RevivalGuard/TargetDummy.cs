using System;
using System.Collections.Generic;
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
    /// <summary>
    /// RETAIL'S PYREAL TARGET DRUDGE AS A DAMAGE METER (owner 2026-11-22: "look for existing things turbine made in the game
    /// that let players test damage and re-use it"). Weenie 24888: 10,000 health, Tolerance NoAttack (never fights back),
    /// XpOverride 0, no loot, neutral resistances; retail stood rows of them in the Society strongholds. Placed in Holtburg
    /// and outside Arwic's south gate by the 2026-11-25 target-dummies datafix; every retail placement reports too.
    ///
    /// With rg_target_dummy on: every damage a player does to one (melee, missile, war and life projectiles, harm spells,
    /// damage over time: all pass DamageHistory.Add) is summed per player and per dummy, and 5 s after that player's last
    /// damage (or evaded swing, or resisted spell) they get two lines: total, time, damage per second, hits, biggest hit,
    /// critical hits, ticks over time, evaded, resisted. Testers on one dummy each get their own. The dummy's health is
    /// topped up around every hit, so it never dies (Die and OnDeath are refused as a last guard, @smite included): no
    /// kill, no XP, no corpse, and the next tester finds it whole. A single blow counts at most its 10,000 health (ACE
    /// records what the vital lost). When nobody is testing on it any more, its enchantments (a tester's vulnerabilities
    /// and DoTs) are cleared. Off: ACE as is (the drudge can be killed; 0 XP, no loot). Proven on shard2 2026-11-22.
    /// </summary>
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
        }

        static readonly object s_Lock = new object();
        // (dummy guid, player guid) -> that player's run on that dummy
        static readonly Dictionary<(uint, uint), Run> s_Sessions = new Dictionary<(uint, uint), Run>();

        // Set by the hit that is about to reach DamageHistory.Add on this thread (melee/missile TakeDamage, a projectile's
        // DamageTarget); an Add with neither is damage over time or a harm spell.
        [ThreadStatic] static bool t_Crit;
        [ThreadStatic] static bool t_Direct;

        static void Refill(Creature c)
        {
            var max = c.Health.MaxValue;
            if (c.Health.Current < max) c.Health.Current = max;
        }

        static void Record(Creature dummy, Player p, uint amount, bool crit, bool direct, bool miss = false, bool resist = false)
        {
            var key = (dummy.Guid.Full, p.Guid.Full);
            var now = DateTime.UtcNow;
            bool schedule = false;
            lock (s_Lock)
            {
                if (!s_Sessions.TryGetValue(key, out var s)) s_Sessions[key] = s = new Run { First = now };
                s.Last = now;
                s.Total += amount;
                if (resist) s.Resists++; else if (miss) s.Misses++; else if (direct) s.Hits++; else s.Over++;
                if (crit) s.Crits++;
                if (amount > s.Biggest) s.Biggest = amount;
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
            if (p.Session == null || p.IsDestroyed) return;
            foreach (var line in Lines(dummy.Name, s))
                p.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
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

        /// <summary>Every damage a creature records. The meter, and the top-up that keeps the dummy alive.</summary>
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
                    if (amount > 0 && attacker is Player p) Record(c, p, amount, crit, direct);
                }
                catch (Exception e) { Fail("Add", e); }
            }
        }

        /// <summary>Melee and missile hits (and the combined DoT tick, source null): full health before the blow lands, so
        /// the hit is measured whole and Health never reaches 0; the crit flag for the meter.</summary>
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
            static void Postfix(WorldObject __instance, WorldObject target, bool __result)
            {
                try
                {
                    if (!__result || !(__instance is Player p) || !Is(target)) return;
                    Record((Creature)target, p, 0, false, true, resist: true);
                }
                catch (Exception e) { Fail("Resisted", e); }
            }
        }

        /// <summary>War and life projectiles.</summary>
        [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.DamageTarget))]
        static class Bolt
        {
            static void Prefix(Creature target, bool critical)
            {
                try
                {
                    if (!Is(target)) return;
                    Refill(target);
                    t_Crit = critical;
                    t_Direct = true;
                }
                catch (Exception e) { Fail("DamageTarget", e); }
            }

            static void Postfix() { t_Crit = t_Direct = false; }
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
