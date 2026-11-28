using System.Collections.Concurrent;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

namespace RevivalGuard
{
    /// <summary>Our own clothingtables.</summary>
    static class CustomClothing
    {
        const string P_ON = "rg_custom_clothing";
        /// <summary>Our tables are in the portal cache: a runtime look draws (off, its item reads an empty table).</summary>
        internal static bool Serving => PkCommon.On(P_ON);
        const string PREFIX = "RevivalGuard.clothing.";
        static readonly ConcurrentDictionary<uint, ClothingTable> s_Tables = new ConcurrentDictionary<uint, ClothingTable>();
        static Timer s_Timer;
        static bool? s_Served, s_ShowcaseServed;
        const uint SHOWCASE_LO = 0x1000F200, SHOWCASE_HI = 0x1000F2FF;

        static string Dir => Path.Combine(ModFolder.Path(), "clothing");

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard CustomClothing: serve our ClothingTables 0x1000F000+ from Mods/RevivalGuard/clothing/*.json (the rune-skin suits); false: those ids are empty");
            Seed();
            Load();
            s_Timer = new Timer(_ => Sync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
            Mod.Log.Info($"[RevivalGuard] CustomClothing: {s_Tables.Count} table(s) from {Dir} ({P_ON}, default off)");
        }

        static void Seed()
        {
            Directory.CreateDirectory(Dir);
            var asm = typeof(CustomClothing).Assembly;
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(PREFIX, StringComparison.Ordinal)))
            {
                try
                {
                    string name = res.Substring(PREFIX.Length), path = Path.Combine(Dir, name), side = path + ".seed";
                    using var rs = asm.GetManifestResourceStream(res);
                    using var sr = new StreamReader(rs);
                    string seed = sr.ReadToEnd(), eh = SeedFileRule.Hash(seed);
                    string dh = File.Exists(path) ? SeedFileRule.Hash(File.ReadAllText(path)) : null;
                    string wh = File.Exists(side) ? File.ReadAllText(side).Trim() : null;
                    var act = SeedFileRule.Decide(dh, eh, string.IsNullOrEmpty(wh) ? null : wh, Array.Empty<string>(), false);
                    if (act == SeedFileRule.Act.KeepEdited)
                        Mod.Log.Warn($"[RevivalGuard] CustomClothing: {name} was edited by hand; a newer built-in version {eh} is not used");
                    if (act == SeedFileRule.Act.UpToDate && wh != eh) File.WriteAllText(side, eh);
                    if (!SeedFileRule.Replaces(act)) continue;
                    if (dh != null) File.Copy(path, SeedFileRule.BackupName(path, eh), true);
                    File.WriteAllText(path + ".tmp", seed);
                    File.Move(path + ".tmp", path, true);
                    File.WriteAllText(side, eh);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CustomClothing: seeding {res}: {e.Message}"); }
            }
        }

        static void Load()
        {
            foreach (var path in Directory.GetFiles(Dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var (id, blob, setups, templates) = CustomClothingRule.Build(File.ReadAllText(path));
                    var t = new ClothingTable();
                    using (var r = new BinaryReader(new MemoryStream(blob))) t.Unpack(r);
                    if (!s_Tables.TryAdd(id, t)) { Mod.Log.Warn($"[RevivalGuard] CustomClothing: {Path.GetFileName(path)}: 0x{id:X8} is already loaded, skipped"); continue; }
                    Mod.Log.Info($"[RevivalGuard] CustomClothing: table 0x{id:X8} from {Path.GetFileName(path)}: {setups} bodies, {templates} palette templates");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CustomClothing: {Path.GetFileName(path)} not loaded: {e.Message}"); }
            }
        }

        /// <summary>Look lab "make it now": a table built at runtime from a recipe</summary>
        internal const uint RUNTIME_LO = 0x1000FF00, RUNTIME_HI = 0x1000FFFF;
        internal static uint? AddRuntime(Func<uint, string> jsonFor)
        {
            lock (s_Tables)
            {
                foreach (var f in Directory.GetFiles(Dir, "looklabrun-*.json"))
                {
                    try
                    {
                        var (fid, _, _, _) = CustomClothingRule.Build(File.ReadAllText(f));
                        if (File.ReadAllText(f) == jsonFor(fid)) return fid;   // this look was made before: its table again
                    }
                    catch { }
                }
                uint id = RUNTIME_LO;
                while (id <= RUNTIME_HI && s_Tables.ContainsKey(id)) id++;
                if (id > RUNTIME_HI) return null;
                string json = jsonFor(id);
                var (bid, blob, _, _) = CustomClothingRule.Build(json);
                var t = new ClothingTable();
                using (var r = new BinaryReader(new MemoryStream(blob))) t.Unpack(r);
                File.WriteAllText(Path.Combine(Dir, $"looklabrun-{bid:X8}.json"), json);
                s_Tables[bid] = t;
                if (PkCommon.On(P_ON)) { var cache = DatManager.PortalDat?.FileCache; if (cache != null) cache[bid] = t; }
                Mod.Log.Info($"[RevivalGuard] CustomClothing: runtime table 0x{bid:X8} (Look Lab, Make it now)");
                return bid;
            }
        }

        /// <summary>Make the portal cache match the switch: ours in it when on, out of it when off.</summary>
        static void Sync()
        {
            try
            {
                var cache = DatManager.PortalDat?.FileCache;
                if (cache == null || s_Tables.IsEmpty) return;
                bool on = PkCommon.On(P_ON), showcase = PkCommon.On(LookShowcase.P_ON);
                foreach (var (id, t) in s_Tables)
                {
                    // the look showcase's own range answers to its own switch alone (LookShowcase; gen_look_showcase.py)
                    if (id >= SHOWCASE_LO && id <= SHOWCASE_HI ? showcase : on) { if (!cache.TryGetValue(id, out var f) || !ReferenceEquals(f, t)) cache[id] = t; }
                    else if (cache.TryGetValue(id, out var f) && ReferenceEquals(f, t)) cache.TryRemove(id, out _);
                }
                if (s_Served != on || s_ShowcaseServed != showcase) Mod.Log.Info($"[RevivalGuard] CustomClothing: {(on ? "serving" : "not serving")} {s_Tables.Count} table(s) ({P_ON} {on}; showcase 0x{SHOWCASE_LO:X8}+ {LookShowcase.P_ON} {showcase})");
                s_Served = on; s_ShowcaseServed = showcase;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CustomClothing: {e.Message}"); }
        }
    }
}
