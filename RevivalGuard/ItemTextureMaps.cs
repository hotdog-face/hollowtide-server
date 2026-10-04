using System.Collections.ObjectModel;
using System.Reflection;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AN ITEM'S OWN TEXTURE ROWS, WORN OR DROPPED (the seasonal Jack o' Lantern Masks, weenies
    /// 900713 and 900718-900720; docs/SEASONAL-UPGRADE.md). Made, not added: the switch
    /// `expansion_item_textures` defaults OFF and no live weenie has rows for it.
    ///
    /// Stock ACE takes a worn item's texture changes from its ClothingTable and nothing else
    /// (Creature.CalculateObjDesc), and a dropped item's the same way (WorldObject.CalculateObjDesc),
    /// so a weenie's weenie_properties_texture_map rows are never sent for an item. This postfix lets
    /// a 900000+ item REWRITE a texture change its ClothingTable already made: a row (part, old, new)
    /// replaces the NewTexture of an existing change with the same part and OldTexture, and does
    /// nothing else. That one rule keeps it exact, the same way ApplyOwnAnimParts only replaces a
    /// part the table already changed: a row meant for the dropped mask (part 0 of its item setup)
    /// finds no matching change on the wearer's abdomen and is ignored, and a row can never add a
    /// texture the retail table did not already put there.
    ///
    /// The use: retail's Scarecrow Mask (ClothingTable 0x1000032A) wears pumpkin 0x010027B6 with
    /// texture 0x050019F5; the Jack o' Lantern family paints the same pumpkin mesh with four other
    /// carved faces (0x05002D09-0x05002D0C). A clone with one row per part wears retail's pumpkin with
    /// another of retail's faces: no new art. Off, the clone looks exactly like the Scarecrow Mask.
    /// </summary>
    static class ItemTextureMaps
    {
        const string P_ON = "expansion_item_textures";
        const uint WCID_LO = 900000, WCID_HI = 999999;
        static int s_Registered;

        static bool On => PropertyManager.GetBool(P_ON, false).Item;

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var inner = typeof(ReadOnlyDictionary<string, Property<bool>>)
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Select(f => f.GetValue(DefaultPropertyManager.DefaultBooleanProperties))
                    .OfType<IDictionary<string, Property<bool>>>().FirstOrDefault();
                if (inner != null && !inner.ContainsKey(P_ON))
                    inner[P_ON] = new Property<bool>(false, "ItemTextureMaps: a 900000+ item's own texture_map rows rewrite the texture changes its ClothingTable made (our client and retail's alike)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ItemTextureMaps: switch not registered with @modifybool ({e.Message}); it stays off"); }
            Mod.Log.Info($"[RevivalGuard] ItemTextureMaps: item texture rows ({P_ON}), default off");
        }

        static void Rewrite(ACE.Entity.ObjDesc objDesc, WorldObject item)
        {
            if (item == null || item.WeenieClassId < WCID_LO || item.WeenieClassId > WCID_HI) return;
            var rows = new List<PropertiesTextureMap>();
            item.Biota.PropertiesTextureMap.CopyTo(rows, item.BiotaDatabaseLock);
            if (rows.Count == 0) return;
            for (int i = 0; i < objDesc.TextureChanges.Count; i++)
            {
                var tc = objDesc.TextureChanges[i];
                var r = rows.FirstOrDefault(x => x.PartIndex == tc.PartIndex && x.OldTexture == tc.OldTexture);
                if (r != null)
                    objDesc.TextureChanges[i] = new PropertiesTextureMap { PartIndex = tc.PartIndex, OldTexture = tc.OldTexture, NewTexture = r.NewTexture };
            }
        }

        /// <summary>A dropped or carried item. A hook forwards to its item, whose own call already ran.</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CalculateObjDesc))]
        static class Item
        {
            static bool Prepare() { Register(); return true; }
            static void Postfix(WorldObject __instance, ACE.Entity.ObjDesc __result)
            {
                try
                {
                    if (__result == null || __instance is Creature || __instance is Hook || !On) return;
                    Rewrite(__result, __instance);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ItemTextureMaps.Item: {e.Message}"); }
            }
        }

        /// <summary>A wearer: every equipped 900000+ item's rows, after ACE's clothing pass.</summary>
        [HarmonyPatch(typeof(Creature), nameof(Creature.CalculateObjDesc))]
        static class Wearer
        {
            static void Postfix(Creature __instance, ACE.Entity.ObjDesc __result)
            {
                try
                {
                    if (__result == null || !On) return;
                    foreach (var w in __instance.EquippedObjects.Values.ToList())
                        Rewrite(__result, w);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ItemTextureMaps.Wearer: {e.Message}"); }
            }
        }
    }
}
