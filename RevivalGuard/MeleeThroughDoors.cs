using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A closed door stops a sword.</summary>
    public static class MeleeThroughDoors
    {
        const float DOOR_HALF_WIDTH = 1.0f;   // metres either side of the door's centre the line may cross
        const float DOOR_HEIGHT = 3.0f;       // the crossing must be within this of the door's base height
        const double DOOR_REFRESH = 10.0;     // seconds between re-reading a landblock's doors

        sealed class Doors { public List<Door> List = new List<Door>(); public DateTime At; }
        static readonly ConditionalWeakTable<Landblock, Doors> s_Doors = new ConditionalWeakTable<Landblock, Doors>();

        static List<Door> DoorsOf(Landblock lb)
        {
            var d = s_Doors.GetValue(lb, _ => new Doors { At = DateTime.MinValue });
            lock (d)
            {
                if ((DateTime.UtcNow - d.At).TotalSeconds > DOOR_REFRESH)
                {
                    var list = new List<Door>();
                    foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                        if (wo is Door door && door.Location != null) list.Add(door);
                    d.List = list;
                    d.At = DateTime.UtcNow;
                }
                return d.List;
            }
        }

        /// <summary>True when a closed door stands between the two bodies.</summary>
        public static bool DoorBetween(WorldObject a, WorldObject b)
        {
            try
            {
                var lb = a?.CurrentLandblock;
                if (lb == null || b?.Location == null || a.Location == null) return false;
                if (a.Location.LandblockId.Landblock != b.Location.LandblockId.Landblock) return false;
                var pa = a.Location.Pos; var pb = b.Location.Pos;
                foreach (var door in DoorsOf(lb))
                {
                    if (door.IsOpen) continue;
                    var pd = door.Location.Pos;
                    var rot = door.Location.Rotation;
                    var n = Vector3.Transform(Vector3.UnitY, rot);   // the panel's normal
                    var w = Vector3.Transform(Vector3.UnitX, rot);   // along the panel
                    float sa = Vector3.Dot(pa - pd, n), sb = Vector3.Dot(pb - pd, n);
                    if (sa * sb >= 0f) continue;                     // same side
                    float t = sa / (sa - sb);
                    var cross = pa + (pb - pa) * t;
                    if (Math.Abs(Vector3.Dot(cross - pd, w)) > DOOR_HALF_WIDTH) continue;
                    float dz = cross.Z - pd.Z;
                    if (dz < -1f || dz > DOOR_HEIGHT) continue;
                    return true;
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] door check: {e.Message}"); }
            return false;
        }

        [HarmonyPatch(typeof(Creature), nameof(Creature.IsMeleeRange))]
        static class MonsterReach
        {
            static void Postfix(Creature __instance, ref bool __result)
            {
                if (!__result) return;
                var target = __instance.AttackTarget;
                if (target != null && target != __instance && DoorBetween(__instance, target)) __result = false;
            }
        }

        // Out of reach when the blow lands.
        const float PLAYER_SLACK = 0.5f, MONSTER_SLACK = 1.25f;
        const double TELL_EVERY = 2.0;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime> s_Told = new System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime>();

        static bool OutOfReach(Creature attacker, Creature target, float reach)
        {
            try
            {
                if (attacker?.PhysicsObj == null || target?.PhysicsObj == null) return false;
                return attacker.GetCylinderDistance(target) > reach;
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.DamageTarget))]
        static class PlayerHit
        {
            static bool Prefix(Player __instance, Creature target, WorldObject damageSource, ref DamageEvent __result)
            {
                try { return Check(__instance, target, damageSource, ref __result); }
                catch (Exception e) { TickGuard.Report("MeleeThroughDoors.PlayerHit", e); return true; }
            }

            static bool Check(Player __instance, Creature target, WorldObject damageSource, ref DamageEvent __result)
            {
                if (target == null || damageSource?.ProjectileSource != null) return true;   // a projectile: physics decides
                if (!DoorBetween(__instance, target) && !OutOfReach(__instance, target, Player.StickyDistance + PLAYER_SLACK)) return true;
                __result = null;                                                              // swung at a door, or at air
                // Say so.
                var now = DateTime.UtcNow;
                if (!s_Told.TryGetValue(__instance.Guid.Full, out var last) || (now - last).TotalSeconds >= TELL_EVERY)
                {
                    s_Told[__instance.Guid.Full] = now;
                    __instance.Session?.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(
                        $"{target.Name} is out of reach.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(DamageEvent), nameof(DamageEvent.CalculateDamage))]
        static class MonsterHit
        {
            static void Postfix(Creature attacker, Creature defender, WorldObject damageSource, DamageEvent __result)
            {
                try
                {
                    if (__result == null || attacker == null || attacker is Player || defender == null) return;
                    if (damageSource?.ProjectileSource != null) return;
                    if (!DoorBetween(attacker, defender) && !OutOfReach(attacker, defender, Creature.MaxMeleeRange + MONSTER_SLACK)) return;
                    __result.Evaded = true;
                    __result.Damage = 0f;
                }
                catch (Exception e) { TickGuard.Report("MeleeThroughDoors.MonsterHit", e); }
            }
        }
    }
}
