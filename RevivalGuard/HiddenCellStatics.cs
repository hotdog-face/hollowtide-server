using System.Collections.Concurrent;
using HarmonyLib;
using PhysEnvCell = ACE.Server.Physics.Common.EnvCell;
using DatEnvCell = ACE.DatLoader.FileTypes.EnvCell;

namespace RevivalGuard
{
    /// <summary>
    /// CELL FURNITURE TAKEN OUT OF THE WORLD. An EnvCell's static objects (tables, benches, shelves)
    /// come from client_cell_1.dat, not from the database: ACE's physics EnvCell copies them from the
    /// DAT in its constructor and `init_static_objects` builds a PhysicsObj for each, so no SQL and
    /// no admin command can remove one. Hiding one only in the client left an invisible wall, because
    /// the server still collided with it.
    ///
    /// So the constructor's lists are filtered here, before `init_static_objects` ever sees them, for
    /// exactly the (cell, index in the DAT list, model id) entries below; all three must match, so a
    /// DAT whose list differs removes nothing. THE SAME LIST is in the client's
    /// Assets/StreamingAssets/hidden-cell-statics.json (AcHiddenCellStatics), which skips drawing
    /// and colliding them; change both together.
    ///
    /// Patched on the declaring type (ACE.Server.Physics.Common.EnvCell's own constructor from the
    /// DAT EnvCell, the only one DBObj.GetEnvCell calls). Cells already built stay as they were
    /// until the shard restarts, which a deploy does.
    ///
    /// First use: Hotel Swank's Jeeves room, owner 2026-09-27 ("yes remove the swank furniture").
    /// </summary>
    [HarmonyPatch(typeof(PhysEnvCell), MethodType.Constructor, new[] { typeof(DatEnvCell) })]
    public static class HiddenCellStatics
    {
        // (cell, index, model). Hotel Swank 0x018A0249: the three north-wall benches, the west table,
        // the bookshelf, and the eight display weapons lying on that table. Kept: the lifestone-style
        // crystal pillar (13), the rugs (14, 15), the light (16), the numbered plaques (17-25) and the
        // crystal on its pedestal (26).
        static readonly (uint cell, int index, uint model)[] s_Hidden =
        {
            (0x018A0249, 0, 0x020002FB), (0x018A0249, 1, 0x020002FB), (0x018A0249, 2, 0x020002FB),
            (0x018A0249, 3, 0x020002FB), (0x018A0249, 4, 0x02000183),
            (0x018A0249, 5, 0x02000FC8), (0x018A0249, 6, 0x02000FC0), (0x018A0249, 7, 0x02000FC9),
            (0x018A0249, 8, 0x02000FCA), (0x018A0249, 9, 0x02000FCD), (0x018A0249, 10, 0x02000FCB),
            (0x018A0249, 11, 0x02000FC4), (0x018A0249, 12, 0x02000FCC),
            // The Witch Cave 0x0069: retail's placeholder sky dome (setup 0x020015DB, an effect with no mesh or
            // collision), listed so the two lists stay in step (owner wants black behind the cave, 2026-09-29).
            (0x00690100, 0, 0x020015DB),
            (0x00690101, 1, 0x020015DB),
            (0x00690102, 0, 0x020015DB),
            (0x00690103, 0, 0x020015DB),
            (0x00690104, 0, 0x020015DB),
            (0x00690105, 0, 0x020015DB),
            (0x0069010C, 0, 0x020015DB),
            (0x0069010D, 0, 0x020015DB),
            (0x0069010E, 0, 0x020015DB),
            (0x0069010F, 0, 0x020015DB),
            (0x00690110, 2, 0x020015DB),
            (0x00690111, 0, 0x020015DB),
            (0x00690112, 2, 0x020015DB),
            (0x00690113, 5, 0x020015DB),
            (0x00690114, 2, 0x020015DB),
            (0x00690115, 2, 0x020015DB),
            (0x00690116, 2, 0x020015DB),
            (0x00690117, 2, 0x020015DB),
            (0x00690118, 0, 0x020015DB),
            (0x00690119, 2, 0x020015DB),
            (0x0069011A, 0, 0x020015DB),
        };

        static readonly ConcurrentDictionary<uint, bool> s_Logged = new ConcurrentDictionary<uint, bool>();

        static void Postfix(PhysEnvCell __instance)
        {
            try
            {
                if (__instance?.StaticObjectIDs == null || __instance.StaticObjectFrames == null) return;
                uint cell = __instance.ID;
                bool any = false;
                foreach (var h in s_Hidden) if (h.cell == cell) { any = true; break; }
                if (!any) return;

                var ids = __instance.StaticObjectIDs;
                var frames = __instance.StaticObjectFrames;
                int removed = 0;
                // highest index first, so the indices still to check keep their DAT positions
                for (int i = ids.Count - 1; i >= 0; i--)
                {
                    bool hide = false;
                    foreach (var h in s_Hidden)
                        if (h.cell == cell && h.index == i && h.model == ids[i]) { hide = true; break; }
                    if (!hide) continue;
                    ids.RemoveAt(i);
                    if (i < frames.Count) frames.RemoveAt(i);
                    removed++;
                }
                __instance.NumStaticObjects = ids.Count;
                __instance.NumStabs = ids.Count;
                if (s_Logged.TryAdd(cell, true))
                    Mod.Log.Info($"[RevivalGuard] hidden cell statics: 0x{cell:X8} lost {removed} static object(s) (list of {s_Hidden.Length})");
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] hidden cell statics: {e.Message}");
            }
        }
    }
}
