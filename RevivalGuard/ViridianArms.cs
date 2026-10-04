using System.Collections.ObjectModel;
using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE VIRIDIAN ARMS (weenies 900850-900862; docs/VIRIDIAN-RISE-UPGRADE.md). Made, not added: both
    /// switches default OFF, and no emote hands the weapons out yet.
    ///
    /// expansion_weapon_parts. A weapon of ours names its own GfxObj in a weenie_properties_anim_part row
    /// (index 0 -> 0x01FF08xx). ACE never sends an item's own anim parts: WorldObject.CalculateObjDesc
    /// returns right after AddBaseModelData for anything without a ClothingBase, which is every weapon.
    /// This postfix appends them for weapons and casters in the 900000+ block only, so the client's
    /// AcWielded (which already applies an item's AnimPartChanges to its held parts) draws ours in the
    /// hand. Off, the weapon is held as its base setup's retail model. It stays off on any shard a
    /// retail acclient.exe plays on: that client has no 0x01FF.... GfxObj to draw.
    ///
    /// viridian_set_bonus. The Viridian Grove Set is EquipmentSetId 900, which client_portal.dat's
    /// SpellTable.SpellSet does not have, so ACE's own set code finds no spells and the set does nothing.
    /// These postfixes answer for set 900 exactly the way the DAT answers for a retail set (a tier per
    /// piece count, the highest tier the full list), using the DAT's own item-set spells:
    ///   2 pieces: Crude Piercing Resistance, Apprentice Survivor's Health
    ///   3 pieces: Effective Piercing Resistance, Journeyman Survivor's Health, Journeyman Tracker's Stamina
    /// Pieces are any Viridian weapon (two when dual wielding) and the Heartseed of the Deru trinket.
    /// </summary>
    static class ViridianArms
    {
        internal const int SET_ID = 900;
        const uint WCID_LO = 900000, WCID_HI = 999999;
        const string P_PARTS = "expansion_weapon_parts";
        const string P_SET = "viridian_set_bonus";
        static readonly uint[][] TIERS =
        {
            new uint[0],
            new uint[0],
            new uint[] { 4781, 4754 },
            new uint[] { 4782, 4755, 4759 },
        };
        static int s_Registered;

        static bool Parts => PropertyManager.GetBool(P_PARTS, false).Item;
        static bool SetOn => PropertyManager.GetBool(P_SET, false).Item;

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_PARTS)) b[P_PARTS] = new Property<bool>(false, "ViridianArms: send a 900000+ weapon's own anim_part rows in its ObjDesc (our client only)");
                if (!b.ContainsKey(P_SET)) b[P_SET] = new Property<bool>(false, "ViridianArms: the Viridian Grove Set's bonus (EquipmentSetId 900)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ViridianArms: switches not registered with @modifybool ({e.Message}); both stay off"); }
            Mod.Log.Info("[RevivalGuard] ViridianArms: weapon part rows (expansion_weapon_parts) and set 900 (viridian_set_bonus), both default off");
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        static List<Spell> Tier(int pieces)
        {
            var ids = TIERS[Math.Clamp(pieces, 0, TIERS.Length - 1)];
            return ids.Select(id => new Spell(id, false)).ToList();
        }

        // ------------------------------------------------------------------ the held model
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CalculateObjDesc))]
        static class ObjDesc
        {
            static bool Prepare() { Register(); return true; }
            static void Postfix(WorldObject __instance, ACE.Entity.ObjDesc __result)
            {
                try
                {
                    var wo = __instance;
                    if (__result == null || wo is Creature || wo is Hook || !Parts) return;
                    if (wo.WeenieClassId < WCID_LO || wo.WeenieClassId > WCID_HI || wo.ClothingBase.HasValue) return;
                    if ((wo.ItemType & (ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster)) == 0) return;
                    var rows = new List<PropertiesAnimPart>();
                    wo.Biota.PropertiesAnimPart.CopyTo(rows, wo.BiotaDatabaseLock);
                    foreach (var ap in rows)
                    {
                        __result.AnimPartChanges.RemoveAll(x => x.Index == ap.Index);
                        __result.AnimPartChanges.Add(new PropertiesAnimPart { Index = ap.Index, AnimationId = ap.AnimationId });
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ViridianArms.ObjDesc: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ the set
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetSpellSet))]
        static class SetSpells
        {
            static void Postfix(List<WorldObject> setItems, int levelDiff, ref List<Spell> __result)
            {
                try
                {
                    var first = setItems?.FirstOrDefault();
                    if (first == null || (int?)first.EquipmentSetId != SET_ID || !SetOn) return;
                    __result = Tier(setItems.Count);   // no item levels on these: pieces worn is the tier, as ACE counts it
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ViridianArms.SetSpells: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetSpellSetAll))]
        static class SetSpellsAll
        {
            static void Postfix(EquipmentSet equipmentSet, ref List<Spell> __result)
            {
                if ((int)equipmentSet != SET_ID || !SetOn) return;
                __result = TIERS.SelectMany(t => t).Distinct().Select(id => new Spell(id, false)).ToList();
            }
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ItemSetContains))]
        static class SetContains
        {
            static void Postfix(WorldObject __instance, uint spellID, ref bool __result)
            {
                if (__result || (int?)__instance.EquipmentSetId != SET_ID || !SetOn) return;
                __result = TIERS.Any(t => t.Contains(spellID));
            }
        }
    }
}
