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
    /// THE WARDENFORGED PLATE (weenies 901350-901360; docs/ARMOR-SET.md). Made, not added: both switches
    /// default OFF, and nothing hands the set out yet.
    ///
    /// wardenforged_shield_part. The shield (901359) draws its own GfxObj 0x01FF1361 in the hand. Its
    /// weenie deliberately carries NO anim_part row: ACE's appearance pass lists every equipped
    /// ItemType.Armor item, shields included, and ApplyOwnAnimParts would copy a part-0 row onto the
    /// wearer's abdomen. So the part comes from here, on the shield's OWN ObjDesc only (never a
    /// Creature's), replacing part 0 of its setup the way AcWielded reads it. Off, it is held as the
    /// Covenant Shield it clones. Keep it off on any shard a retail acclient.exe plays on.
    ///
    /// wardenforged_set_bonus. EquipmentSetId 901, which client_portal.dat's SpellTable does not have,
    /// answered the way the DAT answers for a retail set: a tier per piece count, from retail's own
    /// item-set spells, tiers at 2, 4, 6, 8 and 9 pieces as the Society armour set (30) does, so the
    /// full nine is the reward:
    ///   2: Incidental Flame Resistance, Incidental Lightning Resistance
    ///   4: Crude Flame and Lightning Resistance, Novice Hero's Endurance
    ///   6: Effective Flame and Lightning Resistance, Apprentice Hero's Endurance, Novice Guardian's Invulnerability
    ///   8: the same, Journeyman Hero's Endurance, Apprentice Guardian's Invulnerability, Apprentice Wayfarer's Impregnability
    ///   9: Masterwork Flame and Lightning Resistance, Journeyman Hero's Endurance, Journeyman Survivor's Health,
    ///      Journeyman Guardian's Invulnerability, Journeyman Wayfarer's Impregnability
    /// </summary>
    static class WardenforgedPlate
    {
        internal const int SET_ID = 901;
        const uint SHIELD_WCID = 901359, SHIELD_GFX = 0x01FF1361;
        const string P_SHIELD = "wardenforged_shield_part";
        const string P_SET = "wardenforged_set_bonus";
        // index = pieces worn (0-9)
        static readonly uint[] T2 = { 4768, 4776 };
        static readonly uint[] T4 = { 4769, 4777, 4734 };
        static readonly uint[] T6 = { 4770, 4778, 4735, 4858 };
        static readonly uint[] T8 = { 4770, 4778, 4736, 4859, 4863 };
        static readonly uint[] T9 = { 4771, 4779, 4736, 4755, 4860, 4864 };
        static readonly uint[][] TIERS = { new uint[0], new uint[0], T2, T2, T4, T4, T6, T6, T8, T9 };
        static int s_Registered;

        static bool ShieldOn => PropertyManager.GetBool(P_SHIELD, false).Item;
        static bool SetOn => PropertyManager.GetBool(P_SET, false).Item;

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_SHIELD)) b[P_SHIELD] = new Property<bool>(false, "WardenforgedPlate: the Wardenforged Shield (901359) draws its own GfxObj in the hand (our client only)");
                if (!b.ContainsKey(P_SET)) b[P_SET] = new Property<bool>(false, "WardenforgedPlate: the Wardenforged Plate set bonus (EquipmentSetId 901)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WardenforgedPlate: switches not registered with @modifybool ({e.Message}); both stay off"); }
            Mod.Log.Info("[RevivalGuard] WardenforgedPlate: shield part (wardenforged_shield_part) and set 901 (wardenforged_set_bonus), both default off");
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        static uint[] Tier(int pieces) => TIERS[Math.Clamp(pieces, 0, TIERS.Length - 1)];

        // ------------------------------------------------------------------ the held shield
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CalculateObjDesc))]
        static class ObjDesc
        {
            static bool Prepare() { Register(); return true; }
            static void Postfix(WorldObject __instance, ACE.Entity.ObjDesc __result)
            {
                try
                {
                    if (__result == null || __instance is Creature || __instance is Hook) return;
                    if (__instance.WeenieClassId != SHIELD_WCID || !ShieldOn) return;
                    __result.AnimPartChanges.RemoveAll(x => x.Index == 0);
                    __result.AnimPartChanges.Add(new PropertiesAnimPart { Index = 0, AnimationId = SHIELD_GFX });
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WardenforgedPlate.ObjDesc: {e.Message}"); }
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
                    __result = Tier(setItems.Count).Select(id => new Spell(id, false)).ToList();   // pieces worn is the tier, as ACE counts it
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WardenforgedPlate.SetSpells: {e.Message}"); }
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
