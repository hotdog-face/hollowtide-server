using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace RevivalGuard
{
    /// <summary>
    /// GROUND PAINTING (owner F7 261126-0004: "a ground type editor so i can make paths and set the ground to look like dirt or
    /// stone or grass"; docs/proposals/world-decorator.md "Ground painting"). A landblock's ground is 81 terrain words (9x9
    /// points; bits 0-1 road, 2-6 type, 11-15 scenery) in its 0xXXYYFFFF cell record. The mod only KEEPS the painted words, as
    /// it keeps map pieces: one entry per landblock, key g:&lt;blk&gt;, kind "ground", after/before = {point: word}. The client
    /// draws them through its cell overlay; tools/decor/decor.py writes them into the published overlay's landblock records.
    /// Undo, redo, Clear area and locks go through the generic entry path (Reconcile's default branch).
    ///   ground &lt;blk&gt; &lt;i&gt;=&lt;new&gt;:&lt;old&gt; ... [&lt;blk&gt; ...]   point i (0-80) of block blk; new == old takes the point back
    /// </summary>
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
                if (before[vk] == null) before[vk] = (int)was;   // ints: a JsonValue<ushort> will not read back as int   // the first-seen word stays the point's retail word
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
