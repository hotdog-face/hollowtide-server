using System;
using System.Linq;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AN UNWIELD INTO A FULL PACK LEAVES THE ITEM IN THE HAND. Reports 261025-0025/0026 (an admin character): "have a
    /// weapon equipped but fighting with unarmed combat", then "Source item not found!" x10.
    ///
    /// ACE's `DoHandleActionPutItemInContainer` dequips a wielded item first, then adds it to the named container
    /// main-pack-only. When that is full it falls back to `itemRootOwner.TryAddToInventory` (side packs); when those
    /// are full too it only logs "failed to re-add to original location". The item is then in no list: the server
    /// fights bare-handed, the client still draws it wielded, and every later move of it is "Source item not found!"
    /// until relog (the database still has it wielded). Hit 5 times by the mercs on 2026-10-25 too.
    ///
    /// Prefix: a wielded item whose target cannot take it (the same capacity test ACE's add makes) is refused before
    /// the dequip, with ACE's own refusal text. Retail never sends such a move (ItemHolder::AttemptToPlaceInContainer
    /// @00588140); the client now refuses it locally as well (BRIDGE-NOTES.md note 122).
    /// </summary>
    [HarmonyPatch(typeof(Player), "DoHandleActionPutItemInContainer")]
    static class UnwieldToFullPack
    {
        static bool Prefix(Player __instance, WorldObject item, bool itemWasEquipped, Container container, ref bool __result)
        {
            try
            {
                if (!itemWasEquipped || item == null || container == null || item.CurrentLandblock != null) return true;
                if (Fits(container, item)) return true;
                var s = __instance.Session;
                if (s != null)
                {
                    s.Network.EnqueueSend(new GameEventCommunicationTransientString(s, $"Unable to put {item.Name} into container"));
                    s.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(s, item.Guid.Full));
                }
                Mod.Log.Info($"[UnwieldToFullPack] {__instance.Name}: {item.Name} (0x{item.Guid.Full:X8}) stays wielded, {container.Name} (0x{container.Guid.Full:X8}) is full");
                __result = false;
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[UnwieldToFullPack] {item?.Name}: {e.Message}"); return true; }
        }

        // Container.TryAddToInventory(item, placement, limitToMainPackOnly: true) capacity test, nothing else.
        internal static bool Fits(Container c, WorldObject item)
        {
            if (c.Inventory.ContainsKey(item.Guid)) return false;
            if (item.UseBackpackSlot)
                return (c.ContainerCapacity ?? 0) > c.Inventory.Values.Count(i => i.UseBackpackSlot);
            return (c.ItemCapacity ?? 0) > c.Inventory.Values.Count(i => !i.UseBackpackSlot);
        }
    }
}
