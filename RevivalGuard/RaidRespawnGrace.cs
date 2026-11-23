using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A RESPAWN AT A RAIDED LIFESTONE IS NOT A DEATH LOOP (live 2026-11-22: a level 157 bound at Candeth Keep died 7 times in
    /// 83 s to the T'thuun wave standing on the stone). For a short window after a death, raid creatures (T'thuun, Holtburg,
    /// Hero raids) do not pick that player. Hitting a raid creature ends the window at once. Players are never filtered, so
    /// PK fights stay Darktide; ACE's own lifestone protection is untouched. Switches: rg_raid_respawn_grace (on),
    /// rg_raid_respawn_grace_s (30).
    /// </summary>
    static class RaidRespawnGrace
    {
        internal const string P_ON = "rg_raid_respawn_grace", P_SECONDS = "rg_raid_respawn_grace_s";
        const long DEFAULT_S = 30;
        const double LOAD_ALLOWANCE_S = 30;   // the clock restarts on arrival; this only covers a client still loading

        sealed class Window { public double Until; public bool Arrived; }
        static readonly ConcurrentDictionary<uint, Window> s_Windows = new ConcurrentDictionary<uint, Window>();

        static bool On { get { try { return PropertyManager.GetBool(P_ON, true).Item; } catch { return false; } } }
        static double Seconds { get { try { return Math.Clamp(PropertyManager.GetLong(P_SECONDS, DEFAULT_S).Item, 1, 120); } catch { return DEFAULT_S; } } }
        static double Now => ACE.Common.Time.GetUnixTime();

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard RaidRespawnGrace: raid creatures leave a freshly respawned player alone for a short window");
            try
            {
                var l = MonsterAi.Inner(ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_SECONDS)) l[P_SECONDS] = new ACE.Server.Managers.Property<long>(DEFAULT_S, "RevivalGuard RaidRespawnGrace: the window in seconds (1 to 120)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {P_SECONDS}: not registered with @modifylong ({e.Message}); read with its default"); }
        }

        internal static bool IsRaider(Creature c) =>
            c != null && !(c is Player) && (TthuunRaids.IsRaider(c) || TownRaid.IsRaider(c) || HeroRaids.IsRaider(c));

        internal static bool Covers(Player p)
        {
            if (p == null || !s_Windows.TryGetValue(p.Guid.Full, out var w)) return false;
            if (Now < w.Until) return On;
            s_Windows.TryRemove(p.Guid.Full, out _);
            return false;
        }

        internal static void OnDeathTeleport(Player p)
        {
            if (!On) return;
            foreach (var old in s_Windows.Where(kv => kv.Value.Until <= Now).Select(kv => kv.Key).ToList()) s_Windows.TryRemove(old, out _);
            s_Windows[p.Guid.Full] = new Window { Until = Now + Seconds + LOAD_ALLOWANCE_S };
        }

        /// <summary>The body is back in the world: the window runs from here, and raiders already on this player let go.</summary>
        internal static void OnArrived(Player p)
        {
            if (!s_Windows.TryGetValue(p.Guid.Full, out var w) || w.Arrived || !Covers(p)) return;
            w.Arrived = true;
            w.Until = Now + Seconds;
            int released = 0;
            foreach (var c in p.PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeCreature())
            {
                if (c.AttackTarget != p || !IsRaider(c)) continue;
                released++;
                var cc = c;
                var chain = new ActionChain();
                chain.AddAction(cc, () =>
                {
                    try
                    {
                        if (cc.IsDead || cc.IsDestroyed || cc.AttackTarget != p || !Covers(p)) return;
                        cc.AttackTarget = null;
                        cc.FindNextTarget();
                    }
                    catch (Exception e) { TickGuard.Report("RaidRespawnGrace.Release", e); }
                });
                chain.EnqueueChain();
            }
            Mod.Log.Info($"[RevivalGuard] RaidRespawnGrace: {p.Name} 0x{p.Guid.Full:X8} back at 0x{p.Location?.Cell:X8}, {Seconds:0} s, {released} raider(s) let go");
        }

        internal static void OnAttack(Player p, Creature monster)
        {
            if (IsRaider(monster) && s_Windows.TryRemove(p.Guid.Full, out _))
                Mod.Log.Info($"[RevivalGuard] RaidRespawnGrace: {p.Name} 0x{p.Guid.Full:X8} attacked {monster.Name}; window over");
        }

        internal static void FilterTargets(Creature c, List<Creature> targets)
        {
            if (targets == null || targets.Count == 0 || s_Windows.IsEmpty || !IsRaider(c)) return;
            targets.RemoveAll(t => t is Player p && Covers(p));
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ThreadSafeTeleportOnDeath))]
    static class RaidRespawnGraceDeath
    {
        static void Postfix(Player __instance)
        {
            try { RaidRespawnGrace.OnDeathTeleport(__instance); }
            catch (Exception e) { TickGuard.Report("RaidRespawnGrace.Death", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnTeleportComplete))]
    static class RaidRespawnGraceArrived
    {
        static void Postfix(Player __instance)
        {
            try { if (!__instance.Teleporting) RaidRespawnGrace.OnArrived(__instance); }
            catch (Exception e) { TickGuard.Report("RaidRespawnGrace.Arrived", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnAttackMonster))]
    static class RaidRespawnGraceAttack
    {
        static void Prefix(Player __instance, Creature monster)
        {
            try { RaidRespawnGrace.OnAttack(__instance, monster); }
            catch (Exception e) { TickGuard.Report("RaidRespawnGrace.Attack", e); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.AlertMonster))]
    static class RaidRespawnGraceAlert
    {
        static bool Prefix(Creature __instance, Creature monster, ref bool __result)
        {
            try
            {
                if (__instance is Player p && RaidRespawnGrace.Covers(p) && RaidRespawnGrace.IsRaider(monster)) { __result = false; return false; }
            }
            catch (Exception e) { TickGuard.Report("RaidRespawnGrace.Alert", e); }
            return true;
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.GetAttackTargets))]
    static class RaidRespawnGraceTargets
    {
        static void Postfix(Creature __instance, List<Creature> __result)
        {
            try { RaidRespawnGrace.FilterTargets(__instance, __result); }
            catch (Exception e) { TickGuard.Report("RaidRespawnGrace.Targets", e); }
        }
    }
}
