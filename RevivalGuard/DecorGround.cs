using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace RevivalGuard
{
    /// <summary>Ground painting.</summary>
    internal static partial class Decor
    {
        static void Ground(string[] a, Action<string> reply)
        {
            string blk = null;
            int points = 0;
            for (int i = 1; i < a.Length; i++)
            {
                string t = a[i];
                int eq = t.IndexOf('=');
                if (eq < 0)
                {
                    if (t.Length != 4 || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) { reply("ground: bad block " + t); return; }
                    blk = t.ToUpperInvariant();
                    continue;
                }
                if (blk == null) { reply("ground: a point before its block"); return; }
                var vals = t.Substring(eq + 1).Split(':');
                if (!int.TryParse(t.Substring(0, eq), out int v) || v < 0 || v > 80 || vals.Length != 2
                    || !ushort.TryParse(vals[0], out ushort now) || !ushort.TryParse(vals[1], out ushort was)) { reply("ground: bad point " + t); return; }
                string key = "g:" + blk;
                if (Locked(key, reply)) return;
                var e = Entry(key, () => new JsonObject { ["kind"] = "ground", ["block"] = blk, ["after"] = new JsonObject(), ["before"] = new JsonObject() });
                var after = (JsonObject)e["after"]!;
                var before = (JsonObject)e["before"]!;
                string vk = v.ToString(CultureInfo.InvariantCulture);
                if (before[vk] == null) before[vk] = (int)was;
                ushort retail = (ushort)(int)before[vk]!;
                if (now == retail) { after.Remove(vk); before.Remove(vk); }
                else after[vk] = (int)now;
                if (after.Count == 0) Del(key);
                points++;
            }
            if (points == 0) reply("ground: no points");
        }

        /// <summary>BlockOfEdit for a ground entry: its key names the block.</summary>
        static uint GroundBlock(string key) => key.StartsWith("g:", StringComparison.Ordinal) ? uint.Parse(key.Substring(2), NumberStyles.HexNumber) : 0;
    }
}
