using System.IO;
using System.Linq;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ROOMS AND LAND ADDED BEFORE THE DAT (the Staff Base's enterable buildings and the building showroom island,
    /// 2026-11-02). Records may also be a block's terrain (0xFFFF, a raised island) or building list (0xFFFE). A house's
    /// rooms belong to its outdoor landblock, so its model placed in a dungeon is a shell. Each *.bin in
    /// Mods/RevivalGuard/cell-overlay/ holds records in client_cell_1.dat's own format: copies of the house's rooms with new
    /// ids after the block's last, the block's own rooms with a doorway to them, and its 0xFFFE record with the higher
    /// count. They go into DatManager.CellDat's file cache, which ReadFromDat consults before the DAT, so landblock loads,
    /// physics and AdjustCell all see them. THE SAME FILE is the client's StreamingAssets/cell-overlay.bin
    /// (AcCellOverlay); change both together. Made by tools/cell-overlay.
    ///
    /// ACE loads mods before it opens the DATs (Program: ModManager.Initialize, then DatManager.Initialize), so this
    /// runs after DatManager.Initialize rather than from Mod.Initialize. A restart applies a changed file.
    /// </summary>
    [HarmonyPatch(typeof(DatManager), nameof(DatManager.Initialize))]
    public static class CellOverlay
    {
        const uint MAGIC = 0x4C455648;   // "HVEL"

        static void Postfix()
        {
            try
            {
                var dir = ModFolder.Sub("cell-overlay");
                if (dir == null || DatManager.CellDat == null) return;
                int total = 0;
                foreach (var path in Directory.GetFiles(dir, "*.bin").OrderBy(p => Path.GetFileName(p), System.StringComparer.Ordinal))   // by name: a later file's record replaces an earlier one's
                {
                    var r = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
                    if (r.ReadUInt32() != MAGIC) { Mod.Log.Error($"[RevivalGuard] cell overlay {Path.GetFileName(path)}: not an overlay"); continue; }
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        uint id = r.ReadUInt32();
                        var bytes = r.ReadBytes(r.ReadInt32());
                        FileType obj = (id & 0xFFFF) == 0xFFFF ? new CellLandblock() : (id & 0xFFFF) == 0xFFFE ? new LandblockInfo() : new EnvCell();
                        using (var br = new BinaryReader(new MemoryStream(bytes))) obj.Unpack(br);
                        DatManager.CellDat.FileCache[id] = obj;
                    }
                    total += n;
                    Mod.Log.Info($"[RevivalGuard] cell overlay {Path.GetFileName(path)}: {n} record(s) before the DAT");
                }
                if (total == 0) Mod.Log.Info("[RevivalGuard] cell overlay: none");
            }
            catch (System.Exception e) { Mod.Log.Error($"[RevivalGuard] cell overlay failed, rooms come from the DAT alone: {e.Message}"); }
        }
    }

    /// <summary>
    /// A ROOM NESTED IN ANOTHER holds its own space. The overlay's house rooms lie inside the dungeon room they stand in, and
    /// find_cell_list takes the first room holding the point, the current one first, so a body would never walk in. A doorway
    /// with flag 8 (not retail; only the overlay writes it) names a room inside this one, and this one then gives up the
    /// points that room holds. The client's Hollowtide.Physics EnvCell.PointInCell does the same.
    /// </summary>
    /// <summary>
    /// A HOUSE SHELL PLACED AS AN OBJECT collides like a building (owner ticket 261103-0059: "able to run into the walls and
    /// jump through the roof of this cottage"). An object with PhysicsState 0x2000 (retail's Unused2; only the overlay
    /// cottages' shell weenies set it) is tested with SpherePath.BuildingCheck, as BuildingObj.find_building_collisions
    /// does: a body touching the house's rooms meets only the shell's walls and roof, never its solid inside, so the shell
    /// is solid from outside and the door stays open. The client's Hollowtide.Physics PhysicsState.BuildingShell does the same.
    /// </summary>
    [HarmonyPatch(typeof(ACE.Server.Physics.PhysicsObj), nameof(ACE.Server.Physics.PhysicsObj.FindObjCollisions))]
    public static class CellOverlayShell
    {
        const ACE.Entity.Enum.PhysicsState SHELL = (ACE.Entity.Enum.PhysicsState)0x2000;

        static void Prefix(ACE.Server.Physics.PhysicsObj __instance, ACE.Server.Physics.Animation.Transition transition, out bool __state)
        {
            __state = (__instance.State & SHELL) != 0 && !transition.SpherePath.BuildingCheck;
            if (__state) transition.SpherePath.BuildingCheck = true;
        }

        static void Postfix(ACE.Server.Physics.Animation.Transition transition, bool __state)
        {
            if (__state) transition.SpherePath.BuildingCheck = false;
        }
    }

    [HarmonyPatch(typeof(ACE.Server.Physics.Common.EnvCell), nameof(ACE.Server.Physics.Common.EnvCell.point_in_cell))]
    public static class CellOverlayNested
    {
        static void Postfix(ACE.Server.Physics.Common.EnvCell __instance, System.Numerics.Vector3 point, ref bool __result)
        {
            if (!__result || __instance.Portals == null) return;
            foreach (var portal in __instance.Portals)
            {
                if (((int)portal.Flags & 8) == 0) continue;
                uint id = (__instance.ID & 0xFFFF0000) | portal.OtherCellId;
                var inner = __instance.GetVisible(id) ?? ACE.Server.Physics.Common.LScape.get_landcell(id) as ACE.Server.Physics.Common.EnvCell;
                if (inner != null && inner.point_in_cell(point)) { __result = false; return; }
            }
        }
    }
}
