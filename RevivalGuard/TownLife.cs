using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using static RevivalGuard.TownLifeRule;

namespace RevivalGuard
{
    /// <summary>
    /// HOLTBURG'S TOWN LIFE (owner 2026-11-14: "bring Holtburg's NPCs to life the way the Royal Guards were"; and "i dont
    /// want holtberg to be flooded with random npc talking ... keep it at a reasonable limit"). Ours, not retail
    /// (docs/DIVERGENCES.md; findings docs/findings/holtburg-town-life-2026-11-14.md). ONE data-driven layer for the town's
    /// placed NPCs (TownLife.json: who, role, post, rounds, gestures, lines, exchanges, rumours), the decisions in
    /// TownLifeRule (pure, nettest TownLifeCase).
    ///
    ///   SILENT LIFE (most of it)  rounds out and back along legs checked offline against the DATs
    ///             (tools/townlife/check_legs.py; ACE's MoveTo walks them server-side as the guards' patrols), stays through
    ///             the evening (patrons drift into the tavern hall) or the night (townsfolk at their doors), idle gestures
    ///             (ACE motion commands, as ACE's own emote Motion step sends them).
    ///   SPEECH (the seasoning)    unprompted lines only through the town's speech budget (TownLifeRule.Budget), which the
    ///             Royal Guards and Azimar's idle lines also go through; prompted answers to a greeting or keyword in local
    ///             chat; an arrival greeting by name and visit count from the greeters; rare two-NPC exchanges; one NPC at
    ///             most reacts to an event (a monster in town, a PK death, a level-up); rumours by level from the tavern.
    ///   BUSINESS FIRST            a Use, an NPC's own emote chain or a vendor's customer stops it where it stands; it walks
    ///             back to its post when that is over; a player at a vendor's post calls it back. Nothing here blocks a Use, a
    ///             sale or a quest step (retail's emotes run untouched; this only adds).
    ///
    /// Switches (registered, read live):
    ///   @modifybool rg_town_life false          the whole layer off: NPCs walk back to their posts and stand as in retail
    ///   @modifybool rg_town_life_speech false   silent life only: no line from the layer (guards' and Azimar's idle lines too)
    ///   @modifystring rg_town_life_off "patron,dwennon"   roles or NPC keys turned off (comma list)
    ///   @modifydouble rg_town_life_line_gap_s 60 | _npc_gap_s 300 | _exchange_gap_s 600 | _talk_m 10 | _greet_gap_s 3600
    ///                 | _again_gap_s 60 | _arrival_gap_s 300 | _screen_s 6 | _gesture_minutes 3
    ///   @modifylong rg_town_life_trace 1        every line, routine step and gesture in the server log
    ///   @townlife [status | reload | moment day|evening|night|auto | event raid|pkdeath]   (Developer)
    /// </summary>
    static class TownLife
    {
        internal const string P_ON = "rg_town_life", P_SPEECH = "rg_town_life_speech", P_OFF = "rg_town_life_off", P_TRACE = "rg_town_life_trace";
        const string P_LINE = "rg_town_life_line_gap_s", P_NPC = "rg_town_life_npc_gap_s", P_EXCH = "rg_town_life_exchange_gap_s",
                     P_TALK = "rg_town_life_talk_m", P_GREET = "rg_town_life_greet_gap_s", P_ARRIVE = "rg_town_life_arrival_gap_s",
                     P_SCREEN = "rg_town_life_screen_s", P_GESTURE = "rg_town_life_gesture_minutes",
                     P_AGAIN = "rg_town_life_again_gap_s";
        const string FILE = "TownLife.json", VISITS = "TownLifeVisits.json";
        const uint LB = 0xA9B4;
        static readonly string[] AGAIN = { "Again, friend?", "Hello again." };
        const float BUSINESS_M = 4f, GREET_M = 6f, CHAT_M = 6f, EXCHANGE_PAIR_M = 12f;

        // ---------------------------------------------------------------- data (TownLife.json)
        sealed class Data
        {
            public List<NpcDef> Npcs { get; set; } = new();
            public List<ExchangeDef> Exchanges { get; set; } = new();
            public Dictionary<string, string[]> Keywords { get; set; } = new();
            public List<RumourDef> Rumours { get; set; } = new();
            public Dictionary<string, string[]> Lines { get; set; } = new();
        }
        sealed class NpcDef
        {
            public string Key { get; set; } public string Name { get; set; } public string Guid { get; set; } public uint Wcid { get; set; } public string Role { get; set; }
            public bool Vendor { get; set; } public bool Quest { get; set; } public bool Greets { get; set; } public bool Rumours { get; set; } public bool NoChat { get; set; }
            public List<JsonElement> Post { get; set; }
            public string[] Gestures { get; set; } = Array.Empty<string>();
            public List<RoundJson> Rounds { get; set; } = new();
        }
        sealed class RoundJson
        {
            public string Name { get; set; } public string[] When { get; set; } public List<List<JsonElement>> Path { get; set; } = new();
            public bool[] Stay { get; set; } public double[] Pause { get; set; } public bool Run { get; set; } public double Weight { get; set; } = 1;
        }
        sealed class ExchangeDef { public string A { get; set; } public string B { get; set; } public string[] When { get; set; } public List<string[]> Lines { get; set; } = new(); }
        sealed class RumourDef { public int Min { get; set; } public int Max { get; set; } public string[] Lines { get; set; } = Array.Empty<string>(); }

        struct Pt { public float X, Y, Z; public uint Cell; }   // Cell: full cell id (outdoor cells resolved at runtime)

        /// <summary>A loaded roster entry: its definition and its rounds, parsed.</summary>
        sealed class Def
        {
            public NpcDef D; public uint Guid; public Pt Post; public RoundDef[] Rounds = Array.Empty<RoundDef>(); public Pt[][] Paths = Array.Empty<Pt[]>();
            public MotionCommand[] Gestures = Array.Empty<MotionCommand>();
        }

        sealed class Npc
        {
            public Def Def; public Creature Obj;
            public readonly Routine R = new Routine();
            public readonly HoltburgGuardsRule.LineBag Bag = new HoltburgGuardsRule.LineBag();
            public Position Home; public bool PostOk;
            public double NextThink, NextLine, NextGesture, MotionUntil, UsedAt, NextTrace, HeldSince;
            public int LastGesture = -1;
        }

        static readonly object s_Lock = new object();
        static Data s_Data = new Data();
        static Dictionary<uint, Def> s_Defs = new Dictionary<uint, Def>();
        static volatile HashSet<uint> s_Guids = new HashSet<uint>();
        // generator-spawned NPCs (Alcott, Buckminster, the tavern's quest folk...) get a new guid each spawn: matched by
        // weenie inside Holtburg's landblock instead (each roster weenie stands once in the town)
        static Dictionary<uint, Def> s_ByWcid = new Dictionary<uint, Def>();
        static volatile HashSet<uint> s_Wcids = new HashSet<uint>();
        static readonly HashSet<uint> s_Live = new HashSet<uint>();   // guids of the town NPCs seen ticking (Use lookups)
        static readonly Dictionary<uint, Npc> s_Npcs = new Dictionary<uint, Npc>();
        static DateTime s_Stamp; static double s_NextLoadCheck;
        static readonly Budget s_Budget = new Budget();
        static readonly Random s_Rng = Random.Shared;   // thread-safe: landblocks tick in parallel, and a shared new Random() corrupts under races
        static readonly Dictionary<uint, (int count, double last)> s_Visits = new Dictionary<uint, (int, double)>();
        static readonly Dictionary<uint, double> s_Died = new Dictionary<uint, double>();
        static readonly Dictionary<uint, List<string>> s_Heard = new Dictionary<uint, List<string>>();   // player -> lines heard lately
        static bool s_VisitsDirty; static double s_NextVisitSave, s_NextPrune, s_NextExchange;
        static string s_ForcedMoment;                              // @townlife moment (tests)
        static (string key, double until, Vector2 at) s_Event;     // the town's current event (raid, pkdeath)

        static double Now => Timers.RunningTime;
        static double Wall => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, true).Item; } catch { return false; } } }
        static bool Speech { get { try { return PropertyManager.GetBool(P_SPEECH, true).Item; } catch { return true; } } }
        static bool Tracing { get { try { return PropertyManager.GetLong(P_TRACE, 0).Item > 0; } catch { return false; } } }
        static double D(string k, double def) { try { return PropertyManager.GetDouble(k, def).Item; } catch { return def; } }
        static string OffList { get { try { return PropertyManager.GetString(P_OFF, "").Item ?? ""; } catch { return ""; } } }

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard TownLife: Holtburg's NPCs walk short rounds, gesture and now and then speak (false: retail's standing NPCs)");
            PkCommon.RegisterBool(P_SPEECH, true, "RevivalGuard TownLife: town NPCs may speak at all (false: silent life only; the guards' and Azimar's idle lines too)");
            try
            {
                var s = MonsterAi.Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_OFF)) s[P_OFF] = new Property<string>("", "RevivalGuard TownLife: roles or NPC keys (TownLife.json) turned off, comma separated, e.g. patron,dwennon");
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_TRACE)) l[P_TRACE] = new Property<long>(0, "RevivalGuard TownLife: 1 logs every town line, routine step and gesture");
                var d = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                void Dbl(string k, double v, string desc) { if (!d.ContainsKey(k)) d[k] = new Property<double>(v, "RevivalGuard TownLife: " + desc); }
                var L = new Limits();
                Dbl(P_LINE, L.LineGap, "seconds between unprompted town lines a player hears (whole town, guards and Azimar included)");
                Dbl(P_NPC, L.NpcGap, "seconds between one NPC's unprompted lines");
                Dbl(P_EXCH, L.ExchangeGap, "seconds between two-NPC exchanges a player hears");
                Dbl(P_TALK, L.TalkRange, "metres: an unprompted line only with a player this close");
                Dbl(P_GREET, L.GreetGap, "seconds before one NPC greets the same player again");
                Dbl(P_AGAIN, L.AgainGap, "seconds before one NPC acknowledges the same player's repeat greeting again");
                Dbl(P_ARRIVE, L.ArrivalGap, "seconds between arrival greetings to one player (whole town)");
                Dbl(P_SCREEN, L.Screen, "seconds a town line holds a player's screen (another unprompted line is dropped meanwhile)");
                Dbl(P_GESTURE, 3, "minutes between an NPC's idle gestures (0: no gestures)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownLife: tunables not registered ({e.Message}); read at their defaults"); }
            CommandManager.TryAddCommand(Cmd, "townlife", AccessLevel.Developer, CommandHandlerFlag.None,
                "Holtburg's town life: status, reload (the file beside the dll; 'reload embedded' takes the dll's copy over a hand-edited one), force the moment or an event (tests).",
                "[status | reload | reload embedded | moment day|evening|night|auto | event raid|pkdeath]");
            SyncSeed(false);
            Load(true);
            LoadVisits();
            Mod.Log.Info($"[RevivalGuard] TownLife: {s_Defs.Count} Holtburg NPCs, {s_Data.Lines.Values.Sum(v => v.Length)} lines, {s_Data.Exchanges.Count} exchanges ({P_ON}, {P_SPEECH}, {P_OFF})");
        }

        static void ApplyLimits()
        {
            var L = s_Budget.L;
            L.LineGap = D(P_LINE, 60); L.NpcGap = D(P_NPC, 300); L.ExchangeGap = D(P_EXCH, 600); L.TalkRange = (float)D(P_TALK, 10);
            L.GreetGap = D(P_GREET, 3600); L.AgainGap = D(P_AGAIN, 60); L.ArrivalGap = D(P_ARRIVE, 300); L.Screen = D(P_SCREEN, 6);
            L.HearRange = Math.Max(L.TalkRange, 20f);
        }

        // ---------------------------------------------------------------- loading
        static string FilePath => SeasonalCalendar.FileBeside(FILE);

        static string Seed()
        {
            using var rs = typeof(TownLife).Assembly.GetManifestResourceStream("RevivalGuard.TownLife.json");
            if (rs == null) return "{}";
            using var rd = new StreamReader(rs);
            return rd.ReadToEnd();
        }

        // Every TownLife.json shipped before the .seed sidecar (SeedFileRule.Hash of each git version): a sidecar-less
        // file matching one was never hand-edited, so the newer embedded copy may replace it. Frozen: later versions
        // are tracked by the sidecar.
        static readonly string[] s_SeedPast = { "b3e5d0de99a00c26", "7eda957a397af871" };
        static string SeedSidecar => FilePath + ".seed";

        /// <summary>Bring the file beside the dll up to the embedded copy when it is an untouched older one we wrote
        /// (SeedFileRule); a hand-edited file is kept with one WARN unless <paramref name="force"/> (@townlife reload
        /// embedded). The replaced file is kept as TownLife.json.before-&lt;version&gt;. Returns what happened.</summary>
        static string SyncSeed(bool force)
        {
            lock (s_Lock)
            {
                try
                {
                    var path = FilePath;
                    var seed = Seed();
                    var eh = SeedFileRule.Hash(seed);
                    string dh = File.Exists(path) ? SeedFileRule.Hash(File.ReadAllText(path)) : null;
                    string wh = null;
                    try { if (File.Exists(SeedSidecar)) wh = File.ReadAllText(SeedSidecar).Trim(); } catch { }
                    if (string.IsNullOrEmpty(wh)) wh = null;
                    var act = SeedFileRule.Decide(dh, eh, wh, s_SeedPast, force);
                    string msg;
                    switch (act)
                    {
                        case SeedFileRule.Act.UpToDate:
                            if (wh != eh) File.WriteAllText(SeedSidecar, eh);
                            msg = $"{FILE} is the embedded version {eh}";
                            break;
                        case SeedFileRule.Act.Keep:
                            msg = $"{FILE} is hand-edited ({dh}); the embedded version {eh} is the one it was written from, kept";
                            break;
                        case SeedFileRule.Act.KeepEdited:
                            msg = $"{FILE} was edited by hand ({dh}) and a newer embedded version {eh} exists: kept the edited file. " +
                                  "`@townlife reload embedded` (admin panel Server: town data) takes the embedded one, keeping the edit as " +
                                  Path.GetFileName(SeedFileRule.BackupName(path, eh));
                            Mod.Log.Warn("[RevivalGuard] TownLife: " + msg);
                            return msg;
                        default:   // Write, Update, ForceUpdate
                            string kept = null;
                            if (dh != null)
                            {
                                kept = SeedFileRule.BackupName(path, eh);
                                for (int i = 2; File.Exists(kept); i++) kept = SeedFileRule.BackupName(path, eh) + "-" + i;
                                File.Copy(path, kept);
                            }
                            File.WriteAllText(path + ".tmp", seed);
                            File.Move(path + ".tmp", path, true);
                            File.WriteAllText(SeedSidecar, eh);
                            msg = act == SeedFileRule.Act.Write ? $"wrote {FILE} (embedded version {eh})"
                                : $"{(act == SeedFileRule.Act.ForceUpdate ? "forced" : "updated")} {FILE} {dh} -> embedded version {eh}, the old one kept as {Path.GetFileName(kept)}";
                            break;
                    }
                    Mod.Log.Info("[RevivalGuard] TownLife: " + msg);
                    return msg;
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"[RevivalGuard] TownLife: {FILE} not checked against the embedded copy: {e.Message}");
                    return "not checked: " + e.Message;
                }
            }
        }

        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };

        /// <summary>The data file beside the dll (seeded from the embedded copy), read again when it changes; a file that will
        /// not parse keeps the last good data.</summary>
        static void Load(bool force)
        {
            lock (s_Lock)
            {
                try
                {
                    var path = FilePath;
                    string text;
                    if (!File.Exists(path))
                    {
                        text = Seed();
                        try { File.WriteAllText(path, text); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownLife: could not write {path}: {e.Message}"); }
                    }
                    else
                    {
                        var stamp = File.GetLastWriteTimeUtc(path);
                        if (!force && stamp == s_Stamp) return;
                        text = File.ReadAllText(path);
                    }
                    s_Stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                    var data = JsonSerializer.Deserialize<Data>(text, s_Json) ?? throw new InvalidDataException("empty");
                    var defs = new Dictionary<uint, Def>();
                    foreach (var n in data.Npcs)
                    {
                        if (string.IsNullOrEmpty(n.Guid) || string.IsNullOrEmpty(n.Key)) continue;
                        var d = new Def { D = n, Guid = Convert.ToUInt32(n.Guid, 16), Post = ParsePt(n.Post) };
                        var rounds = new List<RoundDef>(); var paths = new List<Pt[]>();
                        foreach (var r in n.Rounds ?? new())
                        {
                            var pts = r.Path.Select(ParsePt).ToArray();
                            var when = (r.When ?? new[] { "day", "evening" }).Select(w => TryMoment(w, out var m) ? m : Moment.Day).ToArray();
                            rounds.Add(new RoundDef
                            {
                                Name = r.Name ?? "round", When = when, Count = pts.Length, Stay = r.Stay ?? new bool[pts.Length], Run = r.Run, Weight = r.Weight,
                                PauseMin = r.Pause != null && r.Pause.Length > 0 ? r.Pause[0] : 8, PauseSpan = r.Pause != null && r.Pause.Length > 1 ? Math.Max(0, r.Pause[1] - r.Pause[0]) : 6,
                            });
                            paths.Add(pts);
                        }
                        d.Rounds = rounds.ToArray(); d.Paths = paths.ToArray();
                        d.Gestures = (n.Gestures ?? Array.Empty<string>()).Select(g => Enum.TryParse<MotionCommand>(g, out var mc) ? mc : MotionCommand.Invalid)
                                                                     .Where(mc => mc != MotionCommand.Invalid).ToArray();
                        defs[d.Guid] = d;
                    }
                    var byW = new Dictionary<uint, Def>();
                    foreach (var d in defs.Values) if (d.D.Wcid != 0) byW[d.D.Wcid] = d;
                    s_Data = data; s_Defs = defs; s_Guids = new HashSet<uint>(defs.Keys); s_ByWcid = byW; s_Wcids = new HashSet<uint>(byW.Keys);
                    // a reload re-reads definitions; NPCs pick them up at their next think (their walk state starts over at the post)
                    foreach (var npc in s_Npcs.Values)
                        if (defs.TryGetValue(npc.Obj.Guid.Full, out var nd) || byW.TryGetValue(npc.Obj.WeenieClassId, out nd)) npc.Def = nd;
                    Mod.Log.Info($"[RevivalGuard] TownLife: read {FILE} ({defs.Count} NPCs, {data.Lines.Count} pools, {data.Exchanges.Count} exchanges, {data.Rumours.Count} rumour bands)");
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] TownLife: {FILE} not read, keeping the last data: {e.Message}");
                    try { s_Stamp = File.GetLastWriteTimeUtc(FilePath); } catch { }
                }
            }
        }

        static Pt ParsePt(List<JsonElement> a)
        {
            if (a == null || a.Count < 3) return default;
            uint low = 0;
            if (a.Count > 3 && a[3].ValueKind == JsonValueKind.String && a[3].GetString() != "out") low = Convert.ToUInt32(a[3].GetString(), 16);
            return new Pt { X = a[0].GetSingle(), Y = a[1].GetSingle(), Z = a[2].GetSingle(), Cell = low };
        }

        // ---------------------------------------------------------------- visits (TownLifeVisits.json beside the dll)
        static string VisitsPath => SeasonalCalendar.FileBeside(VISITS);

        static void LoadVisits()
        {
            try
            {
                if (!File.Exists(VisitsPath)) return;
                var d = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(VisitsPath));
                lock (s_Lock) foreach (var kv in d) s_Visits[Convert.ToUInt32(kv.Key, 16)] = ((int)kv.Value[0], kv.Value[1]);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownLife: visits not read ({e.Message})"); }
        }

        static void SaveVisits(double now)
        {
            if (!s_VisitsDirty || now < s_NextVisitSave) return;
            s_NextVisitSave = now + 120; s_VisitsDirty = false;
            Dictionary<string, double[]> d;
            lock (s_Lock) d = s_Visits.ToDictionary(kv => kv.Key.ToString("X8"), kv => new double[] { kv.Value.count, kv.Value.last });
            var path = VisitsPath;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(d)); File.Move(path + ".tmp", path, true); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownLife: visits not saved ({e.Message})"); }
            });
        }

        static readonly Dictionary<uint, double> s_AwayDays = new Dictionary<uint, double>();   // player -> days away before this visit

        /// <summary>A player seen in Holtburg by a town NPC: a new visit after VISIT_GAP away (TownLifeRule.NewVisit).</summary>
        static void Seen(uint id)
        {
            double wall = Wall;
            lock (s_Lock)
            {
                s_Visits.TryGetValue(id, out var v);
                if (v.last > 0 && wall - v.last < 30) return;   // seen just now: nothing to write
                if (NewVisit(v.last, wall)) { s_AwayDays[id] = v.last > 0 ? (wall - v.last) / 86400 : 0; v.count++; }
                v.last = wall; s_Visits[id] = v; s_VisitsDirty = true;
            }
        }

        static (int count, double daysAway) Visit(Player p)
        {
            Seen(p.Guid.Full);
            lock (s_Lock) return (s_Visits.TryGetValue(p.Guid.Full, out var v) ? v.count : 1, s_AwayDays.TryGetValue(p.Guid.Full, out var a) ? a : 0);
        }

        static int Visits(Player p) { lock (s_Lock) return s_Visits.TryGetValue(p.Guid.Full, out var v) ? v.count : 0; }

        // ---------------------------------------------------------------- who
        internal static bool Owns(WorldObject wo) => wo != null && (s_Guids.Contains(wo.Guid.Full)
            || s_Wcids.Contains(wo.WeenieClassId) && wo.Location != null && wo.Location.Landblock == LB);

        static Def DefOf(Creature c)
        {
            if (s_Defs.TryGetValue(c.Guid.Full, out var d)) return d;
            return s_ByWcid.TryGetValue(c.WeenieClassId, out d) ? d : null;
        }

        static bool Disabled(Def d)
        {
            var off = OffList;
            if (string.IsNullOrWhiteSpace(off)) return false;
            foreach (var t in off.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (t.Equals(d.D.Key, StringComparison.OrdinalIgnoreCase) || t.Equals(d.D.Role, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static Npc Get(Creature c)
        {
            lock (s_Lock)
            {
                if (s_Npcs.TryGetValue(c.Guid.Full, out var n) && ReferenceEquals(n.Obj, c)) return n;
                var d = DefOf(c);
                if (d == null) return null;
                s_Live.Add(c.Guid.Full);
                n = new Npc { Def = d, Obj = c, Home = new Position(c.Home ?? c.Location) };
                var here = Local(n.Home);
                n.PostOk = Vector2.Distance(here, new Vector2(d.Post.X, d.Post.Y)) <= 2f;
                if (!n.PostOk) Mod.Log.Warn($"[RevivalGuard] TownLife: {d.D.Name} stands at {here.X:0.0},{here.Y:0.0}, not at its post in {FILE}: its rounds are off");
                double now = Now;
                n.R.Until = now + 20 + s_Rng.NextDouble() * POST_SPAN;
                n.NextLine = now + 60 + s_Rng.NextDouble() * 240;
                n.NextGesture = now + 20 + s_Rng.NextDouble() * Math.Max(0, D(P_GESTURE, 3)) * 60;   // spread: not the whole town at once
                s_Npcs[c.Guid.Full] = n;
                return n;
            }
        }

        // ---------------------------------------------------------------- geometry
        static Vector2 Local(Position p)
        {
            var gl = p.ToGlobal();
            return new Vector2(gl.X - (LB >> 8) * 192f, gl.Y - (LB & 0xFF) * 192f);
        }

        static Position PtPos(Pt p, Pt from)
        {
            uint cell = p.Cell != 0 ? (LB << 16) | p.Cell : (LB << 16) | (uint)(Math.Clamp((int)(p.X / 24), 0, 7) * 8 + Math.Clamp((int)(p.Y / 24), 0, 7) + 1);
            var pos = new Position(cell, p.X, p.Y, p.Z, 0, 0, 0, 1);
            var dir = new Vector2(p.X - from.X, p.Y - from.Y);
            if (dir.LengthSquared() > 0.01f) pos.Rotate(new Vector3(Vector2.Normalize(dir), 0));
            return pos;
        }

        static Pt PointOf(Npc n, int round, int index) => index < 0 ? n.Def.Post : n.Def.Paths[round][index];

        static List<Listener> Listeners(WorldObject c, float range, out Player nearest)
        {
            var l = new List<Listener>(); nearest = null; float best = float.MaxValue;
            if (c?.PhysicsObj == null || c.Location == null || c.PhysicsObj.ObjMaint.GetKnownPlayersCount() == 0) return l;
            foreach (var p in c.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
            {
                if (p?.Location == null || p.Teleporting) continue;
                float d = c.Location.DistanceTo(p.Location);
                if (d > range) continue;
                l.Add(new Listener(p.Guid.Full, d));
                if (d < best) { best = d; nearest = p; }
            }
            return l;
        }

        static Moment NowMoment()
        {
            var f = s_ForcedMoment;
            if (f != null && TryMoment(f, out var m)) return m;
            try { return MomentOf(Timers.CurrentInGameTime.Hour); } catch { return Moment.Day; }
        }

        static string Festival()
        {
            foreach (var k in s_Data.Lines.Keys)
                if (k.StartsWith("fest.", StringComparison.Ordinal))
                {
                    var key = k.Substring(5);
                    try { if (SeasonalCalendar.IsFestivalActive(key)) return key; } catch { }
                }
            return null;
        }

        // ---------------------------------------------------------------- the think (once a second, the NPC's Monster_Tick)
        internal static void Tick(Creature c)
        {
            var n = Get(c);
            if (n == null || c.Location == null || c.PhysicsObj == null) return;
            double now = Now;
            if (now < n.NextThink) return;
            n.NextThink = now + 1.0;
            if (now >= s_NextLoadCheck) { s_NextLoadCheck = now + 30; Load(false); ApplyLimits(); }
            if (now >= s_NextPrune) { s_NextPrune = now + 120; lock (s_Lock) s_Budget.Prune(now); }
            SaveVisits(now);

            var d = n.Def;
            bool on = On && !Disabled(d);
            if (!on && n.R.State == RState.Post && !n.R.Held) return;   // retail: nothing to do

            var listeners = Listeners(c, s_Budget.L.HearRange, out var nearest);
            bool seen = listeners.Count > 0;
            foreach (var l in listeners) Seen(l.Id);
            Routines(n, now, on, listeners, nearest);
            if (!on) return;
            if (seen) Gestures(n, now, listeners);
            if (seen && Speech) { Arrivals(n, now, listeners); Ambient(n, now, listeners); Exchanges(n, now); }
        }

        // ---------------------------------------------------------------- routines
        static void Routines(Npc n, double now, bool on, List<Listener> listeners, Player nearest)
        {
            var c = n.Obj; var d = n.Def;
            if (!n.PostOk || d.Rounds.Length == 0) return;
            bool busyEmotes = c.EmoteManager?.IsBusy ?? false;
            bool used = now - n.UsedAt < 60;
            bool customer = d.D.Vendor && listeners.Any(l => l.Dist <= BUSINESS_M);
            bool hold = used || customer || busyEmotes && now - n.UsedAt < 300;   // a Use and the emotes it set off; its own heartbeat emotes are not business: they only hold off the next move
            // summon: switched off, an alarm, or a player waiting at the post of a vendor or quest NPC that is away
            bool summon = !on || s_Event.key == "raid" && now < s_Event.until;
            if (!summon && Away(n.R) && (d.D.Vendor || d.D.Quest) && nearest != null)
            {
                var post = new Vector2(d.Post.X, d.Post.Y);
                foreach (var p in c.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                    if (p?.Location != null && Vector2.Distance(Local(p.Location), post) <= BUSINESS_M && p.Location.Landblock == LB) { summon = true; break; }
            }
            if ((now < n.MotionUntil || busyEmotes) && !hold && !summon) return;   // a gesture or its own emote plays out before any move order
            var mo = NowMoment();
            var target = n.R.State == RState.Post ? d.Post : PointOf(n, Math.Max(0, n.R.Round), n.R.Index);
            float dist = Vector2.Distance(Local(c.Location), new Vector2(target.X, target.Y));
            // ACE's MoveTo ends up to a metre short of the point (its arrival distance): stopped that close counts as there
            if ((n.R.State == RState.Walking || n.R.State == RState.Returning) && dist <= 1.2f && !c.PhysicsObj.IsMovingTo()) dist = 0;
            var before = n.R.State; int beforeIdx = n.R.Index;
            var act = Step(n.R, now, mo, dist, d.Rounds, hold, summon, listeners.Count > 0, s_Rng.NextDouble());
            if (n.R.Held && n.HeldSince == 0) n.HeldSince = now; else if (!n.R.Held) n.HeldSince = 0;
            switch (act)
            {
                case RAct.WalkTo:
                {
                    int r = Math.Max(0, n.R.Round);
                    var to = PointOf(n, r, n.R.Index);
                    var from = n.R.State == RState.Returning ? PointOf(n, r, Math.Min(d.Paths[r].Length - 1, n.R.Index + 1)) : PointOf(n, r, n.R.Index - 1);
                    bool run = n.R.State == RState.Walking && d.Rounds[r].Run;
                    c.MoveTo(PtPos(to, from), run ? 1f : 0f);
                    break;
                }
                case RAct.Stop:
                    Stop(c);
                    break;
                case RAct.FacePost:
                    if (n.Home != null) { var face = new Position(c.Location); face.Rotation = n.Home.Rotation; c.TurnTo(Ahead(face)); }
                    break;
                case RAct.Teleport:
                    if (c.Location.Landblock == n.Home.Landblock) c.FakeTeleport(new Position(n.Home));
                    Mod.Log.Info($"[RevivalGuard] TownLife: {d.D.Name} stuck on its way home, sent back to its post");
                    break;
            }
            if (Tracing && (act != RAct.None || before != n.R.State))
                Mod.Log.Info($"[RevivalGuard] TownLife step: {d.D.Key} {before}/{beforeIdx} -> {n.R.State}/{n.R.Index} {act} round {(n.R.Round >= 0 ? d.Rounds[n.R.Round].Name : "-")} {mo} hold {hold} summon {summon} dist {dist:0.0} at {Local(c.Location).X:0.0},{Local(c.Location).Y:0.0}");
        }

        static Position Ahead(Position p)
        {
            // a point a metre ahead along the rotation (TurnTo takes a place to face)
            var q = p.Rotation; var fwd = Vector3.Transform(new Vector3(0, 1, 0), q);
            var a = new Position(p); a.PositionX += fwd.X; a.PositionY += fwd.Y;
            return a;
        }

        static void Stop(Creature c)
        {
            try
            {
                if (c.PhysicsObj?.IsMovingTo() ?? false) c.CancelMoveTo();
                c.EnqueueBroadcastMotion(new Motion(c.CurrentMotionState?.Stance ?? MotionStance.NonCombat, MotionCommand.Ready));
            }
            catch (Exception e) { TickGuard.Report("TownLife.Stop", e); }
        }

        // ---------------------------------------------------------------- gestures
        static float Length(Creature c, MotionCommand cmd)
        {
            try
            {
                var mt = ACE.DatLoader.DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.MotionTable>(c.MotionTableId);
                return mt?.GetAnimationLength(MotionStance.NonCombat, cmd, MotionCommand.Ready) ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>One gesture, as ACE's emote Motion step plays it (the guards' Play): broadcast, then Ready when it ends. A
        /// motion table without the gesture (a non-human NPC) plays nothing.</summary>
        static bool Play(Npc n, MotionCommand cmd, double delay, string why)
        {
            var c = n.Obj;
            float len = Length(c, cmd);
            if (len <= 0) return false;
            double now = Now;
            n.MotionUntil = Math.Max(n.MotionUntil, now + delay + len + 0.4);
            var chain = new ActionChain();
            chain.AddDelaySeconds(Math.Max(0, delay));
            chain.AddAction(c, () =>
            {
                try
                {
                    if (c.IsDead || (c.PhysicsObj?.IsMovingTo() ?? true) || c.CurrentMotionState?.Stance != MotionStance.NonCombat) return;
                    c.ExecuteMotion(new Motion(MotionStance.NonCombat, cmd), true, WorldObject.LocalBroadcastRange);
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] TownLife gesture: {n.Def.D.Key} {cmd} ({why}) {len:0.00} s");
                    var back = new ActionChain();
                    back.AddDelaySeconds(len);
                    back.AddAction(c, () =>
                    {
                        try
                        {
                            if (!c.IsDead && c.CurrentMotionState?.MotionState.ForwardCommand == cmd)
                                c.ExecuteMotion(new Motion(MotionStance.NonCombat, MotionCommand.Ready), true, WorldObject.LocalBroadcastRange);
                        }
                        catch (Exception e) { TickGuard.Report("TownLife.Gesture", e); }
                    });
                    back.EnqueueChain();
                }
                catch (Exception e) { TickGuard.Report("TownLife.Gesture", e); }
            });
            chain.EnqueueChain();
            return true;
        }

        static void Gestures(Npc n, double now, List<Listener> listeners)
        {
            double minutes = D(P_GESTURE, 3);
            var g = n.Def.Gestures;
            if (minutes <= 0 || g.Length == 0 || n.Obj.EmoteManager?.IsBusy == true) return;
            if (n.NextGesture > now + minutes * 60 * 1.25) n.NextGesture = NextGesture(now, minutes, s_Rng.NextDouble());
            if (now < n.NextGesture) return;
            bool near = listeners.Any(l => l.Dist <= 30);
            if (!MayGesture(n.R.State, n.Obj.PhysicsObj.IsMovingTo(), near, n.R.Held, now, n.MotionUntil)) { n.NextGesture = now + 10; return; }
            int k = PickGesture(g.Length, n.LastGesture, s_Rng.NextDouble());
            // a festival evening in the tavern: now and then a dance among the patrons
            var cmd = g[k];
            if (n.Def.D.Role == "patron" && NowMoment() == Moment.Evening && Festival() != null && s_Rng.NextDouble() < 0.3) cmd = MotionCommand.DrudgeDance;
            n.LastGesture = k;
            Play(n, cmd, 0, "idle");
            n.NextGesture = NextGesture(now, minutes, s_Rng.NextDouble());
        }

        // ---------------------------------------------------------------- speech
        /// <summary>A line from the first pool that has one nobody listening has heard lately (each listener remembers its last
        /// 24 town lines; the box hour of 2026-11-14 had three NPCs say the same news line within three minutes). When every
        /// pool is spent for them, the first usable line is said anyway. Lines needing a token nobody can fill are skipped.</summary>
        static string FromPools(Npc n, IEnumerable<string> pools, Player p, out string pool, IList<Listener> hearers = null)
        {
            pool = null;
            string chronicle = null; bool chronicleRead = false;
            var ids = new List<uint>();
            if (p != null) ids.Add(p.Guid.Full);
            if (hearers != null) foreach (var h in hearers) if (!ids.Contains(h.Id)) ids.Add(h.Id);
            var names = pools.Where(x => x != null).ToList();
            for (int pass = 0; pass < 2; pass++)
                foreach (var name in names)
                {
                    if (!s_Data.Lines.TryGetValue(name, out var lines) || lines.Length == 0) continue;
                    var usable = new List<int>();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var l = lines[i];
                        if (p == null && (NeedsToken(l, "name") || NeedsToken(l, "title") || NeedsToken(l, "visits"))) continue;
                        if (NeedsToken(l, "chronicle")) { if (!chronicleRead) { chronicle = NoticeBoard.ChronicleTitle(); chronicleRead = true; } if (string.IsNullOrEmpty(chronicle)) continue; }
                        usable.Add(i);
                    }
                    if (usable.Count == 0) continue;
                    string pick = null;
                    lock (s_Lock)
                    {
                        bool Heard(string line) { foreach (var id in ids) if (s_Heard.TryGetValue(id, out var h) && h.Contains(line)) return true; return false; }
                        for (int tries = 0; tries < lines.Length * 2 && pick == null; tries++)
                        {
                            int i = n.Bag.Next(name, lines.Length, s_Rng);
                            if (!usable.Contains(i) || pass == 0 && Heard(lines[i])) continue;
                            pick = lines[i];
                        }
                        if (pick != null)
                            foreach (var id in ids)
                            {
                                if (!s_Heard.TryGetValue(id, out var h)) { if (s_Heard.Count > 500) s_Heard.Clear(); s_Heard[id] = h = new List<string>(); }
                                h.Add(pick); while (h.Count > 24) h.RemoveAt(0);
                            }
                    }
                    if (pick == null) continue;
                    pool = name;
                    string title = p != null && NeedsToken(pick, "title") ? TitleOf(p) : null;
                    return Fill(pick, p?.Name?.TrimStart('+'), p == null ? 0 : Visits(p), p?.Level ?? 1, title, chronicle);
                }
            return null;
        }

        static string TitleOf(Player p)
        {
            try
            {
                var id = p.CharacterTitleId ?? 0;
                if (id <= 0) return null;
                var name = Enum.GetName(typeof(CharacterTitle), id);
                return Humanize(name);
            }
            catch { return null; }
        }

        static void Say(Npc n, string text, List<Listener> listeners, string kind)
        {
            var c = n.Obj;
            if (string.IsNullOrEmpty(text) || c == null || c.IsDestroyed) return;
            c.EnqueueBroadcast(new GameMessageHearSpeech(text, c.Name, c.Guid.Full, ChatMessageType.Emote), s_Budget.L.HearRange);
            if (Tracing) Mod.Log.Info($"[RevivalGuard] TownLife said ({kind}): {n.Def.D.Key} to [{string.Join(",", listeners.Select(l => $"{l.Id:X8}@{l.Dist:0}"))}] \"{text}\"");
        }

        static void SayLater(Npc n, double delay, string text, List<Listener> listeners, string kind)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(n.Obj, () => { try { Say(n, text, listeners, kind); } catch (Exception e) { TickGuard.Report("TownLife.Say", e); } });
            chain.EnqueueChain();
        }

        /// <summary>An unprompted line of this NPC's own, when its turn has come and the town's budget allows it.</summary>
        static void Ambient(Npc n, double now, List<Listener> listeners)
        {
            if (now < n.NextLine || n.R.Held || n.Obj.EmoteManager?.IsBusy == true) return;
            var mo = NowMoment();
            string ev = null;
            if (s_Event.key != null && now < s_Event.until && Vector2.Distance(Local(n.Obj.Location), s_Event.at) < 80)
            {
                lock (s_Lock) { if (s_Budget.CheckAmbient(n.Obj.Guid.Full, listeners, now) == No.Ok && s_Budget.ClaimEvent("town:" + s_Event.key + ":" + (int)(s_Event.until), now)) ev = s_Event.key; }
            }
            var ctx = new Ctx { Npc = n.Def.D.Key, Role = n.Def.D.Role, Moment = mo, Event = ev, Festival = Festival(), News = true };
            var pools = AmbientPools(ctx, s_Rng.NextDouble());
            // rumour-tellers sometimes point the nearest player at something for their level
            Player near = NearestPlayer(n.Obj, listeners);
            bool rumour = ev == null && n.Def.D.Rumours && near != null && s_Rng.NextDouble() < 0.15;
            string line = (rumour ? Rumour(near) : null) ?? FromPools(n, pools, null, out _, listeners);
            if (line == null) { n.NextLine = now + 30; return; }
            bool ok;
            lock (s_Lock) ok = s_Budget.TryAmbient(n.Obj.Guid.Full, listeners, now);
            if (!ok) { n.NextLine = now + 15 + s_Rng.NextDouble() * 30; return; }
            Say(n, line, listeners, ev != null ? "event " + ev : rumour ? "rumour" : "ambient");
            n.NextLine = NextLine(now, s_Budget.L.NpcGap, s_Rng.NextDouble());
        }

        static Player NearestPlayer(WorldObject c, List<Listener> listeners)
        {
            if (listeners.Count == 0) return null;
            var id = listeners.OrderBy(l => l.Dist).First().Id;
            return PlayerManager.GetOnlinePlayer(id);
        }

        /// <summary>A rumour for the player's level: from every band that holds it, one they have not heard lately; none left
        /// (box 2026-11-14: the same daily twice in two minutes), null, and the caller says something else. Unprompted
        /// rumours only point at what is fresh; an asked one may repeat.</summary>
        static string Rumour(Player p, bool asked = false)
        {
            var bands = s_Data.Rumours.Select(r => (r.Min, r.Max)).ToList();
            var all = RumourBands(p.Level ?? 1, bands).SelectMany(i => s_Data.Rumours[i].Lines).Distinct().ToList();
            if (all.Count == 0) return null;
            lock (s_Lock)
            {
                s_Heard.TryGetValue(p.Guid.Full, out var heard);
                var fresh = all.Where(l => heard == null || !heard.Contains(l)).ToList();
                if (fresh.Count == 0 && !asked) return null;
                var pick = fresh.Count > 0 ? fresh[s_Rng.Next(fresh.Count)] : all[s_Rng.Next(all.Count)];
                if (heard == null) s_Heard[p.Guid.Full] = heard = new List<string>();
                heard.Add(pick); while (heard.Count > 24) heard.RemoveAt(0);
                return pick;
            }
        }

        /// <summary>A greeter greets a player who comes within a few metres: by name, visit count, level, title or a fresh
        /// death. Prompted (not counted against the budget), once per player per NPC per GreetGap, once per ArrivalGap per player.</summary>
        static void Arrivals(Npc n, double now, List<Listener> listeners)
        {
            if (!n.Def.D.Greets || n.R.Held || n.Obj.EmoteManager?.IsBusy == true) return;
            foreach (var l in listeners)
            {
                if (l.Dist > GREET_M) continue;
                var p = PlayerManager.GetOnlinePlayer(l.Id);
                if (p == null) continue;
                bool ok;
                lock (s_Lock) ok = s_Budget.TryGreet(n.Obj.Guid.Full, p.Guid.Full, now, true);
                if (!ok) continue;
                var (visits, away) = Visit(p);
                double died; lock (s_Lock) died = s_Died.TryGetValue(p.Guid.Full, out var t) ? Wall - t : -1;
                var pools = GreetPools(n.Def.D.Key, visits, away, p.Level ?? 1, died, TitleOf(p) != null, NowMoment(), s_Rng.NextDouble());
                var line = FromPools(n, pools, p, out _);
                if (line == null) continue;
                lock (s_Lock) s_Budget.PromptedLine(listeners, now);
                double turn = n.R.State == RState.Post || n.R.State == RState.Paused ? n.Obj.Rotate(p) : 0;
                if (n.Def.Gestures.Contains(MotionCommand.Wave)) Play(n, MotionCommand.Wave, turn + 0.1, "greeting");
                SayLater(n, turn + 0.5, line, listeners, "arrival");
                return;   // one greeting per think
            }
        }

        /// <summary>A two-NPC exchange: rare, counted as one line, only with both standing and a player close.</summary>
        static void Exchanges(Npc n, double now)
        {
            if (now < s_NextExchange || s_Data.Exchanges.Count == 0) return;
            s_NextExchange = now + 20;   // one look every 20 s, town-wide
            var mo = NowMoment();
            var mine = s_Data.Exchanges.Where(e => (e.A == n.Def.D.Key || e.B == n.Def.D.Key) && (e.When == null || e.When.Any(w => TryMoment(w, out var m) && m == mo))).ToList();
            if (mine.Count == 0) return;
            var ex = mine[s_Rng.Next(mine.Count)];
            Npc a, b;
            lock (s_Lock)
            {
                a = s_Npcs.Values.FirstOrDefault(x => x.Def.D.Key == ex.A);
                b = s_Npcs.Values.FirstOrDefault(x => x.Def.D.Key == ex.B);
            }
            if (a?.Obj?.Location == null || b?.Obj?.Location == null || a.Obj.IsDestroyed || b.Obj.IsDestroyed) return;
            if (Disabled(a.Def) || Disabled(b.Def)) return;
            if (a.Obj.Location.DistanceTo(b.Obj.Location) > EXCHANGE_PAIR_M) return;
            foreach (var x in new[] { a, b })
                if (x.R.State == RState.Walking || x.R.State == RState.Returning || x.R.Held || x.Obj.EmoteManager?.IsBusy == true || now < x.MotionUntil) return;
            var ls = Listeners(a.Obj, s_Budget.L.HearRange, out _);
            foreach (var l in Listeners(b.Obj, s_Budget.L.HearRange, out _)) if (!ls.Any(o => o.Id == l.Id)) ls.Add(l);
            double gap = 4.5, length = ex.Lines.Count * gap + 1;
            bool ok;
            lock (s_Lock) ok = s_Budget.TryExchange(a.Obj.Guid.Full, b.Obj.Guid.Full, ls, now, length);
            if (!ok) return;
            a.NextLine = NextLine(now, s_Budget.L.NpcGap, s_Rng.NextDouble()); b.NextLine = NextLine(now, s_Budget.L.NpcGap, s_Rng.NextDouble());
            double t = 0.6;
            double ta = a.Obj.Rotate(b.Obj), tb = b.Obj.Rotate(a.Obj);
            t += Math.Max(ta, tb);
            foreach (var row in ex.Lines)
            {
                if (row == null || row.Length < 2) continue;
                var who = row[0] == "b" ? b : a;
                SayLater(who, t, row[1], ls, "exchange");
                t += gap;
            }
            a.MotionUntil = Math.Max(a.MotionUntil, now + t); b.MotionUntil = Math.Max(b.MotionUntil, now + t);
        }

        // ---------------------------------------------------------------- the budget, for the guards and Azimar
        /// <summary>For another module's unprompted line (the Royal Guards' ambient lines, Azimar's idle line): true = say it
        /// now (the budget has counted it). Range: the listeners it is counted for (say it to that range).</summary>
        internal static bool TryAmbient(WorldObject npc, out float range)
        {
            range = s_Budget.L.HearRange;
            if (!Speech) return false;
            var ls = Listeners(npc, range, out _);
            bool ok;
            lock (s_Lock) ok = s_Budget.TryAmbient(npc.Guid.Full, ls, Now);
            if (ok && Tracing) Mod.Log.Info($"[RevivalGuard] TownLife said (other): {npc.Name} to [{string.Join(",", ls.Select(l => $"{l.Id:X8}@{l.Dist:0}"))}] (its own line)");
            return ok;
        }

        internal static float HearRange => s_Budget.L.HearRange;

        // ---------------------------------------------------------------- events
        /// <summary>Something happened in town: a monster in the streets (the guards' call-out), a PK death. For a while the
        /// NPCs near it go home (raid) and the first one whose turn comes says one line about it.</summary>
        internal static void Notice(string key, Vector2 at, double seconds)
        {
            if (!On) return;
            double now = Now;
            if (s_Event.key == key && now < s_Event.until) return;
            s_Event = (key, now + seconds, at);
            lock (s_Lock) foreach (var n in s_Npcs.Values) if (Vector2.Distance(Local(n.Obj.Location), at) < 80) n.NextLine = Math.Min(n.NextLine, now + 2 + s_Rng.NextDouble() * 6);
            if (Tracing) Mod.Log.Info($"[RevivalGuard] TownLife event: {key} at {at.X:0},{at.Y:0} for {seconds:0} s");
        }

        internal static void NoticeAt(string key, Position p, double seconds)
        {
            if (p == null || p.Landblock != LB) return;
            Notice(key, Local(p), seconds);
        }

        /// <summary>A player levelled in town: one NPC near them says something about it (counted against the budget).</summary>
        internal static void LevelUp(Player p)
        {
            if (!On || !Speech || p?.Location == null || p.Location.Landblock != LB) return;
            double now = Now;
            Npc best = null; float bd = 15f;
            lock (s_Lock)
                foreach (var n in s_Npcs.Values)
                {
                    if (n.Obj?.Location == null || n.Obj.IsDestroyed || Disabled(n.Def) || n.R.Held) continue;
                    float d = n.Obj.Location.DistanceTo(p.Location);
                    if (d < bd) { bd = d; best = n; }
                }
            if (best == null) return;
            var ls = Listeners(best.Obj, s_Budget.L.HearRange, out _);
            bool ok;
            lock (s_Lock) ok = s_Budget.ClaimEvent("levelup:" + p.Guid.Full, now) && s_Budget.TryAmbient(best.Obj.Guid.Full, ls, now);
            if (!ok) return;
            var line = FromPools(best, new[] { $"npc.{best.Def.D.Key}.event.levelup", $"role.{best.Def.D.Role}.event.levelup", "event.levelup" }, p, out _);
            if (line == null) return;
            if (best.Def.Gestures.Contains(MotionCommand.ClapHands)) Play(best, MotionCommand.ClapHands, best.Obj.Rotate(p) + 0.1, "level-up");
            SayLater(best, 1.2, line, ls, "event levelup");
        }

        internal static void Died(Player p, bool byPlayer)
        {
            if (p == null) return;
            lock (s_Lock) { if (s_Died.Count > 500) s_Died.Clear(); s_Died[p.Guid.Full] = Wall; }
            if (byPlayer) NoticeAt("pkdeath", p.Location, 300);
        }

        // ---------------------------------------------------------------- reactions: a Use, local chat
        internal static void Used(Player p, uint target)
        {
            lock (s_Lock) if (!s_Live.Contains(target)) return;
            lock (s_Lock) if (s_Npcs.TryGetValue(target, out var n)) n.UsedAt = Now;
        }

        /// <summary>A player spoke in local chat: the nearest town NPC within a few metres (or one named) answers a greeting
        /// or a keyword. Prompted: not counted against the budget, but once per player per NPC per GreetGap for a greeting; a
        /// repeat greeting inside it gets a short acknowledgement (greet.again) or, on a busy screen, a nod (Budget.TryAgain).</summary>
        internal static void Heard(Player p, string message)
        {
            if (!On || !Speech || p?.Location == null || p.Location.Landblock != LB || s_Npcs.Count == 0) return;
            if (string.IsNullOrWhiteSpace(message) || message.Length > 120 || message.StartsWith("@") || message.StartsWith("/")) return;
            bool greet = HoltburgGuardsRule.IsGreeting(message);
            string kw = Keyword(message, s_Data.Keywords);
            if (!greet && kw == null) return;
            // a Royal Guard close by answers greetings itself (HoltburgGuards.Heard)
            Npc best = null; float bd = float.MaxValue;
            lock (s_Lock)
                foreach (var n in s_Npcs.Values)
                {
                    if (n.Obj?.Location == null || n.Obj.IsDestroyed || n.Def.D.NoChat || Disabled(n.Def)) continue;
                    float d = n.Obj.Location.DistanceTo(p.Location);
                    bool named = Names(message, n.Def.D.Name);
                    if (d > (named ? 15f : CHAT_M)) continue;
                    float score = named ? d - 100 : d;
                    if (score < bd) { bd = score; best = n; }
                }
            if (best == null) return;
            double now = Now;
            bool again = false, nodOnly = false;
            if (greet && kw == null)
            {
                bool ok; No ag = No.Ok;
                lock (s_Lock)
                {
                    ok = s_Budget.TryGreet(best.Obj.Guid.Full, p.Guid.Full, now, false);
                    if (!ok) ag = s_Budget.TryAgain(best.Obj.Guid.Full, p.Guid.Full, now);
                }
                if (!ok)
                {
                    if (ag == No.Again) return;
                    again = true; nodOnly = ag == No.Screen;
                }
            }
            string line;
            var mo = NowMoment();
            if (again) line = nodOnly ? null : FromPools(best, new[] { $"npc.{best.Def.D.Key}.again", "greet.again" }, p, out _)
                ?? AGAIN[s_Rng.Next(AGAIN.Length)];   // a TownLife.json seeded before greet.again existed (the live copy is never re-seeded)
            else if (kw == "rumour") line = best.Def.D.Rumours ? Rumour(p, true) : FromPools(best, new[] { "kw.rumour.other" }, p, out _);
            else if (kw == "drink") line = FromPools(best, best.Def.D.Role is "barkeeper" or "server" ? new[] { "kw.drink" } : new[] { "kw.drink.other" }, p, out _);
            else if (kw != null) line = FromPools(best, new[] { $"npc.{best.Def.D.Key}.kw.{kw}", "kw." + kw }, p, out _);
            else line = FromPools(best, new[] { $"npc.{best.Def.D.Key}.reply", $"role.{best.Def.D.Role}.reply", "reply." + Key(mo) }, p, out _);
            if (line == null && !again) return;
            var ls = Listeners(best.Obj, s_Budget.L.HearRange, out _);
            if (line != null) lock (s_Lock) s_Budget.PromptedLine(ls, now);
            var npc = best;
            var chain = new ActionChain();
            chain.AddAction(npc.Obj, () =>
            {
                try
                {
                    double turn = npc.R.State == RState.Post || npc.R.State == RState.Paused ? npc.Obj.Rotate(p) : 0;
                    // a repeat hello: a nod, or a wave from an NPC with no nod (box 2026-11-14: a patron without Nod made no sign)
                    var ack = npc.Def.Gestures.Contains(MotionCommand.Nod) ? MotionCommand.Nod : npc.Def.Gestures.Contains(MotionCommand.Wave) ? MotionCommand.Wave : MotionCommand.Invalid;
                    if (again && ack != MotionCommand.Invalid) Play(npc, ack, turn + 0.1, "again");
                    else if (greet && npc.Def.Gestures.Contains(MotionCommand.Wave)) Play(npc, MotionCommand.Wave, turn + 0.1, "answer");
                    else if (npc.Def.Gestures.Contains(MotionCommand.Nod)) Play(npc, MotionCommand.Nod, turn + 0.1, "answer");
                    if (line != null) SayLater(npc, turn + 0.8, line, ls, again ? "again" : "prompted");
                    else if (Tracing) Mod.Log.Info($"[RevivalGuard] TownLife again: {npc.Def.D.Key} to {p.Name}: {(ack == MotionCommand.Invalid ? "nothing shown" : ack + " only")} (screen busy)");
                }
                catch (Exception e) { TickGuard.Report("TownLife.Heard", e); }
            });
            chain.EnqueueChain();
        }

        // ---------------------------------------------------------------- @townlife
        static void Cmd(Session session, params string[] a)
        {
            void Out(string s) { if (session != null) session.Network.EnqueueSend(new GameMessageSystemChat(s, ChatMessageType.Broadcast)); else Mod.Log.Info("[RevivalGuard] @townlife: " + s); }
            try
            {
                var verb = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
                if (verb == "reload" && a.Length > 1 && a[1].ToLowerInvariant() == "embedded")
                {
                    Out("TownLife: " + SyncSeed(true));
                    Load(true); ApplyLimits(); Out($"TownLife: read {FILE} ({s_Defs.Count} NPCs)."); return;
                }
                if (verb == "reload") { Load(true); ApplyLimits(); Out($"TownLife: read {FILE} ({s_Defs.Count} NPCs)."); return; }
                if (verb == "moment" && a.Length > 1) { s_ForcedMoment = a[1] == "auto" ? null : a[1]; Out($"TownLife: moment {(s_ForcedMoment ?? "auto")} (now {NowMoment()})."); return; }
                if (verb == "event" && a.Length > 1)
                {
                    var p = session?.Player;
                    if (p?.Location == null) return;
                    NoticeAt(a[1], p.Location, a[1] == "raid" ? 120 : 300);
                    Out($"TownLife: event {a[1]} at your place.");
                    return;
                }
                var L = s_Budget.L;
                Out($"TownLife: {(On ? "on" : "OFF")}, speech {(Speech ? "on" : "OFF")}, off list '{OffList}', moment {NowMoment()}{(s_ForcedMoment != null ? " (forced)" : "")}, festival {Festival() ?? "none"}, event {(s_Event.key != null && Now < s_Event.until ? s_Event.key : "none")}");
                Out($"Budget: line gap {L.LineGap} s, NPC gap {L.NpcGap} s, exchange gap {L.ExchangeGap} s, talk {L.TalkRange} m, hear {L.HearRange} m, screen {L.Screen} s. Said: {s_Budget.Unprompted} unprompted ({s_Budget.Exchanges} exchanges), {s_Budget.Prompted} prompted, {s_Budget.Dropped} dropped.");
                lock (s_Lock)
                    foreach (var n in s_Npcs.Values.OrderBy(x => x.Def.D.Key))
                    {
                        var at = n.Obj?.Location != null ? Local(n.Obj.Location) : default;
                        Out($"  {n.Def.D.Key} ({n.Def.D.Role}): {n.R.State}{(n.R.Held ? " held" : "")} round {(n.R.Round >= 0 && n.R.Round < n.Def.Rounds.Length ? n.Def.Rounds[n.R.Round].Name : "-")} at {at.X:0.0},{at.Y:0.0}{(n.PostOk ? "" : " POST MISMATCH")}");
                    }
            }
            catch (Exception e) { Out("TownLife: " + e.Message); }
        }
    }

    // ================= hooks =================

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class TownLifeTick
    {
        static bool s_Done;
        static bool Prepare() { if (!s_Done) { s_Done = true; TownLife.Register(); } return true; }

        static void Prefix(Creature __instance)
        {
            if (!TownLife.Owns(__instance)) return;
            try { TownLife.Tick(__instance); }
            catch (Exception e) { TickGuard.Report("TownLife", e); }
        }
    }

    /// <summary>A Use of a town NPC holds it where it stands (business first); retail's Use runs untouched.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseItem), new[] { typeof(uint) })]
    static class TownLifeUse
    {
        static void Prefix(uint itemGuid)
        {
            try { TownLife.Used(null, itemGuid); }
            catch (Exception e) { TickGuard.Report("TownLife.Use", e); }
        }
    }

    /// <summary>Retail's own heartbeat chatter of a town NPC (Buckminster's, the Pathwarden's) goes through the town's speech
    /// budget too: a heartbeat set that would speak is skipped when the budget says no (motions alone always pass).</summary>
    [HarmonyPatch(typeof(ACE.Server.WorldObjects.Managers.EmoteManager), nameof(ACE.Server.WorldObjects.Managers.EmoteManager.GetEmoteSet),
        new[] { typeof(EmoteCategory), typeof(string), typeof(VendorType?), typeof(uint?), typeof(bool) })]
    static class TownLifeHeartbeat
    {
        static void Postfix(ACE.Server.WorldObjects.Managers.EmoteManager __instance, EmoteCategory category, ref ACE.Entity.Models.PropertiesEmote __result)
        {
            if (category != EmoteCategory.HeartBeat || __result == null) return;
            try
            {
                var wo = __instance.WorldObject;
                if (!TownLife.On || !TownLife.Owns(wo)) return;
                bool speaks = false;
                foreach (var a in __result.PropertiesEmoteAction)
                    if (a.Type == (uint)EmoteType.Say || a.Type == (uint)EmoteType.Tell || a.Type == (uint)EmoteType.TellFellow
                        || a.Type == (uint)EmoteType.DirectBroadcast || a.Type == (uint)EmoteType.LocalBroadcast || a.Type == (uint)EmoteType.WorldBroadcast) { speaks = true; break; }
                if (speaks && !TownLife.TryAmbient(wo, out _)) __result = null;
            }
            catch (Exception e) { TickGuard.Report("TownLife.Heartbeat", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnTalk), new[] { typeof(string) })]
    static class TownLifeHear
    {
        static void Postfix(Player __instance, string message)
        {
            try { TownLife.Heard(__instance, message); }
            catch (Exception e) { TickGuard.Report("TownLife.Hear", e); }
        }
    }

    [HarmonyPatch(typeof(Player), "CheckForLevelup")]
    static class TownLifeLevel
    {
        static void Prefix(Player __instance, out int __state) => __state = __instance.Level ?? 0;
        static void Postfix(Player __instance, int __state)
        {
            try { if ((__instance.Level ?? 0) > __state) TownLife.LevelUp(__instance); }
            catch (Exception e) { TickGuard.Report("TownLife.Level", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class TownLifeDeath
    {
        static void Postfix(Player __instance, DamageHistoryInfo lastDamager)
        {
            try { TownLife.Died(__instance, lastDamager != null && lastDamager.IsPlayer && lastDamager.Guid != __instance.Guid); }
            catch (Exception e) { TickGuard.Report("TownLife.Death", e); }
        }
    }
}
