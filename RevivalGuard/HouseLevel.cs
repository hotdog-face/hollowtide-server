using ACE.Entity.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionBuyHouse))]
    static class HouseLevel
    {
        internal const int LEVEL = 50;

        static bool Prefix(Player __instance, uint slumlord_id)
        {
            var p = __instance;
            if (p?.Session == null || p.Session.AccessLevel != AccessLevel.Player || (p.Level ?? 1) >= LEVEL) return true;
            if (!(p.CurrentLandblock?.GetObject(slumlord_id) is SlumLord slumlord) || slumlord.House == null
                || slumlord.House.HouseType == HouseType.Apartment) return true;
            var msg = $"You must be at least level {LEVEL} to purchase this dwelling. This applies to all housing except apartments.";
            p.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(p.Session, msg), new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
            return false;
        }
    }
}
