using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>New characters accept fellowship invites.</summary>
    [HarmonyPatch(typeof(PlayerFactory), nameof(PlayerFactory.Create))]
    public static class FellowshipInviteDefault
    {
        static void Postfix(Player player)
        {
            if (player == null) return;
            player.SetCharacterOption(CharacterOption.IgnoreFellowshipRequests, false);
        }
    }
}
