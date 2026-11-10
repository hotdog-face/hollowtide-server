using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RevivalGuard
{
    /// <summary>
    /// EVERY NEW CHARACTER STARTS IN HOLTBURG (owner 2026-11-09: "only one starter town is available. holtburg only,
    /// remove the other options"; Holtburg is the hub). The byte work of <see cref="HoltburgOnly"/>, pure C# (no ACE
    /// types) so NetTest runs it (tools/nettest/HoltburgOnlyCase.cs).
    ///
    /// CharacterCreateInfo.Unpack ends with three u32: StartArea, IsAdmin, IsSentinel. StartArea's low six bits are the
    /// index into the DAT's StarterAreas (Holtburg, Shoushi, Yaraq, Sanamar) or, when starter_towns_override is set, into
    /// that list; 0x80 (skip the Training Academy, allow_town_spawn) and 0x40 (Samurai loadout) are create-time flags our
    /// ACE strips in Unpack. Only the index is rewritten; the flags stay the player's.
    /// </summary>
    static class HoltburgOnlyRule
    {
        public const string TOWN = "Holtburg";
        /// <summary>The index bits of StartArea (our ACE's own mask in CharacterCreateInfo.Unpack).</summary>
        public const uint AREA_MASK = 0x3Fu;
        /// <summary>StartArea, IsAdmin, IsSentinel: the last three u32 of the create.</summary>
        public const int TAIL = 12;

        /// <summary>Holtburg's StartArea index: in the shard's starter_towns_override list first (index N means its Nth
        /// entry), else among the DAT's starter area names; -1 when neither names Holtburg (then nothing is rewritten).</summary>
        public static int HomeIndex(string overrideList, IList<string> datNames)
        {
            if (!string.IsNullOrWhiteSpace(overrideList))
            {
                var towns = overrideList.Split(',');
                for (int i = 0; i < towns.Length; i++)
                    if (towns[i].Trim().Equals(TOWN, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;   // the override decides what every index means: Holtburg absent from it is unreachable
            }
            if (datNames != null)
                for (int i = 0; i < datNames.Count; i++)
                    if (string.Equals(datNames[i], TOWN, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Set the StartArea index of the create whose last byte sits at <paramref name="end"/> to
        /// <paramref name="home"/>, keeping the flag bits. The stream's position is left where it was. True when a byte
        /// changed; <paramref name="before"/> and <paramref name="after"/> are the whole u32.</summary>
        public static bool Rewrite(Stream s, long end, int home, out uint before, out uint after)
        {
            before = after = 0;
            long at = end - TAIL;
            if (s == null || !s.CanSeek || !s.CanRead || !s.CanWrite || at < 0 || end > s.Length || home < 0 || home > AREA_MASK)
                return false;
            long pos = s.Position;
            try
            {
                var b = new byte[4];
                s.Position = at;
                if (s.Read(b, 0, 4) != 4) return false;
                before = BinaryPrimitives.ReadUInt32LittleEndian(b);
                after = (before & ~AREA_MASK) | (uint)home;
                if (after == before) return false;
                BinaryPrimitives.WriteUInt32LittleEndian(b, after);
                s.Position = at;
                s.Write(b, 0, 4);
                return true;
            }
            finally { s.Position = pos; }
        }
    }
}
