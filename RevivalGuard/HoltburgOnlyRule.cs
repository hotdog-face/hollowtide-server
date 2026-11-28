using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RevivalGuard
{
    /// <summary>Every new character starts in holtburg.</summary>
    static class HoltburgOnlyRule
    {
        public const string TOWN = "Holtburg";
        /// <summary>The index bits of StartArea (our ACE's own mask in CharacterCreateInfo.Unpack).</summary>
        public const uint AREA_MASK = 0x3Fu;
        /// <summary>StartArea, IsAdmin, IsSentinel: the last three u32 of the create.</summary>
        public const int TAIL = 12;

        /// <summary>Holtburg's StartArea index</summary>
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

        /// <summary>Set the StartArea index of the create whose last byte sits at end to home, keeping the flag bits.</summary>
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
