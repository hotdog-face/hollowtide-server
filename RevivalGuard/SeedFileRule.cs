using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace RevivalGuard
{
    /// <summary>A data file the mod seeds beside its dll from an embedded copy, then reads from disk (TownLife.json).</summary>
    public static class SeedFileRule
    {
        public enum Act
        {
            Write,          // no file: write the embedded copy
            UpToDate,       // the file is the embedded copy (refresh the sidecar if it says otherwise)
            Update,         // the file is an older copy we wrote, untouched since: replace it, keep the old as .before-<v>
            Keep,           // hand-edited, nothing newer embedded: leave it, quietly
            KeepEdited,     // hand-edited and a newer embedded copy exists: leave it, WARN once (force to take the new)
            ForceUpdate,    // staff asked for the embedded copy: replace it, keep the old as .before-<v>
        }

        /// <summary>Content hash (16 hex) of a seed file's text: BOM and CRs ignored, so a checkout's line endings do not read as an edit.</summary>
        public static string Hash(string text)
        {
            var t = (text ?? "").TrimStart('﻿').Replace("\r\n", "\n");
            using var sha = SHA256.Create();
            var b = sha.ComputeHash(Encoding.UTF8.GetBytes(t));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        public static Act Decide(string diskHash, string embeddedHash, string writtenHash, ICollection<string> pastHashes, bool force)
        {
            if (diskHash == null) return Act.Write;
            if (diskHash == embeddedHash) return Act.UpToDate;
            if (force) return Act.ForceUpdate;
            bool untouched = writtenHash != null ? diskHash == writtenHash : pastHashes != null && pastHashes.Contains(diskHash);
            if (untouched) return Act.Update;
            // edited by hand: only a warning when the dll brings something the file has not seen
            return writtenHash != null && writtenHash == embeddedHash ? Act.Keep : Act.KeepEdited;
        }

        /// <summary>Where the replaced file is kept: `TownLife.json.before-`.</summary>
        public static string BackupName(string file, string embeddedHash) => file + ".before-" + embeddedHash;

        public static bool Replaces(Act a) => a == Act.Write || a == Act.Update || a == Act.ForceUpdate;
    }
}
