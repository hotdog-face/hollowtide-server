using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Hook), nameof(Hook.CheckUseRequirements))]
    static class UnownedHouseHook
    {
        static void Postfix(Hook __instance, WorldObject activator, ref ActivationResult __result)
        {
            try
            {
                if (__result == null || !__result.Success || !(activator is Player player) || !player.IgnoreHouseBarriers) return;
                var root = __instance.House?.RootHouse;
                if (root == null || (root.HouseOwner ?? 0) != 0) return;
                var item = __instance.Item;
                if (item == null)
                    __result = new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.HookItemNotUsable_CannotOpen));
                else if (item is Hooker)
                    __result = new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouAreNotPermittedToUseThatHook));
                else
                    __result = new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.ItemUnusableOnHook_CannotOpen, __instance.Name));
            }
            catch (System.Exception e) { Mod.Log.Warn($"[UnownedHouseGuard] hook: {e.Message}"); }
        }
    }

}
