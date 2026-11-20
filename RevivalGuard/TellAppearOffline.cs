using System;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.Network.GameEvent.Events;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A tell by name to a character who appears offline is answered as if they were offline
    /// (TellAppearOfflineRule). Runs last, after the Town Crier's and the Dereth Network's prefixes, and only when none of
    /// them took the tell; the payload is put back for ACE. Our client names the person in chat ("X is offline now.",
    /// BRIDGE-NOTES note 164) from ACE's own 0x052B, so offline and hidden read the same.</summary>
    [HarmonyPatch(typeof(GameActionTell), nameof(GameActionTell.Handle))]
    static class TellAppearOffline
    {
        internal static readonly TellAppearOfflineRule Rule = new TellAppearOfflineRule();

        [HarmonyPriority(Priority.Last)]
        static bool Prefix(ClientMessage clientMessage, Session session, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (clientMessage?.Payload == null || session?.Player == null) return true;
            long pos = clientMessage.Payload.BaseStream.Position;
            try
            {
                clientMessage.Payload.ReadString16L();
                string name = clientMessage.Payload.ReadString16L().Trim();
                clientMessage.Payload.BaseStream.Position = pos;
                var p = session.Player;
                if (p.IsGagged) return true;
                var t = PlayerManager.GetOnlinePlayer(name);
                if (t == null) return true;   // ACE's own answer
                var now = DateTime.UtcNow;
                if (Rule.Hide(p.Guid.Full, t.Guid.Full, t.GetAppearOffline(), session.AccessLevel >= AccessLevel.Sentinel, now))
                {
                    session.Network.EnqueueSend(new GameEventWeenieError(session, WeenieError.CharacterNotAvailable));
                    return false;
                }
                Rule.Told(p.Guid.Full, t.Guid.Full, now);
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] TellAppearOffline: {e.Message}");
                try { clientMessage.Payload.BaseStream.Position = pos; } catch { }
                return true;
            }
        }
    }
}
