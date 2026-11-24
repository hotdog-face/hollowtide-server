using System;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE GARBAGE BARREL TAKES YOUR OWN ATTUNED ITEMS (owner report 261124-0007, Staff Base: "dragging items to the garbage
    /// barrel doesnt work on many items. for example envoy's robe"). Retail weenie 34726 is AiAcceptEverything, but ACE's
    /// GiveObjectToNPC drops that for a sticky-attuned item (Attuned 2: Envoy's Robe, most quest gear), and the barrel has no
    /// Give emote, so ACE answered "doesn't want that". Retail let you destroy your own attuned items in a garbage barrel.
    /// Only the sticky-attuned case is taken over here; everything else (and every refusal ACE makes first: a summoned pet's
    /// device, an item in a trade, an Olthoi player) stays ACE's. Item Lock still refuses earlier, in HandleActionGiveObjectRequest.
    /// </summary>
    [HarmonyPatch]
    static class GarbageBarrel
    {
        internal const uint WCID = 34726;   // ace34726-garbagebarrel

        static readonly MethodInfo s_RemoveItemForGive = AccessTools.Method(typeof(Player), "RemoveItemForGive");

        static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(Player), "GiveObjectToNPC",
            new[] { typeof(WorldObject), typeof(WorldObject), typeof(Container), typeof(Container), typeof(bool), typeof(int) });

        static bool Prepare()
        {
            if (TargetMethod() != null && s_RemoveItemForGive != null) return true;
            Mod.Log.Warn("[RevivalGuard] GarbageBarrel: Player.GiveObjectToNPC or RemoveItemForGive not found; attuned items stay out of barrels");
            return false;
        }

        /// <summary>Would this give be ours to finish (a sticky-attuned item, a barrel, nothing ACE refuses first)?</summary>
        internal static bool Takes(WorldObject target, WorldObject item, bool olthoi, bool trading) =>
            target != null && item != null && target.WeenieClassId == WCID && target.AiAcceptEverything
            && item.IsStickyAttunedOrContainsStickyAttuned
            && !(item is PetDevice pd && pd.Pet != null) && !olthoi && !trading && !target.EmoteManager.IsBusy;

        static bool Prefix(Player __instance, WorldObject target, WorldObject item, Container itemFoundInContainer,
            Container itemRootOwner, bool itemWasEquipped, int amount, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            bool taken = false;
            try
            {
                var p = __instance;
                bool trading = p.IsTrading && item != null && item.IsBeingTradedOrContainsItemBeingTraded(p.ItemsInTradeWindow);
                if (!Takes(target, item, p.IsOlthoiPlayer, trading)) return true;

                var args = new object[] { item, itemFoundInContainer, itemWasEquipped, itemRootOwner, amount, null, false };
                if (!(bool)s_RemoveItemForGive.Invoke(p, args)) return false;   // it sent its own InventoryServerSaveFailed
                taken = true;
                var gone = (WorldObject)args[5];

                if (item == gone)
                    p.Session.Network.EnqueueSend(new GameEventItemServerSaysContainId(p.Session, item, target));
                var n = gone.StackSize ?? 1;
                var name = gone.GetNameWithMaterial(n);
                p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You give {target.Name} {(n != 1 ? $"{n:N0} " : "")}{name}.", ChatMessageType.Broadcast));
                target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.ReceiveItem));
                Mod.Log.Info($"[RevivalGuard] GarbageBarrel: {p.Name} destroyed attuned {name} 0x{gone.Guid.Full:X8} (wcid {gone.WeenieClassId}) in 0x{target.Guid.Full:X8}");
                gone.Destroy();
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] GarbageBarrel: {e}");
                return !taken;   // before the take: ACE's own answer; after it, never a second give of the same item
            }
        }
    }
}
