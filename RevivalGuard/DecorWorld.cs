using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>The decorator indoors and on new ground.</summary>
    internal static partial class Decor
    {
        // ---- indoor poses ----

        static bool IsRoomCell(uint cell) => (cell & 0xFFFF) >= 0x100 && (cell & 0xFFFF) <= 0xFFFD;

        /// <summary>True (and the reason said) when a room id names no room of its landblock.</summary>
        static bool WhyNotRoom(uint cell, Action<string> reply)
        {
            if (!IsRoomCell(cell)) { reply?.Invoke($"{cell:X8} is not a room."); return true; }
            LandblockInfo info = null;
            try { info = DatManager.CellDat.ReadFromDat<LandblockInfo>((cell & 0xFFFF0000u) | 0xFFFE); } catch { }
            if (info == null || (cell & 0xFFFF) - 0x100 >= info.NumCells) { reply?.Invoke($"Room {cell:X8} does not exist here."); return true; }
            return false;
        }

        /// <summary>True when a pose cannot stand where it says: an indoor pose's room must exist and hold the point.</summary>
        static bool WhyNotCell(ACE.Entity.Position p, Action<string> reply)
        {
            uint cell = p.Cell;
            if (!IsRoomCell(cell)) return false;
            if (WhyNotRoom(cell, reply)) return true;
            ACE.Server.Physics.Common.EnvCell room = null;
            try { room = ACE.Server.Physics.Common.LScape.get_landcell(cell) as ACE.Server.Physics.Common.EnvCell; } catch { }
            if (room == null) return false;   // not loadable here: the placement itself says no if it must
            var pt = new System.Numerics.Vector3(p.PositionX, p.PositionY, p.PositionZ + 0.1f);
            if (room.find_visible_child_cell(pt, true) != null) return false;
            reply?.Invoke("That spot is not inside the rooms here (in a wall or the rock).");
            return true;
        }

        static string CleanName(string s)
        {
            var t = new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '\'' || c == '-' || c == ',' || c == '(' || c == ')').ToArray()).Trim();
            return t.Length > 48 ? t.Substring(0, 48) : t;
        }

        // ---- verbs ----

        static bool WorldVerb(Session s, Player me, string verb, string[] a, Action<string> reply)
        {
            switch (verb)
            {
                case "newisland": NewIsland(s, me, a.Length > 1 ? a[1].ToLowerInvariant() : "flat", reply); return true;
                case "newdungeon":
                    if (a.Length < 2) { reply("Which retail dungeon? (its landblock)"); return true; }
                    NewDungeon(s, me, Hex(a[1]) & 0xFFFF, a.Length > 2 ? string.Join(" ", a.Skip(2)) : null, reply);
                    return true;
                case "worldbin": SendWorld(s, a.Length > 1 ? a[1] : null, false); return true;
                case "dropworld": DropWorld(s, me, a.Length > 1 ? a[1] : null, reply); return true;
                default: return false;
            }
        }

        // ---- new ground ----

        const int ISLAND_BLOCKS = 2;   // 2 x 2 landblocks: a 384 m square of sea, the island about 280 m across
        const float SEA = -5f;

        static string WorldBin(string id) => Path.Combine(ModFolder.Sub("cell-overlay") ?? Dir, $"area-{id}.bin");

        /// <summary>Every landblock some overlay file (the islands, other new areas) already writes.</summary>
        static HashSet<uint> OverlayBlocks()
        {
            var held = new HashSet<uint>();
            var dir = ModFolder.Sub("cell-overlay");
            if (dir == null) return held;
            foreach (var f in Directory.GetFiles(dir, "*.bin"))
                try
                {
                    using var r = new BinaryReader(File.OpenRead(f));
                    if (r.ReadUInt32() != 0x4C455648) continue;
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++) { uint id = r.ReadUInt32(); held.Add(id >> 16); r.BaseStream.Position += r.ReadInt32(); }
                }
                catch { }
            return held;
        }

        /// <summary>Open sea with nothing on it: every vertex water, no rooms, objects or buildings, no overlay record.</summary>
        static bool EmptySea(uint blk, HashSet<uint> held)
        {
            if (held.Contains(blk)) return false;
            var raw = DatManager.CellDat.GetReaderForFile((blk << 16) | 0xFFFF)?.Buffer;
            if (raw == null || raw.Length < 8 + 81 * 2) return false;
            for (int i = 0; i < 81; i++)
            {
                int type = (BitConverter.ToUInt16(raw, 8 + i * 2) >> 2) & 0x1F;
                if (type < 16 || type > 20) return false;
            }
            var info = DatManager.CellDat.GetReaderForFile((blk << 16) | 0xFFFE)?.Buffer;
            if (info != null && info.Length >= 16)
            {
                uint cells = BitConverter.ToUInt32(info, 4), objs = BitConverter.ToUInt32(info, 8);
                if (cells != 0 || objs != 0 || BitConverter.ToUInt16(info, 12 + 32 * (int)objs) != 0) return false;
            }
            return true;
        }

        /// <summary>The first n x n patch of empty sea, with a ring of empty sea round it, in the far east ocean</summary>
        static uint FreePatch(int n)
        {
            var held = OverlayBlocks();
            foreach (var ar in s_Areas.Values)
                if (ar.Layer["world"]?["blocks"] is JsonArray bl) foreach (var b in bl) held.Add(Hex((string)b!));
            for (int x = 0xE0; x + n < 0xFF; x += n + 1)
                for (int y = 0x10; y + n < 0xF0; y += n + 1)
                {
                    if (x + n >= 0xEE && y + n >= 0x99 && y - 1 <= 0xAE) continue;
                    bool ok = true;
                    for (int i = -1; i <= n && ok; i++)
                        for (int j = -1; j <= n && ok; j++)
                            ok = EmptySea((uint)(((x + i) << 8) | (y + j)), held);
                    if (ok) return (uint)((x << 8) | y);
                }
            return 0xFFFF;
        }

        /// <summary>The island's ground height in metres at patch metres. flat</summary>
        static float IslandHeight(string look, float X, float Y, int n)
        {
            float size = n * 192f, c = size / 2, R = size * 0.36f, dx = X - c, dy = Y - c;
            float r = MathF.Sqrt(dx * dx + dy * dy) / R;
            float h;
            if (look == "hills")
            {
                float hills = 6f * MathF.Sin(X / 97f + 0.7f) * MathF.Cos(Y / 83f - 0.4f) + 4f * MathF.Sin((X + Y) / 61f);
                h = 18f * (1 - r * r) + hills * 0.5f;
            }
            else h = r < 0.72f ? 8f : 8f * Math.Clamp((1f - r) / 0.28f, 0f, 1f);
            if (r >= 1f) h = MathF.Min(h, 0);
            return h;
        }

        static void NewIsland(Session s, Player me, string look, Action<string> reply)
        {
            if (look != "flat" && look != "hills") look = "flat";
            int n = ISLAND_BLOCKS;
            uint at = FreePatch(n);
            if (at == 0xFFFF) { reply("No free open sea left for a new island."); return; }
            int x0 = (int)(at >> 8), y0 = (int)(at & 0xFF);
            var heights = DatManager.PortalDat.RegionDesc.LandDefs.LandHeightTable;
            const int GRASS = 1, LUSH = 3, SAND = 10, ROCK = 14, FOREST = 21, SHALLOW = 18, DEEP = 20;
            int g = n * 8 + 1;
            var hIdx = new int[g, g]; var type = new int[g, g];
            float size = n * 192f, c = size / 2, R = size * 0.36f;
            for (int i = 0; i < g; i++)
                for (int j = 0; j < g; j++)
                {
                    float X = i * 24f, Y = j * 24f;
                    float h = IslandHeight(look, X, Y, n);
                    if (i == 0 || j == 0 || i == g - 1 || j == g - 1) h = SEA;   // the patch meets the open sea
                    int k = Math.Min((int)MathF.Round(MathF.Max(0, h) / 2f), 120);
                    hIdx[i, j] = k;
                    float r = MathF.Sqrt((X - c) * (X - c) + (Y - c) * (Y - c)) / R;
                    if (h <= 0) type[i, j] = r < 1.15f ? SHALLOW : DEEP;
                    else if (k <= 1) type[i, j] = SAND;
                    else if (k >= 20) type[i, j] = ROCK;
                    else if (look == "flat") type[i, j] = GRASS;
                    else type[i, j] = MathF.Sin(X / 53f) * MathF.Sin(Y / 47f + 1.3f) > 0.45f ? FOREST : (MathF.Sin(X / 31f + Y / 37f) > 0 ? LUSH : GRASS);
                }
            var recs = new List<(uint id, byte[] b)>();
            for (int bi = 0; bi < n; bi++)
                for (int bj = 0; bj < n; bj++)
                {
                    uint blk = (uint)((x0 + bi) << 24 | (y0 + bj) << 16);
                    var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                    w.Write(blk | 0xFFFF); w.Write(1u);   // HasObjects: the (empty) object list below is read, so map pieces publish here
                    for (int vx = 0; vx < 9; vx++)
                        for (int vy = 0; vy < 9; vy++)
                        {
                            int i = bi * 8 + vx, j = bj * 8 + vy;
                            // flat: scenery 0 everywhere, the plainest ground for building (?: what retail grows there); hills: varied
                            int scen = look == "flat" ? 0 : ((i * 7 + j * 13) % 6) + 2;
                            w.Write((ushort)((type[i, j] << 2) | (scen << 11)));
                        }
                    for (int vx = 0; vx < 9; vx++) for (int vy = 0; vy < 9; vy++) w.Write((byte)hIdx[bi * 8 + vx, bj * 8 + vy]);
                    recs.Add((blk | 0xFFFF, ms.ToArray()));
                    recs.Add((blk | 0xFFFE, EmptyInfo(blk | 0xFFFE, 0)));
                }
            // the spot: the patch's middle, on the ground
            uint homeBlk = (uint)(((x0 + n / 2) << 8) | (y0 + n / 2));
            float hx = 4f, hy = 4f;
            float ground = heights[hIdx[n * 4, n * 4]];
            var home = new ACE.Entity.Position((homeBlk << 16) | (uint)((int)(hx / 24f) * 8 + (int)(hy / 24f) + 1), hx, hy, ground + 0.5f, 0f, 0f, 0f, 1f);
            var blocks = Enumerable.Range(0, n * n).Select(k => $"{(x0 + k / n):X2}{(y0 + k % n):X2}").ToList();
            MakeWorldArea(s, me, "island", look == "flat" ? "New island (flat)" : "New island (hills)", blocks, recs, home, null, reply);
        }

        static byte[] EmptyInfo(uint id, uint cells)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(id); w.Write(cells); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            return ms.ToArray();
        }

        /// <summary>A retail dungeon's rooms copied to a free sea landblock with the same room numbers, every object stripped</summary>
        static void NewDungeon(Session s, Player me, uint src, string name, Action<string> reply)
        {
            LandblockInfo info = null;
            try { info = DatManager.CellDat.ReadFromDat<LandblockInfo>((src << 16) | 0xFFFE); } catch { }
            if (info == null || info.NumCells == 0) { reply($"Landblock {src:X4} has no rooms to copy."); return; }
            if (info.NumCells > 2000) { reply("That dungeon is too big to copy."); return; }
            uint dst = FreePatch(1);
            if (dst == 0xFFFF) { reply("No free landblock left for a new dungeon."); return; }
            var recs = new List<(uint id, byte[] b)>();
            for (uint i = 0; i < info.NumCells; i++)
            {
                var raw = DatManager.CellDat.GetReaderForFile((src << 16) | (0x100 + i))?.Buffer;
                if (raw == null) { reply($"Room {src:X4}{0x100 + i:X4} unreadable: nothing made."); return; }
                recs.Add(((dst << 16) | (0x100 + i), StripRoom(raw, (dst << 16) | (0x100 + i))));
            }
            recs.Add(((dst << 16) | 0xFFFE, EmptyInfo((dst << 16) | 0xFFFE, info.NumCells)));
            // the sea block's own terrain, saying it has a block list now (or neither ACE nor the client reads the rooms)
            var land = (byte[])DatManager.CellDat.GetReaderForFile((dst << 16) | 0xFFFF).Buffer.Clone();
            BitConverter.GetBytes(1u).CopyTo(land, 4);
            recs.Add(((dst << 16) | 0xFFFF, land.Take(8 + 81 * 3).ToArray()));
            string title = string.IsNullOrWhiteSpace(name) ? $"New dungeon (from {src:X4})" : $"New dungeon ({CleanName(name)})";
            MakeWorldArea(s, me, "dungeon", title, new List<string> { $"{dst:X4}" }, recs, null, src, reply);
        }

        /// <summary>An EnvCell record renumbered and stripped of its objects: the bytes up to its visible-cell list are the DAT's own.</summary>
        static byte[] StripRoom(byte[] raw, uint id)
        {
            uint flags = BitConverter.ToUInt32(raw, 4);
            int nSurf = raw[12], nPortal = raw[13], nStab = BitConverter.ToUInt16(raw, 14);
            int end = 16 + 2 * nSurf + 4 + 28 + 8 * nPortal + 2 * nStab;
            var o = new byte[end];
            Array.Copy(raw, o, end);
            BitConverter.GetBytes(id).CopyTo(o, 0);
            BitConverter.GetBytes(flags & ~(2u | 8u)).CopyTo(o, 4);
            BitConverter.GetBytes(id).CopyTo(o, 8);
            return o;
        }

        /// <summary>The records into ACE's DAT cache and the area's overlay file, a new area round them, and the bytes to the client.</summary>
        static void MakeWorldArea(Session s, Player me, string kind, string name, List<string> blocks, List<(uint id, byte[] b)> recs,
                                  ACE.Entity.Position home, uint? src, Action<string> reply)
        {
            int n = 1;
            string id;
            while (s_Areas.ContainsKey(id = $"{kind}-{n}") || File.Exists(WorldBin($"{kind}-{n}"))) n++;
            using (var f = File.Create(WorldBin(id)))
            {
                var w = new BinaryWriter(f);
                w.Write(0x4C455648u); w.Write(recs.Count);
                foreach (var (rid, b) in recs) { w.Write(rid); w.Write(b.Length); w.Write(b); }
            }
            Install(recs);
            if (home == null)
            {
                home = DungeonSpot((Hex(blocks[0]) << 16), recs.Count(r => IsRoomCell(r.id)));
                if (home == null) { File.Delete(WorldBin(id)); Uninstall(recs.Select(r => r.id)); reply("The copied rooms have no floor to stand on: nothing made."); return; }
            }
            var world = new JsonObject
            {
                ["kind"] = kind, ["blocks"] = new JsonArray(blocks.Select(b => (JsonNode)b).ToArray()), ["bin"] = $"area-{id}.bin",
                ["records"] = recs.Count, ["bytes"] = recs.Sum(r => r.b.Length),
            };
            if (src.HasValue) world["from"] = $"{src.Value:X4}";
            s_Areas[id] = new Area
            {
                Id = id,
                Layer = new JsonObject
                {
                    ["area"] = id, ["name"] = n > 1 ? $"{name} {n}" : name, ["blocks"] = new JsonArray(blocks.Select(b => (JsonNode)b).ToArray()),
                    ["home"] = PoseOf(home), ["next"] = 1, ["edits"] = new JsonObject(), ["locks"] = new JsonObject(), ["world"] = world,
                },
            };
            s_Active[me.Guid.Full] = id;
            Save();
            Mod.Log.Info($"[RevivalGuard] Decor: new {kind} {id} on {string.Join(",", blocks)} ({recs.Count} records)");
            reply($"{s_Areas[id].Layer["name"]}: made on open sea. Your changes go in it now; Go there takes you in.");
            SendState(s, me);
            SendWorld(s, id, true);
        }

        /// <summary>A floor in the copied rooms: the first room (in number order) whose middle, a hand above its origin, is inside it.</summary>
        static ACE.Entity.Position DungeonSpot(uint blk, int rooms)
        {
            for (uint i = 0; i < rooms; i++)
            {
                uint cell = blk | (0x100 + i);
                if (!(ACE.Server.Physics.Common.LScape.get_landcell(cell) is ACE.Server.Physics.Common.EnvCell ec)) continue;
                var o = ec.Pos.Frame.Origin;
                if (ec.point_in_cell(o + new System.Numerics.Vector3(0f, 0f, 0.5f)))
                    return new ACE.Entity.Position(cell, o.X, o.Y, o.Z + 0.005f, 0f, 0f, 0f, 1f);
            }
            return null;
        }

        static void Install(IEnumerable<(uint id, byte[] b)> recs)
        {
            foreach (var (id, bytes) in recs)
            {
                FileType obj = (id & 0xFFFF) == 0xFFFF ? new CellLandblock() : (id & 0xFFFF) == 0xFFFE ? new LandblockInfo() : new ACE.DatLoader.FileTypes.EnvCell();
                using (var br = new BinaryReader(new MemoryStream(bytes))) obj.Unpack(br);
                DatManager.CellDat.FileCache[id] = obj;
            }
        }

        static void Uninstall(IEnumerable<uint> ids) { foreach (var id in ids) DatManager.CellDat.FileCache.TryRemove(id, out _); }

        static List<(uint id, byte[] b)> ReadBin(string path)
        {
            var o = new List<(uint, byte[])>();
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != 0x4C455648) return o;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) { uint id = r.ReadUInt32(); o.Add((id, r.ReadBytes(r.ReadInt32()))); }
            return o;
        }

        const int WORLD_CHUNK = 12000;   // base64 characters a message

        /// <summary>A new area's overlay bytes to the client, in parts. go = it was just made</summary>
        static void SendWorld(Session s, string id, bool go)
        {
            if (id == null || !s_Areas.TryGetValue(id, out var ar) || !(ar.Layer["world"] is JsonObject world)) return;
            var path = WorldBin(id);
            if (!File.Exists(path)) return;
            string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
            int n = (b64.Length + WORLD_CHUNK - 1) / WORLD_CHUNK;
            for (int i = 0; i < n; i++)
                Send(s, new JsonObject
                {
                    ["k"] = "world", ["id"] = id, ["kind"] = world["kind"]?.DeepClone(), ["blocks"] = world["blocks"]?.DeepClone(),
                    ["i"] = i, ["n"] = n, ["go"] = go, ["b64"] = b64.Substring(i * WORLD_CHUNK, Math.Min(WORLD_CHUNK, b64.Length - i * WORLD_CHUNK)),
                });
        }

        /// <summary>dropworld &lt;id&gt;: a new area taken away whole: its overlay file, its layer and undo, its ways.</summary>
        static void DropWorld(Session s, Player me, string id, Action<string> reply)
        {
            if (id == null || !s_Areas.TryGetValue(id, out var ar) || !(ar.Layer["world"] is JsonObject)) { reply("That is not a new island or dungeon."); return; }
            foreach (var kv in ar.Edits.ToList())
                if (kv.Value is JsonObject e)
                {
                    if ((string)e["kind"] == "way") ReconcileWay(kv.Key, e, null, ar);
                    else if ((string)e["kind"] == "add" && e["guid"] != null) { uint g = Hex((string)e["guid"]!); if (s_Added.ContainsKey(g)) Find(me, g)?.Destroy(); s_Added.Remove(g); }
                }
            var path = WorldBin(id);
            if (File.Exists(path)) { Uninstall(ReadBin(path).Select(r => r.id)); File.Delete(path); }
            if (File.Exists(ar.LayerPath)) File.Delete(ar.LayerPath);
            if (File.Exists(ar.UndoPath)) File.Delete(ar.UndoPath);
            s_Areas.Remove(id);
            foreach (var k in s_Active.Where(x => x.Value == id).Select(x => x.Key).ToList()) s_Active.Remove(k);
            reply($"{(string)ar.Layer["name"] ?? id} taken away. A restart clears it from the server's memory.");
            Mod.Log.Info($"[RevivalGuard] Decor: dropped {id}");
            SendState(s, me);
        }

        // ---- ways in and out: portals (Stuck copies of the Staff Base hub portal 902561, click to use) ----

        const uint WAY_WCID = 902561;

        /// <summary>way in|out: a portal at your feet.</summary>
        static void Way(Player me, string[] a, Action<string> reply)
        {
            string dir = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            if (dir != "in" && dir != "out") { reply("way in | way out"); return; }
            var ar = s_Cur;
            string wait = DecorAccessRule.WhyNotWay(Mode, ar.Layer["world"] != null);
            if (wait != null) { reply(wait); return; }
            if (!(ar.Layer["home"] is JsonObject home)) { reply("This area has no spot to lead to."); return; }
            JsonObject to;
            if (dir == "in") to = (JsonObject)home.DeepClone();
            else if (Get($"w:{ar.Id}:in") is JsonObject win && win["after"] is JsonObject inAt) to = (JsonObject)inAt.DeepClone();
            else { reply("Set the way in first: the way out leads back to it."); return; }
            var at = new ACE.Entity.Position(me.Location);
            string key = $"w:{ar.Id}:{dir}";
            var want = new JsonObject
            {
                ["kind"] = "way", ["dir"] = dir, ["after"] = PoseOf(at), ["to"] = to,
                ["name"] = dir == "in" ? $"Portal to {(string)ar.Layer["name"] ?? ar.Id}" : "Portal back",
            };
            ReconcileWay(key, Get(key), want, ar);
            reply(dir == "in" ? $"Way in set here: it leads to {(string)ar.Layer["name"] ?? ar.Id}." : "Way out set here: it leads back to the way in.");
        }

        static void ReconcileWay(string key, JsonObject cur, JsonObject want, Area into)
        {
            if (cur?["guid"] is JsonNode gn) { uint g = Hex((string)gn!); if (s_Added.ContainsKey(g)) Find(null, g)?.Destroy(); s_Added.Remove(g); }
            Del(key);
            if (want == null) return;
            want = (JsonObject)want.DeepClone();
            want.Remove("guid");
            var wo = MakeWay(want);
            if (wo != null) { want["guid"] = $"0x{wo.Guid.Full:X8}"; s_Added[wo.Guid.Full] = key; }
            Put(key, want, into);
        }

        /// <summary>A way's portal into the world after a restart or a block load (once: one still standing is left alone).</summary>
        static bool ReapplyWay(string key, JsonObject e)
        {
            if (!(e["after"] is JsonObject after)) return false;
            if (e["guid"] is JsonNode gn && s_Added.ContainsKey(Hex((string)gn!)) && Find(null, Hex((string)gn!)) != null) return false;
            uint blk = Hex((string)after["cell"]!) >> 16;
            if (!ACE.Server.Managers.LandblockManager.GetLoadedLandblocks().Any(l => l.Id.Landblock == blk)) return false;
            var wo = MakeWay(e);
            if (wo == null) return false;
            if (e["guid"] is JsonNode old) s_Added.Remove(Hex((string)old!));
            e["guid"] = $"0x{wo.Guid.Full:X8}";
            s_Added[wo.Guid.Full] = key;
            return true;
        }

        static WorldObject MakeWay(JsonObject e)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(WAY_WCID);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] Decor: no portal weenie {WAY_WCID} for a way"); return null; }
            wo.Name = (string)e["name"] ?? "Portal";
            wo.SetPosition(PositionType.Destination, PosFrom((JsonObject)e["to"]!));
            wo.TimeToRot = -1;
            wo.Location = PosFrom((JsonObject)e["after"]!);
            wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
            if (!wo.EnterWorld()) { wo.Destroy(); return null; }
            return wo;
        }
    }
}
