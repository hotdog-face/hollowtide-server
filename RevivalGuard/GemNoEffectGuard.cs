using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A GEM THAT DOES NOTHING IS NOT USED UP. ACE's Gem.UseGem ends with
    /// `TryConsumeFromInventoryWithNetworking(this, 1)` unless the gem is UnlimitedUse, whether or not
    /// it cast a spell, granted a contract or created an item. Coalesced Aetheria (wcid 42635-42637) is
    /// a WeenieType Gem with none of the three, so double-clicking it simply destroyed it: the owner
    /// lost five on 2026-09-23 trying to put them in his newly visible sigil slots. Retail reveals
    /// Coalesced Aetheria by using an Aetheria Mana Stone ON it (ACE Aetheria.UseObjectOnTarget); a
    /// plain use does nothing.
    ///
    /// So a gem with no spell, no contract and no created item is refused here before UseGem runs,
    /// with a hint for Coalesced Aetheria. Every gem that does something is untouched.
    /// </summary>
    [HarmonyPatch(typeof(Gem), nameof(Gem.UseGem))]
    public static class GemNoEffectGuard
    {
        static bool Prefix(Gem __instance, Player player)
        {
            if (__instance == null || player == null) return true;
            bool hasEffect = __instance.SpellDID.HasValue
                             || (__instance.UseCreateContractId ?? 0) > 0
                             || (__instance.UseCreateItem ?? 0) > 0;
            if (hasEffect) return true;
            player.SendTransientError(__instance.Name == "Coalesced Aetheria"
                ? "Use an Aetheria Mana Stone on the Coalesced Aetheria to reveal it."
                : $"The {__instance.Name} cannot be used by itself.");
            return false;
        }
    }
}
