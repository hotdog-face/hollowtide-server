using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// TAILORING KITS, INTERMEDIATES AND REDUCTION TOOLS ON WORN GEAR (owner 2026-10-20, "Tailoring kits"; audit
    /// docs/findings/accuracy-2026-10-20/items-interactions.md #10). ACE's Tailoring.VerifyUseRequirements searches the
    /// pack only. The retail client sends a contained-target use for anything the player owns, worn included
    /// (ItemHolder::TargetCompatibleWithObject @00587520), but no retail server verdict is on record, so this is ours
    /// (docs/DIVERGENCES.md). The layering tools are ArmorLayering's; Aetheria, core plating and pet devices are not
    /// tailoring tools and keep ACE's refusals. Retained and society armour are still refused, as ACE does.
    ///   - kit on a worn piece: the piece comes off and is used up (ACE's Finalize could not consume a worn item: the
    ///     kit and the target both stayed and the intermediate was made anyway); the result is made first, so a full
    ///     pack refuses before anything is spent;
    ///   - intermediate onto a worn piece: the new look goes to everyone at once (UpdateObject + ObjDescEvent, as ACE's
    ///     RecipeManager does for a worn dye);
    ///   - reduction tool on a worn piece: the piece now covers less, so it is re-slotted to the reduced slots
    ///     (WieldObject) and the look re-sent.
    ///   @modifybool tailoring_worn false   ACE's rule (pack only)
    /// </summary>
    static class WornTailoring
    {
        internal const string P_ON = "tailoring_worn";
        static int s_Registered;
        static bool On => PropertyManager.GetBool(P_ON, true).Item;

        static bool IsTool(WorldObject wo)
            => wo != null && Tailoring.IsTailoringKit(wo.WeenieClassId)
               && wo.WeenieClassId != Tailoring.ArmorLayeringToolTop && wo.WeenieClassId != Tailoring.ArmorLayeringToolBottom;

        static bool IsReductionTool(uint wcid)
            => wcid == Tailoring.ArmorMainReductionTool || wcid == Tailoring.ArmorLowerReductionTool || wcid == Tailoring.ArmorMiddleReductionTool;

        static bool Worn(Player player, WorldObject target)
            => player != null && target != null && target.CurrentWieldedLocation != null && target.WielderId == player.Guid.Full;

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new ACE.Server.Managers.Property<bool>(true, "RevivalGuard WornTailoring: tailoring kits, intermediates and reduction tools work on worn gear (ours; false: ACE, pack only)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WornTailoring: {P_ON} not registered with @modifybool ({e.Message}); it reads its default (on)"); }
        }

        static MethodBase Find(string name)
        {
            var m = AccessTools.DeclaredMethod(typeof(Tailoring), name);
            if (m == null) Mod.Log.Error($"[RevivalGuard] WornTailoring: Tailoring.{name} not found; that part keeps ACE's rule");
            return m;
        }

        /// <summary>The look of a worn piece goes to everyone who can see the wearer.</summary>
        static void SendLook(Player player, WorldObject target)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(target));
            player.EnqueueBroadcast(new GameMessageObjDescEvent(player));
        }

        /// <summary>A worn target passes; ACE's other checks are repeated because it returned before them.</summary>
        [HarmonyPatch]
        static class Verify
        {
            static MethodBase TargetMethod() => Find(nameof(Tailoring.VerifyUseRequirements));
            static bool Prepare(MethodBase original)
            {
                if (original != null) return true;
                if (TargetMethod() == null) return false;
                Register();
                Mod.Log.Info($"[RevivalGuard] WornTailoring: tailoring kits and reduction tools on worn gear ({P_ON}, default on)");
                return true;
            }

            static void Postfix(Player player, WorldObject source, WorldObject target, ref WeenieError __result)
            {
                try
                {
                    if (__result != WeenieError.YouDoNotPassCraftingRequirements || !IsTool(source) || source == target || !On) return;
                    if (player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) == null) return;
                    if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyEquippedItems) == null) return;
                    if (target.Retained)
                    {
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat("You must use Sandstone Salvage to remove the retained property before tailoring.", ChatMessageType.Craft));
                        return;
                    }
                    if (source.IsSocietyArmor || target.IsSocietyArmor) return;
                    __result = WeenieError.None;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WornTailoring: verify {e.Message}; ACE's refusal stands"); }
            }
        }

        /// <summary>A kit on a worn piece: make the intermediate first, then use up the kit and take the piece off and
        /// destroy it (ACE's TryConsumeFromInventoryWithNetworking only looks in the pack).</summary>
        [HarmonyPatch]
        static class TakeLookOffWorn
        {
            static MethodBase TargetMethod() => Find(nameof(Tailoring.Finalize));
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static bool Prefix(Player player, WorldObject source, WorldObject target, WorldObject result)
            {
                if (!Worn(player, target) || !On) return true;
                try
                {
                    if (!player.TryCreateInInventoryWithNetworking(result))
                    {
                        result.Destroy();
                        player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You do not have enough pack space to tailor that!"));
                        player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                        Mod.Log.Info($"[RevivalGuard] WornTailoring: {player.Name} {source.Name} on worn {target.Name} (0x{target.Guid.Full:X8}): no room for {result.Name}, nothing spent");
                        return false;
                    }
                    player.TryConsumeFromInventoryWithNetworking(source, 1);
                    if (!player.TryDequipObjectWithNetworking(target.Guid, out _, Player.DequipObjectAction.ConsumeItem))
                        Mod.Log.Error($"[RevivalGuard] WornTailoring: {player.Name} could not take off {target.Name} (0x{target.Guid.Full:X8}) after {source.Name}; {result.Name} was made");
                    if (PropertyManager.GetBool("player_receive_immediate_save").Item) player.RushNextPlayerSave(5);
                    player.SendUseDoneEvent();
                    Mod.Log.Info($"[RevivalGuard] WornTailoring: {player.Name} {source.Name} on worn {target.Name} (0x{target.Guid.Full:X8}) -> {result.Name} (0x{result.Guid.Full:X8})");
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"[RevivalGuard] WornTailoring: {e.Message} finishing {source?.Name} on worn {target?.Name}; ACE's Finalize runs");
                    return true;
                }
            }
        }

        /// <summary>An intermediate onto a worn piece (armour or a wielded weapon): everyone sees the new look now.
        /// The source being gone is the sign it worked (a refusal keeps it).</summary>
        [HarmonyPatch]
        static class ApplyArmor
        {
            static MethodBase TargetMethod() => Find(nameof(Tailoring.ArmorApply));
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(Player player, WorldObject source, WorldObject target) => AfterApply(player, source, target);
        }

        [HarmonyPatch]
        static class ApplyWeapon
        {
            static MethodBase TargetMethod() => Find(nameof(Tailoring.WeaponApply));
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(Player player, WorldObject source, WorldObject target) => AfterApply(player, source, target);
        }

        static void AfterApply(Player player, WorldObject source, WorldObject target)
        {
            try
            {
                if (!Worn(player, target) || !On || player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) != null) return;
                SendLook(player, target);
                Mod.Log.Info($"[RevivalGuard] WornTailoring: {player.Name} {source.Name} onto worn {target.Name} (0x{target.Guid.Full:X8}); look sent");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WornTailoring: {e.Message} sending the look of {target?.Name}"); }
        }

        /// <summary>A reduction tool on a worn piece: re-slot it to the reduced coverage and re-send the look.</summary>
        [HarmonyPatch]
        static class Reduce
        {
            static MethodBase TargetMethod() => Find(nameof(Tailoring.TailorReduceArmor));
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Prefix(Player player, WorldObject target, out (EquipMask worn, EquipMask valid)? __state)
                => __state = Worn(player, target) ? (target.CurrentWieldedLocation.Value, target.ValidLocations ?? EquipMask.None) : null;

            static void Postfix(Player player, WorldObject source, WorldObject target, (EquipMask worn, EquipMask valid)? __state)
            {
                try
                {
                    if (__state == null || !On || !Worn(player, target)) return;
                    var now = target.ValidLocations ?? EquipMask.None;
                    if (now == EquipMask.None || now == __state.Value.valid || now == __state.Value.worn) return;   // refused, or already in those slots
                    player.UpdateProperty(target, ACE.Entity.Enum.Properties.PropertyInt.CurrentWieldedLocation, (int)now);
                    player.Session.Network.EnqueueSend(new GameEventWieldItem(player.Session, target.Guid.Full, now));
                    target.SaveBiotaToDatabase();
                    SendLook(player, target);
                    Mod.Log.Info($"[RevivalGuard] WornTailoring: {player.Name} {source?.Name} on worn {target.Name} (0x{target.Guid.Full:X8}): slots 0x{(uint)__state.Value.worn:X} -> 0x{(uint)now:X}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WornTailoring: {e.Message} re-slotting {target?.Name}"); }
            }
        }
    }
}
