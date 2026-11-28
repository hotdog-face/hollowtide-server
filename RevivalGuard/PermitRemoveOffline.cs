using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>`@permit remove &lt;a name that is not online&gt;` SAYS nothing, and the silence is a throw.</summary>
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
