using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>The dressing room's lab drawer</summary>
    public static partial class DressingRoom
    {
        internal const string P_LAB = "rg_dress_lab";
        const uint PREVIEW_LO = 0x1000FE00, PREVIEW_HI = 0x1000FEFF;
        static readonly string[] LAB_FIELDS = { "textures", "bareParts", "spectral", "palettes", "palettesFrom", "effect", "effectEvery" };
        static readonly List<uint> s_PreviewIds = new List<uint>();
        static readonly object s_LabLock = new object();
        static readonly Dictionary<string, string[]> s_LabParts = new Dictionary<string, string[]>();

        internal static bool LabOn => PkCommon.On(P_LAB);

        static void RegisterLab() =>
            PkCommon.RegisterBool(P_LAB, false, "RevivalGuard DressingRoom: the staff Dressing Room's Lab drawer (materials, free colours, effects, Lab looks on a piece)");

        /// <summary>The piece's Lab extras, checked; null when it has none, they are bad, or the switch is off.</summary>
        static LookLabMake.Look LabOf(Piece x)
        {
            if (x == null || string.IsNullOrEmpty(x.Lab) || !LabOn) return null;
            try
            {
                var l = LookLabMake.Read(JsonNode.Parse(x.Lab).AsObject());
                return l.Err == null && (l.Table || l.Script != 0) ? l : null;
            }
            catch { return null; }
        }

        static bool FreeColour(Piece x) => LabOf(x)?.Dyes.Count > 0;

        /// <summary>base64 parts of one JSON (a chat line is short): null until the last one is in.</summary>
        static string LabJoin(Player p, string id, int i, int n, string chunk)
        {
            if (n < 1 || n > 64 || i < 0 || i >= n) return null;
            string key = p.Guid.Full + ":" + id;
            lock (s_LabParts)
            {
                if (!s_LabParts.TryGetValue(key, out var parts) || parts.Length != n) s_LabParts[key] = parts = new string[n];
                parts[i] = chunk;
                if (parts.Any(q => q == null)) return null;
                s_LabParts.Remove(key);
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(parts))); }
                catch { return ""; }
            }
        }

        /// <summary>Only the look fields, in a fixed order: the same extras always give the same text (Undo's "did it change").</summary>
        static string LabTidy(JsonObject r, out LookLabMake.Look look)
        {
            var o = new JsonObject();
            foreach (var k in LAB_FIELDS)
            {
                var v = r[k];
                if (v == null || (v is JsonArray a && a.Count == 0)) continue;
                if (k == "effect" && v.GetValue<string>() == "none") continue;
                o[k] = JsonNode.Parse(v.ToJsonString());
            }
            if (o["effect"] == null) o.Remove("effectEvery");
            if (o["palettes"] == null) o.Remove("palettesFrom");
            look = LookLabMake.Read(o);
            return o.Count == 0 ? null : o.ToJsonString();
        }

        static string LabSays(Piece x, LookLabMake.Look l)
        {
            var what = new List<string>();
            if (l.Spectral != 0 || l.Tex.Values.Any(t => (t & 0xFFF00000u) == 0x05E00000u)) what.Add("a material");
            if (l.Tex.Values.Any(t => (t & 0xFFF00000u) != 0x05E00000u) || l.Bare.Count > 0) what.Add("a Lab skin");
            if (l.Dyes.Count > 0) what.Add("free colours");
            if (l.Script != 0) what.Add("an effect (on made items and dressed NPCs; no preview)");
            return what.Count == 0 ? $"{x.N}: its own look again." : $"{x.N}: {string.Join(", ", what)}."
                + (l.Spectral != 0 || l.Script != 0 ? " Only our client shows it." : "");
        }

        /// <summary>lab &lt;piece&gt; &lt;id&gt</summary>
        static bool LabVerb(Player p, Fit f, string verb, string[] a)
        {
            if (!LabOn) { f.Msg = "The Lab drawer is switched off on this server."; return true; }
            if (a.Length != 6 || !int.TryParse(a[3], out int i) || !int.TryParse(a[4], out int n)) { f.Msg = "Use the Lab drawer."; return true; }
            string json = LabJoin(p, verb + a[1] + ":" + a[2], i, n, a[5]);
            if (json == null) return false;
            JsonObject r;
            try { r = JsonNode.Parse(json).AsObject(); } catch { f.Msg = "That arrived garbled; try again."; return true; }
            string tidy = LabTidy(r, out var look);
            if (look.Err != null) { f.Msg = look.Err; return true; }
            if (verb == "lab")
            {
                var x = int.TryParse(a[1], out int pi) && pi >= 0 && pi < f.Pieces.Count ? f.Pieces[pi] : null;
                if (x == null) { f.Msg = "Pick a piece first."; return true; }
                if (tidy != null && (x.Ap != null || x.Tm != null || x.Pl != null))
                { f.Msg = $"{x.N} carries a tailored look of its own: pick its look from the wardrobe first, then use the Lab."; return true; }
                x.Lab = tidy;
                f.Msg = LabSays(x, look);
                return true;
            }
            uint.TryParse(a[1], out var sel);
            var l = LookOf((uint)(r["base"]?.GetValue<int>() ?? 0));
            if (l == null) { f.Msg = "That Lab look's base item is not something you can wear here."; return true; }
            var piece = FromLook(l);
            string name = (r["name"]?.GetValue<string>() ?? "").Trim();
            if (name.Length > 0 && name.Length <= 64) piece.N = name;
            if (r["paletteTemplate"] != null) piece.Pal = r["paletteTemplate"].GetValue<int>();
            if (r["shade"] != null) piece.Sh = Math.Clamp(r["shade"].GetValue<double>(), 0, 1);
            piece.Lab = tidy;
            Pick(f, piece, sel);
            return true;
        }

        static ClothingTable LabTable(uint id, Piece x, LookLabMake.Look l)
        {
            var (_, blob, _, _) = CustomClothingRule.Build(LabTableJson(id, x, l));
            var t = new ClothingTable();
            using (var r = new BinaryReader(new MemoryStream(blob))) t.Unpack(r);
            return t;
        }

        static string LabTableJson(uint id, Piece x, LookLabMake.Look l)
        {
            uint setup = ACE.Database.DatabaseManager.World.GetCachedWeenie(x.W)?.GetProperty(PropertyDataId.Setup) ?? 0;
            return LookLabMake.TableJson(id, setup, x.Cb, l.Tex, l.Bare, l.Spectral, l.Dyes);
        }

        /// <summary>The preview item wears its extras through a table that lives only until LabPreviewEnd.</summary>
        static void LabPreview(WorldObject wo, Piece x, LookLabMake.Look l)
        {
            if (!l.Table) return;
            uint id = PREVIEW_LO + (uint)s_PreviewIds.Count;
            var cache = DatManager.PortalDat?.FileCache;
            if (id > PREVIEW_HI || cache == null) return;
            cache[id] = LabTable(id, x, l);
            s_PreviewIds.Add(id);
            wo.ClothingBase = id;
            if (l.Dyes.Count > 0) wo.PaletteTemplate = LookLabMake.DYE_TEMPLATE;
        }

        static void LabPreviewEnd()
        {
            var cache = DatManager.PortalDat?.FileCache;
            foreach (var id in s_PreviewIds) cache?.TryRemove(id, out _);
            s_PreviewIds.Clear();
        }

        /// <summary>A real item (Make items, Dress NPC) takes its extras for good: a runtime table, as Make it now makes, and its effect.</summary>
        static string LabMake(WorldObject wo, Piece x)
        {
            var l = LabOf(x);
            if (l == null) return null;
            if (l.Table && !CustomClothing.Serving) return $"{x.N} came plain: this server is not serving our own looks (rg_custom_clothing).";
            if (l.Table)
            {
                var table = CustomClothing.AddRuntime(id => LabTableJson(id, x, l));
                if (table == null) return $"{x.N} came plain: the runtime look range is full (256 looks).";
                wo.ClothingBase = table.Value;
                if (l.Dyes.Count > 0) wo.PaletteTemplate = LookLabMake.DYE_TEMPLATE;
            }
            if (l.Script != 0)
            {
                wo.DefaultScriptId = l.Script;
                wo.SetProperty(LookLabMake.P_EVERY, l.Every);
            }
            wo.SetProperty(LookLabMake.P_MADE, $"Dressing Room Lab {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            return null;
        }
    }
}
