using System;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseItem))]
    public static class UseWhileTrading
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(Player __instance, uint itemGuid, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            try
            {
                var p = __instance;
                // PKLogout: ACE answers before the dereference
                if (p == null || p.PKLogout || !p.IsTrading) return true;
                if (p.FindObject(itemGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems
                                           | Player.SearchLocations.Landblock) != null) return true;
                p.StopExistingMoveToChains();
                p.SendUseDoneEvent();
                Mod.Log.Info($"[RevivalGuard] use while trading: {p.Name} (0x{p.Guid.Full:X8}) used 0x{itemGuid:X8}, not found; answered UseDone (ACE would throw)");
                return false;
            }
            catch (Exception e) { TickGuard.Report("UseWhileTrading", e); return true; }
        }
    }
}
