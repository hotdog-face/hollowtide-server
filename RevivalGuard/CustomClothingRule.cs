using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RevivalGuard
{
    /// <summary>The pure part of CustomClothing.cs (no ACE types: tools/nettest compiles it by path, CustomClothingCase.cs).</summary>
    public static class CustomClothingRule
    {
        public const uint LO = 0x1000F000, HI = 0x1000FFFF;

        public static bool InRange(uint id) => id >= LO && id <= HI;

        public static uint Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetUInt32() : Num(e.GetString());

        public static uint Num(string text)
        {
            var s = (text ?? "").Trim();
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : uint.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        /// <summary>The table's id and its DAT bytes; throws (with the reason) on a bad file or an id outside LO..HI.</summary>
        public static (uint id, byte[] blob, int setups, int templates) Build(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            uint id = Num(root.GetProperty("Id"));
            if (!InRange(id)) throw new InvalidDataException($"id 0x{id:X8} is outside 0x{LO:X8}-0x{HI:X8}");
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(id);
            int setups = 0, templates = 0;
            // PackedHashTable: ushort count, ushort bucket size (ACE reads and ignores the second)
            var cbe = root.TryGetProperty("ClothingBaseEffects", out var c) ? c : default;
            var keys = new List<JsonProperty>();
            if (cbe.ValueKind == JsonValueKind.Object) keys.AddRange(cbe.EnumerateObject());
            w.Write((ushort)keys.Count); w.Write((ushort)keys.Count);
            foreach (var s in keys)
            {
                w.Write(Num(s.Name));
                var fx = s.Value.GetProperty("CloObjectEffects");
                w.Write((uint)fx.GetArrayLength());
                foreach (var o in fx.EnumerateArray())
                {
                    w.Write(Num(o.GetProperty("Index")));
                    w.Write(Num(o.GetProperty("ModelId")));
                    var tx = o.TryGetProperty("CloTextureEffects", out var t) ? t : default;
                    int n = tx.ValueKind == JsonValueKind.Array ? tx.GetArrayLength() : 0;
                    w.Write((uint)n);
                    if (n > 0)
                        foreach (var x in tx.EnumerateArray()) { w.Write(Num(x.GetProperty("OldTexture"))); w.Write(Num(x.GetProperty("NewTexture"))); }
                }
                setups++;
            }
            var spe = root.TryGetProperty("ClothingSubPalEffects", out var sp) ? sp : default;
            var pk = new List<JsonProperty>();
            if (spe.ValueKind == JsonValueKind.Object) pk.AddRange(spe.EnumerateObject());
            w.Write((ushort)pk.Count); w.Write((ushort)pk.Count);
            foreach (var k in pk)
            {
                w.Write(Num(k.Name));
                w.Write(k.Value.TryGetProperty("Icon", out var icon) ? Num(icon) : 0u);
                var subs = k.Value.GetProperty("CloSubPalettes");
                w.Write((uint)subs.GetArrayLength());
                foreach (var cs in subs.EnumerateArray())
                {
                    var rg = cs.GetProperty("Ranges");
                    w.Write((uint)rg.GetArrayLength());
                    foreach (var r in rg.EnumerateArray()) { w.Write(Num(r.GetProperty("Offset"))); w.Write(Num(r.GetProperty("NumColors"))); }
                    w.Write(Num(cs.GetProperty("PaletteSet")));
                }
                templates++;
            }
            w.Flush();
            return (id, ms.ToArray(), setups, templates);
        }

        /// <summary>Reads the bytes back the way ACE's ClothingTable.Unpack does: {setup: []} and the palette templates.</summary>
        public static (uint id, Dictionary<uint, List<(uint part, uint model, List<(uint oldTex, uint newTex)> tex)>> parts, List<uint> templates) Read(byte[] blob)
        {
            var r = new BinaryReader(new MemoryStream(blob));
            uint id = r.ReadUInt32();
            var parts = new Dictionary<uint, List<(uint, uint, List<(uint, uint)>)>>();
            int n = r.ReadUInt16(); r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                uint setup = r.ReadUInt32();
                var fx = new List<(uint, uint, List<(uint, uint)>)>();
                uint m = r.ReadUInt32();
                for (int j = 0; j < m; j++)
                {
                    uint idx = r.ReadUInt32(), model = r.ReadUInt32(), t = r.ReadUInt32();
                    var tx = new List<(uint, uint)>();
                    for (int k = 0; k < t; k++) tx.Add((r.ReadUInt32(), r.ReadUInt32()));
                    fx.Add((idx, model, tx));
                }
                parts[setup] = fx;
            }
            var templates = new List<uint>();
            n = r.ReadUInt16(); r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                templates.Add(r.ReadUInt32());
                r.ReadUInt32();   // icon
                uint subs = r.ReadUInt32();
                for (int j = 0; j < subs; j++)
                {
                    uint ranges = r.ReadUInt32();
                    for (int k = 0; k < ranges; k++) { r.ReadUInt32(); r.ReadUInt32(); }
                    r.ReadUInt32();   // palette set
                }
            }
            if (r.BaseStream.Position != blob.Length) throw new InvalidDataException($"{blob.Length - r.BaseStream.Position} bytes left over");
            return (id, parts, templates);
        }
    }
}
