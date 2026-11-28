using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Position = ACE.Entity.Position;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(AdminCommands), "CreateObjectForCommand")]
    public static class CreateInCell
    {
        // Shorter distances tried, in order, along the same heading before giving up on "in front".
        static readonly float[] STEPS = { 2.5f, 1.0f };

        static void Postfix(Session session, WorldObject __result)
        {
            var obj = __result;
            var player = session?.Player;
            if (obj?.Location == null || player?.Location == null) return;

            var loc = obj.Location;
            if (!loc.Indoors) return;              // outdoors: five metres ahead is fine, leave it
            if (InACell(loc)) return;              // ACE's spot is inside a cell, leave it

            bool creature = obj.WeenieType == WeenieType.Creature;
            float asked = creature ? 5f : Math.Max(2, obj.UseRadius ?? 2);   // ACE's own distances

            Position placed = null;
            float used = 0f;
            foreach (var d in STEPS)
            {
                if (d >= asked) continue;
                var cand = player.Location.InFrontOf(d, creature);
                cand.LandblockId = new LandblockId(cand.GetCell());
                if (InACell(cand)) { placed = cand; used = d; break; }
            }

            string how;
            if (placed != null)
                how = $"{used:0.#} m in front of you instead of {asked:0.#} m";
            else
            {
                // Where the player stands, keeping the facing ACE chose (a creature faces the player).
                var p = player.Location;
                placed = new Position(p.Cell, p.PositionX, p.PositionY, p.PositionZ,
                                      loc.RotationX, loc.RotationY, loc.RotationZ, loc.RotationW);
                how = "at your own position";
            }

            obj.Location = placed;
            AdminCommands.LastSpawnPos = placed;

            Mod.Log.Info($"[RevivalGuard] @create: {asked:0.#} m in front of {player.Name} in "
                + $"0x{loc.Cell:X8} is not inside a cell; placed {obj.Name} {how} "
                + $"({placed.ToLOCString()})");
            session.Network?.EnqueueSend(new GameMessageSystemChat(
                $"Placed {obj.Name} {how}: {asked:0.#} m ahead is not inside a cell here.",
                ChatMessageType.Broadcast));
        }

        /// <summary>The test the project uses everywhere: is the point inside the cell the position names?</summary>
        static bool InACell(Position p)
        {
            var cell = ACE.Server.Physics.Common.LScape.get_landcell(p.Cell) as ACE.Server.Physics.Common.EnvCell;
            if (cell == null) return false;
            try { return cell.point_in_cell(p.Pos); }
            catch { return false; }    // a cell whose geometry is not loaded: treat as not in it
        }
    }
}
