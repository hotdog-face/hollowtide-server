using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A HOUSE NOBODY OWNS HAS NO HOOKS OR CHESTS TO USE, NOT EVEN FOR A STAFF CHARACTER (owner,
    /// 2026-09-29: "i was using its hooks before it was purchased. is that normal?").
    ///
    /// For a player it already is: ACE's Hook.CheckUseRequirements refuses to open the hook of a house
    /// with no owner ("The hook does not contain a usable item. You cannot open the hook because you
    /// do not own the house to which it belongs.", seen 2026-09-30 on +Spell Sweep B at cottage
    /// 0x73B9C04F), an item can only be hung on the hook you last opened, and Storage asks
    /// House.HasPermission. But both checks return true FIRST for a character with
    /// PropertyBool.IgnoreHouseBarriers (25), ACE's admin "bypass housing barriers" toggle
    /// (Player.HandleMRT, @mrt), and the owner's an admin character has it on (ace_shard, 2026-09-30). That
    /// is how he could hang things in a house he had not bought.
    ///
    /// The toggle is for walking through closed houses; hanging on or looting a house with no owner
    /// has no use and leaves items for the next buyer. So for an UNOWNED house this puts back exactly
    /// the refusal ACE gives everyone else, with ACE's own messages. Owned houses are untouched:
    /// owners, guests with storage, and allegiance access keep ACE's rules.
    /// </summary>
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
                // Hook.CheckUseRequirements' own not-the-owner branch
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

    [HarmonyPatch(typeof(Storage), nameof(Storage.CheckUseRequirements))]
    static class UnownedHouseStorage
    {
        static void Postfix(Storage __instance, WorldObject activator, ref ActivationResult __result)
        {
            try
            {
                if (__result == null || !__result.Success || !(activator is Player player) || !player.IgnoreHouseBarriers) return;
                var root = __instance.House?.RootHouse;
                if (root == null || (root.HouseOwner ?? 0) != 0) return;
                // Storage.CheckUseRequirements' own no-permission branch
                player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, $"You do not have permission to access {__instance.Name}"));
                __instance.EnqueueBroadcast(new GameMessageSound(__instance.Guid, Sound.OpenFailDueToLock, 1.0f));
                __result = new ActivationResult(false);
            }
            catch (System.Exception e) { Mod.Log.Warn($"[UnownedHouseGuard] storage: {e.Message}"); }
        }
    }
}
