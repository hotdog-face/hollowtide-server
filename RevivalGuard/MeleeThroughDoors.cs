using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CLOSED DOOR STOPS A SWORD (owner, 2026-10-07: "i was able to consistently kill things and be killed
    /// through doors. doors should block things from attacking each other when closed").
    ///
    /// ACE's melee reach is a distance with the line of sight only sometimes asked:
    ///   * a player: Player_Melee.HandleActionTargetedMeleeAttack_Inner attacks when
    ///     `dist &lt;= MeleeDistance (0.6) || dist &lt;= StickyDistance (4) &amp;&amp; IsMeleeVisible(target)` -- under
    ///     0.6 m the sight test is skipped, and a door is thinner than that, so two bodies either side of one
    ///     are "touching"; the hit frames then call DamageTarget without looking again;
    ///   * a monster: Monster_Navigation.IsMeleeRange is `GetDistanceToTarget() &lt;= MaxMeleeRange`, no sight
    ///     test at all.
    ///
    /// NOT IsMeleeVisible everywhere: ACE skips it up close for a reason (a physics sweep that starts inside
    /// the other body's cylinder need not report it, and bodies overlap in a scrum), and a monster asks
    /// IsMeleeRange every tick. So the test is narrow: is a CLOSED door between the two? A door counts when
    /// the two stand on opposite sides of its panel (its local +Y is the panel's normal) and the line between
    /// them crosses the panel within DOOR_HALF_WIDTH of its centre, at a height near it. Doors are listed per
    /// landblock and refreshed every DOOR_REFRESH seconds; an open door (IsOpen) never blocks.
    ///
    /// A monster is then not in melee range of a target behind a closed door, and a player's melee hit on one
    /// does nothing. Arrows and bolts are left alone: projectiles collide with the door on their own.
    /// </summary>
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

        // OUT OF REACH WHEN THE BLOW LANDS (owner, 2026-10-07: "bronnoc was attacking me and i ran away, his
        // swings kept hitting me even after i put a lot of distance between us"). ACE measures reach when the
        // swing STARTS and never again: the hit frames, up to a second later, land wherever the target has run.
        // So at the hit: a player's blow needs the target within StickyDistance (4 m) plus PLAYER_SLACK, a
        // monster's within MaxMeleeRange (0.75 m) plus MONSTER_SLACK (a monster's miss reads as an evade).
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
                if (target == null || damageSource?.ProjectileSource != null) return true;   // a projectile: physics decides
                if (!DoorBetween(__instance, target) && !OutOfReach(__instance, target, Player.StickyDistance + PLAYER_SLACK)) return true;
                __result = null;                                                              // swung at a door, or at air
                // SAY SO (PvP sweep 2026-10-08: a blocked blow printed nothing on either client). Once per
                // TELL_EVERY seconds per attacker, so an auto-repeat chain does not flood the chat.
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
                if (__result == null || attacker == null || attacker is Player || defender == null) return;
                if (damageSource?.ProjectileSource != null) return;
                if (!DoorBetween(attacker, defender) && !OutOfReach(attacker, defender, Creature.MaxMeleeRange + MONSTER_SLACK)) return;
                __result.Evaded = true;
                __result.Damage = 0f;
            }
        }
    }
}
