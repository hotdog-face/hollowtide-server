using System;
using System.Linq;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>An unwield into a full pack leaves the item in the hand.</summary>
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
