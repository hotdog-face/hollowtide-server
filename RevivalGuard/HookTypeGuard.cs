using ACE.Entity.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A HOOK TAKES ONLY WHAT FITS IT (house hooks audit, 2026-09-27, docs/SYSTEMS-HOUSING.md).
    ///
    /// ACE puts ANY item on ANY hook: +Sysprobe hung a Mace, which has no HookType at all, on a wall
    /// hook of cottage 0x742AA1A7, and a rug would go on a wall the same way. Retail refused both. Its
    /// client ran ItemHolder::CheckHookStatus @00586e90 from gmExternalContainerUI::DragItemAcceptable
    /// @004cbb60 before any request went out, and its server had the error for it
    /// (WeenieError.ItemIsWrongTypeForHook 0x04E3, "That item is of the wrong type to be placed on this
    /// hook."). This is CheckHookStatus's rule, server side, so every client gets it:
    ///   the hook has no HookType or no HookItemType   -> allowed
    ///   the hook has no house owner                    -> refused
    ///   item.HookType shares a bit with the hook's     -> allowed if item.ItemType is in HookItemType
    ///   anything else (no HookType on the item too)    -> refused
    /// ACE's hook weenies all author HookItemType -1 (every type), so in practice the rule is the
    /// HookType match: wall 2, floor 1, ceiling 4, yard 8, roof 16.
    /// </summary>
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
            // AN UNOWNED HOUSE IS NOT A TYPE PROBLEM. Refusing it here answered "That item is of the wrong
            // type to be placed on this hook" for a wall trophy on a wall hook in a mansion the player had
            // not bought yet (owner, 2026-09-29, six refusals in a row). Ownership is ACE's own check,
            // with its own reply, so this guard only judges the type and lets ACE judge the owner.
            if ((hook.HouseOwner ?? hook.House?.HouseOwner ?? 0) == 0) return true;
            int itemHookType = item.HookType ?? 0;
            if (itemHookType == 0 || (itemHookType & hookType) == 0) return false;
            return ((int)item.ItemType & hookItemTypes) != 0;
        }
    }
}
