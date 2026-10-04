using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// DEV ACCOUNTS SKIP THE HOUSE PURCHASE WAITS (owner, 2026-09-27: "Relax them for dev accounts").
    ///
    /// Retail and ACE (house_15day_account, house_30day_cooldown, both on here) refuse any dwelling but
    /// an apartment to an account under 15 days old, and a second purchase within 30 days. Every devbot
    /// account dates from 2026-09-16, so none could buy a cottage before 10-01 (docs/SYSTEMS-HOUSING.md).
    /// For an account of AccessLevel Developer or above, the prefix marks the account as past its 15 days
    /// and clears the last purchase time before ACE's own checks read them; players are untouched.
    ///
    /// ONE HOUSE PER ACCOUNT STAYS: ACE refuses maintenance while an owner holds two houses
    /// (Player_House.HandleActionRentHouse), so relaxing it would strand a second house unpaid.
    ///
    /// AND THE MANSION RANK (2026-09-27, for testing the allegiance hall, docs/MANSIONS-AND-PK.md): a
    /// mansion wants a monarch of allegiance rank 6 (mansion_min_rank / the covenant crystal), which
    /// takes dozens of characters. For the same dev accounts the buyer's allegiance rank reads 10 (the top rank) for the
    /// length of the purchase call and is put back after it. The buyer must still be a monarch, and
    /// ACE's maintenance-time requirement check is unchanged.
    /// </summary>
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
