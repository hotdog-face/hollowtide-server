using System;
using System.Collections.Generic;
using System.Linq;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A helm over a robe's hood.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.CalculateObjDesc))]
    static class HelmOverHood
    {
        internal const string P_ON = "helm_over_hood";
        static int s_Registered;

        static void Register()
        {
            if (System.Threading.Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new ACE.Server.Managers.Property<bool>(true, "RevivalGuard HelmOverHood: a drawn helm replaces a robe's built-in hood (false: ACE, both drawn)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] HelmOverHood: {P_ON} not registered with @modifybool ({e.Message}); it reads its default (on)"); }
        }

        static bool Prepare() { Register(); return true; }

        static void Postfix(Creature __instance, ACE.Entity.ObjDesc __result)
        {
            if (__result == null) return;
            try
            {
                if (!ACE.Server.Managers.PropertyManager.GetBool(P_ON, true).Item) return;
                if (__instance is Player p && !p.GetCharacterOption(CharacterOption.ShowYourHelmOrHeadGear)) return;
                uint setup = __instance.SetupTableId, alt = DressingRoom.TableSetup(setup);

                List<HelmOverHoodRule.Piece> worn = null;
                bool helm = false, hood = false;
                foreach (var w in __instance.EquippedObjects.Values.ToList())
                {
                    var loc = w.CurrentWieldedLocation ?? EquipMask.None;
                    // what ACE's clothing pass draws: clothing, armour, cloak, with a table
                    if ((loc & (EquipMask.Clothing | EquipMask.Armor | EquipMask.Cloak)) == 0 || w.ClothingBase == null) continue;
                    var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(w.ClothingBase.Value);
                    if (table == null) continue;
                    if (!table.ClothingBaseEffects.TryGetValue(setup, out var fx) && !table.ClothingBaseEffects.TryGetValue(alt, out fx)) continue;
                    var piece = new HelmOverHoodRule.Piece { IsHelm = loc == EquipMask.HeadWear };
                    foreach (var o in fx.CloObjectEffects)
                    {
                        piece.Parts.Add(((int)o.Index, o.ModelId));
                        foreach (var t in o.CloTextureEffects) piece.Textures.Add(((int)o.Index, t.OldTexture, t.NewTexture));
                    }
                    if (piece.IsHelm) helm = true;
                    else if (piece.Parts.Any(x => x.Index == HelmOverHoodRule.HEAD)) hood = true;
                    else continue;
                    (worn ??= new List<HelmOverHoodRule.Piece>()).Add(piece);
                }
                if (!helm || !hood) return;

                var (models, tex) = HelmOverHoodRule.Drop(worn);
                if (models.Count == 0) return;
                __result.AnimPartChanges.RemoveAll(a => a.Index == HelmOverHoodRule.HEAD && models.Contains(a.AnimationId));
                __result.TextureChanges.RemoveAll(t => t.PartIndex == HelmOverHoodRule.HEAD && tex.Contains((t.OldTexture, t.NewTexture)));
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] HelmOverHood for {__instance?.Name}: {e.Message}"); }
        }
    }
}
