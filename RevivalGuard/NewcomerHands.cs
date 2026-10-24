using ACE.Entity.Enum;
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
    /// and receiving are untouched; staff are exempt. DIVERGENCES row "Newcomers keep their items".
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
}
