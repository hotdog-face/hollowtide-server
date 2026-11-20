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
    /// <summary>
    /// A HELM OVER A ROBE'S HOOD (ticket 261120-0019, the owner: a Hearty Reedshark Robe reduced to the chest under a
    /// Crown of Anointed Blood, "the two hems overlap ... likely an issue with all robes").
    ///
    /// Some robes draw a hood: their ClothingTable puts a hooded head model on the head, part 16 (Hearty Reedshark Robe
    /// 0x100004D6, Marksman's Robe, Enscorcelled Robe, Faran Robe with Hood, Elemental Master Robe, the Jumpsuits...).
    /// The server's ObjDesc is last-wins per part, and our client and retail's both draw it that way, so:
    ///   * a helm on part 16 drew under the hood when ACE's order put the robe last (with ArmorLayering's retail order
    ///     the helm comes after the body pieces and wins);
    ///   * a crown, circlet, coronet or Rynthid mask is not on part 16 at all (part 22 or 21), so the robe's hood stayed
    ///     on the head and the crown sat in it: two head pieces at once, whatever the order.
    /// The hooded robes without a head slot (Reedshark, Marksman's: ValidLocations 0x7F00) met a helm in retail too; the
    /// ones with it (0x7F01) only once SuitReduction lets them be reduced to the chest. Retail's 2014/2017 captures show
    /// no crown worn with a hood (none to copy); a part-16 helm over a robe draws the helm.
    ///
    /// So here a DRAWN helm wins the head: after ACE's clothing pass, a hooded piece's head model and head textures leave
    /// the desc, and the head is the helm's, or the wearer's own (ACE's base head, already in the desc). A hidden helm
    /// (Show Helm off) or one with nothing to draw on this body keeps the hood.
    ///   @modifybool helm_over_hood false   ACE's desc unchanged
    /// </summary>
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
