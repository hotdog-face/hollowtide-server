using System.IO;
using System.Text;

namespace RevivalGuard
{
    /// <summary>ChatGuard's repeat-rule key, pure C# (no ACE types) so NetTest runs it (tools/nettest/ChatRepeatCase.cs).</summary>
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

        /// <summary>ACE's String16L: u16 length, that many bytes.</summary>
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
