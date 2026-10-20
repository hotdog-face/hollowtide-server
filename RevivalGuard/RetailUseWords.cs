using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// USE REFUSALS IN RETAIL'S WORDS (docs/findings/accuracy-2026-10-20/items-interactions.md 7-9). Each wording is
    /// from the January 2017 retail captures (AC-Mac-Revival/Conformance/pcaps), each behind its own switch:
    ///   @modifybool <switch> false   ACE's words
    /// </summary>
    static class RetailUseWords
    {
        internal const string P_COOLDOWN = "retail_cooldown_text", P_LOCKED = "retail_locked_chest_text",
                              P_MANALIST = "retail_mana_stone_list", P_RARE = "retail_rare_confirm_text";

        internal static bool On(string p) => PropertyManager.GetBool(p, true).Item;

        static int s_Registered;
        internal static void RegisterSwitches()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_RARE)) b[P_RARE] = new Property<bool>(true, "RevivalGuard RetailUseWords: Confirm Use of Rare Gems asks \"Are you sure you want to use this rare item?\" as retail (false: ACE's \"Are you sure you want to use <name>?\")");
                if (!b.ContainsKey(P_MANALIST)) b[P_MANALIST] = new Property<bool>(true, "RevivalGuard RetailUseWords: a mana stone's list of items ends without a full stop, as retail (false: ACE's \".\")");
                if (!b.ContainsKey(P_LOCKED)) b[P_LOCKED] = new Property<bool>(true, "RevivalGuard RetailUseWords: a locked chest says \"The <chest> is locked!\" as retail (false: ACE, only with fix_chest_missing_inventory_window and no \"!\")");
                if (!b.ContainsKey(P_COOLDOWN)) b[P_COOLDOWN] = new Property<bool>(true, "RevivalGuard RetailUseWords: an item on cooldown says \"You cannot use that item again for N seconds!\" as retail (false: ACE's \"You have used this item too recently\")");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailUseWords: switches not registered with @modifybool ({e.Message}); still read at their defaults"); }
        }

        /// <summary>The item whose use requirements are being checked on this thread (WorldObject.CheckUseRequirements),
        /// so the transient string it sends can be reworded with that item's cooldown.</summary>
        [ThreadStatic] static WorldObject t_Item;
        [ThreadStatic] static Player t_Player;

        /// <summary>7. WorldObject.CheckUseRequirements (the declaring type; overrides call it through base) sends
        /// "You have used this item too recently" (WorldObject_Use.cs:301-310). Retail's capture:
        /// "You cannot use that item again for 34 seconds!" (also 1 and 44), a TransientString.</summary>
        [HarmonyPatch]
        static class CooldownContext
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(WorldObject), nameof(WorldObject.CheckUseRequirements));
                if (m == null) Mod.Log.Error("[RevivalGuard] RetailUseWords: WorldObject.CheckUseRequirements not found; cooldown keeps ACE's words");
                return m;
            }
            static bool Prepare(MethodBase original)
            {
                if (original != null) return true;
                if (TargetMethod() == null) return false;
                RegisterSwitches();
                Mod.Log.Info("[RevivalGuard] RetailUseWords: use refusals in retail's words");
                return true;
            }
            static void Prefix(WorldObject __instance, WorldObject activator) { t_Item = __instance; t_Player = activator as Player; }
            static Exception Finalizer(Exception __exception) { t_Item = null; t_Player = null; return __exception; }
        }

        /// <summary>8. A locked chest: retail's capture "The Yaruldi's Hoard is locked!" (TransientString). ACE says it
        /// only when fix_chest_missing_inventory_window is on (Chest.cs:104-110); otherwise only the lock sound.</summary>
        [HarmonyPatch(typeof(Chest), nameof(Chest.CheckUseRequirements))]
        static class LockedChest
        {
            static void Postfix(Chest __instance, WorldObject activator, ActivationResult __result)
            {
                try
                {
                    if (__result == null || __result.Success || !__instance.IsLocked || activator is not Player player) return;
                    if (PropertyManager.GetBool("fix_chest_missing_inventory_window").Item || !On(P_LOCKED)) return;
                    player.SendTransientError($"The {__instance.Name} is locked!");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailUseWords: {e.Message}"); }
            }
        }

        /// <summary>9. A charged stone used on yourself: retail's capture ends the item list with no full stop,
        /// "...: Heaume, Branith's Staff, Ring\nYour items are fully charged." (also "\nYou need 1,896 more mana to
        /// fully charge your items."); ACE writes "Ring.\n..." (ManaStone.cs:179).</summary>
        [HarmonyPatch(typeof(GameMessageSystemChat), MethodType.Constructor, new[] { typeof(string), typeof(ChatMessageType) })]
        static class ManaStoneList
        {
            static void Prefix(ref string message)
            {
                try
                {
                    if (message == null || !message.StartsWith("The Mana Stone gives ") || !message.Contains(" points of mana to the following items: ")) return;
                    if (!On(P_MANALIST)) return;
                    message = message.Replace(".\nYou need ", "\nYou need ").Replace(".\nYour items are fully charged.", "\nYour items are fully charged.");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailUseWords: {e.Message}"); }
            }
        }

        /// <summary>2. Confirm Use of Rare Gems. ACE already asks when the option is on (Gem.cs:76), as
        /// "Are you sure you want to use <name>?". Retail's client asked instead (UseObject @00588a80, VolatileRare
        /// 0x10000000 + ConfirmVolatileRareUse -> UsageConfirmation_VolatileRare @00566800) with
        /// "Are you sure you want to use this rare item?". Same Yes/No and the same option, so only the words change
        /// here: ACE's own branch with retail's sentence, the Yes calling ACE's confirmed path.</summary>
        [HarmonyPatch]
        static class RareConfirm
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(Gem), nameof(Gem.ActOnUse), new[] { typeof(WorldObject), typeof(bool) });
                if (m == null) Mod.Log.Error("[RevivalGuard] RetailUseWords: Gem.ActOnUse(WorldObject, bool) not found; rare gems keep ACE's words");
                return m;
            }
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static bool Prefix(Gem __instance, WorldObject activator, bool confirmed)
            {
                try
                {
                    if (confirmed || __instance.RareId == null || activator is not Player player || !On(P_RARE)) return true;
                    if (!player.GetCharacterOption(CharacterOption.ConfirmUseOfRareGems)) return true;
                    var gem = __instance;
                    var confirm = new Confirmation_Custom(player.Guid, () => gem.ActOnUse(activator, true));
                    if (!player.ConfirmationManager.EnqueueSend(confirm, "Are you sure you want to use this rare item?"))
                        player.SendWeenieError(WeenieError.ConfirmationInProgress);
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailUseWords: {e.Message}"); return true; }
            }
        }

        /// <summary>The rewording itself, on the one TransientString constructor every refusal above goes through.</summary>
        [HarmonyPatch(typeof(GameEventCommunicationTransientString), MethodType.Constructor, new[] { typeof(Session), typeof(string) })]
        static class Transient
        {
            static void Prefix(ref string message)
            {
                try
                {
                    if (message == "You have used this item too recently" && t_Item != null && t_Player != null && On(P_COOLDOWN))
                    {
                        int id = t_Item.CooldownId ?? 0;
                        int secs = (int)Math.Ceiling(t_Player.EnchantmentManager.GetCooldown(id));
                        if (secs < 1) secs = 1;
                        message = $"You cannot use that item again for {secs} seconds!";
                    }
                    // 8. ACE's "The <chest> is locked" (with fix_chest_missing_inventory_window) gains retail's "!".
                    else if (message != null && message.StartsWith("The ") && message.EndsWith(" is locked") && On(P_LOCKED))
                        message += "!";
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailUseWords: {e.Message}"); }
            }
        }
    }
}
