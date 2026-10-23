using System.Linq;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// Is the player's SERVER body standing on walkable ground? ACE's MotionInterp.contact_allows_move refuses every
    /// substate motion (Drink, Eat, Ready, RunForward...) for a creature that is not in Contact and OnWalkable: it only
    /// sets the interpreted state, queues no animation, so no motion-done ever comes. The fast chug (Drink then Ready)
    /// and the walk of a use (MoveToManager's RunForward) both wait on exactly that, and nothing times them out.
    /// docs/findings/use-stuck-2026-10-23.md has the measurement (ACE's own physics driven offline on the box).
    /// </summary>
    static class ServerFooting
    {
        const TransientStateFlags FOOTED = TransientStateFlags.Contact | TransientStateFlags.OnWalkable;

        public static bool Walkable(Player p)
        {
            var po = p?.PhysicsObj;
            return po == null || (po.TransientState & FOOTED) == FOOTED;
        }

        /// <summary>One short log fragment: the footing ACE sees, so the next stuck use says why.</summary>
        public static string Describe(Player p)
        {
            var po = p?.PhysicsObj;
            if (po == null) return "no physics";
            int near = 0;
            try
            {
                var me = po.Position.Frame.Origin;
                near = po.ObjMaint?.GetVisibleObjectsValuesWhere(o => o != null && o.WeenieObj != null && o.WeenieObj.IsCreature
                           && o.ID != po.ID && System.Numerics.Vector3.Distance(o.Position.Frame.Origin, me) < 2.0f).Count ?? 0;
            }
            catch { near = -1; }
            return $"contact {(po.TransientState & TransientStateFlags.Contact) != 0}, walkable {(po.TransientState & TransientStateFlags.OnWalkable) != 0}, "
                 + $"plane z {po.ContactPlane.Normal.Z:0.00}, velocity {po.Velocity.Length():0.0}, cell {po.Position.ObjCellID:X8}, creatures within 2 m {near}";
        }
    }
}
