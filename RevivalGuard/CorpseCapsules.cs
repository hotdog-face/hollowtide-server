using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;
using AceLandblock = ACE.Server.Entity.Landblock;
using Position = ACE.Entity.Position;

namespace RevivalGuard
{
    /// <summary>Corpse capsules.</summary>
    static class CorpseCapsules
    {
        internal const string P_ON = "rg_corpse_capsules", P_COUNT = "rg_corpse_capsules_count", P_REFILL = "rg_corpse_capsules_refill_per_hour",
            P_OPEN = "rg_corpse_capsules_open";
        const double LB_COOL = 6 * 3600;     // a landblock where a corpse was looted gets no new one for this long
        const double FOREVER = 10.0 * 365 * 24 * 3600;
        const uint PYREAL = 273;
        const float SPACING = 8f;

        static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }
        static int Target { get { try { var v = PropertyManager.GetLong(P_COUNT, 0).Item; return v > 0 ? (int)v : s_Data?.Target ?? 450; } catch { return s_Data?.Target ?? 450; } } }
        static double s_Skew;                // staff test clock (@corpses age): seconds added to now
        static double Now => ACE.Common.Time.GetUnixTime() + s_Skew;
        static int RefillPerHour { get { try { var v = PropertyManager.GetLong(P_REFILL, 0).Item; return v > 0 ? (int)v : 40; } catch { return 40; } } }

        // ------------------------------------------------------------ data
        sealed class Look { public uint Setup, MTable, Pal; public (byte i, uint g)[] Parts; public (byte i, uint o, uint n)[] Tex; public (uint p, ushort o, ushort l)[] Subs; public int Seen; }
        sealed class Site { public uint Cell; public float X, Y, Z; public int Lv, Tier, Killer, Kind; public double W; public bool Bad; }
        sealed class Item { public uint Wcid; public int W, Min, Max; }
        sealed class Real { public string Name; public uint Cell; public float[] Pos; public Look Look; public uint[] Items; }
        sealed class Data
        {
            public int Target; public double[] Shares;
            public Dictionary<int, uint> TierProfile = new Dictionary<int, uint>();
            public List<Real> Real = new List<Real>(); public List<Look> Looks = new List<Look>(); public List<string> Killers = new List<string>();
            public List<Site>[] Sites;
            public double[][] Cum;
            public double[] LookCum;
            public List<Item> Common = new List<Item>(), Uncommon = new List<Item>(), Valuable = new List<Item>();
            public Dictionary<ushort, List<Item>> ByLb = new Dictionary<ushort, List<Item>>();
            public List<string> Names = new List<string>();
            public Dictionary<ushort, string[]> LocalMonsters = new Dictionary<ushort, string[]>();   // CorpseKillerRule
            public HashSet<string> Monsters = new HashSet<string>(StringComparer.Ordinal);
            public List<NamedSite> Named = new List<NamedSite>();
            public Dictionary<int, NamedSite> NamedByKind = new Dictionary<int, NamedSite>();
        }
        /// <summary>A named site (see NAMED): its sites are kind Kind; Fill standing there at once, Cap in one landblock.</summary>
        sealed class NamedSite { public string Id; public int Kind, Fill, Cap; public float Spacing; }
        /// <summary>The server-only real names file (see NAMES above); null when absent.</summary>
        sealed class Person { public string Name; public Look Look; public bool Darktide; public string[] Killers; }
        sealed class RealNames
        {
            public string Path;
            public List<(uint cell, float x, float y, string name)> Real = new List<(uint, float, float, string)>();
            public List<Person> People = new List<Person>();
            public List<Site> DtSites = new List<Site>();
            public List<string> DtKillers = new List<string>();   // real Darktide player killers (server-only file)
            public List<int> DtIdx = new List<int>(), OtherIdx = new List<int>();   // People indices: Darktide first (PickVictim)
            public Dictionary<string, Person> ByName = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
            // named sites' sides (NAMED): id -> sides of names; those names lie only at their site (Reserved)
            public Dictionary<string, IList<IList<string>>> Sides = new Dictionary<string, IList<IList<string>>>(StringComparer.Ordinal);
            public HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        const int DT_MAX = 6;                // Darktide-place corpses standing at once
        const int KIND_PK = 3, KIND_LAWN = 4, KIND_DT = 6, KIND_TN = 7, KIND_HUB = 8, KIND_AYAN = 9;

        // ------------------------------------------------------------ state
        sealed class Rec
        {
            public int Id; public bool IsReal; public string Name; public Look Look; public Site Site; public bool DtSite, OwnLook;
            public uint Cell; public float X, Y, Z, Qw = 1, Qz; public float[] Rot;
            public int Level, Tier; public string Killer;
            public double Born, Expires;
            public uint[] RealItems;
            public bool Rolled, Gone; public int RolledN = -1, LookIdx = -1, SiteIdx = -1, DtIdx = -1;
            public Corpse Corpse;                   // made, in a loaded landblock
            public List<WorldObject> Held = new List<WorldObject>();   // its items while its landblock is unloaded
            public ushort Lb => (ushort)(Cell >> 16);
        }

        static readonly object s_Lock = new object();
        static Data s_Data;
        static RealNames s_Names;
        static readonly Dictionary<int, Rec> s_Recs = new Dictionary<int, Rec>();
        static readonly Dictionary<ushort, List<Rec>> s_ByLb = new Dictionary<ushort, List<Rec>>();
        static readonly Dictionary<uint, Rec> s_ByGuid = new Dictionary<uint, Rec>();
        static readonly HashSet<string> s_NamesInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static int s_NextId = 1;
        static bool s_RealMade, s_Paused, s_WasOn;
        static double s_NextTick;
        static readonly Random s_Rng = new Random();
        // since start
        static int s_Made, s_Rotted, s_Expired, s_Replaced, s_BadSites, s_Looted;
        static double s_MakeMs, s_MakeMax; static int s_MakeN;

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard CorpseCapsules: a few hundred old player corpses lie across Dereth where people died (needs the owner's yes)");
            PkCommon.RegisterBool(P_OPEN, false, "RevivalGuard CorpseCapsules: anyone may loot the generated corpses (off: retail-strict, nobody may; owner 2026-11-18)");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_COUNT)) l[P_COUNT] = new Property<long>(0, "RevivalGuard CorpseCapsules: how many stand at once (0: the data's target)");
                if (!l.ContainsKey(P_REFILL)) l[P_REFILL] = new Property<long>(0, "RevivalGuard CorpseCapsules: replacements for looted corpses an hour, server-wide (0: 40)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules: count switch not registered ({e.Message})"); }
            CommandManager.TryAddCommand(Cmd, "corpses", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "Corpse capsules (old player corpses across Dereth): status, spawn n (or here), clear, reload, near, goto id, budget n.",
                "[status | spawn [n|here] | clear | reload | near | find <name> | goto <id> | budget [n] | age <hours>]");
            try { s_Data = LoadData(); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] CorpseCapsules: data not loaded ({e.Message}); none will appear"); }
            s_Names = LoadNames();
            Mod.Log.Info($"[RevivalGuard] CorpseCapsules: {(On ? "ON" : "off")}, data {(s_Data == null ? "MISSING" : $"{s_Data.Real.Count} real, {s_Data.Looks.Count} looks, {s_Data.Sites.Sum(x => x.Count)} sites, {s_Data.Names.Count} names")}; {NamesLine()} (@corpses)");
        }

        static string NamesPath => Environment.GetEnvironmentVariable("RG_CORPSE_NAMES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "corpse-capsules", "names.json");

        static string NamesLine() => s_Names == null ? "names invented (no server names file)"
            : $"real names from {s_Names.Path}: {s_Names.Real.Count} real corpses, {s_Names.People.Count} people ({s_Names.People.Count(p => p.Look != null)} in their own look, {s_Names.People.Count(p => p.Darktide)} Darktide), {s_Names.DtKillers.Count} Darktide killers, {s_Names.DtSites.Count} Darktide places, {s_Names.Sides.Count} named sites' sides ({string.Join(", ", s_Names.Sides.Select(kv => $"{kv.Key} {string.Join("/", kv.Value.Select(x => x.Count))}"))})";

        /// <summary>The optional server-only real names file; any problem means the invented names.</summary>
        static RealNames LoadNames()
        {
            var path = NamesPath;
            try
            {
                if (!File.Exists(path)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement; var n = new RealNames { Path = path };
                foreach (var x in r.GetProperty("real").EnumerateArray())
                    n.Real.Add((x[0].GetUInt32(), (float)x[1].GetDouble(), (float)x[2].GetDouble(), x[3].GetString()));
                foreach (var x in r.GetProperty("people").EnumerateArray())
                {
                    var name = x[0].GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    n.People.Add(new Person
                    {
                        Name = name, Look = x[1].ValueKind == JsonValueKind.Array ? ReadLook(x[1]) : null, Darktide = (x[2].GetInt32() & 1) != 0,
                        Killers = x.GetArrayLength() > 3 && x[3].ValueKind == JsonValueKind.Array ? x[3].EnumerateArray().Select(k => k.GetString()).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray() : null,
                    });
                }
                if (r.TryGetProperty("dtKillers", out var dk))
                    foreach (var x in dk.EnumerateArray()) { var k = x.GetString(); if (!string.IsNullOrWhiteSpace(k)) n.DtKillers.Add(k); }
                if (r.TryGetProperty("dtSites", out var ds))
                    foreach (var x in ds.EnumerateArray())
                        n.DtSites.Add(new Site { Cell = x[0].GetUInt32(), X = (float)x[1].GetDouble(), Y = (float)x[2].GetDouble(), Z = (float)x[3].GetDouble(), Lv = 100, Tier = 6, Kind = KIND_DT, W = 1 });
                if (r.TryGetProperty("sites", out var st))
                    foreach (var p in st.EnumerateObject())
                    {
                        if (!p.Value.TryGetProperty("sides", out var sd)) continue;
                        var sides = sd.EnumerateArray().Select(a => (IList<string>)a.EnumerateArray().Select(e => e.GetString())
                            .Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()).ToList();
                        if (sides.Count(x => x.Count > 0) < 2) continue;     // a battlefield needs two sides
                        n.Sides[p.Name] = sides;
                        foreach (var x in sides) foreach (var nm in x) n.Reserved.Add(nm);
                    }
                for (int i = 0; i < n.People.Count; i++)
                {
                    (n.People[i].Darktide ? n.DtIdx : n.OtherIdx).Add(i);
                    n.ByName.TryAdd(n.People[i].Name, n.People[i]);
                }
                if (n.People.Count == 0) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules: {path} has no people; invented names"); return null; }
                return n;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules: names file {path} not read ({e.Message}); invented names"); return null; }
        }

        static Data LoadData()
        {
            using var rs = typeof(CorpseCapsules).Assembly.GetManifestResourceStream("RevivalGuard.CorpseCapsules.json")
                           ?? throw new InvalidOperationException("embedded CorpseCapsules.json missing");
            using var doc = JsonDocument.Parse(rs);
            var r = doc.RootElement; var d = new Data();
            d.Target = r.GetProperty("target").GetInt32();
            d.Shares = r.GetProperty("shares").EnumerateArray().Select(s => s.GetDouble()).ToArray();
            d.Sites = d.Shares.Select(_ => new List<Site>()).ToArray(); d.Cum = new double[d.Shares.Length][];
            foreach (var p in r.GetProperty("tierProfiles").EnumerateObject()) d.TierProfile[int.Parse(p.Name)] = p.Value.GetUInt32();
            foreach (var x in r.GetProperty("looks").EnumerateArray()) d.Looks.Add(ReadLook(x));
            double c = 0; d.LookCum = d.Looks.Select(l => c += Math.Sqrt(Math.Max(1, l.Seen))).ToArray();
            foreach (var x in r.GetProperty("real").EnumerateArray())
            {
                var lk = new Look
                {
                    Setup = x.GetProperty("setup").GetUInt32(), MTable = x.GetProperty("mt").GetUInt32(), Pal = x.GetProperty("pal").GetUInt32(),
                    Parts = x.GetProperty("parts").EnumerateArray().Select(a => ((byte)a[0].GetInt32(), a[1].GetUInt32())).ToArray(),
                    Tex = x.GetProperty("tex").EnumerateArray().Select(a => ((byte)a[0].GetInt32(), a[1].GetUInt32(), a[2].GetUInt32())).ToArray(),
                    Subs = x.GetProperty("subs").EnumerateArray().Select(a => (a[0].GetUInt32(), (ushort)a[1].GetInt32(), (ushort)a[2].GetInt32())).ToArray(),
                };
                d.Real.Add(new Real
                {
                    Name = x.GetProperty("name").GetString(), Cell = x.GetProperty("cell").GetUInt32(), Look = lk,
                    Pos = x.GetProperty("pos").EnumerateArray().Select(a => (float)a.GetDouble()).ToArray(),
                    Items = x.GetProperty("items").EnumerateArray().Select(a => a.GetUInt32()).ToArray(),
                });
            }
            foreach (var x in r.GetProperty("killers").EnumerateArray()) d.Killers.Add(x.GetString());
            foreach (var x in r.GetProperty("sites").EnumerateArray())
            {
                var s = new Site
                {
                    Cell = x[0].GetUInt32(), X = (float)x[1].GetDouble(), Y = (float)x[2].GetDouble(), Z = (float)x[3].GetDouble(),
                    Lv = x[4].GetInt32(), Tier = Math.Clamp(x[5].GetInt32(), 1, 8), W = x[6].GetDouble(), Killer = x[7].GetInt32(), Kind = x[8].GetInt32(),
                };
                if (s.Kind >= 0 && s.Kind < d.Sites.Length) d.Sites[s.Kind].Add(s);
            }
            for (int i = 0; i < d.Sites.Length; i++) { double t = 0; d.Cum[i] = d.Sites[i].Select(s => t += s.W).ToArray(); }
            // who could have killed a corpse where: each landblock's creatures, from its own sites
            d.LocalMonsters = CorpseKillerRule.LocalMonsters(d.Sites.SelectMany(l => l).Where(s => s.Killer >= 0 && s.Killer < d.Killers.Count)
                .Select(s => (s.Cell, s.Kind, d.Killers[s.Killer])));
            foreach (var k in d.Killers) if (k != CorpseKillerRule.PK_LINE && k != CorpseKillerRule.MISADVENTURE) d.Monsters.Add(k);
            var pools = r.GetProperty("pools");
            List<Item> Pool(JsonElement a) => a.EnumerateArray().Select(e => new Item { Wcid = e[0].GetUInt32(), W = e[1].GetInt32(), Min = e.GetArrayLength() > 2 ? e[2].GetInt32() : 1, Max = e.GetArrayLength() > 3 ? e[3].GetInt32() : 1 }).ToList();
            d.Common = Pool(pools.GetProperty("common")); d.Uncommon = Pool(pools.GetProperty("uncommon")); d.Valuable = Pool(pools.GetProperty("valuable"));
            foreach (var p in pools.GetProperty("byLandblock").EnumerateObject()) d.ByLb[Convert.ToUInt16(p.Name, 16)] = Pool(p.Value);
            foreach (var x in r.GetProperty("names").EnumerateArray()) d.Names.Add(x.GetString());
            if (r.TryGetProperty("named", out var nd))
                foreach (var x in nd.EnumerateArray())
                {
                    var ns = new NamedSite
                    {
                        Id = x.GetProperty("id").GetString(), Kind = x.GetProperty("kind").GetInt32(), Fill = x.GetProperty("fill").GetInt32(),
                        Cap = x.GetProperty("cap").GetInt32(), Spacing = (float)x.GetProperty("spacing").GetDouble(),
                    };
                    if (ns.Kind < CorpseKillerRule.KIND_NAMED || ns.Kind >= d.Sites.Length || d.Sites[ns.Kind].Count == 0) continue;
                    d.Named.Add(ns); d.NamedByKind[ns.Kind] = ns;
                }
            return d;
        }

        static Look ReadLook(JsonElement x) => new Look
        {
            Setup = x[0].GetUInt32(), MTable = x[1].GetUInt32(), Pal = x[2].GetUInt32(),
            Parts = x[3].EnumerateArray().Select(a => ((byte)a[0].GetInt32(), a[1].GetUInt32())).ToArray(),
            Tex = x[4].EnumerateArray().Select(a => ((byte)a[0].GetInt32(), a[1].GetUInt32(), a[2].GetUInt32())).ToArray(),
            Subs = x[5].EnumerateArray().Select(a => (a[0].GetUInt32(), (ushort)a[1].GetInt32(), (ushort)a[2].GetInt32())).ToArray(),
            Seen = x[6].GetInt32(),
        };

        // ------------------------------------------------------------ the tick (world thread)
        internal static void Tick()
        {
            double now = Now;
            if (now < s_NextTick) return;
            s_NextTick = now + 5;
            if (s_Data == null) return;
            bool on = On;
            if (!on)
            {
                if (s_WasOn) { ClearAll("switched off"); s_WasOn = false; }
                return;
            }
            s_WasOn = true;
            if (!s_RealMade) { MakeReal(); s_RealMade = true; if (!s_Restored) { s_Restored = true; Restore(); } }
            // no expiry: a generated corpse stays until someone loots it
            if (now > s_NextSave) { s_NextSave = now + 300; Save(); }
            if (s_Paused) return;
            TopUpNamed(now);
            int want = Target, made = 0;
            while (made < 25 && Generated() < want)
            {
                bool initial = s_Made < want;
                if (!initial)
                {
                    // replacements for looted corpses: at most RefillPerHour an hour, so the loot that enters the world is capped
                    while (s_Refills.Count > 0 && s_Refills.Peek() < now - 3600) s_Refills.Dequeue();
                    if (s_Refills.Count >= RefillPerHour) break;
                }
                var r = NewRecord(now, initial);
                if (r == null) break;
                if (!initial) s_Refills.Enqueue(now);
                made++;
            }
        }

        static readonly Queue<double> s_Refills = new Queue<double>();
        static readonly Dictionary<ushort, double> s_LbCool = new Dictionary<ushort, double>();
        static bool s_Restored; static double s_NextSave; static int s_RestoredN;
        static string StatePath => Path.Combine(ModFolder.Sub("CorpseCapsules"), "state.json");

        /// <summary>A record whose corpse lost an item to a player (or emptied): not kept over a restart.</summary>
        static bool Taken(Rec r) => r.Rolled && r.RolledN >= 0 && (r.Corpse != null ? r.Corpse.Inventory.Count : r.Held.Count) < r.RolledN;

        /// <summary>The standing generated corpses, so they outlive a restart.</summary>
        static void Save()
        {
            try
            {
                List<object> rows;
                lock (s_Lock)
                    rows = s_Recs.Values.Where(r => !r.IsReal && r.Site != null && !r.Gone && !Taken(r))
                        .Select(r => (object)new object[] { r.Name, r.OwnLook ? -1 : r.LookIdx, r.DtSite ? KIND_DT : r.Site.Kind, r.DtSite ? r.DtIdx : r.SiteIdx,
                            r.Cell, r.Level, r.Tier, r.Killer, Math.Round(r.Born), r.Qw, r.Qz }).ToList();
                var tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new { saved = Now, rows }));
                File.Move(tmp, StatePath, true);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules: state not saved ({e.Message})"); }
        }

        static void Restore()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                int bad = 0, relabeled = 0, renamed = 0;
                var saved = new HashSet<string>(doc.RootElement.GetProperty("rows").EnumerateArray().Select(x => x[0].GetString() ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var x in doc.RootElement.GetProperty("rows").EnumerateArray())
                {
                    string name = x[0].GetString(); int lookIdx = x[1].GetInt32(), kind = x[2].GetInt32(), idx = x[3].GetInt32();
                    uint cell = x[4].GetUInt32();
                    Site site = kind == KIND_DT ? (s_Names?.DtSites != null && idx >= 0 && idx < s_Names.DtSites.Count ? s_Names.DtSites[idx] : null)
                              : kind >= 0 && kind < s_Data.Sites.Length && idx >= 0 && idx < s_Data.Sites[kind].Count ? s_Data.Sites[kind][idx] : null;
                    if (site == null || site.Cell != cell || LiveName(name)) { bad++; continue; }     // the data changed, or a character took the name
                    Look look = lookIdx >= 0 && lookIdx < s_Data.Looks.Count ? s_Data.Looks[lookIdx] : null;
                    if (lookIdx < 0) look = s_Names?.People.FirstOrDefault(pp => pp.Name == name)?.Look;
                    var r = new Rec
                    {
                        Name = name, Look = look ?? PickLook(), OwnLook = lookIdx < 0 && look != null, Site = site, DtSite = kind == KIND_DT,
                        Cell = site.Cell, X = site.X, Y = site.Y, Z = site.Z, Level = x[5].GetInt32(), Tier = x[6].GetInt32(),
                        Killer = x[7].ValueKind == JsonValueKind.String ? x[7].GetString() : null, Born = x[8].GetDouble(),
                        Qw = (float)x[9].GetDouble(), Qz = (float)x[10].GetDouble(), LookIdx = lookIdx,
                        SiteIdx = kind == KIND_DT ? -1 : idx, DtIdx = kind == KIND_DT ? idx : -1,
                    };
                    if (r.Look != null && r.LookIdx < 0 && !r.OwnLook) r.LookIdx = s_Data.Looks.IndexOf(r.Look);
                    r.Level = CorpseKillerRule.SiteLevel(KindOf(r), r.Level, ShieldLevel);
                    var person = PersonNamed(name);
                    var named = NamedOf(r); var sides = SidesOf(named);
                    if (named != null)
                    {
                        // a named site keeps its own sides: a victim on neither side takes a free one, and a killer not from the other side is chosen again
                        if (sides != null && CorpseKillerRule.SideOf(name, sides) < 0)
                        {
                            var v = SideVictim(named, sides, saved);
                            if (v != null)
                            {
                                saved.Add(v.Name); name = r.Name = v.Name; person = v; renamed++;
                                if (r.OwnLook || r.LookIdx < 0) { r.Look = v.Look ?? PickLook(); r.OwnLook = v.Look != null; r.LookIdx = r.OwnLook ? -1 : s_Data.Looks.IndexOf(r.Look); }
                            }
                        }
                        if (sides != null && !CorpseKillerRule.OtherSide(name, r.Killer, sides)) r.Killer = CorpseKillerRule.PK_LINE;   // KillerFor below
                    }
                    else if (s_Names != null && (person?.Darktide != true || s_Names.Reserved.Contains(name)) && s_Names.DtIdx.Count > 0)
                    {
                        var dt = PickPerson(darktideOnly: true, avoid: saved);
                        if (dt != null)
                        {
                            saved.Add(dt.Name); name = r.Name = dt.Name; person = dt; renamed++;
                            if (r.OwnLook || r.LookIdx < 0) { r.Look = dt.Look ?? PickLook(); r.OwnLook = dt.Look != null; r.LookIdx = r.OwnLook ? -1 : s_Data.Looks.IndexOf(r.Look); }
                            if (r.Killer != null && !s_Data.Monsters.Contains(r.Killer) && r.Killer != CorpseKillerRule.MISADVENTURE)
                                r.Killer = SiteKiller(r) ?? CorpseKillerRule.PK_LINE;   // a player: chosen again for this victim
                        }
                    }
                    bool fits = Plausible(r, r.Killer);
                    if (r.Killer == CorpseKillerRule.PK_LINE || r.Killer == null || !fits)
                    {
                        var k = KillerFor(r, person, fits ? r.Killer : SiteKiller(r));
                        if (!fits && k != r.Killer) relabeled++;
                        r.Killer = k;
                    }
                    lock (s_Lock)
                    {
                        if (s_NamesInUse.Contains(name)) { bad++; continue; }
                        r.Id = s_NextId++; Add(r); s_NamesInUse.Add(name); s_Made++; s_RestoredN++;
                    }
                }
                Mod.Log.Info($"[RevivalGuard] CorpseCapsules: {s_RestoredN} standing corpses kept from before the restart ({bad} dropped: data or names changed; {relabeled} killers that could not have killed them there replaced; {renamed} given a Darktide name)");
                s_Relabeled = relabeled; s_Renamed = renamed;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules: state not restored ({e.Message}); a fresh set"); }
        }

        static int Generated() { lock (s_Lock) return s_Recs.Values.Count(r => !r.IsReal && NamedOf(r) == null); }   // named sites are on top (TopUpNamed)
        static bool Loaded(ushort lb) { try { return LandblockManager.IsLoaded(new LandblockId((uint)lb << 16 | 0xFFFF)); } catch { return true; } }

        static void MakeReal()
        {
            lock (s_Lock)
            {
                foreach (var re in s_Data.Real)
                {
                    var r = new Rec
                    {
                        Id = s_NextId++, IsReal = true, Name = re.Name, Look = re.Look, Cell = re.Cell, X = re.Pos[0], Y = re.Pos[1], Z = re.Pos[2],
                        Rot = re.Pos.Length >= 7 ? new[] { re.Pos[3], re.Pos[4], re.Pos[5], re.Pos[6] } : null, RealItems = re.Items, Born = Now,
                    };
                    var near = s_Data.Sites.SelectMany(x => x).Where(s => s.Cell >> 16 == re.Cell >> 16).OrderBy(s => (s.X - r.X) * (s.X - r.X) + (s.Y - r.Y) * (s.Y - r.Y)).FirstOrDefault();
                    r.Level = near?.Lv ?? 50; r.Tier = near?.Tier ?? 3; r.Killer = near != null ? s_Data.Killers[near.Killer] : null;
                    r.Killer = KillerFor(r, null, r.Killer);
                    // its real name from the server-only file, unless a character on this shard has it
                    var real = s_Names?.Real.FirstOrDefault(x => x.cell == re.Cell && Math.Abs(x.x - r.X) < 0.05f && Math.Abs(x.y - r.Y) < 0.05f).name;
                    if (real != null && !LiveName(real)) r.Name = real;      // a character's two corpses share it
                    s_NamesInUse.Add(r.Name);
                    Add(r);
                }
            }
        }

        static void Add(Rec r)
        {
            s_Recs[r.Id] = r;
            if (!s_ByLb.TryGetValue(r.Lb, out var l)) s_ByLb[r.Lb] = l = new List<Rec>();
            l.Add(r);
        }

        /// <summary>Drop a record that is not made (or whose corpse is gone): its held items are destroyed.</summary>
        static void Forget(Rec r)
        {
            s_Recs.Remove(r.Id);
            if (s_ByLb.TryGetValue(r.Lb, out var l)) { l.Remove(r); if (l.Count == 0) s_ByLb.Remove(r.Lb); }
            s_NamesInUse.Remove(r.Name);
            foreach (var it in r.Held) { try { it.Destroy(); } catch { } }
            r.Held.Clear();
        }

        // ------------------------------------------------------------ a new record on a site in an unloaded landblock
        static string KillerFor(Rec r, Person person, string fallback)
        {
            bool pkOk = PkOk(r);
            // a named site: someone from the other side; without its sides, the PK rules below
            var sides = SidesOf(NamedOf(r));
            if (pkOk && sides != null)
            {
                var k = CorpseKillerRule.OtherSideKiller(r.Name, sides, n => !LiveName(n), s_Rng);
                if (k != null) return k;
            }
            // a Darktide victim on a hunting spot in the open world
            bool dtPk = person?.Darktide == true && (person.Killers?.Length > 0 || s_Rng.NextDouble() < DT_OPEN_PK);
            // the placeholder line is never shown: a site that says "a player killer" gets a real name too
            bool placeholder = fallback == CorpseKillerRule.PK_LINE;
            if (pkOk && (CorpseKillerRule.PkKind(KindOf(r)) || dtPk || placeholder))
            {
                var pk = PkName(r, person);
                if (pk != null) return pk;
            }
            if (!placeholder && Plausible(r, fallback)) return fallback;
            return CorpseKillerRule.Replacement(placeholder ? null : fallback, Local(r.Lb), pkOk, () => PkName(r, person), s_Rng);
        }

        /// <summary>A real Darktide PK for this corpse (server-only file), or null.</summary>
        static string PkName(Rec r, Person person)
        {
            var all = s_Names?.DtKillers;
            if (all == null || all.Count == 0) all = s_Data?.Names;   // no server names file: the embedded names
            if (all == null || all.Count == 0) return null;
            var fought = person?.Killers?.Where(k => !k.Equals(r.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fought != null && fought.Length > 0) return fought[s_Rng.Next(fought.Length)];
            for (int tries = 0; tries < 12; tries++)
            {
                var k = all[s_Rng.Next(all.Count)];
                if (!k.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && !LiveName(k)) return k;
            }
            int start = s_Rng.Next(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var k = all[(start + i) % all.Count];
                if (!k.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && !LiveName(k)) return k;
            }
            return null;
        }

        static int ShieldLevel { get { try { return (int)Tunables.D("newcomer.shield_level", 5); } catch { return 5; } } }
        static int KindOf(Rec r) => r.DtSite ? KIND_DT : r.Site?.Kind ?? -1;
        static bool PkOk(Rec r) => CorpseKillerRule.PkPossible(KindOf(r), r.Cell, r.Level, ShieldLevel);
        static string[] Local(ushort lb) => s_Data.LocalMonsters.TryGetValue(lb, out var l) ? l : Array.Empty<string>();
        static bool Plausible(Rec r, string killer) =>
            CorpseKillerRule.Plausible(killer, KindOf(r), r.Cell, r.Level, ShieldLevel, Local(r.Lb), s_Data.Monsters);
        static string SiteKiller(Rec r) => r.DtSite || r.Site == null ? CorpseKillerRule.PK_LINE
            : r.Site.Killer >= 0 && r.Site.Killer < s_Data.Killers.Count ? s_Data.Killers[r.Site.Killer] : null;
        static int s_Relabeled, s_Renamed, s_NonDtPicks;
        const double DT_OPEN_PK = 0.33;

        static Person PersonNamed(string name) => name != null && s_Names != null && s_Names.ByName.TryGetValue(name, out var p) ? p : null;

        // ------------------------------------------------------------ named sites (NAMED above)
        static NamedSite NamedOf(Rec r) => r != null && !r.DtSite && r.Site != null && s_Data.NamedByKind.TryGetValue(r.Site.Kind, out var n) ? n : null;
        static NamedSite NamedOf(Site s) => s != null && s_Data.NamedByKind.TryGetValue(s.Kind, out var n) ? n : null;
        static IList<IList<string>> SidesOf(NamedSite n) => n != null && s_Names != null && s_Names.Sides.TryGetValue(n.Id, out var s) ? s : null;

        /// <summary>A free victim from a named site's sides, from the side with fewer lying there now</summary>
        static Person SideVictim(NamedSite n, IList<IList<string>> sides, ISet<string> avoid = null)
        {
            var standing = new int[sides.Count];
            lock (s_Lock)
                foreach (var o in s_Recs.Values)
                    if (!o.IsReal && NamedOf(o) == n) { int s = CorpseKillerRule.SideOf(o.Name, sides); if (s >= 0) standing[s]++; }
            bool FreeName(string nm)
            {
                if (avoid != null && avoid.Contains(nm)) return false;
                lock (s_Lock) if (s_NamesInUse.Contains(nm)) return false;
                return !LiveName(nm);
            }
            var v = CorpseKillerRule.PickSideVictim(sides, standing, FreeName, s_Rng);
            return v == null ? null : PersonNamed(v) ?? new Person { Name = v, Darktide = true };
        }

        /// <summary>One of a named site's free sites (in an unloaded landblock) and a victim for it.</summary>
        static (Site site, int idx, Person who) NamedPick(NamedSite n)
        {
            var d = s_Data;
            for (int tries = 0; tries < 12; tries++)
            {
                int si = PickIdx(d.Cum[n.Kind]); var s = si >= 0 ? d.Sites[n.Kind][si] : null;
                if (!Free(s)) continue;
                var sides = SidesOf(n);
                var who = sides != null ? SideVictim(n, sides) : null;
                return (s, si, who ?? PickPerson());      // no sides (no names file) or nobody free: as anywhere
            }
            return (null, -1, null);
        }

        /// <summary>Fill the named sites to their own count, on top of the target.</summary>
        static void TopUpNamed(double now)
        {
            foreach (var n in s_Data.Named)
            {
                for (int made = 0; made < 10; made++)
                {
                    int standing; lock (s_Lock) standing = s_Recs.Values.Count(o => !o.IsReal && NamedOf(o) == n);
                    if (standing >= n.Fill) { s_NamedFilled.Add(n.Id); break; }
                    bool initial = !s_NamedFilled.Contains(n.Id);
                    if (!initial)
                    {
                        while (s_Refills.Count > 0 && s_Refills.Peek() < now - 3600) s_Refills.Dequeue();
                        if (s_Refills.Count >= RefillPerHour) return;
                    }
                    var (site, idx, who) = NamedPick(n);
                    if (site == null || who == null) break;
                    if (NewRecord(now, initial, at: site, atIdx: idx, who: who) == null) break;
                    if (!initial) s_Refills.Enqueue(now);
                }
            }
        }
        static readonly HashSet<string> s_NamedFilled = new HashSet<string>(StringComparer.Ordinal);

        static Rec NewRecord(double now, bool initial, Site at = null, Position here = null, Person who = null, int atIdx = -1)
        {
            var d = s_Data;
            var person = who ?? PickPerson();
            if (person == null) return null;
            var name = person.Name;
            Site site = at; bool dt = false; int dtIdx = -1, siteIdx = at != null ? atIdx : -1;
            if (site == null && here == null)
            {
                // a Darktide name goes first to where Darktide player corpses lay (the PK places), a few at once
                var dts = s_Names?.DtSites;
                if (person.Darktide && dts != null && dts.Count > 0 && s_Rng.NextDouble() < 0.7)
                {
                    int standing; lock (s_Lock) standing = s_Recs.Values.Count(o => o.DtSite);
                    for (int tries = 0; standing < DT_MAX && tries < 8 && site == null; tries++)
                    {
                        int di = s_Rng.Next(dts.Count); var s = dts[di];
                        if (Free(s)) { site = s; dt = true; dtIdx = di; }
                    }
                }
                // Ayan Baqur, the Direlands PK town: a Darktide name goes there first now and then
                if (site == null && person.Darktide && d.Sites.Length > KIND_AYAN && d.Sites[KIND_AYAN].Count > 0 && s_Rng.NextDouble() < 0.15)
                    for (int tries = 0; tries < 8 && site == null; tries++)
                    {
                        int si = PickIdx(d.Cum[KIND_AYAN]); var s = si >= 0 ? d.Sites[KIND_AYAN][si] : null;
                        if (Free(s)) { site = s; siteIdx = si; }
                    }
                for (int tries = 0; tries < 40 && site == null; tries++)
                {
                    int kind = PickKind();
                    if (kind < 0) return null;
                    int si = PickIdx(d.Cum[kind]); var s = si >= 0 ? d.Sites[kind][si] : null;
                    if (Free(s)) { site = s; siteIdx = si; }
                }
                if (site == null) return null;
            }
            var r = new Rec { Name = name, Look = person.Look ?? PickLook(), OwnLook = person.Look != null, Site = site, DtSite = dt, DtIdx = dtIdx, SiteIdx = siteIdx };
            if (!r.OwnLook) r.LookIdx = s_Data.Looks.IndexOf(r.Look);
            if (dt)
            {
                var near = d.Sites.SelectMany(x => x).Where(s => s.Cell >> 16 == site.Cell >> 16).FirstOrDefault();
                r.Cell = site.Cell; r.X = site.X; r.Y = site.Y; r.Z = site.Z;
                r.Level = Math.Clamp((int)Math.Round((near?.Lv ?? 120) * (0.8 + 0.6 * s_Rng.NextDouble())), 20, 275);
                r.Tier = near?.Tier ?? 6; r.Killer = "a player killer";
            }
            else if (here != null)
            {
                r.Cell = here.Cell; r.X = here.PositionX; r.Y = here.PositionY; r.Z = here.PositionZ;
                var near = d.Sites.SelectMany(x => x).Where(s => s.Cell >> 16 == here.Cell >> 16).FirstOrDefault();
                r.Level = near?.Lv ?? 60; r.Tier = near?.Tier ?? 4; r.Killer = near != null ? d.Killers[near.Killer] : null;
            }
            else
            {
                r.Cell = site.Cell; r.X = site.X; r.Y = site.Y; r.Z = site.Z;
                r.Level = Math.Clamp((int)Math.Round(site.Lv * (0.9 + 0.35 * s_Rng.NextDouble())), 1, 275);
                r.Tier = site.Tier; r.Killer = d.Killers[site.Killer];
                r.Level = CorpseKillerRule.SiteLevel(site.Kind, r.Level, ShieldLevel);   // the hub has no creatures: PK-able victims
            }
            r.Killer = KillerFor(r, person, r.Killer);
            double a = s_Rng.NextDouble() * Math.PI * 2;
            r.Qw = (float)Math.Cos(a / 2); r.Qz = (float)Math.Sin(a / 2);
            // at start the world has been "lived in" for a while (retail's 5 min a level, at least an hour, as an age only)
            r.Born = initial ? now - s_Rng.NextDouble() * Math.Max(3600, r.Level * 300) : now;
            r.Expires = 0;
            lock (s_Lock)
            {
                r.Id = s_NextId++;
                Add(r);
                s_NamesInUse.Add(name);
                if (!initial && here == null) s_Replaced++;
                s_Made++;
            }
            return r;
        }

        static int PickKind()
        {
            var d = s_Data; double tot = 0;
            for (int i = 0; i < d.Sites.Length; i++) if (d.Sites[i].Count > 0) tot += d.Shares[i];
            if (tot <= 0) return -1;
            double x = s_Rng.NextDouble() * tot;
            for (int i = 0; i < d.Sites.Length; i++) { if (d.Sites[i].Count == 0) continue; if ((x -= d.Shares[i]) <= 0) return i; }
            for (int i = d.Sites.Length - 1; i >= 0; i--) if (d.Sites[i].Count > 0) return i;
            return -1;
        }

        static Site Pick(List<Site> l, double[] cum) { int i = PickIdx(cum); return i < 0 ? null : l[i]; }

        static int PickIdx(double[] cum)
        {
            if (cum == null || cum.Length == 0) return -1;
            double x = s_Rng.NextDouble() * cum[^1];
            int i = Array.BinarySearch(cum, x); if (i < 0) i = ~i;
            return Math.Min(i, cum.Length - 1);
        }

        static Look PickLook()
        {
            var d = s_Data; double x = s_Rng.NextDouble() * d.LookCum[^1];
            int i = Array.BinarySearch(d.LookCum, x); if (i < 0) i = ~i;
            return d.Looks[Math.Min(i, d.Looks.Count - 1)];
        }

        /// <summary>Who lies here: a real retail character from the server-only names file, a Darktide one first, else an invented name.</summary>
        static Person PickPerson(bool darktideOnly = false, ISet<string> avoid = null)
        {
            var n = s_Names;
            if (n != null)
            {
                bool Usable(int i)
                {
                    var nm = n.People[i].Name;
                    if (avoid != null && avoid.Contains(nm)) return false;
                    if (n.Reserved.Contains(nm)) return false;        // a named site's side: lies only there
                    lock (s_Lock) if (s_NamesInUse.Contains(nm)) return false;
                    return !LiveName(nm);
                }
                int i = CorpseKillerRule.PickVictim(n.DtIdx, darktideOnly ? null : n.OtherIdx, Usable, s_Rng, out bool fellBack);
                if (i < 0) return null;
                if (fellBack) s_NonDtPicks++;
                return n.People[i];
            }
            if (darktideOnly) return null;
            var names = s_Data.Names;
            for (int tries = 0; tries < 60; tries++)
            {
                var p = new Person { Name = names[s_Rng.Next(names.Count)] };
                lock (s_Lock) if (s_NamesInUse.Contains(p.Name)) continue;
                if (LiveName(p.Name)) continue;
                return p;
            }
            return null;
        }

        static bool LiveName(string n) { try { return PlayerManager.FindByName(n) != null; } catch { return false; } }

        /// <summary>A site a new record may take: not dropped, its landblock unloaded (nobody looking), no corpse within SPACING.</summary>
        static bool Free(Site s)
        {
            if (s == null || s.Bad) return false;
            var lb = (ushort)(s.Cell >> 16);
            if (Loaded(lb)) return false;
            lock (s_Lock)
            {
                if (s_LbCool.TryGetValue(lb, out var until) && Now < until) return false;     // looted here lately: not a farm
                if (!s_ByLb.TryGetValue(lb, out var l)) return true;
                var named = NamedOf(s);     // a named site: its own landblock cap and spacing (a dense battlefield)
                int cap = CorpseKillerRule.LandblockCap(s.Kind, (s.Cell & 0xFFFF) >= 0x100, named?.Cap ?? 0);
                if (l.Count(o => !o.IsReal) >= cap) return false;
                float sp = named?.Spacing ?? SPACING;
                return !l.Any(o => (o.X - s.X) * (o.X - s.X) + (o.Y - s.Y) * (o.Y - s.Y) + (o.Z - s.Z) * (o.Z - s.Z) < sp * sp);
            }
        }

        // ------------------------------------------------------------ made when the landblock loads
        internal static void OnLandblockLoaded(AceLandblock lb)
        {
            if (s_Data == null || !On) return;
            ushort id = (ushort)lb.Id.Landblock;
            lock (s_Lock) { if (!s_ByLb.ContainsKey(id)) return; }
            lb.EnqueueAction(new ActionEventDelegate(() =>
            {
                try { MakeIn(lb); }
                catch (Exception e) { TickGuard.Report("CorpseCapsules.Make", e); }
            }));
        }

        static void MakeIn(AceLandblock lb)
        {
            List<Rec> todo;
            lock (s_Lock)
            {
                if (!s_ByLb.TryGetValue((ushort)lb.Id.Landblock, out var l)) return;
                todo = l.Where(r => r.Corpse == null && !r.Gone).ToList();
            }
            foreach (var r in todo) Make(lb, r);
        }

        internal static bool Refuses(Corpse c)
        {
            if (c == null) return false;
            bool ours; lock (s_Lock) ours = s_ByGuid.ContainsKey(c.Guid.Full);
            if (!ours) return false;
            try { return !PropertyManager.GetBool(P_OPEN, false).Item; } catch { return true; }
        }

        static void Make(AceLandblock lb, Rec r)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pos = PlaceOf(lb, r);
            if (pos == null) { BadSite(r, "no ground"); return; }
            var corpse = WorldObjectFactory.CreateNewWorldObject(DatabaseManager.World.GetCachedWeenie("corpse")) as Corpse;
            if (corpse == null) return;
            var lk = r.Look;
            corpse.SetupTableId = lk.Setup;
            corpse.MotionTableId = lk.MTable;
            corpse.PaletteBaseDID = lk.Pal;
            corpse.Biota.PropertiesAnimPart = lk.Parts.Select(p => new PropertiesAnimPart { Index = p.i, AnimationId = p.g }).ToList();
            corpse.Biota.PropertiesTextureMap = lk.Tex.Select(t => new PropertiesTextureMap { PartIndex = t.i, OldTexture = t.o, NewTexture = t.n }).ToList();
            corpse.Biota.PropertiesPalette = lk.Subs.Select(s => new PropertiesPalette { SubPaletteId = s.p, Offset = s.o, Length = s.l }).ToList();
            corpse.Name = $"Corpse of {r.Name}";
            corpse.LongDesc = $"Killed by {(r.Killer == null || r.Killer == CorpseKillerRule.PK_LINE ? CorpseKillerRule.MISADVENTURE : r.Killer)}.";
            corpse.IsMonster = true;
            corpse.RemoveProperty(PropertyInt.Value);
            corpse.Location = pos;
            // the rest of its life, ticking only while its landblock is awake (ACE: 15 s once empty)
            corpse.TimeToRot = FOREVER;
            if (!r.Rolled) { Roll(r); r.Rolled = true; r.RolledN = r.Held.Count; }
            foreach (var it in r.Held) corpse.TryAddToInventory(it, burdenCheck: false);
            r.Held.Clear();
            if (!lb.AddWorldObject(corpse))
            {
                foreach (var g in corpse.Inventory.Keys.ToList()) if (corpse.TryRemoveFromInventory(g, out var it)) r.Held.Add(it);
                corpse.Destroy(false);
                BadSite(r, "placement refused");
                return;
            }
            lock (s_Lock) { r.Corpse = corpse; s_ByGuid[corpse.Guid.Full] = r; }
            sw.Stop();
            lock (s_Lock) { s_MakeMs += sw.Elapsed.TotalMilliseconds; s_MakeN++; s_MakeMax = Math.Max(s_MakeMax, sw.Elapsed.TotalMilliseconds); }
        }

        static Position PlaceOf(AceLandblock lb, Rec r)
        {
            var rot = r.Rot != null ? new Quaternion(r.Rot[1], r.Rot[2], r.Rot[3], r.Rot[0]) : new Quaternion(0, 0, r.Qz, r.Qw);
            if ((r.Cell & 0xFFFF) >= 0x100)
            {
                // a generated indoor site's cell may be a neighbour's: put it in the EnvCell that really holds the point, or drop the site
                if (!r.IsReal && !(LScape.get_landcell(r.Cell) is EnvCell ec && ec.point_in_cell(new Vector3(r.X, r.Y, r.Z + 0.3f))))
                {
                    var hit = lb.PhysicsLandblock?.get_envcells()?.FirstOrDefault(c => c.point_in_cell(new Vector3(r.X, r.Y, r.Z + 0.3f)));
                    if (hit == null) return null;
                    r.Cell = hit.ID;
                }
                return new Position(r.Cell, new Vector3(r.X, r.Y, r.Z), rot);
            }
            var cell = (r.Cell & 0xFFFF0000) | (uint)((int)(r.X / 24) * 8 + (int)(r.Y / 24) + 1);
            bool gridProven = r.Site != null && (r.Site.Kind == KIND_PK || r.Site.Kind == KIND_LAWN || r.Site.Kind == KIND_AYAN || NamedOf(r) != null);   // gen.py: 4 m clear of any building
            if (LScape.get_landcell(cell) is SortCell sc && sc.has_building() && !r.IsReal && !gridProven) return null;
            float z = r.IsReal ? r.Z : lb.PhysicsLandblock.GetZ(new Vector3(r.X, r.Y, 0));
            return new Position(cell, new Vector3(r.X, r.Y, z + 0.02f), rot);
        }

        static void BadSite(Rec r, string why)
        {
            lock (s_Lock)
            {
                s_BadSites++;
                if (r.Site != null) r.Site.Bad = true;
                if (r.IsReal) r.Gone = true; else Forget(r);
            }
            Mod.Log.Warn($"[RevivalGuard] CorpseCapsules: site {r.Cell:X8} {r.X:0.0} {r.Y:0.0} {r.Z:0.0} dropped ({why})");
        }

        // ------------------------------------------------------------ contents (the dead player's death drop, by area tier)
        static void Roll(Rec r)
        {
            if (r.IsReal && r.RealItems != null && r.RealItems.Length > 0)
            {
                foreach (var w in r.RealItems) { var it = Fixed(new Item { Wcid = w, Min = 1, Max = 1 }); if (it != null) r.Held.Add(it); }
                return;
            }
            var got = Contents(r.Level, r.Tier, r.Lb, r.Site?.Kind == KIND_HUB);
            DarktideFeel.SignAll(got, r.Name);   // rg_signed_loot: his gear as the tinkers of his day left it
            r.Held.AddRange(got);
        }

        /// <summary>@darktidefeel roll: one magic loot item of a tier, as a corpse rolls it (needs the loaded data).</summary>
        internal static WorldObject LootForTest(int tier) => s_Data == null ? null : Loot(tier, TreasureItemCategory.MagicItem);

        internal static List<WorldObject> Contents(int level, int tier, ushort lb, bool noob = false)
        {
            var d = s_Data; var got = new List<WorldObject>();
            d.ByLb.TryGetValue(lb, out var local);
            WorldObject Leftover() => local != null && local.Count > 0 && s_Rng.NextDouble() < 0.5 ? Fixed(PickItem(local)) : Fixed(PickItem(d.Common));
            void Put(WorldObject wo, int cap)
            {
                if (wo == null) return;
                if ((wo.GetProperty(PropertyInt.Attuned) ?? 0) != 0 || (wo.GetProperty(PropertyInt.Bonded) ?? 0) != 0 || (cap > 0 && (wo.Value ?? 0) > cap))
                {
                    wo.Destroy();
                    if (cap > 0) { var l = Leftover(); if (l != null && (l.Value ?? 0) <= cap) got.Add(l); else l?.Destroy(); }
                    return;
                }
                got.Add(wo);
            }
            int capC = noob ? 300 : Math.Min(4000, 600 * tier), capU = Math.Min(8000, 1200 * tier);
            // retail death drop: floor(level/20) + 0..2 items over level 20 (fewer below), half the pyreals
            int n = level <= 10 ? 1 + s_Rng.Next(2) : level <= 20 ? 1 + s_Rng.Next(3) : Math.Min(10, level / 20 + s_Rng.Next(3));
            n = Math.Max(1, n);
            for (int i = 0; i < n; i++)
            {
                bool uncommon = !noob && s_Rng.NextDouble() < 0.20;
                double x = s_Rng.NextDouble();
                if (!uncommon)
                {
                    if (x < 0.20) Put(Loot(Math.Max(1, tier - 1), TreasureItemCategory.MagicItem), capC);
                    else if (x < 0.45) Put(Loot(tier, TreasureItemCategory.MundaneItem), capC);
                    else Put(Leftover(), capC);
                }
                else
                {
                    if (x < 0.35) Put(Loot(tier, TreasureItemCategory.MagicItem), capU);
                    else Put(Fixed(PickItem(d.Uncommon)), capU);
                }
            }
            if (!noob && s_Rng.NextDouble() < 0.02)  // about 1 in 50: something genuinely valuable
            {
                double x = s_Rng.NextDouble();
                if (x < 0.4) Put(Loot(Math.Min(8, tier + 2), TreasureItemCategory.MagicItem), 0);
                else if (x < 0.7) Put(Fixed(PickItem(d.Valuable)), 0);
                else Put(Pyreals(8000 + s_Rng.Next(12001)), 0);
            }
            if (level > 5 && s_Rng.NextDouble() < 0.75)
            {
                int cap = Math.Min(6000, 30 * level + 100);
                Put(Pyreals((int)(cap * (0.3 + 0.7 * s_Rng.NextDouble()))), 0);
            }
            return got;
        }

        static Item PickItem(List<Item> l)
        {
            if (l == null || l.Count == 0) return null;
            int tot = l.Sum(i => i.W), x = s_Rng.Next(Math.Max(1, tot));
            foreach (var i in l) if ((x -= i.W) < 0) return i;
            return l[^1];
        }

        static WorldObject Fixed(Item i)
        {
            if (i == null) return null;
            var wo = WorldObjectFactory.CreateNewWorldObject(i.Wcid);
            if (wo == null) return null;
            if ((wo.MaxStackSize ?? 1) > 1 && i.Max > 1) wo.SetStackSize(Math.Min(wo.MaxStackSize ?? 1, i.Min + s_Rng.Next(i.Max - i.Min + 1)));
            return wo;
        }

        static WorldObject Pyreals(int n)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(PYREAL);
            wo?.SetStackSize(Math.Clamp(n, 1, wo.MaxStackSize ?? 25000));
            return wo;
        }

        static WorldObject Loot(int tier, TreasureItemCategory cat)
        {
            if (!s_Data.TierProfile.TryGetValue(Math.Clamp(tier, 1, 8), out var id)) return null;
            var profile = DatabaseManager.World.GetCachedDeathTreasure(id);
            if (profile == null) return null;
            try { return LootGenerationFactory.CreateRandomLootObjects(profile, cat); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules: loot roll tier {tier} {cat}: {e.Message}"); return null; }
        }

        // ------------------------------------------------------------ hooks
        internal static void OnUnload(AceLandblock lb)
        {
            lock (s_Lock)
            {
                if (!s_ByLb.TryGetValue((ushort)lb.Id.Landblock, out var l)) return;
                foreach (var r in l)
                {
                    var c = r.Corpse;
                    if (c == null) continue;
                    foreach (var g in c.Inventory.Keys.ToList())
                        if (c.TryRemoveFromInventory(g, out var it)) r.Held.Add(it);
                    if (!r.IsReal && r.RolledN >= 0 && r.Held.Count < r.RolledN) s_LbCool[r.Lb] = Now + LB_COOL;   // partly looted
                    s_ByGuid.Remove(c.Guid.Full);
                    r.Corpse = null;
                }
            }
        }

        internal static void OnDestroy(WorldObject wo, bool fromUnload)
        {
            if (!(wo is Corpse) || fromUnload) return;
            lock (s_Lock)
            {
                if (!s_ByGuid.TryGetValue(wo.Guid.Full, out var r)) return;
                s_ByGuid.Remove(wo.Guid.Full);
                r.Corpse = null;
                s_Rotted++;
                if ((wo as Corpse).Inventory.Count == 0) { s_Looted++; if (!r.IsReal) s_LbCool[r.Lb] = Now + LB_COOL; }
                if (r.IsReal) r.Gone = true;     // back at the next restart
                else Forget(r);                  // the tick puts a new one in an unloaded landblock
            }
        }

        static void ClearAll(string why)
        {
            List<Corpse> made;
            lock (s_Lock)
            {
                made = s_Recs.Values.Where(r => r.Corpse != null).Select(r => r.Corpse).ToList();
                foreach (var r in s_Recs.Values.ToList()) { if (r.Corpse != null) { s_ByGuid.Remove(r.Corpse.Guid.Full); r.Corpse = null; } Forget(r); }
                s_RealMade = false; s_Made = 0; s_Refills.Clear(); s_NamedFilled.Clear();   // the next fill is a fresh start, not refills
            }
            foreach (var c in made)
            {
                var chain = new ActionChain();
                chain.AddAction(c, () => { try { if (!c.IsDestroyed) c.Destroy(); } catch (Exception e) { TickGuard.Report("CorpseCapsules.Clear", e); } });
                chain.EnqueueChain();
            }
            Mod.Log.Info($"[RevivalGuard] CorpseCapsules: cleared ({why}): {made.Count} made corpses removed");
        }

        // ------------------------------------------------------------ staff
        static void Tell(Session s, string t) => s?.Network?.EnqueueSend(new GameMessageSystemChat(t, ChatMessageType.Broadcast));

        static void Cmd(Session session, params string[] a)
        {
            var verb = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
            var p = session?.Player;
            switch (verb)
            {
                case "status": Tell(session, Status()); break;
                case "reload":
                    try { var nd = LoadData(); ClearAll("reload"); s_Data = nd; s_Names = LoadNames(); s_Paused = false; Tell(session, $"Corpse capsules: data reloaded ({NamesLine()}) and all records made again (they appear as landblocks load)."); }
                    catch (Exception e) { Tell(session, $"Corpse capsules: reload failed: {e.Message}"); }
                    break;
                case "clear": ClearAll("staff"); s_Paused = true; Tell(session, "Corpse capsules: cleared; none come back until @corpses reload, @corpses spawn or a restart."); break;
                case "spawn":
                    if (s_Data == null) { Tell(session, "no data"); break; }
                    if (a.Length > 1 && a[1] == "here" && p != null)
                    {
                        var who = a.Length > 2 ? PersonNamed(string.Join(" ", a.Skip(2))) : null;   // staff test: a named one
                        if (a.Length > 2 && who == null) { Tell(session, "no such person in the names file"); break; }
                        lock (s_Lock) if (who != null && s_NamesInUse.Contains(who.Name)) { Tell(session, $"{who.Name} already lies somewhere"); break; }
                        var r = NewRecord(Now, false, here: new Position(p.Location), who: who);
                        if (r == null) { Tell(session, "no name or site free"); break; }
                        var lb = p.CurrentLandblock;
                        lb?.EnqueueAction(new ActionEventDelegate(() => { try { Make(lb, r); } catch (Exception e) { TickGuard.Report("CorpseCapsules.Here", e); } }));
                        Tell(session, $"Corpse capsules: #{r.Id} Corpse of {r.Name} at your feet (staff test, never expires).");
                    }
                    else
                    {
                        int n = a.Length > 1 && int.TryParse(a[1], out var k) ? Math.Clamp(k, 1, 500) : 10, made = 0;
                        s_Paused = false;
                        for (int i = 0; i < n; i++) if (NewRecord(Now, false) != null) made++;
                        Tell(session, $"Corpse capsules: {made} new records in unloaded landblocks (they appear when someone goes there).");
                    }
                    break;
                case "near":
                    if (p == null) break;
                    var lbp = (ushort)(p.Location.Cell >> 16);
                    List<Rec> list;
                    lock (s_Lock) list = s_Recs.Values.OrderBy(r => r.Lb == lbp ? 0 : 1).ThenBy(r => Dist(p.Location, r)).Take(8).ToList();
                    foreach (var r in list) Tell(session, Line(r));
                    break;
                case "list":
                    lock (s_Lock) list = s_Recs.Values.Where(r => a.Length < 2 || (a[1] == "real" ? r.IsReal : r.Site?.Kind.ToString() == a[1])).Take(30).ToList();
                    foreach (var r in list) Tell(session, Line(r));
                    break;
                case "goto":
                    if (p == null || a.Length < 2 || !int.TryParse(a[1], out var id)) { Tell(session, "@corpses goto <id> (ids from @corpses near/list)"); break; }
                    Rec g;
                    lock (s_Lock) s_Recs.TryGetValue(id, out g);
                    if (g == null) { Tell(session, "no such record"); break; }
                    // stand back along +x looking at it (yaw 90: AC's forward +y turned to -x), so a camera behind sees it
                    float back = a.Length > 2 && float.TryParse(a[2], out var bk) ? Math.Clamp(bk, 0.5f, 20f) : (g.Cell & 0xFFFF) < 0x100 ? 5f : 2f;
                    var face = new Quaternion(0, 0, (float)Math.Sin(Math.PI / 4), (float)Math.Cos(Math.PI / 4));
                    var to = new Position(g.Cell, new Vector3(g.X + back, g.Y, g.Z + 0.5f), face);
                    if ((g.Cell & 0xFFFF) < 0x100)
                    {
                        float tx = Math.Clamp(g.X + back, 0.5f, 191.5f);
                        to = new Position((g.Cell & 0xFFFF0000) | (uint)((int)(tx / 24) * 8 + (int)(g.Y / 24) + 1), new Vector3(tx, g.Y, 0), face);
                        to.AdjustMapCoords();          // terrain height, as @tele does
                        to.PositionZ += 0.5f;
                    }
                    p.Teleport(to);
                    Tell(session, $"to {Line(g)}");
                    break;
                case "find":
                    var q = string.Join(" ", a.Skip(1));
                    lock (s_Lock) list = s_Recs.Values.Where(r => q.Length > 0 && r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(8).ToList();
                    if (list.Count == 0) Tell(session, "no corpse by that name");
                    foreach (var r in list) Tell(session, Line(r));
                    break;
                case "age":
                    // staff test clock: pretend this many hours passed (proves nothing expires; also runs out cooldowns)
                    double h = a.Length > 1 && double.TryParse(a[1], out var hh) ? Math.Clamp(hh, 0, 24 * 365) : 24;
                    int before = Generated();
                    s_Skew += h * 3600;
                    Tell(session, $"Corpse capsules: clock moved {h:0.#} h ahead (total {s_Skew / 3600:0.#} h). Generated standing {before}; " +
                                  $"under the old retail timer every corpse older than max(1 h, level x 5 min) x 1.5 would have gone. Run @corpses status after 10 s.");
                    break;
                case "budget":
                    if (s_Data == null) break;
                    int nb = a.Length > 1 && int.TryParse(a[1], out var kb) ? Math.Clamp(kb, 10, 2000) : 200;
                    Tell(session, Budget(nb, a.Length > 2 && int.TryParse(a[2], out var bk2) ? bk2 : -1));
                    break;
                default: Tell(session, "@corpses status | spawn [n|here] | clear | reload | near | list [0-6|real] | find <name> | goto <id> | budget [n] | age <hours>"); break;
            }
        }

        static double Dist(Position at, Rec r)
        {
            if ((at.Cell >> 16) != r.Lb) return 1e9 + Math.Abs((int)(at.Cell >> 24) - (int)(r.Cell >> 24)) * 192 + Math.Abs((int)((at.Cell >> 16) & 0xFF) - (int)((r.Cell >> 16) & 0xFF)) * 192;
            return Math.Sqrt((at.PositionX - r.X) * (at.PositionX - r.X) + (at.PositionY - r.Y) * (at.PositionY - r.Y));
        }

        static string KindName(int k) => k switch { 0 => "2017", 1 => "dungeon", 2 => "outdoor", 3 => "pk", 4 => "lawn", 5 => "daily", 7 => "townnet", 8 => "hub", 9 => "ayan",
            _ when s_Data != null && s_Data.NamedByKind.TryGetValue(k, out var n) => n.Id, _ => "darktide" };

        static string Line(Rec r)
        {
            string kind = r.IsReal ? "REAL" : r.Site == null ? "here" : KindName(r.Site.Kind);
            string state = r.Corpse != null ? $"made, {r.Corpse.Inventory.Count} items" : r.Gone ? "gone" : r.Rolled ? $"held {r.Held.Count}" : "unrolled";
            string left = $"lying {(Now - r.Born) / 3600:0.0} h, until looted";
            return $"#{r.Id} {kind} Corpse of {r.Name} lv {r.Level} t{r.Tier} {r.Cell:X8} [{r.X:0.0} {r.Y:0.0} {r.Z:0.0}] {state}, {left}, killer {r.Killer}, {(r.IsReal || r.OwnLook ? "own look" : "retail look")}";
        }

        static string Status()
        {
            if (s_Data == null) return "Corpse capsules: data MISSING";
            lock (s_Lock)
            {
                var recs = s_Recs.Values.ToList();
                int real = recs.Count(r => r.IsReal), made = recs.Count(r => r.Corpse != null), held = recs.Count(r => r.Corpse == null && r.Rolled);
                var kinds = recs.Where(r => !r.IsReal && r.Site != null).GroupBy(r => r.DtSite ? KIND_DT : r.Site.Kind).OrderBy(g => g.Key).Select(g => $"{KindName(g.Key)} {g.Count()}");
                long value = 0; int items = 0, rolled = 0;
                foreach (var r in recs)
                {
                    IEnumerable<WorldObject> its = r.Corpse != null ? r.Corpse.Inventory.Values : r.Held;
                    if (r.Rolled) rolled++;
                    foreach (var it in its) { items++; value += (long)(it.Value ?? 0); }
                }
                var lbs = recs.Select(r => r.Lb).Distinct().Count();
                var named = s_Data.Named.Select(n =>
                {
                    var at = recs.Where(r => !r.IsReal && NamedOf(r) == n).ToList(); var sd = SidesOf(n);
                    if (sd == null) return $"{n.Id} {at.Count}/{n.Fill} (no sides: names file)";
                    var by = Enumerable.Range(0, sd.Count).Select(i => at.Count(r => CorpseKillerRule.SideOf(r.Name, sd) == i));
                    return $"{n.Id} {at.Count}/{n.Fill} (sides {string.Join("/", by)}, killed by the other side {at.Count(r => CorpseKillerRule.OtherSide(r.Name, r.Killer, sd))}, most in one landblock {at.GroupBy(r => r.Lb).Select(g => g.Count()).DefaultIfEmpty(0).Max()}, cap {n.Cap})";
                }).ToList();
                return $"Corpse capsules {(On ? "ON" : "off")}{(s_Paused ? " (paused)" : "")}: {recs.Count - real} standing (target {Target}, named sites on top) + {real} real ({recs.Count(r => r.Gone)} real gone); " +
                       $"{string.Join(", ", kinds)}; named: {(named.Count == 0 ? "none" : string.Join("; ", named))}; in {lbs} landblocks; made now {made}, held {held}. Contents rolled {rolled}: {items} items worth {value:N0} " +
                       $"({(rolled > 0 ? value / rolled : 0):N0} a corpse). Since start: made {s_Made}, replaced {s_Replaced}, rotted {s_Rotted} (emptied {s_Looted}), expired {s_Expired} (none: no timer), kept over the restart {s_RestoredN}, refills this hour {s_Refills.Count}/{RefillPerHour}, landblocks cooling {s_LbCool.Count(kv => kv.Value > Now)}, test clock +{s_Skew / 3600:0.#} h, bad sites {s_BadSites}, killers replaced at restore {s_Relabeled}, killers that could not have killed them there now {recs.Count(r => !Plausible(r, r.Killer))}; " +
                       $"victims Darktide {recs.Count(r => !r.IsReal && PersonNamed(r.Name)?.Darktide == true)}, other {recs.Count(r => !r.IsReal && PersonNamed(r.Name)?.Darktide != true)} (Darktide pool ran out {s_NonDtPicks} times), renamed to Darktide at restore {s_Renamed}, PK line placeholders {recs.Count(r => r.Killer == CorpseKillerRule.PK_LINE)}; " +
                       $"making a corpse {(s_MakeN > 0 ? s_MakeMs / s_MakeN : 0):0.00} ms avg over {s_MakeN}, {s_MakeMax:0.0} ms max. Names: {NamesLine()}.";
            }
        }

        static string Budget(int n, int only = -1)
        {
            var vals = new List<long>(); int valuable = 0, itemsN = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                int kind = only >= 0 && only < s_Data.Sites.Length && s_Data.Sites[only].Count > 0 ? only : PickKind(); var s = Pick(s_Data.Sites[kind], s_Data.Cum[kind]);
                int lv = Math.Clamp((int)Math.Round(s.Lv * (0.9 + 0.35 * s_Rng.NextDouble())), 1, 275);
                var got = Contents(lv, s.Tier, (ushort)(s.Cell >> 16), kind == KIND_HUB);
                long v = got.Sum(w => (long)(w.Value ?? 0));
                if (got.Any(w => (w.Value ?? 0) >= 8000)) valuable++;
                itemsN += got.Count; vals.Add(v);
                foreach (var w in got) w.Destroy();
            }
            vals.Sort();
            return $"Corpse capsules budget over {n} rolled corpses: items {itemsN / (double)n:0.0} a corpse; value mean {vals.Average():N0}, median {vals[n / 2]:N0}, " +
                   $"90% {vals[(int)(n * 0.9)]:N0}, max {vals[^1]:N0}; with an item worth 8,000+: {valuable} ({100.0 * valuable / n:0.0}%). " +
                   $"At {Target} standing: about {vals.Average() * Target:N0} in all. {sw.Elapsed.TotalMilliseconds / n:0.00} ms a roll.";
        }
    }

    // ================= hooks =================

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class CorpseCapsulesTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done) { s_Done = true; try { CorpseCapsules.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] CorpseCapsules: register: {e.Message}"); } }
            return true;
        }
        static void Postfix()
        {
            try { CorpseCapsules.Tick(); }
            catch (Exception e) { TickGuard.Report("CorpseCapsules", e); }
        }
    }

    /// <summary>After ACE has queued a loading landblock's saved corpses (its last load step), queue ours.</summary>
    [HarmonyPatch]
    static class CorpseCapsulesLoad
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(AceLandblock), "SpawnDynamicShardObjects");
            if (m == null) Mod.Log.Error("[RevivalGuard] CorpseCapsules: Landblock.SpawnDynamicShardObjects not found; no corpse capsule ever appears");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(AceLandblock __instance)
        {
            try { CorpseCapsules.OnLandblockLoaded(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules load: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(AceLandblock), nameof(AceLandblock.Unload))]
    static class CorpseCapsulesUnload
    {
        static void Prefix(AceLandblock __instance)
        {
            try { CorpseCapsules.OnUnload(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules unload: {e.Message}"); }
        }
    }

    /// <summary>Retail-strict looting of the generated corpses</summary>
    [HarmonyPatch(typeof(Corpse), nameof(Corpse.HasPermission))]
    static class CorpseCapsulesPermission
    {
        static bool Prefix(Corpse __instance, ref bool __result)
        {
            try { if (CorpseCapsules.Refuses(__instance)) { __result = false; return false; } }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules permission: {e.Message}"); }
            return true;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Destroy), new[] { typeof(bool), typeof(bool) })]
    static class CorpseCapsulesDestroy
    {
        static void Prefix(WorldObject __instance, bool fromLandblockUnload)
        {
            try { if (__instance is Corpse && !__instance.IsDestroyed) CorpseCapsules.OnDestroy(__instance, fromLandblockUnload); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CorpseCapsules destroy: {e.Message}"); }
        }
    }
}
