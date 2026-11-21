using System;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// BANNER OF THE HELD GROUND (owner 2026-11-20: "i want it to be a house hook item for players to use at their
    /// allegance mansion. do C."; docs/proposals/pk-night-buff-item.md option C). The weenie (908120, a retail-style
    /// Hooker) does the work in data: a 20 h quest timer and three level VI casts. ACE lets anyone the house admits use
    /// a hooked Hooker (an open house admits everyone); this narrows THIS item to the house owner's allegiance (the
    /// owner, their other characters, anyone under the same monarch). Rule: HeldGroundBannerRule (nettest heldbanner).
    /// It runs after ACE's own checks, so the hook, house and permission answers ACE gives come first.
    /// PkNight.cs hands the banner out.
    /// </summary>
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
