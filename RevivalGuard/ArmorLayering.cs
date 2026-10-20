using System.Reflection;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ARMOR LAYERING TOOLS AS RETAIL HAD THEM (docs/CRAFTING-TAILORING.md "Layering"; owner 2026-10-20).
    /// Retail's own words: Top "make it show on top of other armor visually covering the same locations"; Bottom
    /// "reset it to its default layering priority" (item text, ace_world 42724/42726), and the 2010 dev teaser:
    /// Bottom "removes that quality". ACE differs in three ways, each fixed here behind a switch:
    ///   1. Bottom SET TopLayerPriority=false, which ACE sorts BELOW every unflagged piece. Here Bottom clears the flag,
    ///      and a false left by ACE reads as unflagged (layering_bottom_resets).
    ///   2. Worn targets: ACE's Tailoring.VerifyUseRequirements searches the pack only. The tools are SourceContained/
    ///      TargetContained (ItemUseable 0x80008), which ACE's own RecipeManager.VerifyUse reads as pack OR worn, and the
    ///      retail client sends a contained-target use for anything the player owns, worn included
    ///      (ItemHolder::TargetCompatibleWithObject @00587520 -> IsOwnedByObject @0058ceb0 checks the wielder). So a
    ///      worn piece is layered in place and the look is re-sent at once (layering_worn).
    ///   3. Default order: see RetailOrder below (layering_retail_order).
    /// Ours, not retail's: a line saying what changed (ACE and the record are silent), on the Craft channel.
    ///   @modifybool layering_worn false / layering_bottom_resets false / layering_retail_order false   ACE's rules
    /// </summary>
    static class ArmorLayering
    {
        internal const string P_WORN = "layering_worn", P_BOTTOM = "layering_bottom_resets", P_ORDER = "layering_retail_order";

        static bool On(string p) => PropertyManager.GetBool(p, true).Item;

        static bool IsLayeringTool(WorldObject wo)
            => wo != null && (wo.WeenieClassId == Tailoring.ArmorLayeringToolTop || wo.WeenieClassId == Tailoring.ArmorLayeringToolBottom);

        static int s_Registered;
        static void RegisterSwitches()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_WORN)) b[P_WORN] = new Property<bool>(true, "RevivalGuard ArmorLayering: layering tools work on worn armor (false: ACE, pack only)");
                if (!b.ContainsKey(P_BOTTOM)) b[P_BOTTOM] = new Property<bool>(true, "RevivalGuard ArmorLayering: the Bottom tool resets to default layering, as retail (false: ACE, forced under)");
                if (!b.ContainsKey(P_ORDER)) b[P_ORDER] = new Property<bool>(true, "RevivalGuard ArmorLayering: unflagged armor layers by its coverage (ClothingPriority), as retail (false: ACE's VisualClothingPriority)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ArmorLayering: switches not registered with @modifybool ({e.Message}); still read at their defaults"); }
        }

        static MethodBase Find(Type t, string name, string what)
        {
            var m = AccessTools.DeclaredMethod(t, name);
            if (m == null) Mod.Log.Error($"[RevivalGuard] ArmorLayering: {t.Name}.{name} not found; {what} keeps ACE's rule");
            return m;
        }

        /// <summary>2. A worn target passes for a layering tool; the rest of ACE's checks (same item, tool in the
        /// pack, retained, society armour) are repeated because ACE returned before reaching them.</summary>
        [HarmonyPatch]
        static class WornTarget
        {
            static MethodBase TargetMethod() => Find(typeof(Tailoring), nameof(Tailoring.VerifyUseRequirements), "worn layering");
            static bool Prepare(MethodBase original)
            {
                if (original != null) return true;
                if (TargetMethod() == null) return false;
                RegisterSwitches();
                Mod.Log.Info("[RevivalGuard] ArmorLayering: layering tools on worn armor, Bottom resets, retail default order");
                return true;
            }

            static void Postfix(Player player, WorldObject source, WorldObject target, ref WeenieError __result)
            {
                if (__result != WeenieError.YouDoNotPassCraftingRequirements || player == null || target == null) return;
                if (!IsLayeringTool(source) || source == target || !On(P_WORN)) return;
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
        }

        /// <summary>1 and 2: the tool itself. Top sets the flag; Bottom clears it (retail) or sets false (ACE, switch
        /// off). A worn piece re-sends the wearer's look so everyone sees the new order now, not at the next equip.</summary>
        [HarmonyPatch]
        static class Apply
        {
            static MethodBase TargetMethod() => Find(typeof(Tailoring), nameof(Tailoring.TailorLayerArmor), "the Bottom tool");
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static bool Prefix(Player player, WorldObject source, WorldObject target)
            {
                if (player == null || source == null || target == null) return true;
                try
                {
                    bool top = source.WeenieClassId == Tailoring.ArmorLayeringToolTop;
                    bool wasTop = target.TopLayerPriority == true;
                    bool? now = top ? true : On(P_BOTTOM) ? (bool?)null : false;
                    player.UpdateProperty(target, PropertyBool.TopLayerPriority, now);
                    player.TryConsumeFromInventoryWithNetworking(source, 1);
                    target.SaveBiotaToDatabase();

                    bool worn = target.CurrentWieldedLocation != null && target.WielderId == player.Guid.Full;
                    var name = string.IsNullOrEmpty(target.Name) ? "armor" : target.Name;
                    string line = top
                        ? (wasTop ? $"Your {name} was already on top; it stays over other armor covering the same places."
                                  : $"Your {name} now shows on top of other armor covering the same places.")
                        : now == null
                            ? (wasTop ? $"Your {name} is back to its normal layering: other armor covering the same places can show over it."
                                      : $"Your {name} already has its normal layering.")
                            : $"Your {name} now shows under other armor covering the same places.";
                    if (!worn) line += " Wear it to see the change.";
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Craft));

                    if (worn) player.EnqueueBroadcast(new GameMessageObjDescEvent(player));
                    player.SendUseDoneEvent();
                    Mod.Log.Info($"[RevivalGuard] ArmorLayering: {player.Name} {(top ? "Top" : "Bottom")} on {name} (0x{target.Guid.Full:X8}, {(worn ? "worn" : "pack")}): {(wasTop ? "top" : "default")} -> {(now == true ? "top" : now == null ? "default" : "under")}");
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"[RevivalGuard] ArmorLayering: {e.Message}; ACE's TailorLayerArmor runs instead");
                    return true;
                }
            }
        }

        /// <summary>1: a TopLayerPriority=false that ACE's old Bottom left on an item reads as unflagged, so it sorts
        /// with the default pieces instead of under them (CalculateObjDesc and MansionFurniture both read it).</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.TopLayerPriority), MethodType.Getter)]
        static class FalseIsDefault
        {
            static void Postfix(ref bool? __result)
            {
                if (__result == false && On(P_BOTTOM)) __result = null;
            }
        }

        /// <summary>3: THE DEFAULT ORDER. ACE sorts unflagged armour by VisualClothingPriority, its own ephemeral
        /// PropertyInt 9013 (custom range): the coverage the ClothingTable DRAWS. The 2017 retail captures (18 worn
        /// lists, 11 characters; docs/CRAFTING-TAILORING.md) sort by the item's wire ClothingPriority (the REDUCED value), lowest
        /// first, the helm after the body pieces, then the Top-tool tail, also by ClothingPriority. So a reduced coat
        /// drew under separate sleeves until a Top tool (Chakram, pkt 1485671265), as the wiki's Tailoring notes say.
        /// ACE's sort key becomes that. Not matched: retail drew the cloak last, ACE first.</summary>
        [HarmonyPatch]
        static class RetailOrder
        {
            static MethodBase TargetMethod() => Find(typeof(WorldObject), nameof(WorldObject.setVisualClothingPriority), "the default layering order");
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static bool Prefix(WorldObject __instance)
            {
                if (!On(P_ORDER)) return true;
                int key = __instance.GetProperty(PropertyInt.ClothingPriority) ?? 0;
                if (((__instance.CurrentWieldedLocation ?? EquipMask.None) & EquipMask.HeadWear) != 0) key |= 0x01000000;
                __instance.VisualClothingPriority = (CoverageMask)key;
                return false;
            }
        }
    }
}
