using System;
using System.Numerics;
using ACE.Server.Managers;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A PK'S SERVER BODY LEFT IN THE AIR AT REST LANDS (ticket 261109-0039, Olivia killed in the Olthoi hive 0x028B after
    /// "You can't do that while in the air!"; before it 261029-0005/0013, 261028-0011). For a FastTick player ACE's IsJumping
    /// is just !OnWalkable, and refuses swings, kits, spells, potions and gems. Player.UpdateObjectPhysics ticks the body only
    /// while it moves, animates or has velocity, and an AutonomousPosition at the same spot runs no physics at all
    /// (UpdatePlayerPosition's distance check). So a body that ends a report off the ground with no velocity (a dungeon ramp,
    /// a crowd of olthoi) is never ticked again: gravity never lands it, every action is refused until the player walks.
    /// Here such a body is ticked as if it moved, so gravity lands it within a few ticks (tools/ace-physprobe `air`: 0.33 s;
    /// as shipped it was still in the air after 12 s). Only when the client's last report said it stands on the ground
    /// (LastContact). Location (the client's) is untouched; only the physics body falls. Switch: rg_ground_settle (on).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateObjectPhysics))]
    static class GroundSettle
    {
        const string P_ON = "rg_ground_settle";
        static double s_LastLog;

        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance; var po = p?.PhysicsObj;
                if (po == null || po.CurCell == null) return;
                bool walkable = (po.TransientState & TransientStateFlags.OnWalkable) != 0;
                if (!GroundSettleRule.Needs(p.FastTick, p.Teleporting, walkable, po.IsMovingOrAnimating, po.Velocity != Vector3.Zero, p.LastContact)) return;
                if (!PropertyManager.GetBool(P_ON, true).Item) return;
                var now = ACE.Common.Time.GetUnixTime();
                if (now - s_LastLog > 10)
                {
                    s_LastLog = now;
                    Mod.Log.Info($"[RevivalGuard] ground settle: {p.Name} (0x{p.Guid.Full:X8}) body off the ground at rest, ticked so it lands ({ServerFooting.Describe(p)})");
                }
                po.update_object();   // UpdatePlayerPhysics' physics step alone (not its PK-logout or turn bits)
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ground settle: {e.Message}"); }
        }
    }

    /// <summary>GroundSettle's rule, pure (tools/modcheck): tick this body although ACE would not. ACE already ticks a body
    /// that moves, animates or has velocity; one in the air on purpose (a jump, a fall) has velocity or told us so.</summary>
    public static class GroundSettleRule
    {
        public static bool Needs(bool fastTick, bool teleporting, bool onWalkable, bool movingOrAnimating, bool hasVelocity, bool clientContact) =>
            fastTick && !teleporting && !onWalkable && !movingOrAnimating && !hasVelocity && clientContact;
    }
}
