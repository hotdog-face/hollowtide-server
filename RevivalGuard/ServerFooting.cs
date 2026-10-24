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
            // the pose and the animation queue: getting up from a sit or sleep queues seconds of links before the use
            string pose = "?";
            try
            {
                var mi = po.MovementManager?.MotionInterpreter;
                var q = po.PartArray?.MotionTableManager?.PendingAnimations;
                pose = $"{(ACE.Entity.Enum.MotionCommand)(mi?.InterpretedState.ForwardCommand ?? 0)}, queue {q?.Count ?? 0}"
                     + (q != null && q.Count > 0 ? $" ({string.Join(",", q.Select(a => ((ACE.Entity.Enum.MotionCommand)a.Motion).ToString()).Take(4))})" : "");
            }
            catch { }
            // THE TICK GATES (261024-0003): a body whose queue never drains on walkable ground is not being advanced at all.
            // Player.UpdateObjectPhysics -> update_object (CurCell, Parent, Frozen, UpdateTime) -> UpdateObjectInternal
            // (Active) -> UpdatePositionInternal (Hidden) -> PartArray.Update. Offline every input tried drains it.
            string gate = "";
            try
            {
                var seq = po.PartArray?.Sequence;
                var head = po.PartArray?.MotionTableManager?.PendingAnimations?.FirstOrDefault();
                gate = $"; gates: cell {(po.CurCell != null ? "set" : "NULL")}, active {(po.TransientState & TransientStateFlags.Active) != 0}, "
                     + $"hidden {(po.State & ACE.Entity.Enum.PhysicsState.Hidden) != 0}, frozen {(po.State & ACE.Entity.Enum.PhysicsState.Frozen) != 0}, parent {po.Parent != null}, "
                     + $"animating {po.IsAnimating}, first cyclic {seq?.is_first_cyclic()}, moving/animating {po.IsMovingOrAnimating}, "
                     + $"update lag {ACE.Server.Physics.Common.PhysicsTimer.CurrentTime - po.UpdateTime:0.00}s"
                     + (head != null ? $", head {(ACE.Entity.Enum.MotionCommand)head.Motion}/{head.NumAnims}" : "");
            }
            catch { }
            return $"contact {(po.TransientState & TransientStateFlags.Contact) != 0}, walkable {(po.TransientState & TransientStateFlags.OnWalkable) != 0}, "
                 + $"plane z {po.ContactPlane.Normal.Z:0.00}, velocity {po.Velocity.Length():0.0}, cell {po.Position.ObjCellID:X8}, creatures within 2 m {near}, "
                 + $"pose {pose}{gate}";
        }
    }
}
