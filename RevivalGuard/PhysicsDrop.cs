using ACE.Server.Physics;
using ACE.Server.Physics.Common;

namespace RevivalGuard
{
    /// <summary>
    /// Drop a placed physics object straight down onto what is under it, with ACE's own transition (the creature's own
    /// spheres, cells, buildings, other objects), as one fall of retail's server physics would. ACE types only, no
    /// Harmony, so the box probe (docs/findings/floating-creatures-2026-10-23.md) compiles it by path.
    /// </summary>
    public static class PhysicsDrop
    {
        /// <summary>True when it moved the object; <paramref name="fell"/> is the metres it came down.</summary>
        public static bool Drop(PhysicsObj po, float max, out float fell, out string why)
        {
            fell = 0f; why = "";
            if (po?.CurCell == null) { why = "no cell"; return false; }
            var from = new Position(po.Position);
            var to = new Position(po.Position);
            to.Frame.Origin.Z -= max;
            var t = po.transition(from, to, false);
            if (t == null) { why = "no valid path down"; return false; }
            var ci = t.CollisionInfo;
            bool walkable = ci.ContactPlaneValid && ci.ContactPlane.Normal.Z >= PhysicsGlobals.FloorZ;
            fell = from.Frame.Origin.Z - t.SpherePath.CurPos.Frame.Origin.Z;
            uint startCell = po.CurCell.ID, landCell = t.SpherePath.CurCell?.ID ?? 0;
            bool indoors = SpawnGroundRule.Indoors(startCell);
            bool landIndoors = SpawnGroundRule.Indoors(landCell);
            if (!SpawnGroundRule.Accept(fell, ci.ContactPlaneValid, walkable, max, indoors, landCell == startCell, landIndoors))
            {
                why = !ci.ContactPlaneValid ? $"nothing within {max:0} m" : !walkable ? "landing too steep"
                    : indoors && landCell != startCell ? $"would leave its room 0x{startCell:X8} for 0x{landCell:X8}"
                    : !indoors && landIndoors ? $"would fall from the landscape into 0x{landCell:X8}" : $"fell {fell:0.00} m";
                return false;
            }
            po.SetPositionInternal(t);
            return true;
        }
    }
}
