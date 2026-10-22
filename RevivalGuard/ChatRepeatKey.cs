using System.IO;
using System.Text;

namespace RevivalGuard
{
    /// <summary>
    /// ChatGuard's repeat-rule key, pure C# (no ACE types) so NetTest runs it (tools/nettest/ChatRepeatCase.cs).
    /// ChatChannel 0x0147 is [u32 channel][string16L text]. The guard used to read a string16L first, which took
    /// the channel's low 16 bits as the length: co-vassals (0x01000000) and allegiance broadcast (0x02000000) read
    /// "" for EVERY line, so the third line in 30 s was refused as "already said that" (report 261022-0020).
    /// The channel is part of the key: the same line on two channels is not a repeat.
    /// </summary>
    static class ChatRepeatKey
    {
        public const uint Talk = 0x0015, Tell = 0x005D, Emote = 0x01DF, TalkDirect = 0x0032, ChatChannel = 0x0147;

        /// <summary>The text to compare, or null when the action carries none the rule can key on.</summary>
        public static string Of(uint opcode, BinaryReader r)
        {
            switch (opcode)
            {
                case TalkDirect: return null;
                case ChatChannel: { uint ch = r.ReadUInt32(); return "#" + ch.ToString("X") + ":" + String16L(r); }
                default: return String16L(r);
            }
        }

        /// <summary>ACE's String16L: u16 length, that many bytes. Only a comparison key, so Latin-1 is enough.</summary>
        static string String16L(BinaryReader r)
        {
            int n = r.ReadUInt16();
            if (n == 0) return "";
            var b = r.ReadBytes(n);
            if (b.Length < n) throw new EndOfStreamException();
            return Encoding.Latin1.GetString(b);
        }
    }
}
