using System;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A USE OF AN OBJECT THE SERVER CANNOT FIND, WHILE TRADING (Haha, 2026-11-09, retail client, 4 throws in 3 s):
    /// ACE's Player.HandleActionUseItem (Player_Use.cs:186-188) does `item = FindObject(...)` and then
    /// `if (IsTrading &amp;&amp; item.IsBeingTradedOrContainsItemBeingTraded(...))` before its own null check, so with a
    /// trade open and the item not found it throws a NullReferenceException and never sends the UseDone the client
    /// waits on. In exactly that case this sends what ACE sends for a not-found item outside a trade (SendUseDoneEvent()
    /// after StopExistingMoveToChains) and skips the original; every other case runs ACE untouched.
    /// Runs last, and does nothing when another prefix (PkShrines, MansionFurniture) already took the use.
    /// </summary>
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
