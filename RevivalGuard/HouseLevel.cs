using ACE.Entity.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A HOUSE NEEDS LEVEL 50, NOT A 15-DAY ACCOUNT (owner 2026-10-24, a player's "Your account must be at least 15 days
    /// old to purchase this dwelling": "drop it but require level 50"). ACE's house_15day_account is switched off by the
    /// datafix 2026-10-24-house-15day-off.sql; this asks for a level-50 character instead, so free throwaway guest accounts
    /// cannot buy up the houses. Apartments stay open to all, as they were under the 15-day rule; staff are exempt.
    /// DIVERGENCES row "Houses need level 50".
    /// </summary>
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
