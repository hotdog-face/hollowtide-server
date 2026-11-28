using System;
using System.Numerics;
using ACE.Server.Managers;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A PK'S server body left in the air at rest lands.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateObjectPhysics))]
    static class GroundSettle
    {
        internal const string P_ON = "rg_ground_settle";
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

    /// <summary>GroundSettle's rule, pure (tools/modcheck): tick this body although ACE would not.</summary>
    public static class GroundSettleRule
    {
        public static bool Needs(bool fastTick, bool teleporting, bool onWalkable, bool movingOrAnimating, bool hasVelocity, bool clientContact) =>
            fastTick && !teleporting && !onWalkable && !movingOrAnimating && !hasVelocity && clientContact;
    }
}
