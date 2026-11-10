using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevivalGuard
{
    /// <summary>
    /// THE TOWN CRIER'S HEAD (owner 2026-11-09: "yes do the Holtburg Town Crier"). A deterministic question answerer over
    /// our own data, no model and no network: the client's offline wiki pack (tools/wiki/pack.py: 34k fandom pages plus our
    /// own), the Journal's Where next grounds (where-next.json), the quest tracker's travel layer (travel.json: portals,
    /// the Town Network), the map's places (map-places.json) and the wiki's quest levels (quests.tsv). The folder is
    /// `crier/` beside the mod dll, filled by tools/crier/sync-knowledge.sh from the client's StreamingAssets. Nothing
    /// here is ACE: TownCrier.cs is the glue, and tools/crier/harness runs this file on its own for the Q&A sheet.
    ///
    /// A question is read as one of: a level ("where do I level at 30"), a place ("how do I get to Cragstone", "where
    /// is the Mana Forge"), a thing ("what does a Greater Mana Stone do", "what level is the Acid Axe Quest"), a creature
    /// ("where are drudges"), or small talk. The subject is looked up as a town, a wiki title (aliases followed; "quest"
    /// tried on and off; the singular tried), a map place, then the closest title whose words the subject holds. Every
    /// fact in an answer is quoted from that data: a page's summary, its "Getting there" and "Level range" lines (the
    /// wiki builder wrote those from the game data), a ground's level band, a portal's destination. When nothing fits he
    /// says so and points at the Journal's Wiki tab. No em dashes (house rule). He never explains how anything of ours
    /// works under the hood: questions about bots, staff or the server get one polite line.
    /// </summary>
    public sealed class TownCrierBrain
    {
        public const string WHO = "Holtburg Herald";   // owner 2026-11-10: renamed so he is not taken for retail's Town Crier nearby
        const double KM_PER_UNIT = 0.24;          // one map unit is 240 m (ACE PositionExtensions.GetMapCoords)
        const double NEAR_KM = 1.2;               // nearer than this a route is just "walk"
        const int OPEN_SPAN = 40;                 // a ground with no top is good for this many levels above its floor (where-next tune)
        const int TELL_MAX = 330;                 // one tell's length; longer answers are split at sentences
        const int TELLS_MAX = 3;
        static readonly string[] DIRS = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        static readonly Regex COORD = new Regex(@"(\d+(?:\.\d+)?)\s*([NS])\s*,?\s*(\d+(?:\.\d+)?)\s*([EW])\b", RegexOptions.Compiled);
        static readonly Regex LINK = new Regex("\x01([^\x02]*)\x02([^\x03]*)\x03", RegexOptions.Compiled);
        static readonly Regex WORD = new Regex(@"[a-z0-9][a-z0-9']*", RegexOptions.Compiled);
        static readonly Regex STOP = new Regex(@"^(the|a|an|of|to|in|on|at|and|or|for|is|are|it|its|my|me|i|you|some|this|that|one|do|does|did|can|could|would|should|will|find|get|spawn|spawns|live|there|here|near|around|from|with|where|what|how|who|be|was|were|any|all|go|about|up|down|out|into)$", RegexOptions.Compiled);
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Who asks and from where. CrierNS/EW: where the crier himself stands (the lifestone question).</summary>
        public sealed class Asker { public string Name = ""; public double NS = 42.1, EW = 33.6; public bool Outdoors = true; public int Level = 1; public double CrierNS = 42.1, CrierEW = 33.6; }

        sealed class Entry { public string Title; public uint Offset, Length; public byte Flags, Rank, Kind; }
        sealed class Ground { public string Kind, Name, Town; public double NS, EW; public int Lo, Hi; }
        sealed class Portal { public string Name, DHub, Hub; public double NS, EW, DNS, DEW; public int Min, Max; public string Flag, Pk; }
        sealed class Place { public string Name, Kind; public double NS, EW; public int MinLv; }
        sealed class QuestLevel { public int Level, Lo, Hi; public string Type; }
        sealed class Target { public string Title, Kind, Page, Asked; public double? NS, EW; }

        readonly string m_Dir;
        readonly Action<string> m_Log;
        Entry[] m_Pages; Dictionary<string, int> m_ByLower; Dictionary<string, List<int>> m_TitleWords;
        Dictionary<string, (uint Off, uint Len)> m_Words;   // words.bin: every word of every page, postings read on demand
        readonly List<Ground> m_Grounds = new List<Ground>();
        readonly List<Portal> m_Portals = new List<Portal>();
        readonly List<Place> m_Places = new List<Place>();
        readonly Dictionary<string, QuestLevel> m_Quests = new Dictionary<string, QuestLevel>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> m_Routes = new List<string>();
        public string Status { get; private set; } = "not loaded";
        public bool Ready => m_Pages != null;
        public int Asked { get; private set; }

        TownCrierBrain(string dir, Action<string> log) { m_Dir = dir; m_Log = log ?? (_ => { }); }

        /// <summary>Load every file the folder has; a missing file is a smaller head, not a failure (Status says).</summary>
        public static TownCrierBrain Load(string dir, Action<string> log)
        {
            var b = new TownCrierBrain(dir, log);
            var have = new List<string>();
            try { if (b.LoadPack()) have.Add($"wiki {b.m_Pages.Length}"); } catch (Exception e) { b.m_Log($"wiki.pack: {e.Message}"); }
            try { if (b.LoadWords()) have.Add($"words {b.m_Words.Count}"); } catch (Exception e) { b.m_Log($"words.bin: {e.Message}"); }
            try { if (b.LoadGrounds()) have.Add($"grounds {b.m_Grounds.Count}"); } catch (Exception e) { b.m_Log($"where-next.json: {e.Message}"); }
            try { if (b.LoadTravel()) have.Add($"portals {b.m_Portals.Count}"); } catch (Exception e) { b.m_Log($"travel.json: {e.Message}"); }
            try { if (b.LoadPlaces()) have.Add($"places {b.m_Places.Count}"); } catch (Exception e) { b.m_Log($"map-places.json: {e.Message}"); }
            try { if (b.LoadQuests()) have.Add($"quests {b.m_Quests.Count}"); } catch (Exception e) { b.m_Log($"quests.tsv: {e.Message}"); }
            try { if (b.LoadRoutes()) have.Add($"routes {b.m_Routes.Count}"); } catch (Exception e) { b.m_Log($"routes.txt: {e.Message}"); }
            b.Status = have.Count == 0 ? $"nothing readable in {dir}" : string.Join(", ", have);
            return b;
        }

        // ------------------------------------------------------------------ the data
        bool LoadPack()
        {
            string pack = Path.Combine(m_Dir, "wiki.pack");
            if (!File.Exists(pack)) return false;
            using (var f = File.OpenRead(pack))
            using (var r = new BinaryReader(f))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "ACWK") throw new InvalidDataException("not a wiki pack");
                int version = (int)r.ReadUInt32();
                int n = (int)r.ReadUInt32();
                var pages = new Entry[n];
                var by = new Dictionary<string, int>(n, StringComparer.Ordinal);
                var words = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (int i = 0; i < n; i++)
                {
                    int len = r.ReadUInt16();
                    var e = new Entry { Title = Encoding.UTF8.GetString(r.ReadBytes(len)), Offset = r.ReadUInt32(), Length = r.ReadUInt32(), Flags = r.ReadByte() };
                    if (version >= 2) { e.Rank = r.ReadByte(); e.Kind = r.ReadByte(); } else e.Rank = 5;
                    pages[i] = e;
                    by[e.Title.ToLowerInvariant()] = i;
                    foreach (var w in Words(e.Title).Select(Stem).Distinct())
                    {
                        if (!words.TryGetValue(w, out var l)) words[w] = l = new List<int>();
                        l.Add(i);
                    }
                }
                m_Pages = pages; m_ByLower = by; m_TitleWords = words;
            }
            return true;
        }

        bool LoadWords()
        {
            string p = Path.Combine(m_Dir, "words.bin");
            if (!File.Exists(p) || m_Pages == null) return false;
            using (var f = File.OpenRead(p))
            using (var r = new BinaryReader(f))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "ACWW") throw new InvalidDataException("not a word index");
                r.ReadUInt32();
                int n = (int)r.ReadUInt32();
                var d = new Dictionary<string, (uint, uint)>(n, StringComparer.Ordinal);
                for (int i = 0; i < n; i++)
                {
                    string w = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
                    d[w] = (r.ReadUInt32(), r.ReadUInt32());
                }
                m_Words = d;
            }
            return true;
        }

        /// <summary>The pages a word appears in (ascending indices), or null for a word the index lacks.</summary>
        List<int> Postings(string word)
        {
            if (m_Words == null || !m_Words.TryGetValue(word, out var at)) return null;
            var list = new List<int>();
            using (var f = File.OpenRead(Path.Combine(m_Dir, "words.bin")))
            {
                f.Seek(at.Off, SeekOrigin.Begin);
                var buf = new byte[at.Len];
                int got = 0;
                while (got < buf.Length) { int k = f.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                int prev = 0, v = 0, shift = 0;
                foreach (byte b in buf)
                {
                    v |= (b & 0x7F) << shift;
                    if ((b & 0x80) != 0) { shift += 7; continue; }
                    prev += v; list.Add(prev); v = 0; shift = 0;
                }
            }
            return list;
        }

        /// <summary>The pages whose text has every one of the words (each tried as said, as its stem and as its plural).</summary>
        List<int> PagesWithAll(IEnumerable<string> words)
        {
            List<int> acc = null;
            foreach (var w in words)
            {
                var set = new HashSet<int>();
                foreach (var form in new[] { w, Stem(w), Stem(w) + "s", Stem(w) + "es" }.Distinct())
                {
                    var l = Postings(form);
                    if (l != null) set.UnionWith(l);
                }
                if (set.Count == 0) return new List<int>();
                acc = acc == null ? set.ToList() : acc.Where(set.Contains).ToList();
                if (acc.Count == 0) break;
            }
            return acc ?? new List<int>();
        }

        bool LoadGrounds()
        {
            string p = Path.Combine(m_Dir, "where-next.json");
            if (!File.Exists(p)) return false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(p)))
            {
                if (!doc.RootElement.TryGetProperty("grounds", out var gs)) return false;
                foreach (var g in gs.EnumerateArray())
                {
                    var gr = new Ground { Kind = Str(g, "kind"), Name = Str(g, "name"), Town = Str(g, "town"), NS = Num(g, "ns"), EW = Num(g, "ew") };
                    if (g.TryGetProperty("lv", out var lv) && lv.ValueKind == JsonValueKind.Array && lv.GetArrayLength() == 2)
                    {
                        gr.Lo = lv[0].ValueKind == JsonValueKind.Number ? lv[0].GetInt32() : 1;
                        gr.Hi = lv[1].ValueKind == JsonValueKind.Number ? lv[1].GetInt32() : 0;
                    }
                    if (!string.IsNullOrEmpty(gr.Name)) m_Grounds.Add(gr);
                }
            }
            return m_Grounds.Count > 0;
        }

        bool LoadTravel()
        {
            string p = Path.Combine(m_Dir, "travel.json");
            if (!File.Exists(p)) return false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(p)))
            {
                if (!doc.RootElement.TryGetProperty("portals", out var ps)) return false;
                foreach (var r in ps.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() < 11) continue;
                    m_Portals.Add(new Portal
                    {
                        Name = r[0].GetString(), NS = r[1].GetDouble(), EW = r[2].GetDouble(), DNS = r[3].GetDouble(), DEW = r[4].GetDouble(),
                        DHub = r[5].GetString() ?? "", Hub = r[6].GetString() ?? "", Min = r[7].GetInt32(), Max = r[8].GetInt32(),
                        Flag = r[9].GetString() ?? "", Pk = r[10].GetString() ?? "",
                    });
                }
            }
            return m_Portals.Count > 0;
        }

        bool LoadPlaces()
        {
            string p = Path.Combine(m_Dir, "map-places.json");
            if (!File.Exists(p)) return false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(p)))
            {
                if (!doc.RootElement.TryGetProperty("places", out var ps)) return false;
                foreach (var r in ps.EnumerateArray())
                {
                    var pl = new Place { Name = Str(r, "name"), Kind = Str(r, "kind"), NS = Num(r, "ns"), EW = Num(r, "ew") };
                    if (r.TryGetProperty("minlv", out var m) && m.ValueKind == JsonValueKind.Number) pl.MinLv = m.GetInt32();
                    if (!string.IsNullOrEmpty(pl.Name)) m_Places.Add(pl);
                }
            }
            return m_Places.Count > 0;
        }

        bool LoadQuests()
        {
            string p = Path.Combine(m_Dir, "quests.tsv");
            if (!File.Exists(p)) return false;
            foreach (var line in File.ReadLines(p))
            {
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                var q = new QuestLevel { Type = c.Length > 2 ? c[2] : "" };
                int.TryParse(c[1], out q.Level);
                if (c.Length > 4) { int.TryParse(c[3], out q.Lo); int.TryParse(c[4], out q.Hi); }
                m_Quests[c[0]] = q;
            }
            return m_Quests.Count > 0;
        }

        /// <summary>The client's route files' names (tools/crier/sync-knowledge.sh lists Assets/StreamingAssets/routes), so he
        /// can say the Assistant knows the way: "Olthoi Brood Hive, level 20+".</summary>
        bool LoadRoutes()
        {
            string p = Path.Combine(m_Dir, "routes.txt");
            if (!File.Exists(p)) return false;
            foreach (var line in File.ReadLines(p)) if (!string.IsNullOrWhiteSpace(line)) m_Routes.Add(line.Trim());
            return m_Routes.Count > 0;
        }

        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        // ------------------------------------------------------------------ the wiki
        /// <summary>The page index a title resolves to, aliases followed (an alias page's body is its target title).</summary>
        int Index(string title)
        {
            if (m_Pages == null || string.IsNullOrWhiteSpace(title)) return -1;
            string t = title.Trim().Replace('_', ' ');
            for (int hop = 0; hop < 4; hop++)
            {
                if (!m_ByLower.TryGetValue(t.ToLowerInvariant(), out int i)) return -1;
                if ((m_Pages[i].Flags & 1) == 0) return i;
                t = Read(i)?.Trim();
                if (string.IsNullOrEmpty(t)) return -1;
            }
            return -1;
        }

        string Read(int i)
        {
            try
            {
                var e = m_Pages[i];
                using (var f = File.OpenRead(Path.Combine(m_Dir, "wiki.pack")))
                {
                    f.Seek(e.Offset, SeekOrigin.Begin);
                    var buf = new byte[e.Length];
                    int got = 0;
                    while (got < buf.Length) { int k = f.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                    using (var z = new DeflateStream(new MemoryStream(buf), CompressionMode.Decompress))
                    using (var sr = new StreamReader(z, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
            }
            catch (Exception ex) { m_Log($"page {i} unreadable: {ex.Message}"); return null; }
        }

        sealed class PageInfo
        {
            public string Title, Kind = "", Summary, GettingThere, LevelRange, Repeat, Description, SoldBy, Directions;
            public double? NS, EW;
            public readonly List<string> Body = new List<string>();
            public readonly List<string> Bullets = new List<string>();
            public readonly List<string> Tables = new List<string>();
            public readonly List<KeyValuePair<string, string>> Box = new List<KeyValuePair<string, string>>();
        }

        static readonly Regex DIRECTIONS_HEAD = new Regex(@"^#\d\s*(getting (there|to)|location|locations|directions|how to get there|route|travel|where to find|access)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        PageInfo Parse(string title, string text)
        {
            var p = new PageInfo { Title = title };
            if (text == null) return p;
            bool underDirections = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("@kind ")) { p.Kind = line.Substring(6).Trim(); continue; }
                if (line.StartsWith("@summary ")) { p.Summary = Tidy(Plain(line.Substring(9))); continue; }
                if (line.StartsWith("@map "))
                {
                    var m = COORD.Match(line);
                    if (m.Success) { p.NS = Coord(m.Groups[1].Value, m.Groups[2].Value == "S"); p.EW = Coord(m.Groups[3].Value, m.Groups[4].Value == "W"); }
                    continue;
                }
                if (line.StartsWith("@")) continue;
                if (line.StartsWith("#")) { underDirections = DIRECTIONS_HEAD.IsMatch(line); continue; }
                if (line.StartsWith("Getting there:")) { p.GettingThere = Plain(line); continue; }
                if (line.StartsWith("Level range:")) { p.LevelRange = Plain(line); continue; }
                if (line.StartsWith("Repeat:")) { p.Repeat = Plain(line); continue; }
                if (line.StartsWith("Sold by:")) { p.SoldBy = Plain(line.Substring(8)); continue; }
                if (line.StartsWith("| "))
                {
                    var c = line.Substring(2).Split(new[] { " | " }, 2, StringSplitOptions.None);
                    if (c.Length == 2)
                    {
                        string k = Plain(c[0]).Trim(), v = Plain(c[1]).Trim();
                        p.Box.Add(new KeyValuePair<string, string>(k, v));
                        if (k.Equals("Description", StringComparison.OrdinalIgnoreCase) && p.Description == null) p.Description = v;
                    }
                    continue;
                }
                if (line.StartsWith("||"))
                {
                    var cells = line.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries).Select(c => Plain(c)).Where(c => c.Length > 0).ToList();
                    if (cells.Count > 0) p.Tables.Add(string.Join(" / ", cells));
                    // an item page's first table row carries the use text ("Use on a magic item to destroy that item...")
                    if (p.Description == null && p.Tables.Count == 1) { var d = cells.FirstOrDefault(c => c.Length > 25 && c.Any(char.IsLetter) && !c.Contains('/')); if (d != null) p.Description = d; }
                    continue;
                }
                if (line.StartsWith("* ")) { var b = Plain(line.Substring(2)); p.Bullets.Add(b); if (underDirections) Direct(p, b); continue; }
                if (Regex.IsMatch(line, @"^\d+\. ")) { var b = Plain(Regex.Replace(line, @"^\d+\. ", "")); if (underDirections) Direct(p, b); continue; }
                if (line.Length == 0 || line.StartsWith("[box]") || line.StartsWith("~") || line.StartsWith("See also:") || line.StartsWith(">") || line.StartsWith("**")) continue;
                var pl = Plain(line);
                p.Body.Add(pl);
                if (underDirections) Direct(p, pl);
            }
            return p;
        }

        /// <summary>The first line under a "Getting there" style heading; a line that only announces a list ("There are three
        /// means:") takes the next one too.</summary>
        static void Direct(PageInfo p, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (p.Directions == null) p.Directions = line;
            else if (p.Directions.EndsWith(":") || p.Directions.Length < 40) p.Directions = p.Directions + " " + line;
        }

        static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = LINK.Replace(s, m => m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value);
            s = s.Replace('\x01', ' ').Replace('\x02', ' ').Replace('\x03', ' ');
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        /// <summary>The corpus cleaner's leftovers a summary may carry ("Repeats every None.").</summary>
        static string Tidy(string s) => Regex.Replace(s ?? "", @"\s*Repeats every None\.?", "").Trim();

        static double Coord(string n, bool negative) => (negative ? -1 : 1) * double.Parse(n, IC);

        static bool FirstCoord(string s, out double ns, out double ew)
        {
            ns = ew = 0;
            var m = COORD.Match(s ?? "");
            if (!m.Success) return false;
            ns = Coord(m.Groups[1].Value, m.Groups[2].Value == "S"); ew = Coord(m.Groups[3].Value, m.Groups[4].Value == "W");
            return true;
        }

        // ------------------------------------------------------------------ geometry
        static double Km(double ns1, double ew1, double ns2, double ew2)
        {
            double dn = ns2 - ns1, de = ew2 - ew1;
            return Math.Sqrt(dn * dn + de * de) * KM_PER_UNIT;
        }

        static string Dir(double ns1, double ew1, double ns2, double ew2)
        {
            double a = Math.Atan2(ew2 - ew1, ns2 - ns1) * 180 / Math.PI;   // 0 = north, 90 = east
            if (a < 0) a += 360;
            return DIRS[(int)Math.Round(a / 45) % 8];
        }

        static string Fmt(double ns, double ew) =>
            $"{Math.Abs(ns).ToString("0.0", IC)}{(ns >= 0 ? "N" : "S")} {Math.Abs(ew).ToString("0.0", IC)}{(ew >= 0 ? "E" : "W")}";

        static string FmtKm(double km) => km < 0.95 ? $"{(int)Math.Round(km * 100) * 10} m" : km < 10 ? $"{km.ToString("0.0", IC)} km" : $"{(int)Math.Round(km)} km";

        /// <summary>From where the asker stands to a spot: "2.3 km north-east of here" with the best portal hop from
        /// travel.json when it saves most of the walk: a surface portal near the asker whose far side is near the spot, or
        /// in through a hub (the Town Network) and out again. Indoors the asker's spot is unknown, so only hub routes.</summary>
        string Route(Asker who, double ns, double ew)
        {
            double direct = who.Outdoors ? Km(who.NS, who.EW, ns, ew) : double.MaxValue;
            string walk = who.Outdoors ? $"{FmtKm(direct)} {Dir(who.NS, who.EW, ns, ew)} of here" : null;
            if (who.Outdoors && direct <= NEAR_KM) return $"a short walk, {walk}.";

            double best = direct * 0.8; string via = null;   // a portal must save a fifth of the walk to be worth naming
            foreach (var a in m_Portals)
            {
                if (a.Hub != "" || !Usable(a, who)) continue;
                double toA = who.Outdoors ? Km(who.NS, who.EW, a.NS, a.EW) : 2.0;
                if (toA > 3.0) continue;
                if (a.DHub == "")
                {
                    double cost = toA + Km(a.DNS, a.DEW, ns, ew);
                    if (cost < best) { best = cost; via = $"Take the {a.Name} at {Fmt(a.NS, a.EW)}, then {FmtKm(Km(a.DNS, a.DEW, ns, ew))} {Dir(a.DNS, a.DEW, ns, ew)} to {Fmt(ns, ew)}."; }
                    continue;
                }
                foreach (var b in m_Portals)
                {
                    if (b.Hub != a.DHub || b.DHub != "" || !Usable(b, who)) continue;
                    double cost = toA + 0.2 + Km(b.DNS, b.DEW, ns, ew);
                    if (cost < best) { best = cost; via = $"Take the {a.Name} at {Fmt(a.NS, a.EW)}, then the {b.Name} inside, then {FmtKm(Km(b.DNS, b.DEW, ns, ew))} {Dir(b.DNS, b.DEW, ns, ew)} to {Fmt(ns, ew)}."; }
                }
            }
            if (via != null) return (walk != null ? $"{walk}. " : "") + via;
            return walk != null ? $"{walk}, at {Fmt(ns, ew)}." : $"at {Fmt(ns, ew)}.";
        }

        static bool Usable(Portal p, Asker who) =>
            (p.Min == 0 || who.Level >= p.Min) && (p.Max == 0 || who.Level <= p.Max) && p.Flag == "" && p.Pk != "npk";

        /// <summary>A portal named for the subject ("Town Network", "Facility Hub"): the nearest usable one, through a hub
        /// if that is where it stands. Null when no portal carries all the subject's words.</summary>
        string PortalRoute(string subj, Asker who)
        {
            var words = Words(subj).Select(Stem).Where(w => !STOP.IsMatch(w) && w != "portal").ToList();
            if (words.Count == 0) return null;
            Portal best = null; double bestCost = double.MaxValue; string via = null;
            Portal barred = null; double barredKm = double.MaxValue;   // one he cannot use yet (a level floor)
            foreach (var p in m_Portals)
            {
                var pw = Words(p.Name).Select(Stem).ToList();
                if (!words.All(pw.Contains)) continue;
                if (!Usable(p, who))
                {
                    if (p.Min > who.Level && p.Hub == "") { double km = who.Outdoors ? Km(who.NS, who.EW, p.NS, p.EW) : 5.0; if (km < barredKm) { barredKm = km; barred = p; } }
                    continue;
                }
                if (p.Hub == "")
                {
                    double cost = who.Outdoors ? Km(who.NS, who.EW, p.NS, p.EW) : 5.0;
                    if (cost < bestCost) { bestCost = cost; best = p; via = null; }
                    continue;
                }
                foreach (var a in m_Portals)
                {
                    if (a.Hub != "" || a.DHub != p.Hub || !Usable(a, who)) continue;
                    double cost = (who.Outdoors ? Km(who.NS, who.EW, a.NS, a.EW) : 5.0) + 0.2;
                    if (cost < bestCost) { bestCost = cost; best = p; via = a.Name + " at " + Fmt(a.NS, a.EW); }
                }
            }
            if (best == null)
            {
                if (barred == null) return null;
                return $"The {barred.Name} opens only at level {barred.Min}; the nearest stands {(who.Outdoors ? FmtKm(barredKm) + " " + Dir(who.NS, who.EW, barred.NS, barred.EW) + " of here, " : "")}at {Fmt(barred.NS, barred.EW)}.";
            }
            if (via != null) return $"Take the {via}, then the {best.Name} inside.";
            string where = who.Outdoors ? $"{FmtKm(bestCost)} {Dir(who.NS, who.EW, best.NS, best.EW)} of here, at {Fmt(best.NS, best.EW)}" : $"at {Fmt(best.NS, best.EW)}";
            return $"The nearest {best.Name} stands {where}.";
        }

        // ------------------------------------------------------------------ the question
        public List<string> Answer(string question, Asker who)
        {
            Asked++;
            who ??= new Asker();
            string q = Norm(question);
            var parts = new List<string>();
            try { Think(q, who, parts); }
            catch (Exception e) { m_Log($"answer failed for '{question}': {e}"); parts.Clear(); parts.Add("Hear ye! My voice fails me on that one. Ask again, or try your Journal's Wiki tab."); }
            return Tells(parts);
        }

        static readonly Regex NOISE = new Regex(@"\b(town crier|crier|hear ye|please|hey|hi|hello|excuse me|um|uh|so|ok|okay|sir|good sir|mister|master)\b", RegexOptions.Compiled);
        static readonly Regex HUNT_LEVEL = new Regex(@"\b(?:level|lvl|lv)\s*(\d{1,3})\b|\b(\d{1,3})\s*(?:th|st|nd|rd)?\s*(?:level|lvl)\b|\bat\s+(\d{1,3})\b|\bfor\s+(?:a\s+)?(\d{1,3})\b|\b(?:i am|i'm|im|am)\s+(?:a\s+)?(?:level\s+)?(\d{1,3})\b", RegexOptions.Compiled);
        static readonly Regex HUNT_WORDS = new Regex(@"\b(hunt|hunting|level up|level|lvl|xp|exp|experience|grind|train|fight|kill things|kill stuff|good spot|where to go)\b", RegexOptions.Compiled);
        static readonly Regex DEFLECT = new Regex(@"\b(borg|borgs|bot|bots|android|androids|macro|macros|script|scripts|cheat|cheats|hack|hacks|exploit|admin|admins|staff|server|servers|owner|dev|devs|developer|mod|mods|plugin|hollow company|mercenary|mercenaries|merc|mercs|assistant|ai)\b", RegexOptions.Compiled);
        static readonly Regex[] TRAVEL =
        {
            new Regex(@"^(?:how|which way) (?:do|can|would|should|might) (?:i|we|one) (?:get|go|travel|walk|run|come|reach) (?:back )?(?:to|into) (?:the |a |an )?(.+)$"),
            new Regex(@"^how (?:do|can|would|should) (?:i|we) reach (?:the )?(.+)$"),
            new Regex(@"^how to (?:get|go|travel|walk|run) (?:to|into) (?:the )?(.+)$"),
            new Regex(@"^(?:the )?(?:way|ways|directions|direction|route|road|path|fastest way|quickest way|best way|shortest way) (?:to|into) (?:the )?(.+)$"),
            new Regex(@"^(?:which|what) (?:way|road|path|direction) (?:to|into|leads to|goes to) (?:the )?(.+)$"),
            new Regex(@"^where (?:do|does|did) (?:the )?(.+?) (?:live|spawn|spawns|lurk|roam|hang out|stand|sit)$"),
            new Regex(@"^where (?:can|do|could|might|would|should) (?:i|we|one) (?:find|get|buy|see|reach|hunt|kill|farm) (?:the |a |an |some )?(.+)$"),
            new Regex(@"^(?:where|wheres|where's) (?:is|are|was|be) (?:the |a |an )?(.+?)(?: located| at| found)?$"),
            new Regex(@"^(?:where|wheres|where's) (?!do |does |did |can |could |should |would |is |are )(?:the |a |an )?(.+?)(?: located| at)?$"),
            new Regex(@"^(?:take me|point me|send me|guide me|show me the way|show me) (?:to|towards|toward) (?:the )?(.+)$"),
            new Regex(@"^how far (?:is|to|away is) (?:the |it to the |it to )?(.+)$"),
            new Regex(@"^(?:directions|route|way|path) (?:for|to) (?:the )?(.+)$"),
        };
        static readonly Regex[] QUEST =
        {
            new Regex(@"^how (?:do|can|would|should) (?:i|we) (?:start|begin|do|complete|finish|run|solve|get|earn|obtain) (?:the |a |an )?(?!to )(.+)$"),
            new Regex(@"^what level (?:is|for|do i need for|do you need for|should i be for|can i do|to do) (?:the |a |an )?(.+)$"),
            new Regex(@"^(?:who|where) (?:starts|gives|begins|hands out) (?:the |a |an )?(.+)$"),
            new Regex(@"^(?:how often|when) can (?:i|you) (?:repeat|do|redo) (?:the |a |an )?(.+)$"),
        };
        static readonly Regex[] WHAT =
        {
            new Regex(@"^what (?:does|do|will|would|can) (?:the |a |an |this |that |my )?(.+?) (?:do|does|give|give me|make|mean|grant|cast)(?: for me| exactly)?$"),
            new Regex(@"^what (?:is|are|was|were) (?:the |a |an |this |that |my )?(.+?) (?:for|used for|good for|about)$"),
            new Regex(@"^(?:what|whats|what's) (?:is|are|was|were) (?:the |a |an |this |that |my )?(.+)$"),
            new Regex(@"^(?:what|whats|what's) (?:the |a |an |this |that |my )?(.+)$"),
            new Regex(@"^(?:tell me|tell us|say|speak) (?:about|of|on) (?:the |a |an )?(.+)$"),
            new Regex(@"^(?:who|whos|who's) (?:is|are|was) (?:the )?(.+)$"),
            new Regex(@"^(?:info|information|details|lore|news|anything|something) (?:on|about|of) (?:the |a |an )?(.+)$"),
            new Regex(@"^(?:describe|explain) (?:the |a |an )?(.+)$"),
            new Regex(@"^(?:do you know|know you|do you know of|have you heard of|heard of) (?:anything about |about |of )?(?:the |a |an )?(.+)$"),
        };

        static string Norm(string s)
        {
            s = (s ?? "").ToLowerInvariant().Trim();
            s = s.Replace('’', '\'').Replace("—", " ").Replace("–", " ");   // curly quote, em and en dash a player may type
            s = Regex.Replace(s, @"[^a-z0-9' \-]", " ");
            s = NOISE.Replace(s, " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.TrimEnd('?', '!', '.', ',').Trim();
        }

        static IEnumerable<string> Words(string s) => WORD.Matches(s.ToLowerInvariant()).Cast<Match>().Select(m => m.Value);

        void Think(string q, Asker who, List<string> o)
        {
            if (q.Length == 0 || Regex.IsMatch(q, @"^(greetings|good (day|morrow|evening|morning)|hail|well met|yo|sup|hola|howdy)$"))
            { o.Add(Greeting()); return; }
            if (Regex.IsMatch(q, @"^(help|help me|what can you (do|tell me|say)|what do you know|how do you work|what are you for|menu|commands|questions)$"))
            { o.Add(Help()); return; }
            if (Regex.IsMatch(q, @"^(thanks|thank you|thankee|cheers|ty|thx|many thanks|much obliged)")) { o.Add("Hear ye! The pleasure is a crier's. Safe roads to you."); return; }
            if (DEFLECT.IsMatch(q))
            { o.Add("Hear ye! Of such matters a crier knows nothing, and would not shout it if he did. Ask me of places, hunting, quests and things."); return; }

            // A level question: "where do i level at 30", "im 45 where should i hunt", "good hunting for level 20"
            var hm = HUNT_LEVEL.Match(q);
            int level = 0;
            if (hm.Success) for (int g = 1; g < hm.Groups.Count; g++) if (hm.Groups[g].Success) { level = int.Parse(hm.Groups[g].Value); break; }
            bool huntWords = HUNT_WORDS.IsMatch(q);
            if (level > 0 && (huntWords || Regex.IsMatch(q, @"^(where|what|which|any|good|best)\b")) && !Regex.IsMatch(q, @"\b(quest|portal|dungeon|gem|is the|is a)\b.*\blevel\b"))
            { Hunt(level, who, o); return; }
            if (huntWords && Regex.IsMatch(q, @"\b(where|what|which|any|good|best|spot|spots|ground|grounds|place|places)\b") && !HasSubjectBeyondHunting(q))
            { Hunt(who.Level, who, o); return; }
            if (Regex.IsMatch(q, @"^(what|which) level am i$")) { o.Add($"Hear ye! {who.Name}, by my reckoning you stand at level {who.Level}. Ask me where to hunt at it."); return; }

            // The town's lifestone: he stands beside Holtburg's
            if (Regex.IsMatch(q, @"\blifestone\b") && !Regex.IsMatch(q, @"\b(recall|tie|spell|scroll|gem)\b"))
            {
                string town = Regex.Match(q, @"\b([a-z][a-z'\-]+) lifestone\b").Groups[1].Value;
                if (town == "" || town == "the" || town == "a" || town == "nearest" || town == "closest" || town == "holtburg" || town == "this")
                { o.Add($"Hear ye! Holtburg's lifestone stands here beside me, at {Fmt(who.CrierNS, who.CrierEW)}. Touch it and you will wake here when you fall."); return; }
                var ls = m_Places.Where(p => p.Kind == "lifestone" && Words(p.Name).Contains(town)).OrderBy(p => Km(who.NS, who.EW, p.NS, p.EW)).FirstOrDefault();
                if (ls != null) { o.Add($"Hear ye! {ls.Name} stands at {Fmt(ls.NS, ls.EW)}, {Route(who, ls.NS, ls.EW)}"); return; }
                var tn = m_Places.FirstOrDefault(p => p.Kind == "town" && Words(p.Name).Contains(town));
                if (tn != null) { o.Add($"Hear ye! Every town keeps its lifestone near its heart; {tn.Name}'s is on your map once you have been there. {tn.Name} lies at {Fmt(tn.NS, tn.EW)}, {Route(who, tn.NS, tn.EW)}"); return; }
            }

            // Shopping: "who sells spell components", "where can i buy arrows": the trade, then the nearest one of it
            var shop = Regex.Match(q, @"^(?:who sells|who (?:has|carries|stocks)|where (?:can|do|could) (?:i|we|one) (?:buy|purchase|get|find) (?:some |a |an |the )?|where (?:is|are) (?:the )?(?:shop|vendor|seller|merchant) (?:for|with|that sells) )(.+)$");
            if (shop.Success)
            {
                string goods = shop.Groups[1].Value.Trim();
                foreach (var (rx, trade) in TRADES)
                {
                    if (!rx.IsMatch(goods)) continue;
                    // the trade under each of its names ("provisioner" or "grocer"), the nearest of them all
                    Target seller = null;
                    foreach (var name in trade.Split('|'))
                    {
                        var s = NearestNamed(name, who);
                        if (s != null && (seller == null || Km(who.NS, who.EW, s.NS.Value, s.EW.Value) < Km(who.NS, who.EW, seller.NS.Value, seller.EW.Value))) seller = s;
                    }
                    if (seller == null) break;
                    o.Add($"Hear ye! {Cap(goods)} you will find with the {trade.Split('|')[0]}. The nearest, {seller.Title}, stands at {Fmt(seller.NS.Value, seller.EW.Value)}, {Route(who, seller.NS.Value, seller.EW.Value)}");
                    var tg = Exact(goods); var ti = tg != null ? Parse(tg.Title, tg.Page) : null;
                    if (ti?.Summary != null) o.Add(Clip(ti.Summary, 160));
                    return;
                }
                var sold = Resolve(goods);
                if (sold != null && sold.Kind == "item") { Describe(sold, who, o, false); return; }
            }

            // Housing: one page answers every "how do I get a house"
            if (Regex.IsMatch(q, @"\b(house|housing|cottage|villa|mansion|apartment)\b") && Regex.IsMatch(q, @"\b(get|buy|own|rent|purchase|afford|how)\b"))
            { var h = Exact("Housing"); if (h != null) { Describe(h, who, o, false); return; } }

            foreach (var rx in TRAVEL)
            {
                var m = rx.Match(q);
                if (!m.Success) continue;
                string subj = Regex.Replace(m.Groups[1].Value.Trim(), @"^(the|a|an|some|my) ", "");
                // 1. a named thing that stands somewhere (a town, an NPC, a dungeon, a quest's start), or a thing
                Target exact = null;
                foreach (var cand in Candidates(subj)) { exact = Exact(cand); if (exact != null) { exact.Asked = subj; break; } }
                // (a place reached only through a hub, the Facility Hub, is told by its portal, not by a spot on the map)
                string pr = exact == null || exact.Kind == "dungeon" || !IsNamedKind(exact.Kind) ? PortalRoute(subj, who) : null;
                if (exact != null && IsNamedKind(exact.Kind) && (pr == null || exact.Kind != "dungeon")) { WhereIs(exact, who, o); return; }
                // 2. a creature: the Journal's hunting grounds that name it
                if (TryCreatureGrounds(subj, who, o)) { var ci = exact != null ? Parse(exact.Title, exact.Page) : null; if (ci?.Summary != null) o.Add(Clip(ci.Summary, 180)); return; }
                // 3. a portal named for it (the Town Network, the Facility Hub)
                if (pr != null) { o.Add($"Hear ye! {pr}"); var pi = exact != null ? Parse(exact.Title, exact.Page) : null; if (pi?.Summary != null) o.Add(Clip(pi.Summary, 180)); return; }
                // 4. a general page with its own directions or a list of sites (the Mana Forge, Aerlinthe, the Marketplace)
                if (exact != null)
                {
                    var ei = Parse(exact.Title, exact.Page);
                    if (ei.Directions != null || ei.Box.Select(kv => kv.Value).Concat(ei.Bullets).Concat(ei.Tables).Any(l => COORD.IsMatch(l))) { WhereIs(exact, who, o); return; }
                }
                // 5. the nearest page that answers to the words (a tailor, the barber in Holtburg)
                var near = NearestNamed(subj, who);
                if (near != null) { WhereIs(near, who, o); return; }
                // 6. whatever else the words name
                var t = exact ?? Resolve(subj);
                if (t != null) { WhereIs(t, who, o); return; }
                o.Add(Unknown(subj)); return;
            }
            foreach (var rx in QUEST)
            {
                var m = rx.Match(q);
                if (!m.Success) continue;
                var t = Resolve(m.Groups[1].Value);
                if (t != null) { Describe(t, who, o, wantLevel: true); return; }
            }
            foreach (var rx in WHAT)
            {
                var m = rx.Match(q);
                if (!m.Success) continue;
                string subj = m.Groups[1].Value.Trim();
                var t = Resolve(subj);
                if (t != null) { Describe(t, who, o, wantLevel: Regex.IsMatch(q, @"\blevel\b")); return; }
                o.Add(Unknown(subj)); return;
            }
            // No frame at all: "cragstone", "mana forge", "drudges"
            var bare = Resolve(q);
            if (bare != null) { if (bare.NS.HasValue && bare.Kind != "item" && bare.Kind != "quest") WhereIs(bare, who, o); else Describe(bare, who, o, false); return; }
            if (TryCreatureGrounds(q, who, o)) return;
            o.Add(Unknown(q));
        }

        static bool HasSubjectBeyondHunting(string q)
        {
            var rest = Regex.Replace(q, @"\b(where|what|which|any|good|best|spot|spots|ground|grounds|place|places|should|do|can|could|i|we|to|go|for|at|my|a|an|the|is|are|there|near|here|around|now|next|some|hunt|hunting|level|lvl|xp|exp|experience|grind|train|fight|up|me|us|kill|things|stuff|of|in|on|and|or|with|from|get|more)\b", " ");
            return WORD.IsMatch(rest);
        }

        string Greeting() => $"Hear ye, hear ye! The {WHO}, at your service. Ask me where to hunt at your level, how to reach a town or dungeon, or what a thing does. Say \"help\" for more.";

        string Help() => "Hear ye! Ask me plainly: \"where do I level at 30\", \"how do I get to Cragstone\", \"where is the Mana Forge\", \"what does a Greater Mana Stone do\", \"what level is the Acid Axe Quest\". What I do not know, your Journal's Wiki tab may.";

        string Unknown(string subj)
        {
            subj = subj.Trim();
            var near = Loose(subj, strict: false);
            if (near != null) return $"Hear ye! Of \"{subj}\" I have heard nothing, though \"{near.Title}\" has a page in your Journal's Wiki tab. Ask me of that by name.";
            return $"Hear ye! Of \"{subj}\" this crier has heard nothing. Try your Journal's Wiki tab; it knows more than I shout.";
        }

        // ------------------------------------------------------------------ hunting
        void Hunt(int level, Asker who, List<string> o)
        {
            if (level < 1) level = 1;
            if (m_Grounds.Count == 0) { o.Add("Hear ye! My list of hunting grounds has gone missing. Your Journal's Where next page keeps its own."); return; }
            var fit = Fit(level, 0);
            if (fit.Count == 0) fit = Fit(level, 5);
            if (fit.Count == 0) fit = Fit(level, 15);
            if (fit.Count == 0) { o.Add($"Hear ye! I know no grounds I would send a level {level} to. Your Journal's Where next page lists what fits you."); return; }
            // Nearest first, as the Journal does, one entry per name; a ground whose band holds the level squarely beats one
            // the level has outgrown or not yet reached.
            var picks = fit.GroupBy(g => g.Name).Select(gr => gr.OrderBy(g => Km(who.NS, who.EW, g.NS, g.EW)).First())
                           .OrderBy(g => Km(who.NS, who.EW, g.NS, g.EW) + Misfit(g, level) * 1.5).Take(3).ToList();
            var sb = new StringBuilder($"Hear ye! Good hunting for level {level}: ");
            for (int i = 0; i < picks.Count; i++)
            {
                var g = picks[i];
                if (i > 0) sb.Append(i == picks.Count - 1 ? "; and " : "; ");
                sb.Append(Cap(g.Name)).Append(g.Kind == "dungeon" ? " (dungeon, " : " (").Append(Band(g.Lo, g.Hi)).Append(")");
                if (who.Outdoors) sb.Append(", ").Append(FmtKm(Km(who.NS, who.EW, g.NS, g.EW))).Append(' ').Append(Dir(who.NS, who.EW, g.NS, g.EW));
                sb.Append(" at ").Append(Fmt(g.NS, g.EW));
                if (!string.IsNullOrEmpty(g.Town) && g.Kind != "dungeon") sb.Append(", near ").Append(g.Town);
            }
            sb.Append('.');
            o.Add(sb.ToString());
            var first = picks[0];
            string route = Route(who, first.NS, first.EW);
            if (route.Contains("Take ")) o.Add($"To {Cap(first.Name)}: {route}");
            string known = RouteFor(first.Name);
            o.Add(known != null ? $"Your Assistant knows a route called \"{known}\". Your Journal's Where next page shows the rest, with a Go arrow."
                                : "Your Journal's Where next page shows the way to each, with a Go arrow.");
        }

        static int Top(Ground g) => g.Hi != 0 ? g.Hi : g.Lo + OPEN_SPAN;
        List<Ground> Fit(int level, int slack) => m_Grounds.Where(g => g.Lo - slack <= level + 3 && level <= Top(g) + slack).ToList();
        static int Misfit(Ground g, int level) => level < g.Lo ? g.Lo - level : level > Top(g) ? level - Top(g) : 0;

        static string Band(int lo, int hi) => hi == 0 ? $"level {lo} and up" : lo == hi ? $"level {lo}" : $"levels {lo} to {hi}";

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>"where are drudges": grounds whose name holds the creature word, nearest first.</summary>
        bool TryCreatureGrounds(string subj, Asker who, List<string> o)
        {
            // a word counts as a creature's when the wiki has a creature or family page by that name ("Mosswart", "Drudge
            // Skulker"), so "Mana Forge" does not hunt at the Power Forges
            var words = Words(subj).Where(w => w.Length > 3 && !STOP.IsMatch(w) && !Regex.IsMatch(w, @"^(some|many|good|hunt|kill|creature|monster|monsters)$"))
                                   .Select(Stem).Where(IsCreatureWord).ToList();
            if (words.Count == 0) return false;
            var hits = m_Grounds.Where(g => { var gw = Words(g.Name).Select(Stem).ToList(); return words.Any(w => gw.Contains(w)); })
                                .GroupBy(g => g.Name).Select(gr => gr.OrderBy(g => Km(who.NS, who.EW, g.NS, g.EW)).First())
                                .OrderBy(g => Km(who.NS, who.EW, g.NS, g.EW)).Take(3).ToList();
            if (hits.Count == 0) return false;
            var sb = new StringBuilder($"Hear ye! {Cap(subj)} are hunted at: ");
            for (int i = 0; i < hits.Count; i++)
            {
                var g = hits[i];
                if (i > 0) sb.Append("; ");
                sb.Append(Cap(g.Name)).Append(" (").Append(Band(g.Lo, g.Hi)).Append(")");
                if (who.Outdoors) sb.Append(", ").Append(FmtKm(Km(who.NS, who.EW, g.NS, g.EW))).Append(' ').Append(Dir(who.NS, who.EW, g.NS, g.EW));
                sb.Append(" at ").Append(Fmt(g.NS, g.EW));
            }
            sb.Append('.');
            o.Add(sb.ToString());
            return true;
        }

        /// <summary>True when the wiki has a creature page, or a creature family page, by this word (any of the title's
        /// words is this stem and the page is kind creature or list): "drudge", "tusker", "mosswart"; not "forge".</summary>
        bool IsCreatureWord(string stem)
        {
            if (m_TitleWords == null || !m_TitleWords.TryGetValue(stem, out var l)) return false;
            foreach (var i in l)
            {
                var e = m_Pages[i];
                if ((e.Kind == 1 || e.Kind == 13) && (e.Flags & 1) == 0 && Stem(e.Title.ToLowerInvariant()) == stem) return true;
            }
            return false;
        }

        static string Stem(string w)
        {
            if (w.EndsWith("ies") && w.Length > 4) return w.Substring(0, w.Length - 3) + "y";
            if (w.EndsWith("es") && w.Length > 4 && (w.EndsWith("shes") || w.EndsWith("ches") || w.EndsWith("xes") || w.EndsWith("sses"))) return w.Substring(0, w.Length - 2);
            if (w.EndsWith("s") && !w.EndsWith("ss") && w.Length > 3) return w.Substring(0, w.Length - 1);
            return w;
        }

        string RouteFor(string name)
        {
            var nw = Words(name).Select(Stem).Where(w => w.Length > 3).ToList();
            foreach (var r in m_Routes)
            {
                string head = r.Split(',')[0];
                var rw = Words(head).Select(Stem).ToList();
                if (nw.Count > 0 && nw.All(rw.Contains)) return r;
            }
            return null;
        }

        // ------------------------------------------------------------------ places and things
        /// <summary>What a subject names: a town, a wiki page (aliases followed; "quest" tried on and off; singular tried), a
        /// map place, or the closest title whose words the subject holds.</summary>
        Target Resolve(string subj)
        {
            subj = Regex.Replace(subj.Trim(), @"^(the|a|an|some|my) ", "");
            subj = Regex.Replace(subj, @" (please|now|today|from here|again|right now)$", "").Trim();
            if (subj.Length < 2) return null;
            foreach (var cand in Candidates(subj))
            {
                var t = Exact(cand);
                if (t != null) { t.Asked = subj; return t; }
            }
            var l = Loose(subj, strict: true);
            if (l != null) l.Asked = subj;
            return l;
        }

        static IEnumerable<string> Candidates(string s)
        {
            yield return s;
            if (!s.EndsWith(" quest")) yield return s + " quest";
            else yield return s.Substring(0, s.Length - 6);
            var ws = s.Split(' ');
            string last = ws[ws.Length - 1];
            string stem = Stem(last);
            if (stem != last) { ws[ws.Length - 1] = stem; yield return string.Join(" ", ws); }
            if (s.EndsWith(" dungeon")) yield return s.Substring(0, s.Length - 8);
            if (s.EndsWith(" town")) yield return s.Substring(0, s.Length - 5);
            if (s.EndsWith(" portal")) yield return s.Substring(0, s.Length - 7);
            if (s.StartsWith("town of ")) yield return s.Substring(8);
        }

        Target Exact(string name)
        {
            // towns first: a town's own coordinates are the map's
            var town = m_Places.FirstOrDefault(p => p.Kind == "town" && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (town != null) return WithPage(new Target { Title = town.Name, Kind = "town", NS = town.NS, EW = town.EW });
            int i = Index(name);
            if (i >= 0)
            {
                var e = m_Pages[i];
                string text = Read(i);
                var info = Parse(e.Title, text);
                var t = new Target { Title = e.Title, Kind = info.Kind, Page = text, NS = info.NS, EW = info.EW };
                if (!t.NS.HasValue)
                {
                    var pl = m_Places.FirstOrDefault(p => p.Name.Equals(e.Title, StringComparison.OrdinalIgnoreCase) && p.Kind != "portal");
                    if (pl != null) { t.NS = pl.NS; t.EW = pl.EW; if (string.IsNullOrEmpty(t.Kind) || t.Kind == "other") t.Kind = pl.Kind; }
                }
                return t;
            }
            var place = m_Places.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Kind != "portal").OrderBy(p => p.Kind == "dungeon" ? 0 : 1).FirstOrDefault();
            if (place != null) return new Target { Title = place.Name, Kind = place.Kind, NS = place.NS, EW = place.EW };
            return null;
        }

        Target WithPage(Target t)
        {
            int i = Index(t.Title);
            if (i >= 0) { t.Page = Read(i); var info = Parse(t.Title, t.Page); if (!t.NS.HasValue) { t.NS = info.NS; t.EW = info.EW; } }
            return t;
        }

        /// <summary>The best title sharing the subject's words: one whose every word the subject holds (and that covers at least
        /// half the subject), else one holding every word of the subject, else (loose only) the title sharing the most words.</summary>
        Target Loose(string subj, bool strict)
        {
            if (m_TitleWords == null) return null;
            var words = Words(subj).Select(Stem).Where(w => !STOP.IsMatch(w)).Distinct().ToList();
            if (words.Count == 0) return null;
            var score = new Dictionary<int, int>();
            foreach (var w in words)
            {
                if (!m_TitleWords.TryGetValue(w, out var l)) continue;
                foreach (var i in l) score[i] = score.TryGetValue(i, out var c) ? c + 1 : 1;
            }
            if (score.Count == 0) return null;
            int best = -1; double bestKey = double.MinValue;
            foreach (var kv in score)
            {
                var e = m_Pages[kv.Key];
                if ((e.Flags & 1) != 0 || e.Title.Contains(':')) continue;   // an alias scores on its target; no Category: pages
                var tw = Words(e.Title).Select(Stem).Distinct().ToList();
                int shared = kv.Value;
                bool titleInSubject = tw.All(words.Contains) && tw.Count * 2 >= words.Count;
                bool subjectInTitle = words.All(tw.Contains);
                if (strict && !titleInSubject && !subjectInTitle) continue;
                if (!strict && (shared * 2 < words.Count || (shared < 2 && words.Count > 1))) continue;
                // the plain page beats disambiguation pages and "(Something)" variants; a longer exact cover beats a short one
                double key = (titleInSubject ? 100 : 0) + (subjectInTitle ? 50 : 0) + shared * 10 - tw.Count * 2 + e.Rank * 0.5
                             - (e.Kind == 11 ? 30 : 0) - (e.Title.Contains('(') ? 5 : 0);
                if (key > bestKey) { bestKey = key; best = kv.Key; }
            }
            if (best < 0) return null;
            return Exact(m_Pages[best].Title);
        }

        /// <summary>"where is the tailor", "the barber in holtburg": of the pages whose text holds every word of the subject and
        /// that stand somewhere (an @map line: NPCs, dungeons, places, towns), the nearest to the asker. Needs words.bin.</summary>
        Target NearestNamed(string subj, Asker who)
        {
            if (m_Words == null) return null;
            var words = Words(subj).Where(w => w.Length > 2 && !STOP.IsMatch(w)).Distinct().ToList();
            if (words.Count == 0) return null;
            // npc, dungeon, town, place pages only (the kind is in the directory: no read); too many means a common word, not a name
            var hits = PagesWithAll(words).Where(i => (m_Pages[i].Flags & 1) == 0 && (m_Pages[i].Kind == 2 || m_Pages[i].Kind == 6 || m_Pages[i].Kind == 7 || m_Pages[i].Kind == 8)).ToList();
            if (hits.Count == 0 || hits.Count > 600) return null;
            Target best = null; double bestKm = double.MaxValue;
            foreach (var i in hits)
            {
                var e = m_Pages[i];
                string text = Read(i);
                var info = Parse(e.Title, text);
                if (!info.NS.HasValue) continue;
                // the subject words must be in the title or the summary line, not buried in a long page
                string head = (e.Title + " " + (info.Summary ?? "")).ToLowerInvariant();
                if (!words.All(w => head.Contains(w) || head.Contains(Stem(w)))) continue;
                double km = who.Outdoors ? Km(who.NS, who.EW, info.NS.Value, info.EW.Value) : Km(who.CrierNS, who.CrierEW, info.NS.Value, info.EW.Value);
                if (km < bestKm) { bestKm = km; best = new Target { Title = e.Title, Kind = info.Kind, Page = text, NS = info.NS, EW = info.EW, Asked = subj }; }
            }
            return best;
        }

        /// <summary>A page kind that names one thing standing somewhere (or one thing to describe), as against a general page
        /// ("Archmage", "Tailor": the trade, not a person; "Aerlinthe": the island) that the nearest named one may beat.</summary>
        static bool IsNamedKind(string k) =>
            k == "npc" || k == "town" || k == "dungeon" || k == "place" || k == "item" || k == "quest" || k == "creature" || k == "disambig" || k == "spell";

        /// <summary>Who sells what, for "who sells spell components": the trade whose shops carry it (retail's vendor roles).</summary>
        static readonly (Regex Goods, string Trade)[] TRADES =
        {
            (new Regex(@"\b(spell components?|scarabs?|tapers?|herbs?|powders?|potions?|talismans?|foci|focus|wands?|orbs?|staffs?|portal gems?|mana stones?)\b"), "archmage"),
            (new Regex(@"\b(healing kits?|health kits?|stamina kits?|mana kits?|bandages?|kits?)\b"), "healer"),
            (new Regex(@"\b(arrows?|bolts?|bows?|crossbows?|quarrels?|atlatls?|darts?|fletching)\b"), "bowyer"),
            (new Regex(@"\b(armou?r|shields?|helms?|helmets?|gauntlets?|greaves?|breastplates?|cuirass)\b"), "armorer|armourer"),
            (new Regex(@"\b(swords?|axes?|maces?|daggers?|spears?|weapons?|blades?|clubs?)\b"), "weaponsmith|blacksmith"),
            (new Regex(@"\b(food|drink|meat|bread|cheese|water|ale|wine|provisions?|rations?|lockpicks?|backpacks?|packs?|sacks?)\b"), "provisioner|grocer"),
            (new Regex(@"\b(jewel(ry|lery|s)?|rings?|necklaces?|bracelets?|gems?|crystals?)\b"), "jeweler|jeweller"),
            (new Regex(@"\b(clothes|clothing|shirts?|pants|robes?|shoes|boots|hats?|tailoring)\b"), "tailor"),
            (new Regex(@"\b(hair|haircut|beard|barber)\b"), "barber"),
        };

        void WhereIs(Target t, Asker who, List<string> o)
        {
            var info = Parse(t.Title, t.Page);
            string label = Label(t.Kind);
            if (t.Kind == "item") { Describe(t, who, o, false); return; }
            if (t.NS.HasValue)
            {
                o.Add($"Hear ye! {t.Title}{label} stands at {Fmt(t.NS.Value, t.EW.Value)}, {Route(who, t.NS.Value, t.EW.Value)}".TrimEnd('.') + ".");
                if (info.GettingThere != null) o.Add(info.GettingThere);
                else if (t.Kind == "disambig") { var opts = info.Bullets.Take(3).ToList(); if (opts.Count > 0) o.Add($"The wiki knows it as: {string.Join("; ", opts)}."); }
                else if (info.Summary != null && t.Kind != "town") o.Add(Clip(info.Summary, 220));
                if (info.LevelRange != null) o.Add(Clip(info.LevelRange, 160));
                string known = RouteFor(t.Title);
                if (known != null) o.Add($"Your Assistant knows a route called \"{known}\".");
                else if (t.Kind == "dungeon" || t.Kind == "town") o.Add("Your Journal's Wiki tab has the page; its Guide me there line sets the Go arrow.");
                return;
            }
            // A page without a spot of its own: a creature family's hunting grounds; a portal named for it; the page's own
            // directions; the lines that name a spot; its summary
            bool creatureish = t.Kind == "creature" || t.Kind == "list";
            if (creatureish && TryCreatureGrounds(t.Asked ?? t.Title, who, o)) { if (info.Summary != null) o.Add(Clip(info.Summary, 200)); return; }
            string pr = PortalRoute(t.Asked ?? t.Title, who);
            if (pr != null) { o.Add($"Hear ye! {pr}"); if (info.Summary != null) o.Add(Clip(info.Summary, 200)); return; }
            if (info.Directions != null && !COORD.IsMatch(info.Directions)) { o.Add($"Hear ye! To {t.Title}, the wiki says: {Clip(info.Directions, 240)}"); if (info.Summary != null) o.Add(Clip(info.Summary, 180)); return; }
            // the lines that name a spot (an NPC's Location row, a list of sites), nearest first
            var spots = new List<(string Line, double Km)>();
            foreach (var kv in info.Box) if (FirstCoord(kv.Value, out var ns, out var ew)) spots.Add(($"{kv.Key} {kv.Value}", Km(who.NS, who.EW, ns, ew)));
            if (spots.Count == 0) foreach (var b in info.Bullets) if (FirstCoord(b, out var ns, out var ew)) spots.Add((b, Km(who.NS, who.EW, ns, ew)));
            if (spots.Count == 0) foreach (var b in info.Tables) if (FirstCoord(b, out var ns, out var ew)) spots.Add((b, Km(who.NS, who.EW, ns, ew)));
            if (spots.Count == 0) foreach (var b in info.Body) if (FirstCoord(b, out var ns, out var ew)) spots.Add((b, Km(who.NS, who.EW, ns, ew)));
            if (spots.Count > 0)
            {
                var near = spots.OrderBy(s => who.Outdoors ? s.Km : 0).Take(3).Select(s => Clip(s.Line, 110)).ToList();
                o.Add($"Hear ye! {t.Title}{label}, as the wiki has it{(spots.Count > 1 ? ", nearest first" : "")}: {string.Join("; ", near)}.");
                if (spots.Count > 3) o.Add($"And {spots.Count - 3} more: your Journal's Wiki tab has the whole page, \"{t.Title}\".");
                return;
            }
            if (info.Summary != null) { o.Add($"Hear ye! Where exactly {t.Title} lies I cannot say, but the wiki tells: {Clip(info.Summary, 230)}"); o.Add($"The whole page is in your Journal's Wiki tab, \"{t.Title}\"."); return; }
            o.Add($"Hear ye! {t.Title}{label} I know by name, but not where it lies. Your Journal's Wiki tab has the page.");
        }

        void Describe(Target t, Asker who, List<string> o, bool wantLevel)
        {
            var info = Parse(t.Title, t.Page);
            string label = Label(t.Kind);
            if (t.Kind == "disambig")
            {
                var opts = info.Bullets.Take(4).ToList();
                o.Add($"Hear ye! \"{t.Title}\" names more than one thing: {string.Join("; ", opts)}. Ask me of one by its full name.");
                return;
            }
            var sb = new StringBuilder($"Hear ye! {t.Title}{label.TrimEnd(',')}: ");
            string said = info.Summary;
            // a quest summary's fandom level claim gives way to the game's own range (quests.tsv), said below
            if (t.Kind == "quest" && said != null && m_Quests.TryGetValue(t.Title, out var qlv) && qlv.Lo > 0)
                said = Regex.Replace(said, @" for (?:any level|levels? \d+\+?)(?: \(recommended \d+\+?\))?", "");
            if (string.IsNullOrEmpty(said)) said = info.Description;
            if (string.IsNullOrEmpty(said))
            {
                var facts = info.Box.Where(kv => !COORD.IsMatch(kv.Value) && kv.Value.Length > 0 && kv.Value.Length < 120).Take(3).Select(kv => $"{kv.Key} {kv.Value}").ToList();
                if (facts.Count > 0) said = string.Join(", ", facts) + ".";
                else if (info.Body.Count > 0) said = info.Body.OrderByDescending(b => b.Length > 40 ? 1 : 0).First();
            }
            if (!string.IsNullOrEmpty(said)) sb.Append(Clip(said, 240));
            else sb.Append("the name I know, the tale I do not");
            string head = sb.ToString().TrimEnd();
            o.Add(head + (head.EndsWith(".") || head.EndsWith("!") ? "" : "."));
            if (t.Kind == "item")
            {
                var dest = info.Bullets.FirstOrDefault(b => b.StartsWith("Destination:"));
                if (dest != null) o.Add(dest + ".");
                // where it goes: the page's own "turn in", "give to", "hand to" line, if it has one
                var give = info.Bullets.Concat(info.Body).FirstOrDefault(b => Regex.IsMatch(b, @"\b(turn(ed)? in|give|given|hand(ed)? (in|to)|trade(d)? to|exchange)\b", RegexOptions.IgnoreCase) && b.Length < 240);
                if (give != null) o.Add(Clip(give, 200));
                if (info.SoldBy != null)
                {
                    var sellers = info.SoldBy.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    int more = sellers.Count > 2 ? sellers.Count - 2 : 0;
                    var tail = sellers.LastOrDefault() ?? "";
                    var mm = Regex.Match(tail, @"and (\d+) more");
                    if (mm.Success) { more += int.Parse(mm.Groups[1].Value); sellers.RemoveAt(sellers.Count - 1); }
                    o.Add($"Sold by {string.Join(" and ", sellers.Take(2))}{(more > 0 ? $" and {more} more" : "")}.");
                }
            }
            if (t.Kind == "quest" || wantLevel)
            {
                if (m_Quests.TryGetValue(t.Title, out var ql) && (ql.Level > 0 || ql.Lo > 0))
                    o.Add(ql.Lo > 0 ? $"Level: {Band(ql.Lo, ql.Hi)}{(ql.Type != "" ? ", " + ql.Type.ToLowerInvariant() : "")}, by the game's own data." : $"Suggested level {ql.Level}{(ql.Type != "" ? ", " + ql.Type.ToLowerInvariant() : "")}.");
                else if (info.LevelRange != null) o.Add(Clip(info.LevelRange, 160));
            }
            if (t.Kind == "quest" && info.Repeat != null) o.Add(Clip(info.Repeat, 120));
            if (t.Kind == "quest" || t.Kind == "dungeon")
            {
                if (info.GettingThere != null) o.Add(Clip(info.GettingThere, 260));
                else if (t.NS.HasValue) o.Add($"It lies at {Fmt(t.NS.Value, t.EW.Value)}, {Route(who, t.NS.Value, t.EW.Value)}");
                if (t.Kind == "quest") o.Add("Track it in your Journal and the Next step line walks you through it.");
            }
            else if (t.NS.HasValue && t.Kind != "item") o.Add($"Find it at {Fmt(t.NS.Value, t.EW.Value)}, {Route(who, t.NS.Value, t.EW.Value)}");
            if (t.Kind == "creature" || t.Kind == "list") TryCreatureGrounds(t.Asked ?? t.Title, who, o);
            if (o.Count == 1) o.Add($"More in your Journal's Wiki tab, \"{t.Title}\".");
        }

        static string Label(string kind)
        {
            switch (kind)
            {
                case "town": return ", the town,";
                case "dungeon": return ", a dungeon,";
                case "spell": return ", a spell,";
                default: return "";
            }
        }

        static string Clip(string s, int max)
        {
            s = (s ?? "").Trim();
            if (s.Length <= max) return s;
            int cut = s.LastIndexOf(". ", max, StringComparison.Ordinal);
            if (cut > max / 2) return s.Substring(0, cut + 1);
            cut = s.LastIndexOf(' ', max);
            return s.Substring(0, cut > 0 ? cut : max).TrimEnd(',', ';', ':') + "...";
        }

        /// <summary>The answer as tells: short parts share one, none over TELL_MAX, at most TELLS_MAX (the rest is dropped).</summary>
        static List<string> Tells(List<string> parts)
        {
            var outp = new List<string>();
            var cur = new StringBuilder();
            foreach (var raw in parts)
            {
                string p = (raw ?? "").Trim();
                if (p.Length == 0) continue;
                if (p.Length > TELL_MAX) p = Clip(p, TELL_MAX);
                if (cur.Length > 0 && cur.Length + 1 + p.Length <= TELL_MAX) { cur.Append(' ').Append(p); continue; }
                if (cur.Length > 0) outp.Add(cur.ToString());
                cur.Clear(); cur.Append(p);
            }
            if (cur.Length > 0) outp.Add(cur.ToString());
            if (outp.Count > TELLS_MAX) outp = outp.Take(TELLS_MAX).ToList();
            if (outp.Count == 0) outp.Add("Hear ye! Ask me of places, hunting, quests and things.");
            return outp;
        }
    }
}
