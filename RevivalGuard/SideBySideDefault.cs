using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// SIDE-BY-SIDE VITALS IS THE SHARD'S DEFAULT. Owner, 2026-09-23: "make the global default be
    /// side by side vitals". ACE gives a new character CharacterOptions1.Default
    /// (PlayerFactory.Create: SetCharacterOption(CharacterOptions1Default, true)), which retail's
    /// default does not include. This sets the one extra bit right after, on creation only, so a
    /// player who turns it off keeps it off. Existing characters are untouched.
    /// Ours, not retail's: recorded in docs/DIVERGENCES.md.
    /// </summary>
    [HarmonyPatch(typeof(PlayerFactory), nameof(PlayerFactory.Create))]
    public static class SideBySideDefault
    {
        static void Postfix(Player player)
        {
            if (player == null) return;
            player.SetCharacterOption(CharacterOption.SideBySideVitals, true);
        }
    }
}
