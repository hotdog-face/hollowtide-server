using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// `@permit remove &lt;a name that is not online&gt;` SAYS NOTHING, AND THE SILENCE IS A THROW.
    ///
    /// Found by the client command sweep of 2026-09-21 (docs/CLIENT-COMMAND-SWEEP.md, open item
    /// 42): `@permit remove Sweepb Friend` with that character offline answered nothing, twice, a
    /// sweep apart, while the same command against an online name answered "...doesn't have
    /// permission to loot your corpse." and `@permit add Sweepb Friend` answered "Sweepb Friend is
    /// not online." The client's payload is right; the handler is what is wrong:
    ///
    ///     // Player_Death.cs:777
    ///     public void HandleActionRemovePlayerPermission(string playerName)
    ///     {
    ///         var player = PlayerManager.GetOnlinePlayer(playerName);
    ///         if (Name.Equals(player.Name))                         // :781 -- player is null here
    ///         ...
    ///         if (player == null || !player.HasLootPermission(Guid))
    ///             ...($"{player.Name} doesn't have permission...")  // :791 -- and null here too
    ///
    /// The self-revoke test dereferences the looked-up player three lines BEFORE the null check,
    /// and the null check's own branch dereferences it again for the message. An offline target
    /// throws a NullReferenceException inside the handler, the action queue swallows it, and the
    /// player who typed the command is answered with nothing. `HandleActionAddPlayerPermission`,
    /// forty lines up, tests null first and says "{playerName} is not online."; this is the
    /// same test with the same wording, in front of the handler that forgot it.
    ///
    /// A Prefix rather than a Finalizer, because the throw is avoidable: for an offline name there
    /// is nothing for the original to do, and skipping it (return false) costs no exception and
    /// leaves ACE's own path -- the self-revoke, the "doesn't have permission" and the real revoke
    /// -- to run untouched for every name that IS online.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionRemovePlayerPermission))]
    public static class PermitRemoveOffline
    {
        static bool Prefix(Player __instance, string playerName)
        {
            // Online: ACE's handler is correct from here on, including the self-revoke.
            if (PlayerManager.GetOnlinePlayer(playerName) != null) return true;

            __instance.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                $"{playerName} is not online.", ChatMessageType.Broadcast));
            return false;   // the original would have thrown on player.Name
        }
    }
}
