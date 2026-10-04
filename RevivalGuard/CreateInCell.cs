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
    /// <summary>
    /// @CREATE PUTS ITS OBJECT INSIDE A CELL, NOT INSIDE A WALL.
    ///
    /// ACE's CreateObjectForCommand (AdminCommands.cs:2482-2487) places the new object with
    /// Position.InFrontOf: 5 m ahead for a creature, UseRadius (at least 2 m) for anything else.
    /// InFrontOf (ACE.Entity/Position.cs:95) moves x/y along the heading but KEEPS THE PLAYER'S CELL
    /// ID. GetCell() then tries to repair the cell: indoors, GetIndoorCell sweeps every EnvCell of the
    /// landblock with point_in_cell (AdjustCell.GetCell) and, when no cell contains the point,
    /// returns p.Cell -- the player's own cell -- for a point that is not in it. Five metres ahead in
    /// a dungeon corridor is usually rock, so the object goes into the world with a cell id and a
    /// point that disagree. That is what "creatures cannot be spawned in Hotel Swank's basement"
    /// was: not a landblock restriction, just the room being shorter than five metres.
    ///
    /// A point that is in no cell is worse than a miss: it wedges the client that receives it
    /// (WHERE and FIND keep answering, later teleports are silently ignored, only a relog clears it).
    ///
    /// SO THE DESTINATION IS CHECKED WITH THE SAME TEST, EnvCell.point_in_cell on the cell the
    /// position claims, after ACE has done its own placement. Outdoors nothing changes: a landscape
    /// cell always contains its point, and five metres ahead is what everyone's hands know. Indoors,
    /// when ACE's spot fails the test, the patch steps back toward the player along the same heading
    /// (2.5 m, then 1 m) and takes the first point that IS inside a cell; if none is, the object is
    /// placed at the player's own position and cell, which the player is demonstrably standing in.
    /// That is the whole search: two shorter tries on the same line, then where you stand.
    ///
    /// The admin is told in chat when the spot moved. An admin command's feedback is ours, not
    /// retail's; retail's HandleFailureEvent has no line for this.
    ///
    /// Patched on AdminCommands, which declares CreateObjectForCommand (private static, so by name).
    /// @create, @createliveops and @ci all go through it.
    /// </summary>
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

        /// <summary>The test the project uses everywhere: is the point inside the cell the position
        /// names? Outdoor (landscape) cells are not EnvCells and are not asked.</summary>
        static bool InACell(Position p)
        {
            var cell = ACE.Server.Physics.Common.LScape.get_landcell(p.Cell) as ACE.Server.Physics.Common.EnvCell;
            if (cell == null) return false;
            try { return cell.point_in_cell(p.Pos); }
            catch { return false; }    // a cell whose geometry is not loaded: treat as not in it
        }
    }
}
