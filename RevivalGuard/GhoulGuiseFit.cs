using System;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The grave ghoul guise wears a wight.</summary>
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
