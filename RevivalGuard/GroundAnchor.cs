using System;
using ACE.Server.Managers;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// JUMPING UP A HILL IS NOT A Z HACK (report 261024-0030, "Teleporting while jumping a lot is getting old").
    /// ACE's z-pos check snaps a jumping player back to LastGroundPos when a report is more than 10 m above it. LastGroundPos
    /// is only the last AutonomousPosition sent on walkable ground, and retail's client (and ours, which follows it:
    /// SendPositionEvent @006b4770, bridge note 139) sends none in the air or on a slope too steep to stand on, and a
    /// MoveToState resets its one-second pacing. Bouncing up a Direlands slope on the box, a run went 8.6 s with no ground
    /// report. Live 2026-10-24 14:38 and 14:43 UTC: The Forsaken Moustache was snapped back 175 m and 142 m to a spot 17 m
    /// below him, and MovementGuard then snapped the next report again.
    /// Outdoors the terrain under the report is known: within 10 m of it the player is on a hill, not flying, so
    /// LastGroundPos is moved to the terrain under the report before ACE checks. Higher than that, and indoors, ACE's
    /// check stands. Switch: rg_ground_anchor.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    static class GroundAnchor
    {
        const string P_ON = "rg_ground_anchor";

        static void Prefix(Player __instance, ACE.Entity.Position newPosition, bool forceUpdate)
        {
            try
            {
                var p = __instance;
                var g = p?.LastGroundPos;
                if (forceUpdate || g == null || newPosition == null || p.Teleporting) return;
                if (newPosition.PositionZ - g.PositionZ <= GroundAnchorRule.RISE) return;   // the common case: nothing to do
                bool outdoors = (newPosition.Cell & 0xFFFF) < 0x100;
                var lb = outdoors ? LScape.get_landblock(newPosition.Cell) : null;
                if (lb == null) return;
                float terrain = lb.GetZ(newPosition.Pos);
                if (!GroundAnchorRule.Reanchor(outdoors, newPosition.PositionZ, g.PositionZ, terrain)) return;
                if (!PropertyManager.GetBool(P_ON, true).Item) return;
                p.LastGroundPos = new ACE.Entity.Position(newPosition) { PositionZ = terrain };
                Mod.Log.Info($"[RevivalGuard] GroundAnchor: {p.Name} (0x{p.Guid.Full:X8}) report {newPosition.PositionZ - g.PositionZ:F1} m above "
                             + $"the last ground report {g.DistanceTo(newPosition):F0} m away, {newPosition.PositionZ - terrain:F1} m above the terrain; "
                             + $"re-anchored at {p.LastGroundPos.ToLOCString()}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] GroundAnchor: {e.Message}"); }
        }
    }
}
