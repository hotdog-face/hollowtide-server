using System;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Handlers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Olthoi chargen, refused server side.</summary>
    [HarmonyPatch(typeof(CharacterHandler), "CharacterCreateEx")]
    static class OlthoiChargenGuard
    {
        static bool Prefix(ClientMessage message, Session session)
        {
            try
            {
                var stream = message.Payload.BaseStream;
                long pos = stream.Position;
                stream.Position += 4;                  // CharacterCreateInfo.Unpack's own leading skip
                uint heritage = message.Payload.ReadUInt32();
                stream.Position = pos;                 // put it back exactly where ACE's own Unpack expects it

                if (heritage == (uint)HeritageGroup.Olthoi || heritage == (uint)HeritageGroup.OlthoiAcid)
                {
                    session.Network.EnqueueSend(new GameMessageCharacterCreateResponse(
                        CharacterGenerationVerificationResponse.Pending, ObjectGuid.Invalid, string.Empty));
                    Mod.Log.Info($"[OlthoiChargenGuard] refused a {(HeritageGroup)heritage} character create for account '{session.Account}'");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[OlthoiChargenGuard] {e.Message}; letting ACE's own CharacterCreateEx decide");
                return true;
            }
        }
    }
}
