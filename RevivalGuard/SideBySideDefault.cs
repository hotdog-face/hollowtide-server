using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Side-by-side vitals is the shard's default.</summary>
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
