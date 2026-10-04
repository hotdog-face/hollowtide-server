using System;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE GRAVE GHOUL GUISE WEARS A WIGHT. Owner, 2026-09-30: "give it a clothing table that fits everyone", then
    /// "The grave ghoul guise looks like a tumerok? That seems wrong", and chose a real ghoul look.
    ///
    /// The guise (weenie 900702, docs/SEASONAL-UPGRADE.md) was cloned onto ClothingTable 0x100007DC, an unused
    /// grey clawed digitigrade body that no retail item or creature ever wore (it has no wiki page either) and
    /// that reads as a Tumerok. Retail's Wight (setup 0x020016A1) is the ghoul the game already has: the Zombie's
    /// own meshes in the dark, desiccated colours of the Wight's ClothingTable 0x10000066, palette template 68.
    ///
    /// So, once, in memory, on ACE's cached copy of 0x100007DC (the DAT is untouched; only 900702 uses it):
    ///   * its body entries become those of retail's Undead Guise, 0x100003F9: the Zombie meshes, fitted by
    ///     retail to every playable body (on the smoke-legged Umbraen setups, torso and arms only);
    ///   * its colour 39 (the guise's PaletteTemplate) becomes the Wight's colour, 0x10000066 template 68.
    /// Both our client and retail's draw it: they are sent the part swaps and palettes, not the table.
    /// Not carried over: the Wight's glowing eyes, a particle script on the creature's setup.
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.CalculateObjDesc))]
    static class GhoulGuiseFit
    {
        const uint GUISE = 0x100007DC, UNDEAD_GUISE = 0x100003F9, WIGHT = 0x10000066;
        const uint GUISE_COLOUR = 39, WIGHT_COLOUR = 68;
        static bool s_Done;

        static void Prefix()
        {
            if (s_Done) return;
            s_Done = true;
            try
            {
                var guise = DatManager.PortalDat.ReadFromDat<ClothingTable>(GUISE);
                var undead = DatManager.PortalDat.ReadFromDat<ClothingTable>(UNDEAD_GUISE);
                var wight = DatManager.PortalDat.ReadFromDat<ClothingTable>(WIGHT);
                if (guise == null || undead == null || wight == null || !wight.ClothingSubPalEffects.TryGetValue(WIGHT_COLOUR, out var colour)) return;
                guise.ClothingBaseEffects.Clear();
                foreach (var kv in undead.ClothingBaseEffects) guise.ClothingBaseEffects[kv.Key] = kv.Value;
                guise.ClothingSubPalEffects[GUISE_COLOUR] = colour;
                Mod.Log.Info($"[GhoulGuiseFit] Grave Ghoul Guise table 0x{GUISE:X8}: {guise.ClothingBaseEffects.Count} bodies from the Undead Guise, colour {GUISE_COLOUR} = the Wight's {WIGHT_COLOUR}");
            }
            catch (Exception e) { Mod.Log.Warn($"[GhoulGuiseFit] {e.Message}"); }
        }
    }
}
