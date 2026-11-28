using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Dev accounts skip the house purchase waits.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionBuyHouse))]
    static class DevHousing
    {
        const uint MANSION_RANK = 10;
        [ThreadStatic] static uint? t_RealRank;

        static void Prefix(Player __instance)
        {
            t_RealRank = null;
            var session = __instance?.Session;
            if (session == null || session.AccessLevel < AccessLevel.Developer) return;
            __instance.Account15Days = true;
            __instance.HousePurchaseTimestamp = null;
            var node = __instance.AllegianceNode;
            if (node != null && node.Rank < MANSION_RANK)
            {
                t_RealRank = node.Rank;
                node.Rank = MANSION_RANK;
            }
        }

        static void Finalizer(Player __instance)
        {
            if (t_RealRank is uint real && __instance?.AllegianceNode != null) __instance.AllegianceNode.Rank = real;
            t_RealRank = null;
        }
    }
}
