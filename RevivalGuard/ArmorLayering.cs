using System.Reflection;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Armor layering tools as retail had them.</summary>
    static class ArmorLayering
    {
        internal const string P_WORN = "layering_worn", P_BOTTOM = "layering_bottom_resets", P_ORDER = "layering_retail_order",
                              P_NOWASTE = "layering_no_waste", P_CLOAK = "layering_cloak_last", P_OWN = "layering_own_parts_in_order";

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
                if (!b.ContainsKey(P_OWN)) b[P_OWN] = new Property<bool>(true, "RevivalGuard ArmorLayering: a worn item's own part models (expansion clothing) keep their place in the layer order, so armor worn over them shows (false: an own part always wins)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ArmorLayering: switches not registered with @modifybool ({e.Message}); still read at their defaults"); }
        }

        static MethodBase Find(Type t, string name, string what)
        {
            var m = AccessTools.DeclaredMethod(t, name);
            if (m == null) Mod.Log.Error($"[RevivalGuard] ArmorLayering: {t.Name}.{name} not found; {what} keeps ACE's rule");
            return m;
        }

        /// <summary>2. A worn target passes for a layering tool</summary>
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

        /// <summary>1 and 2: the tool itself.</summary>
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

        /// <summary>1: a TopLayerPriority=false that ACE's old Bottom left on an item reads as unflagged</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.TopLayerPriority), MethodType.Getter)]
        static class FalseIsDefault
        {
            static void Postfix(ref bool? __result)
            {
                if (__result == false && On(P_BOTTOM)) __result = null;
            }
        }

        /// <summary>3: the default order.</summary>
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

        /// <summary>4: an item's own part models layer like the item. ACE applies every worn item's own anim-part rows after all
        /// ClothingBase work, so an under-layer piece with its own models replaced the armor worn over it.</summary>
        [HarmonyPatch]
        static class OwnPartsInOrder
        {
            static MethodBase TargetMethod() => Find(typeof(Creature), nameof(Creature.CalculateObjDesc), "own part models in layer order");
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Postfix(Creature __instance, ACE.Entity.ObjDesc __result)
            {
                if (__result == null || !On(P_OWN)) return;
                try
                {
                    var order = WornOrder(__instance);
                    for (int x = 0; x < order.Count - 1; x++)
                    {
                        var own = OwnParts(order[x]);
                        if (own == null) continue;
                        foreach (var ap in own)
                        {
                            // the last piece over this one that covers the same part decides what the part shows
                            uint? over = null;
                            for (int y = x + 1; y < order.Count; y++) over = ModelFor(__instance, order[y], ap.Index) ?? over;
                            if (over == null) continue;
                            int at = __result.AnimPartChanges.FindIndex(a => a.Index == ap.Index && a.AnimationId == ap.AnimationId);
                            if (at >= 0) __result.AnimPartChanges[at] = new ACE.Entity.Models.PropertiesAnimPart { Index = ap.Index, AnimationId = over.Value };
                        }
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ArmorLayering: own part order for {__instance?.Name}: {e.Message}"); }
            }

            /// <summary>The worn pieces in the order Creature.CalculateObjDesc lays them: clothes by ClothingPriority, then armor (under, default, top).</summary>
            internal static List<WorldObject> WornOrder(Creature c)
            {
                var all = c.EquippedObjects.Values.ToList();
                bool Armor(WorldObject w) => w.ItemType == ItemType.Armor || ((w.CurrentWieldedLocation ?? EquipMask.None) & (EquipMask.Armor | EquipMask.Extremity)) != 0;
                var clothes = all.Where(w => w.ItemType == ItemType.Clothing && !Armor(w)).OrderBy(w => w.ClothingPriority);
                var armor = all.Where(Armor).ToList();
                var sorted = armor.Where(w => w.TopLayerPriority == false).OrderBy(w => w.VisualClothingPriority)
                    .Concat(armor.Where(w => w.TopLayerPriority == null).OrderBy(w => w.VisualClothingPriority))
                    .Concat(armor.Where(w => w.TopLayerPriority == true).OrderBy(w => w.VisualClothingPriority));
                return clothes.Concat(sorted)
                    .Where(w => ((w.CurrentWieldedLocation ?? EquipMask.None) & (EquipMask.Clothing | EquipMask.Armor | EquipMask.Cloak)) != 0).ToList();
            }

            static List<ACE.Entity.Models.PropertiesAnimPart> OwnParts(WorldObject w)
            {
                if (w.Biota.PropertiesAnimPart == null || w.Biota.PropertiesAnimPart.GetCount(w.BiotaDatabaseLock) == 0) return null;
                var l = new List<ACE.Entity.Models.PropertiesAnimPart>();
                w.Biota.PropertiesAnimPart.CopyTo(l, w.BiotaDatabaseLock);
                return l;
            }

            /// <summary>The model this piece puts on a part of this wearer (its own row, else its ClothingBase), or null when it leaves the part alone.</summary>
            static uint? ModelFor(Creature c, WorldObject w, byte index)
            {
                var loc = w.CurrentWieldedLocation ?? EquipMask.None;
                if (loc == EquipMask.HeadWear || loc == EquipMask.Cloak) return null;   // may be hidden by the wearer; they share no part with clothes
                var own = OwnParts(w);
                if (own != null) foreach (var ap in own) if (ap.Index == index) return ap.AnimationId;
                if (w.ClothingBase == null) return null;
                var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(w.ClothingBase.Value);
                if (table == null || !table.ClothingBaseEffects.TryGetValue(c.SetupTableId, out var effect)) return null;
                foreach (var t in effect.CloObjectEffects) if ((byte)t.Index == index) return t.ModelId;
                return null;
            }
        }

        /// <summary>3b: the cloak last.</summary>
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
