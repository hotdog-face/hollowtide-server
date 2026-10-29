using System.Collections.Generic;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// NO PASSING ITEMS ON UNTIL LEVEL 10 (owner 2026-10-23). A free browser guest per tab, and as many characters per
    /// account as it likes, made every per-character freebie farmable: the Pathwarden turn-in rewards, the Founder's
    /// Banner (one per account, hookable so not attuned), the 10,000 starting pyreals of ACE's starter gear. A character
    /// under level 10 cannot give an item to another player, put one in a trade, drop one, or put one into a container it
    /// does not carry (a chest or a corpse on the ground). Giving to NPCs (quest turn-ins), selling to vendors, picking up
    /// and receiving are untouched; staff are exempt. What a newcomer sells is not put up for resale (ACE lists a sold item for
    /// anyone to buy for vendor_unique_rot_time): a sale to a vendor with a second tab buying it back was a give.
    /// DIVERGENCES row "Newcomers keep their items"; docs/findings/freebie-audit-2026-10-24.md.
    /// </summary>
    static class NewcomerHands
    {
        internal const int LEVEL = 10;

        internal static bool Held(Player p) => p?.Session != null && (p.Level ?? 1) < LEVEL && p.Session.AccessLevel == AccessLevel.Player;

        internal static void Refuse(Player p, string what)
        {
            p.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Until level {LEVEL} you cannot {what}. Sell what you don't need to a vendor instead.", ChatMessageType.Broadcast));
        }

        internal static bool Carried(Player p, uint guid) =>
            p.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems) != null;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
    static class NewcomerHandsGive
    {
        static bool Prefix(Player __instance, uint targetGuid, uint itemGuid)
        {
            if (!NewcomerHands.Held(__instance) || PlayerManager.GetOnlinePlayer(targetGuid) == null) return true;
            NewcomerHands.Refuse(__instance, "give items to other players");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionAddToTrade))]
    static class NewcomerHandsTrade
    {
        static bool Prefix(Player __instance, uint itemGuid)
        {
            if (!NewcomerHands.Held(__instance)) return true;
            NewcomerHands.Refuse(__instance, "trade items");
            __instance.Session.Network.EnqueueSend(new GameEventTradeFailure(__instance.Session, itemGuid, WeenieError.None));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionDropItem))]
    static class NewcomerHandsDrop
    {
        static bool Prefix(Player __instance, uint itemGuid)
        {
            if (!NewcomerHands.Held(__instance)) return true;
            NewcomerHands.Refuse(__instance, "drop items or put them in chests");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionPutItemInContainer))]
    static class NewcomerHandsContainer
    {
        static bool Prefix(Player __instance, uint itemGuid, uint containerGuid)
        {
            // only an item it carries going into a container it does not (picking up and looting stay free)
            if (!NewcomerHands.Held(__instance) || containerGuid == __instance.Guid.Full
                || !NewcomerHands.Carried(__instance, itemGuid) || NewcomerHands.Carried(__instance, containerGuid)) return true;
            NewcomerHands.Refuse(__instance, "drop items or put them in chests");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid));
            return false;
        }
    }

    // THE STACK PATHS (ticket 261024-0041 audit). A split or a merge moves items without DropItem or PutItemInContainer:
    // split part of a stack to the ground (9,999 of the 10,000 starting pyreals), split it into a chest, or merge a
    // carried stack into one sitting in a chest. Between the player's own packs, and anything taken out, stays free.
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionStackableSplitTo3D))]
    static class NewcomerHandsSplitDrop
    {
        static bool Prefix(Player __instance, uint stackId)
        {
            if (!NewcomerHands.Held(__instance)) return true;
            NewcomerHands.Refuse(__instance, "drop items or put them in chests");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, stackId));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionStackableSplitToContainer))]
    static class NewcomerHandsSplitContainer
    {
        static bool Prefix(Player __instance, uint stackId, uint containerId)
        {
            if (!NewcomerHands.Held(__instance) || containerId == __instance.Guid.Full
                || !NewcomerHands.Carried(__instance, stackId) || NewcomerHands.Carried(__instance, containerId)) return true;
            NewcomerHands.Refuse(__instance, "drop items or put them in chests");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, stackId));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionStackableMerge))]
    static class NewcomerHandsMerge
    {
        static bool Prefix(Player __instance, uint mergeFromGuid, uint mergeToGuid)
        {
            if (!NewcomerHands.Held(__instance)
                || !NewcomerHands.Carried(__instance, mergeFromGuid) || NewcomerHands.Carried(__instance, mergeToGuid)) return true;
            NewcomerHands.Refuse(__instance, "drop items or put them in chests");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, mergeFromGuid));
            return false;
        }
    }

    [HarmonyPatch(typeof(Vendor), nameof(Vendor.ProcessItemsForPurchase))]
    static class NewcomerHandsSell
    {
        // ACE destroys a DestroyOnSell item instead of listing it, the same path; the newcomer is still paid as usual
        static void Prefix(Player player, Dictionary<uint, WorldObject> items)
        {
            if (!NewcomerHands.Held(player) || items == null) return;
            foreach (var item in items.Values) item?.SetProperty(PropertyBool.DestroyOnSell, true);
        }
    }
}
