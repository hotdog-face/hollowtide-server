using System.IO;
using System.Linq;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Rooms and land added before the DAT.</summary>
    [HarmonyPatch(typeof(DatManager), nameof(DatManager.Initialize))]
    public static class CellOverlay
    {
        const uint MAGIC = 0x4C455648;   // "hvel"

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

    /// <summary>A room nested in another holds its own space.</summary>
    [HarmonyPatch(typeof(ACE.Server.Physics.PhysicsObj), nameof(ACE.Server.Physics.PhysicsObj.FindObjCollisions))]
    public static class CellOverlayShell
    {
        const ACE.Entity.Enum.PhysicsState SHELL = (ACE.Entity.Enum.PhysicsState)0x2000;

        static void Prefix(ACE.Server.Physics.PhysicsObj __instance, ACE.Server.Physics.Animation.Transition transition, out bool __state)
        {
            __state = (__instance.State & SHELL) != 0 && !transition.SpherePath.BuildingCheck;
            if (__state) transition.SpherePath.BuildingCheck = true;
        }

        static void Finalizer(ACE.Server.Physics.Animation.Transition transition, bool __state)
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
