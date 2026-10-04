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
    /// <summary>
    /// OLTHOI CHARGEN, REFUSED SERVER SIDE (owner, 2026-09-27: "remove the option to play and make
    /// olthoi characters all together in the character selection screen. its half baked and never
    /// worked very well"). The client (Assets/Streaming/AcCharGenHud.cs, AcCharGenData.cs) no longer
    /// offers the heritage at all -- the two radios are hidden and Random can no longer land on them
    /// -- but the client is in the attacker's hands, so the refusal belongs here too.
    ///
    /// ACE ALREADY HAS THIS GATE, BUT IT DEFAULTS OFF. `CharacterHandler.CharacterCreateEx` already
    /// refuses Olthoi/OlthoiAcid IF the `olthoi_play_disabled` shard property is true
    /// (`PropertyManager.cs:581`, default `false`: "if false, allows players to create and play as
    /// olthoi characters"), and this shard's `ace_shard.config_properties_boolean` has never set that
    /// row (checked 2026-09-27), so the property sits at its `false` default right now -- a modified
    /// client asking for `HeritageGroup.Olthoi` or `OlthoiAcid` today would get a character. This
    /// patch does not depend on that property (which any admin tool could flip back): it refuses both
    /// heritages unconditionally, with the same `CharacterGenerationVerificationResponse.Pending`
    /// ACE's own gate already sends for this exact case.
    ///
    /// PATCHING THE DECLARING TYPE: `CharacterCreateEx` is a `private static` method declared
    /// directly on the static class `CharacterHandler` -- not inherited from anywhere -- so there is
    /// no Chest/Container-style resolution trap (AGENTS.md, "Writing a RevivalGuard Harmony patch");
    /// `typeof(CharacterHandler)` is the only and correct target.
    ///
    /// READING THE HERITAGE WITHOUT BREAKING THE REAL PARSE: `message.Payload` is a `BinaryReader`
    /// over `message.Data`, a seekable `MemoryStream`, and `CharacterCreateInfo.Unpack` reads it
    /// FORWARD ONLY (ACE.Entity/CharacterCreateInfo.cs). Calling that Unpack here, to see the
    /// heritage before ACE's own copy runs, would leave the stream wherever creation info's read
    /// left off, corrupting ACE's later re-read when this prefix returns `true`. So this reads only
    /// the two fields `Unpack` reads first -- 4 bytes of "unknown constant", then Heritage as a
    /// uint -- and rewinds the stream's position back to where it started before returning, so the
    /// real `Unpack` (whether it runs, on a non-Olthoi heritage, or not) sees the message exactly as
    /// the client sent it.
    /// </summary>
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
