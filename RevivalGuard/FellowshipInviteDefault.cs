using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// NEW CHARACTERS ACCEPT FELLOWSHIP INVITES. Owner, 2026-10-01: Ignore Fellowship Requests OFF by
    /// default, "don't follow retail there". Retail's PlayerModule default 0x50c4a54a and ACE's
    /// CharacterOptions1.Default (set in PlayerFactory.Create) both carry IgnoreFellowshipRequests (0x8),
    /// so a fresh character silently refused every invite. This clears it right after, on creation only,
    /// so existing characters and a player who ticks it later are untouched. Same shape as
    /// SideBySideDefault. Ours, not retail's: recorded in docs/DIVERGENCES.md.
    /// </summary>
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
