using ACE.Entity.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A hook takes only what FITS it.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionPutItemInContainer))]
    static class HookTypeGuard
    {
        static bool Prefix(Player __instance, uint itemGuid, uint containerGuid)
        {
            try
            {
                if (!(__instance?.CurrentLandblock?.GetObject(containerGuid) is Hook hook)) return true;
                var item = __instance.FindObject(itemGuid,
                    Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems | Player.SearchLocations.LastUsedHook);
                if (item == null) return true;   // ACE's own verify reports what it cannot find
                if (Fits(item, hook)) return true;
                __instance.Session?.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid, WeenieError.ItemIsWrongTypeForHook));
                Mod.Log.Info($"[HookTypeGuard] refused {item.Name} (0x{itemGuid:X8}, HookType {item.HookType ?? 0}) on {hook.Name} 0x{containerGuid:X8} (HookType {hook.HookType ?? 0}) for {__instance.Name}");
                return false;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"[HookTypeGuard] {e.Message}; letting ACE decide");
                return true;
            }
        }

        /// <summary>CheckHookStatus @00586e90, with TYPE_SELF read as 0.</summary>
        internal static bool Fits(WorldObject item, Hook hook)
        {
            int hookType = hook.HookType ?? 0;
            int hookItemTypes = hook.HookItemType ?? 0;
            if (hookType == 0 || hookItemTypes == 0) return true;
            if ((hook.HouseOwner ?? hook.House?.HouseOwner ?? 0) == 0) return true;
            int itemHookType = item.HookType ?? 0;
            if (itemHookType == 0 || (itemHookType & hookType) == 0) return false;
            return ((int)item.ItemType & hookItemTypes) != 0;
        }
    }
}
