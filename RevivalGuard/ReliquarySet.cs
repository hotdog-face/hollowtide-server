using System.Collections.ObjectModel;
using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE RELIQUARY SET (round 2, The Drowned Reliquary; docs/content-review/round2/set-bonus.md). The switch
    /// reliquary_set_bonus defaults ON since the dungeon's go-live (owner 2026-11-14, content batch 1); @modifybool turns it off.
    ///
    /// The set is EquipmentSetId 924 (weenies 924300-924305), which client_portal.dat's SpellTable.SpellSet does not
    /// have, so ACE's own set code finds no spells and the set does nothing. These postfixes answer for set 924 the way
    /// the DAT answers for a retail set, the same way ViridianArms answers for set 900. The numbers live in
    /// ReliquarySetRule (pure, tested in tools/nettest): pieces counted by slot (weapon, shield, amulet, ring; 4 at
    /// most), 2 pieces Minor Focus, full set (4) Minor Focus and Major Frost Ward.
    ///
    /// Each target is looked up on WorldObject, the type that declares it, with its exact parameter list; a missing
    /// target is logged and that patch skipped, never thrown (a throw in PatchAll would turn every guard off).
    /// Registers itself from Prepare(), so Mod.cs needs no line.
    /// </summary>
    static class ReliquarySet
    {
        const string P_SET = "reliquary_set_bonus";
        static int s_Registered;

        static bool SetOn => PropertyManager.GetBool(P_SET, true).Item;

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_SET)) b[P_SET] = new Property<bool>(true, "ReliquarySet: the Reliquary Set's bonus (EquipmentSetId 924): 2 pieces Minor Focus, 4 pieces adds Major Frost Ward");
                if (!b.ContainsKey(P_SHIELD)) b[P_SHIELD] = new Property<bool>(false, "ReliquarySet: the Reliquary Ward (924303) draws its own GfxObj in the hand (our client only)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ReliquarySet: switch not registered with @modifybool ({e.Message}); it keeps its default (on)"); }
            Mod.Log.Info("[RevivalGuard] ReliquarySet: set 924 (reliquary_set_bonus), default on");
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        static MethodBase Find(string name, Type[] args)
        {
            var m = AccessTools.DeclaredMethod(typeof(WorldObject), name, args);
            if (m == null) Mod.Log.Error($"[RevivalGuard] ReliquarySet: WorldObject.{name} not found; set 924 keeps ACE's answer (no bonus)");
            return m;
        }

        static List<Spell> ToSpells(IEnumerable<uint> ids) => ids.Select(id => new Spell(id, false)).ToList();

        static bool IsSet(EquipmentSet? id) => (int?)id == ReliquarySetRule.SET_ID;

        // ------------------------------------------------------------------ the tier for what is worn
        [HarmonyPatch]
        static class SetSpells
        {
            static MethodBase TargetMethod() => Find(nameof(WorldObject.GetSpellSet), new[] { typeof(List<WorldObject>), typeof(int) });
            static bool Prepare(MethodBase original)
            {
                Register();
                return original != null || TargetMethod() != null;
            }

            static void Postfix(List<WorldObject> setItems, ref List<Spell> __result)
            {
                try
                {
                    var first = setItems?.FirstOrDefault();
                    if (first == null || !IsSet(first.EquipmentSetId) || !SetOn) return;
                    // no item levels on these: pieces worn (by slot) is the tier
                    __result = ToSpells(ReliquarySetRule.Tier(ReliquarySetRule.Pieces(setItems.Select(i => i.WeenieClassId))));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ReliquarySet.SetSpells: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ the held shield (reliquary_shield_part)
        // The Reliquary Ward (924303) draws its own GfxObj 0x01FF6303 in the hand, the WardenforgedPlate way: no anim_part
        // row on the weenie (ApplyOwnAnimParts would copy a part-0 row onto the wearer), part 0 swapped here on the shield's
        // OWN ObjDesc only. Off, it is held as the Covenant Shield it clones. Keep it off where a retail acclient.exe plays.
        const string P_SHIELD = "reliquary_shield_part";
        const uint SHIELD_WCID = 924303, SHIELD_GFX = 0x01FF6303;
        static bool ShieldOn => PropertyManager.GetBool(P_SHIELD, false).Item;

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CalculateObjDesc))]
        static class ShieldObjDesc
        {
            static bool Prepare() { Register(); return true; }
            static void Postfix(WorldObject __instance, ACE.Entity.ObjDesc __result)
            {
                try
                {
                    if (__result == null || __instance is Creature || __instance is Hook) return;
                    if (__instance.WeenieClassId != SHIELD_WCID || !ShieldOn) return;
                    __result.AnimPartChanges.RemoveAll(x => x.Index == 0);
                    __result.AnimPartChanges.Add(new ACE.Entity.Models.PropertiesAnimPart { Index = 0, AnimationId = SHIELD_GFX });
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ReliquarySet.ShieldObjDesc: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ every spell the set can give
        [HarmonyPatch]
        static class SetSpellsAll
        {
            static MethodBase TargetMethod() => Find(nameof(WorldObject.GetSpellSetAll), new[] { typeof(EquipmentSet) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Postfix(EquipmentSet equipmentSet, ref List<Spell> __result)
            {
                try
                {
                    if ((int)equipmentSet != ReliquarySetRule.SET_ID || !SetOn) return;
                    __result = ToSpells(ReliquarySetRule.All());
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ReliquarySet.SetSpellsAll: {e.Message}"); }
            }
        }

        [HarmonyPatch]
        static class SetContains
        {
            static MethodBase TargetMethod() => Find(nameof(WorldObject.ItemSetContains), new[] { typeof(uint) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Postfix(WorldObject __instance, uint spellID, ref bool __result)
            {
                try
                {
                    if (__result || !IsSet(__instance.EquipmentSetId) || !SetOn) return;
                    __result = ReliquarySetRule.All().Contains(spellID);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ReliquarySet.SetContains: {e.Message}"); }
            }
        }
    }
}
