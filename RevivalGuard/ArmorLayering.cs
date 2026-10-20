using System.Reflection;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
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
    ///   3. Default order: see RetailOrder below (layering_retail_order); the cloak last, CloakLast (layering_cloak_last).
    /// Ours, not retail's (docs/DIVERGENCES.md): a line saying what changed (ACE and the record are silent), on the Craft
    /// channel, and a use that would change nothing keeps the tool (layering_no_waste; owner 2026-10-20).
    ///   @modifybool <switch> false   ACE's rule (layering_no_waste false: always consumed)
    /// </summary>
    static class ArmorLayering
    {
        internal const string P_WORN = "layering_worn", P_BOTTOM = "layering_bottom_resets", P_ORDER = "layering_retail_order",
                              P_NOWASTE = "layering_no_waste", P_CLOAK = "layering_cloak_last";

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
                if (!b.ContainsKey(P_NOWASTE)) b[P_NOWASTE] = new Property<bool>(true, "RevivalGuard ArmorLayering: a layering tool that would change nothing is not used up (ours; false: always consumed)");
                if (!b.ContainsKey(P_CLOAK)) b[P_CLOAK] = new Property<bool>(true, "RevivalGuard ArmorLayering: the cloak is drawn last, over everything, as retail (false: ACE, cloak first)");
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
                    // What the piece effectively had (the raw value: the getter is patched), to see whether anything changes.
                    bool? raw = target.GetProperty(PropertyBool.TopLayerPriority);
                    bool? had = raw == false && On(P_BOTTOM) ? null : raw;
                    bool same = had == now;
                    bool spare = same && On(P_NOWASTE);   // ours: a use that changes nothing keeps the tool
                    player.UpdateProperty(target, PropertyBool.TopLayerPriority, now);
                    if (!spare) player.TryConsumeFromInventoryWithNetworking(source, 1);
                    target.SaveBiotaToDatabase();

                    bool worn = target.CurrentWieldedLocation != null && target.WielderId == player.Guid.Full;
                    var name = string.IsNullOrEmpty(target.Name) ? "armor" : target.Name;
                    string line = top
                        ? (same ? $"Your {name} was already on top; it stays over other armor covering the same places."
                                : $"Your {name} now shows on top of other armor covering the same places.")
                        : now == null
                            ? (same ? $"Your {name} already has its normal layering."
                                    : $"Your {name} is back to its normal layering: other armor covering the same places can show over it.")
                            : (same ? $"Your {name} already shows under other armor covering the same places."
                                    : $"Your {name} now shows under other armor covering the same places.");
                    if (spare) line += $" Your {source.Name} was not used up.";
                    else if (!worn && !same) line += " Wear it to see the change.";
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Craft));

                    if (worn && !same) player.EnqueueBroadcast(new GameMessageObjDescEvent(player));
                    player.SendUseDoneEvent();
                    Mod.Log.Info($"[RevivalGuard] ArmorLayering: {player.Name} {(top ? "Top" : "Bottom")} on {name} (0x{target.Guid.Full:X8}, {(worn ? "worn" : "pack")}): {(wasTop ? "top" : "default")} -> {(now == true ? "top" : now == null ? "default" : "under")}{(spare ? ", tool kept" : "")}");
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
        /// ACE's sort key becomes that. The cloak (retail last, ACE first) is CloakLast's.</summary>
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

        /// <summary>3b: THE CLOAK LAST. The 2017 captures put the cloak after everything (helm before cloak 103 times
        /// against 2); ACE lists it first, with the shirts and pants. The 19 cloak tables have no subpalettes and on human
        /// setups touch only parts 29-33, so the order rarely shows; it does where a table changes a shared part (setup
        /// 0x02001B2A, part 0). The cloak's own entries (parts, textures, subpalettes, rebuilt as Creature_Networking
        /// builds them) are moved to the end. A hidden cloak has none to move.</summary>
        [HarmonyPatch]
        static class CloakLast
        {
            static MethodBase TargetMethod() => Find(typeof(Creature), nameof(Creature.CalculateObjDesc), "the cloak order");
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Postfix(Creature __instance, ACE.Entity.ObjDesc __result)
            {
                if (__result == null || !On(P_CLOAK)) return;
                try
                {
                    WorldObject cloak = null;
                    foreach (var w in __instance.EquippedObjects.Values)
                        if (w.CurrentWieldedLocation == EquipMask.Cloak) { cloak = w; break; }
                    if (cloak?.ClothingBase == null) return;
                    var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(cloak.ClothingBase.Value);
                    if (table == null) return;

                    // Parts and textures: the setup entry ACE used is the one whose parts are all in the desc.
                    foreach (var effect in table.ClothingBaseEffects.Values)
                    {
                        var objs = effect.CloObjectEffects;
                        if (objs.Count == 0 || !objs.All(t => __result.AnimPartChanges.Any(a => a.Index == (byte)t.Index && a.AnimationId == t.ModelId))) continue;
                        foreach (var t in objs)
                        {
                            MoveLast(__result.AnimPartChanges, a => a.Index == (byte)t.Index && a.AnimationId == t.ModelId);
                            foreach (var t1 in t.CloTextureEffects)
                                MoveLast(__result.TextureChanges, m => m.PartIndex == (byte)t.Index && m.OldTexture == t1.OldTexture && m.NewTexture == t1.NewTexture);
                        }
                        break;
                    }

                    // Subpalettes, as Creature_Networking: the item's PaletteTemplate entry (else the first), shaded.
                    if (table.ClothingSubPalEffects.Count == 0) return;
                    uint opt = (uint)(cloak.PaletteTemplate ?? 0);
                    if (!table.ClothingSubPalEffects.TryGetValue(opt, out var sub)) sub = table.ClothingSubPalEffects.Values.First();
                    float shade = (float)(cloak.Shade ?? 0);
                    foreach (var sp in sub.CloSubPalettes)
                    {
                        var set = DatManager.PortalDat.ReadFromDat<PaletteSet>(sp.PaletteSet);
                        ushort pal = (ushort)set.GetPaletteID(shade);
                        foreach (var r in sp.Ranges)
                        {
                            ushort off = (ushort)(r.Offset / 8), len = (ushort)(r.NumColors / 8);
                            MoveLast(__result.SubPalettes, p => p.SubPaletteId == pal && p.Offset == off && p.Length == len);
                        }
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ArmorLayering: cloak order for {__instance?.Name}: {e.Message}"); }
            }

            /// <summary>The first matching entry (the cloak's: ACE listed it first) goes to the end.</summary>
            static void MoveLast<T>(List<T> list, Func<T, bool> match)
            {
                int i = list.FindIndex(x => match(x));
                if (i < 0) return;
                var e = list[i];
                list.RemoveAt(i);
                list.Add(e);
            }
        }
    }
}
