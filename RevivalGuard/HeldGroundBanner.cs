using System;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Banner of the HELD ground.</summary>
    static class HeldGroundBanner
    {
        [HarmonyPatch(typeof(Hooker), nameof(Hooker.CheckUseRequirements))]
        static class Gate
        {
            static void Postfix(Hooker __instance, WorldObject activator, ref ActivationResult __result)
            {
                try
                {
                    if (__instance?.WeenieClassId != HeldGroundBannerRule.WCID || __result == null || !__result.Success) return;
                    if (!(activator is Player player)) return;
                    if (!__instance.IsHooked(player, out var hook)) return;
                    var owner = hook.House?.RootHouse?.HouseOwner;
                    IPlayer ownerPlayer = owner != null ? PlayerManager.FindByGuid(owner.Value) : null;
                    bool sameAccount = ownerPlayer?.Account != null && player.Account != null && ownerPlayer.Account.AccountId == player.Account.AccountId;
                    if (HeldGroundBannerRule.MayUse(player.Guid.Full, player.MonarchId, owner, ownerPlayer?.MonarchId, sameAccount)) return;
                    __result = new ActivationResult(false);
                    PkCommon.Say(player, "The banner does not answer you. Only its owner's allegiance may draw on it.");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] HeldGroundBanner: {e.Message}"); }
            }
        }
    }
}
