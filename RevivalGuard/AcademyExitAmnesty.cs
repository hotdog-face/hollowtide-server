using System.Collections.Generic;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    public static class AcademyExitAmnesty
    {
        /// <summary>The twenty landblocks holding weenies 29337-29340, the academy exit portals.</summary>
        internal static readonly HashSet<ushort> ACADEMY = new HashSet<ushort>
        {
            0x7202, 0x7203, 0x7204, 0x7302, 0x7303, 0x7F03, 0x7F04, 0x8002, 0x8003, 0x8004,
            0x8602, 0x8603, 0x8604, 0x8702, 0x8703, 0x8C04, 0x8D02, 0x8D03, 0x8D04, 0x8E02,
        };

        static void Postfix(Player __instance)
        {
            var p = __instance;
            if (p == null || !p.RecallsDisabled || p.Location == null) return;

            // Position.Landblock is the high 16 bits of the cell id -- the block, not the cell.
            var block = (ushort)(p.Location.Cell >> 16);
            if (ACADEMY.Contains(block)) return;   // still in the tutorial; the front door is theirs to walk

            p.RecallsDisabled = false;
            Mod.Log.Info($"[RevivalGuard] academy exit amnesty: {p.Name} (0x{p.Guid.Full:X8}) had "
                + $"RecallsDisabled set while standing in landblock 0x{block:X4}, which is not a "
                + $"Training Academy. It never walked out through an exit portal, so nothing was ever "
                + $"going to clear the flag and it could not attune to a lifestone or recall. Cleared.");
        }
    }
}
