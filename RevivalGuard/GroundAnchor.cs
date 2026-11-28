using System;
using ACE.Server.Managers;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
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
