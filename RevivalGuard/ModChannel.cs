using System.Runtime.CompilerServices;
using System.Text;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;

namespace RevivalGuard
{
    /// <summary>
    /// ONE STRUCTURED CHANNEL FROM THIS MOD TO OUR CLIENT (docs/MODERNIZATION-IDEAS-2026-09-25.md,
    /// "Data between the server mod and the client travels as tagged chat lines"). Ours.
    ///
    /// Until now each feature that needed data on the client (the journal's @questlog) answered in
    /// chat lines starting with a control character the client hid. Chat is throttled, logged, and
    /// capped in length; every new feature (the looking-for-group board next) would have added
    /// another tag. This is a game message of its own, opcode 0x52474D31 ("RGM1", far outside
    /// retail's range), carrying a channel name and a UTF-8 payload of any length (ACE fragments
    /// it like any other message):
    ///
    ///     u32 opcode | u16 channel length | channel (ASCII) | u32 payload length | payload (UTF-8)
    ///
    /// A retail client must never receive it, so it is sent only to a CONNECTION that asked for it:
    /// our client sends "@revivalclient 1" each time it enters the world, and the answer is a
    /// "hello" on the channel. The mark lives on the Session object (a new login is a new Session),
    /// so a character that later logs in with a retail client is not sent anything it cannot read.
    /// </summary>
    public static class ModChannel
    {
        public const uint OPCODE = 0x52474D31;
        public const int VERSION = 1;

        sealed class Mark { public int Version; }
        static readonly ConditionalWeakTable<Session, Mark> s_Capable = new ConditionalWeakTable<Session, Mark>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Hello, "revivalclient", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client announces that it reads the mod channel.", "<version>");
            // Features that exist only on this channel register with it. Both are off by default
            // (rg_expansion_motions, rg_nether_rift): docs/NEW-ANIMATIONS.md.
            ExpansionMotions.Register();
            NetherRift.Register();
        }

        static void Hello(Session session, params string[] parameters)
        {
            if (session?.Player == null) return;
            int v = 1;
            if (parameters != null && parameters.Length > 0) int.TryParse(parameters[0], out v);
            s_Capable.Remove(session);
            s_Capable.Add(session, new Mark { Version = v });
            Send(session, "hello", "{\"v\":" + VERSION + "}");
            ItemLock.Push(session);   // the client's lock marks come from the items themselves
            FriendsAtSelect.NoteCapable(session);   // this account may be sent the select-screen friends
            RagdollCorpse.Hello(session);           // this shard places ragdoll corpses for opted-in clients
            WorldBossSchedule.Hello(session);       // the world bosses' schedule and state, for the map
        }

        /// <summary>Does this connection read the channel?</summary>
        public static bool Capable(Session session) => session != null && s_Capable.TryGetValue(session, out _);

        public static void Send(Session session, string channel, string payload)
        {
            if (session?.Network == null) return;
            session.Network.EnqueueSend(new Message(channel, payload));
        }

        sealed class Message : GameMessage
        {
            public Message(string channel, string payload)
                : base((GameMessageOpcode)OPCODE, GameMessageGroup.UIQueue)
            {
                var c = Encoding.ASCII.GetBytes(channel ?? "");
                Writer.Write((ushort)c.Length);
                Writer.Write(c);
                var b = Encoding.UTF8.GetBytes(payload ?? "");
                Writer.Write((uint)b.Length);
                Writer.Write(b);
            }
        }
    }
}
