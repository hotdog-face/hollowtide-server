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
    /// <summary>The wardenforged plate.</summary>
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
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_SHIELD)) b[P_SHIELD] = new Property<bool>(false, "WardenforgedPlate: the Wardenforged Shield (901359) draws its own GfxObj in the hand (our client only)");
                if (!b.ContainsKey(P_SET)) b[P_SET] = new Property<bool>(false, "WardenforgedPlate: the Wardenforged Plate set bonus (EquipmentSetId 901)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WardenforgedPlate: switches not registered with @modifybool ({e.Message}); both stay off"); }
            Mod.Log.Info("[RevivalGuard] WardenforgedPlate: shield part (wardenforged_shield_part) and set 901 (wardenforged_set_bonus), both default off");
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
