using System.Collections.ObjectModel;
using System.Reflection;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>An item's palette template when its clothing table HAS no entry for its own body.</summary>
    static class ClothingPaletteAnySetup
    {
        const string P_ON = "rg_clothing_palette_any_setup";
        static int s_Registered;

        static bool On => PropertyManager.GetBool(P_ON, true).Item;
        /// <summary>Weenies whose retail look is the recolour (Commemorative Bronze Statue of a Shadow).</summary>
        static readonly HashSet<uint> WCIDS = new HashSet<uint> { 19748 };

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
                    inner[P_ON] = new Property<bool>(true, "ClothingPaletteAnySetup: the Shadow bronze statue (19748) takes its PaletteTemplate although its ClothingTable has no entry for its setup");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ClothingPaletteAnySetup: switch not registered with @modifybool ({e.Message}); default on"); }
            Mod.Log.Info($"[RevivalGuard] ClothingPaletteAnySetup: the Shadow statue's bronze ({P_ON}), default on");
        }

        /// <summary>ACE's sub-palette pass (WorldObject.CalculateObjDesc), for a table that does not know the setup.</summary>
        internal static void Apply(WorldObject wo, ACE.Entity.ObjDesc objDesc)
        {
            if (!WCIDS.Contains(wo.WeenieClassId) || !wo.ClothingBase.HasValue || !(wo.Shade.HasValue || wo.PaletteTemplate.HasValue)) return;
            var table = DatManager.PortalDat.ReadFromDat<ClothingTable>((uint)wo.ClothingBase.Value);
            if (table == null || table.ClothingBaseEffects.ContainsKey(wo.SetupTableId) || table.ClothingSubPalEffects.Count == 0) return;
            uint opt = (uint)(wo.PaletteTemplate ?? 0);
            var sub = table.ClothingSubPalEffects.TryGetValue(opt, out var s) ? s : table.ClothingSubPalEffects[table.ClothingSubPalEffects.Keys.ElementAt(0)];
            float shade = (float)(wo.Shade ?? 0);
            foreach (var cs in sub.CloSubPalettes)
            {
                var set = DatManager.PortalDat.ReadFromDat<PaletteSet>(cs.PaletteSet);
                ushort pal = (ushort)set.GetPaletteID(shade);
                foreach (var r in cs.Ranges)
                    objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = pal, Offset = (ushort)(r.Offset / 8), Length = (ushort)(r.NumColors / 8) });
            }
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CalculateObjDesc))]
        static class Item
        {
            static bool Prepare() { Register(); return true; }
            static void Postfix(WorldObject __instance, ACE.Entity.ObjDesc __result)
            {
                try
                {
                    if (__result == null || __instance is Creature || __instance is Hook || __instance is Corpse || !On) return;
                    Apply(__instance, __result);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ClothingPaletteAnySetup: {e.Message}"); }
            }
        }
    }
}
