using System.Reflection;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// Scrolls of the six player spells (weenies 901431-901436, datafix spells-golive-2-scrolls). Their weenies carry no
    /// spell id: ACE's Scroll constructor throws on a spell that is not in the loaded table, so a scroll with one would
    /// break its vendor's shop and the pack of anyone holding it whenever rg_experimental_spells is off. The spell is
    /// given here instead, on the object only (nothing saved), and only while the spells are loaded.
    /// </summary>
    static class ExperimentalScrolls
    {
        internal static bool HookNew, HookLoad;

        internal static void Bind(Scroll scroll)
        {
            uint id = SeasonalSourcesRule.ExpScrollSpell(scroll.WeenieClassId);
            if (id == 0 || !ExperimentalSpells.Loaded) return;
            if (!DatManager.PortalDat.SpellTable.Spells.ContainsKey(id)) return;
            var spell = new Spell(id, false);
            scroll.Spell = spell;
            scroll.LongDesc = $"Inscribed spell: {spell.Name}\n{spell.Description}";
        }
    }

    [HarmonyPatch]
    static class ExperimentalScrollsNew
    {
        static MethodBase TargetMethod() => AccessTools.Constructor(typeof(Scroll), new[] { typeof(Weenie), typeof(ObjectGuid) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalScrolls.HookNew = TargetMethod() != null);
        static void Postfix(Scroll __instance)
        {
            try { ExperimentalScrolls.Bind(__instance); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalScrolls: new: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class ExperimentalScrollsLoad
    {
        static MethodBase TargetMethod() => AccessTools.Constructor(typeof(Scroll), new[] { typeof(Biota) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalScrolls.HookLoad = TargetMethod() != null);
        static void Postfix(Scroll __instance)
        {
            try { ExperimentalScrolls.Bind(__instance); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalScrolls: load: {e.Message}"); }
        }
    }
}
