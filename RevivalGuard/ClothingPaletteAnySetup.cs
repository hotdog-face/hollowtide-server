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
    /// <summary>
    /// AN ITEM'S PALETTE TEMPLATE WHEN ITS CLOTHING TABLE HAS NO ENTRY FOR ITS OWN BODY (report 261024-0035,
    /// "Shadow statue is a half naked bald woman"). Weenie 19748, the Commemorative Bronze Statue of a Shadow, is
    /// the human female setup 0x0200004E with ClothingBase 0x10000393 and PaletteTemplate 25. That table has
    /// ClothingBaseEffects only for crystal setups (0x02000700, 0x020008F9 ...), and stock ACE applies the
    /// ClothingSubPalEffects only inside its "table knows this setup" test, so the statue went out with no
    /// recolour and drew skin and underwear. Template 25 recolours the whole palette (offset 0, 2048 colours) with
    /// PaletteSet 0x0F0000B2: the solid bronze figure of retail's own screenshot (AC wiki, "Commemorative Bronze
    /// Statue (Shadow)", Live Image).
    ///
    /// ONE WEENIE, NOT A RULE (audit 2026-10-24, docs/findings/clothing-palette-audit-2026-10-24.md). 535 items share the
    /// gap (a ClothingBase with no entry for their setup, plus a template or shade). For the ones retail screenshots
    /// show, the colours they draw WITHOUT the recolour are retail's (shards green/white/red/blue, pyreal ingots gold,
    /// the Olthoi Claw blue-black) and the recolour would turn them brown, red or magenta. So retail's server did not
    /// recolour across setups in general; this statue is the exception its screenshot shows, kept to its weenie.
    /// Switch `rg_clothing_palette_any_setup`, default on.
    /// </summary>
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
