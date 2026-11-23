using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using SetPosition = ACE.Server.Physics.Common.SetPosition;
using SetPositionFlags = ACE.Server.Physics.Common.SetPositionFlags;
using R = RevivalGuard.TownRaidRule;

namespace RevivalGuard
{
    /// <summary>
    /// HOLTBURG RAIDS (owner 2026-11-15: "random raids on Holtburg by neighbouring tribes, to keep the town alive"; ours, not
    /// retail; docs/DIVERGENCES.md; findings docs/findings/holtburg-raids-2026-11-15.md). Retail monsters only, unchanged.
    ///
    ///   WHEN    about 11 a day: none within 75 minutes of the last, then a steady chance a minute (fewer in the dead hours
    ///           Pacific, more with more players online), only with a player in town, never near the nightly restart or
    ///           during a Holtburg event (TownRaidRule.ChancePerMinute, QuietForRestart).
    ///   WARNING the lifestone Royal Guard shouts, every player in and around town reads where they come from; a minute later
    ///           the raiders appear on that side, outside the town and well away from the lifestone, and run in to a point
    ///           in town (their Home: after a fight ACE walks them back there; HomeRadius keeps a chase near town).
    ///   WHO     the tier is the lower median level of the players in town; the kind (drudges mostly, mosswarts or bandits)
    ///           and the wave by TownRaid.json. Raiders above tier 1 never pick a player under the tier's protect level who
    ///           has not hit them (new characters arrive at the lifestone).
    ///   WAVES   three (a big raid four): each a main group and a pack of weaker adds of the same family; the next comes
    ///           when most of the last is down, or after a while if someone is fighting, the lifestone guard shouting it in;
    ///           the leader comes with the last (a big raid always, else now and then). Caps: MaxAlive standing, MaxTotal.
    ///   BIG     now and then a drudge war band: bigger groups, a fourth wave, the leader with escorts; the lifestone guard calls
    ///           for reinforcements: Royal Guards come in from a gate and a placed guard is mustered up (HoltburgGuards
    ///           fights for them all: no credit taken, never killed). They leave when the raid ends.
    ///   END     held (every raider dead): each defender's reward rolls by damage share from the tier's retail treasure table,
    ///           a raid counter (quest flag rg_townraid_defended; /top raids), a retail title at 25. Withdrawn (nobody hit a
    ///           raider for 10 minutes, or 25 minutes in all), or stopped: the raiders are taken away.
    ///
    /// Switches: rg_town_raid (on), rg_town_raid_discord (OFF: owner's yes), rg_town_raid_trace. Staff (Developer):
    /// `@raid start [1-4] [drudge|mosswart|bandit] [south|west|north|east] [big]`, `@raid stop`, `@raid status`, `@raid reload`.
    /// </summary>
    static class TownRaid
    {
        internal const string P_ON = "rg_town_raid", P_DISCORD = "rg_town_raid_discord", P_TRACE = "rg_town_raid_trace";
        const string FILE = "TownRaid.json", STATE = "TownRaidState.json";
        const ushort LB = 0xA9B4;
        const string LIFESTONE_GUARD = "south";
        static readonly Random s_Rng = Random.Shared;

        static bool On { get { try { return PropertyManager.GetBool(P_ON, true).Item; } catch { return false; } } }
        static bool Discord { get { try { return PropertyManager.GetBool(P_DISCORD, false).Item; } catch { return false; } } }
        static bool Tracing { get { try { return PropertyManager.GetLong(P_TRACE, 0).Item > 0; } catch { return false; } } }
        static double Now => ACE.Common.Time.GetUnixTime();

        enum Phase { Warning, Active, Ending }

        sealed class Raid
        {
            public int Id;
            public R.Kind Kind; public R.TierDef Def; public int Tier; public R.Side Side; public bool Big, Forced;
            public R.Theme Theme; public R.ThemeTier ThemeTier;   // the festival's monsters mixed in (null: none on)
            public Phase Phase = Phase.Warning;
            public double WarnAt, SpawnAt, LastHit, EndAt, EmptySince;
            public int Players, Spawned, Killed, Pending;
            public int WaveNo, Waves, WaveSize;                // waves sent, waves in all, the size of the last one
            public double WaveAt;                              // when the last wave appeared
            public bool LeaderRolled, Reinforced, Arrived;
            public readonly List<string> WaveLog = new List<string>();
            public R.Outcome Outcome;
            public readonly List<WeakReference<Creature>> Wave = new List<WeakReference<Creature>>();   // the last wave's
            public readonly List<WeakReference<Creature>> All = new List<WeakReference<Creature>>();
            public readonly List<WeakReference<Creature>> Reserves = new List<WeakReference<Creature>>();
            public readonly Dictionary<uint, double> Damage = new Dictionary<uint, double>();
            public bool Mustered;
            public readonly HashSet<uint> Detached = new HashSet<uint>();
            public readonly Dictionary<uint, (Vector2 at, double since)> Still = new Dictionary<uint, (Vector2, double)>();
            public string ThemeGroup;                          // a themed raid's name in the lines (the tier's, else the theme's)
            public string Group => Theme != null && Theme.Replace ? ThemeGroup ?? Theme.Group : Kind?.Group ?? "raiders";
        }

        sealed class Tag { public int RaidId; public int ProtectBelow; public bool Reserve; }
        static readonly ConditionalWeakTable<Creature, Tag> s_Raiders = new ConditionalWeakTable<Creature, Tag>();
        static readonly ConditionalWeakTable<Creature, Tag> s_Reserves = new ConditionalWeakTable<Creature, Tag>();

        sealed class State
        {
            public double LastStart { get; set; }
            public List<string> History { get; set; } = new List<string>();
        }

        static readonly object s_Lock = new object();
        static R.Config s_Cfg = new R.Config();
        static DateTime s_Stamp; static double s_NextLoad, s_NextRoll, s_NextTick;
        static Raid s_Raid;
        /// <summary>A raid on Holtburg now (Drinking never carries a drunk into one).</summary>
        internal static bool Running => s_Raid != null;
        static int s_NextId = 1;
        static State s_State = new State();

        // ---------------------------------------------------------------- start up

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard TownRaid: random raids on Holtburg by drudges, mosswarts and bandits (false: none; a running raid ends)");
            PkCommon.RegisterBool(P_DISCORD, false, "RevivalGuard TownRaid: post each Holtburg raid's start and result to the Discord bridge's General channel (needs the owner's yes)");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_TRACE)) l[P_TRACE] = new Property<long>(0, "RevivalGuard TownRaid: 1 logs each raider's place and the schedule's rolls");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid: trace switch not registered ({e.Message})"); }
            CommandManager.TryAddCommand(Cmd, "raid", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "Holtburg raids: start one now (tier 1-4, kind, side, big, a festival's monsters mixed in or none), stop the running one, status, reload the data file.",
                "[start [1-4] [drudge|mosswart|bandit] [south|west|north|east] [big] [<festival key>|none] | stop | status | reload]");
            SyncSeed();
            Load(true);
            LoadState();
            // a deploy or restart never raids at once: at least 20 minutes first
            double now = Now;
            if (now - s_State.LastStart > s_Cfg.Schedule.MinGapMinutes * 60 - 20 * 60) s_State.LastStart = now - s_Cfg.Schedule.MinGapMinutes * 60 + 20 * 60;
            Mod.Log.Info($"[RevivalGuard] TownRaid: Holtburg raids {(On ? "ON" : "off")}, {s_Cfg.Kinds.Count} kinds on {s_Cfg.Sides.Count} sides, Discord {(Discord ? "ON" : "off")} (@raid)");
        }

        // ---------------------------------------------------------------- data file (seeded beside the dll, as TownLife.json)

        static string FilePath => SeasonalCalendar.FileBeside(FILE);
        static string Sidecar => FilePath + ".seed";

        static string Seed()
        {
            using var rs = typeof(TownRaid).Assembly.GetManifestResourceStream("RevivalGuard.TownRaid.json");
            if (rs == null) return "{}";
            using var rd = new StreamReader(rs);
            return rd.ReadToEnd();
        }

        static string SyncSeed(bool force = false)
        {
            try
            {
                var path = FilePath; var seed = Seed(); var eh = SeedFileRule.Hash(seed);
                string dh = File.Exists(path) ? SeedFileRule.Hash(File.ReadAllText(path)) : null;
                string wh = null;
                try { if (File.Exists(Sidecar)) wh = File.ReadAllText(Sidecar).Trim(); } catch { }
                if (string.IsNullOrEmpty(wh)) wh = null;
                var act = SeedFileRule.Decide(dh, eh, wh, Array.Empty<string>(), force);
                switch (act)
                {
                    case SeedFileRule.Act.UpToDate: if (wh != eh) File.WriteAllText(Sidecar, eh); return $"{FILE} is the embedded version {eh}";
                    case SeedFileRule.Act.Keep: return $"{FILE} is hand-edited ({dh}), kept";
                    case SeedFileRule.Act.KeepEdited:
                        Mod.Log.Warn($"[RevivalGuard] TownRaid: {FILE} was edited by hand and a newer embedded version {eh} exists: kept the edited file (`@raid reload embedded` takes the new one)");
                        return $"{FILE} hand-edited, kept; a newer embedded version exists";
                    default:
                        if (dh != null) { var kept = SeedFileRule.BackupName(path, eh); for (int i = 2; File.Exists(kept); i++) kept = SeedFileRule.BackupName(path, eh) + "-" + i; File.Copy(path, kept); }
                        File.WriteAllText(path + ".tmp", seed); File.Move(path + ".tmp", path, true); File.WriteAllText(Sidecar, eh);
                        Mod.Log.Info($"[RevivalGuard] TownRaid: wrote {FILE} (embedded version {eh})");
                        return $"wrote {FILE} (embedded version {eh})";
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid: {FILE} not checked against the embedded copy: {e.Message}"); return "not checked: " + e.Message; }
        }

        static string Load(bool force)
        {
            lock (s_Lock)
            {
                try
                {
                    var path = FilePath;
                    string text;
                    if (!File.Exists(path)) text = Seed();
                    else
                    {
                        var stamp = File.GetLastWriteTimeUtc(path);
                        if (!force && stamp == s_Stamp) return null;
                        s_Stamp = stamp;
                        text = File.ReadAllText(path);
                    }
                    var cfg = R.Parse(text);
                    var problems = R.Problems(cfg);
                    if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
                    s_Cfg = cfg;
                    var msg = $"read {FILE}: {cfg.Kinds.Count} kinds, {cfg.Sides.Count} sides, {cfg.Lines.Count} line pools";
                    Mod.Log.Info("[RevivalGuard] TownRaid: " + msg);
                    return msg;
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] TownRaid: {FILE} not read, keeping the last data: {e.Message}");
                    return "not read: " + e.Message;
                }
            }
        }

        static void LoadState()
        {
            try
            {
                var p = SeasonalCalendar.FileBeside(STATE);
                if (File.Exists(p)) s_State = JsonSerializer.Deserialize<State>(File.ReadAllText(p)) ?? new State();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid: {STATE} not read: {e.Message}"); }
        }

        static void SaveState()
        {
            try
            {
                var p = SeasonalCalendar.FileBeside(STATE);
                if (s_State.History.Count > 60) s_State.History.RemoveRange(0, s_State.History.Count - 60);
                File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(s_State, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(p + ".tmp", p, true);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid: {STATE} not written: {e.Message}"); }
        }

        // ---------------------------------------------------------------- geometry (A9B4-local metres)

        static Vector2 Local(Position p)
        {
            var gl = p.ToGlobal();
            return new Vector2(gl.X - 0xA9 * 192f, gl.Y - 0xB4 * 192f);
        }

        /// <summary>A point in A9B4 metres as an outdoor position on the ground (any of the blocks round Holtburg).</summary>
        static Position At(float x, float y)
        {
            int bx = 0xA9 + (int)Math.Floor(x / 192f), by = 0xB4 + (int)Math.Floor(y / 192f);
            float lx = x - (bx - 0xA9) * 192f, ly = y - (by - 0xB4) * 192f;
            uint cell = (uint)((int)(lx / 24) * 8 + (int)(ly / 24) + 1);
            var p = new Position((uint)(bx << 24 | by << 16) | cell, lx, ly, 0, 0, 0, 0, 1);
            try { p.PositionZ = p.GetTerrainZ(); } catch { }
            return p;
        }

        static Position At(float[] pt, float jitter = 0)
        {
            float x = pt[0], y = pt[1];
            if (jitter > 0) { x += (float)(s_Rng.NextDouble() * 2 - 1) * jitter; y += (float)(s_Rng.NextDouble() * 2 - 1) * jitter; }
            return At(x, y);
        }

        static bool NearTown(Player p, float margin)
        {
            var l = p?.Location;
            if (l == null || l.Indoors && l.Landblock != LB) return false;
            if (Math.Abs((int)l.LandblockX - 0xA9) > 1 || Math.Abs((int)l.LandblockY - 0xB4) > 1) return false;
            var at = Local(l);
            return R.InBox(s_Cfg, at.X, at.Y, margin);
        }

        /// <summary>Players in town (the town box and 20 m beyond): not the Hollow Company; the Borg count, as players.</summary>
        static List<Player> InTown() => PlayerManager.GetAllOnline().Where(p => p != null && !Mercenaries.IsMerc(p) && NearTown(p, 20)).ToList();

        /// <summary>Who hears the warnings and results: anyone in or around town (150 m beyond its edge).</summary>
        static List<Player> Around() => PlayerManager.GetAllOnline().Where(p => p != null && NearTown(p, 150)).ToList();

        static void Tell(IEnumerable<Player> to, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var p in to) p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        static void Shout(string route, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var g = HoltburgGuards.GuardOn(route);
            if (g == null) return;
            var chain = new ActionChain();
            chain.AddAction(g, () => HoltburgGuards.Say(g, text, 120f));
            chain.EnqueueChain();
        }

        static Dictionary<string, string> Tokens(Raid r, string leader = null) => new Dictionary<string, string>
        {
            ["group"] = r.Group, ["side"] = r.Side?.Name ?? "", ["leader"] = leader ?? "", ["count"] = "", ["town"] = s_Cfg.Town,
            ["theme"] = r.Theme?.Group ?? "",
        };

        static string L(string key, IDictionary<string, string> tokens = null) => R.Line(s_Cfg, key, s_Rng.NextDouble(), tokens);

        /// <summary>Is a festival on now (its calendar window or a GM's @festival)? A theme's monsters come only then.</summary>
        static bool FestivalOn(string key) { try { return SeasonalCalendar.IsFestivalActive(key); } catch { return false; } }

        static void Post(string text)
        {
            if (!Discord || string.IsNullOrEmpty(text)) return;
            if (!DiscordBridge.PostGeneral(text)) Mod.Log.Info($"[RevivalGuard] TownRaid: Discord not configured; would post: {text}");
        }

        // ---------------------------------------------------------------- the tick (world thread, PlayerManager.Tick)

        internal static void Tick()
        {
            double now = Now;
            if (now < s_NextTick) return;
            s_NextTick = now + 2;
            if (now >= s_NextLoad) { s_NextLoad = now + 30; Load(false); }
            var r = s_Raid;

            if (r != null && ServerManager.ShutdownInitiated && r.Phase != Phase.Ending) { End(r, R.Outcome.Faded, now, "the server is going down"); return; }
            if (r != null && !On && r.Phase != Phase.Ending) { End(r, R.Outcome.Faded, now, "switched off"); return; }

            if (r == null) { MaybeStart(now); return; }
            switch (r.Phase)
            {
                case Phase.Warning:
                    if (now >= r.WarnAt + s_Cfg.Schedule.WarningSeconds && r.Pending == 0) SpawnWave(r);
                    break;
                case Phase.Active:
                    Watch(r, now);
                    break;
                case Phase.Ending:
                    Leave(r, now);
                    break;
            }
        }

        static void MaybeStart(double now)
        {
            if (now < s_NextRoll) return;
            s_NextRoll = now + 60;
            if (!On || ServerManager.ShutdownInitiated) return;
            var s = s_Cfg.Schedule;
            var utc = DateTime.UtcNow;
            if (R.QuietForRestart(utc, s)) return;
            if (R.PausedForEvent(EventDirector.CurrentEvent()?.key, s)) return;
            var town = InTown();
            if (town.Count < s.MinPlayersInTown) return;
            int online = PlayerManager.GetAllOnline().Count(p => !Mercenaries.IsMerc(p));
            double chance = R.ChancePerMinute(s, (now - s_State.LastStart) / 60.0, PkNight.PacificLocal(utc).Hour, online);
            double roll = s_Rng.NextDouble();
            if (Tracing && chance > 0) Mod.Log.Info($"[RevivalGuard] TownRaid: roll {roll:0.000} against {chance:0.000} ({town.Count} in town, {online} online)");
            if (roll >= chance) return;
            Begin(null, null, null, null, false, now, null);
        }

        /// <summary>A raid begins: the warning now, the raiders a minute later. Null arguments are rolled.</summary>
        static string Begin(int? tier, string kind, string side, bool? big, bool forced, double now, string theme)
        {
            if (s_Raid != null) return "a raid is already running";
            var town = InTown();
            int t = tier ?? R.TierFor(town.Select(p => p.Level ?? 1).ToList(), s_Cfg.TierFloors);
            var (k, def) = R.PickKind(s_Cfg, t, s_Rng.NextDouble(), kind);
            if (k == null) return "no kind of raider fits";
            var sd = R.PickSide(s_Cfg, k, s_Rng.NextDouble(), side);
            if (sd == null) return "no such side";
            // a festival on: its monsters make the raid (or are mixed in); the kind still picks the side and the tier's numbers
            var (th, tt) = R.PickTheme(s_Cfg, def.Tier, FestivalOn, s_Rng.NextDouble(), theme);
            if (th != null && th.Replace) def = R.Themed(def, tt);
            bool isBig = R.CanBeBig(def) && (big ?? s_Rng.NextDouble() < k.BigChance);
            var r = new Raid { Id = s_NextId++, Kind = k, Def = def, Tier = def.Tier, Side = sd, Big = isBig, Forced = forced, WarnAt = now, Players = Math.Max(1, town.Count) };
            r.Waves = R.WaveCount(s_Cfg.Waves, isBig);
            r.Theme = th; r.ThemeTier = th != null && !th.Replace ? tt : null;   // ThemeTier: the mix only
            r.ThemeGroup = tt?.Group;
            r.LeaderRolled = s_Rng.NextDouble() < s_Cfg.Waves.LeaderChance;
            s_Raid = r;
            s_State.LastStart = now;
            var tok = Tokens(r);
            Shout(LIFESTONE_GUARD, r.Theme != null && r.Theme.Replace ? L("warn.theme", tok)
                : L($"warn.{k.Key}.{sd.Key}", tok) ?? L($"warn.{k.Key}.south", tok));
            var around = Around();
            Tell(around, L("area.warn", tok));
            if (isBig) Tell(around, L("area.big", tok));
            if (r.Theme != null) Tell(around, L($"theme.{r.Theme.Festival}", tok) ?? L("area.theme", tok));
            var to = sd.To;
            TownLife.Notice("raid", new Vector2(to[0], to[1]), s_Cfg.Schedule.WarningSeconds + 180 + (r.Waves - 1) * s_Cfg.Waves.NextAfterSeconds);
            Post(L("discord.start", tok));
            Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} {(forced ? "started by staff" : "rolled")}: {k.Key} tier {r.Tier}{(isBig ? " BIG" : "")} from the {sd.Key}, {r.Waves} waves{(def.Leader != 0 && (isBig || r.LeaderRolled) ? ", leader with the last" : "")}{(r.Theme != null ? $", {r.Theme.Festival}: {r.Group}{(r.Theme.Replace ? "" : " mixed in")}" : "")}, {town.Count} in town ({string.Join(", ", town.Select(p => $"{p.Name} {p.Level}"))}); raiders in {s_Cfg.Schedule.WarningSeconds:0} s");
            return $"#{r.Id}: {r.Group} tier {r.Tier}{(isBig ? ", a big wave" : "")}{(r.Theme != null ? $" ({r.Theme.Festival})" : "")} from {sd.Name}; they appear in {s_Cfg.Schedule.WarningSeconds:0} s";
        }

        // ---------------------------------------------------------------- spawning

        static int NextSize(Raid r)
        {
            var (main, adds, leader, escorts) = R.PlanWave(s_Cfg, r.Def, r.Players, r.Big, r.WaveNo + 1, r.Waves, r.LeaderRolled, r.Spawned);
            return main + adds + escorts + (leader ? 1 : 0);
        }

        /// <summary>The next wave: the main group, its adds, and with the last the leader (and a big raid's escorts).</summary>
        static void SpawnWave(Raid r)
        {
            var def = r.Def;
            int no = r.WaveNo + 1;
            bool first = no == 1;
            var (main, adds, leader, escorts) = R.PlanWave(s_Cfg, def, r.Players, r.Big, no, r.Waves, r.LeaderRolled, r.Spawned);
            var wcids = R.WaveWeenies(def, r.ThemeTier, r.Theme?.Share ?? 0, main, adds, leader, escorts, s_Rng.Next);
            r.WaveNo = no;
            r.Pending++;
            var side = r.Side;
            if (!first)
            {
                var tok = Tokens(r);
                Shout(LIFESTONE_GUARD, L(no >= r.Waves ? "guard.lastwave" : "guard.wave", tok));
                Tell(Around(), L("area.wave", tok));
            }
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    var made = new List<Creature>();
                    int madeAdds = 0;
                    r.Wave.Clear();
                    foreach (var (w, add, _) in wcids)
                    {
                        // adds scatter wider and come in a little behind and around the group
                        var c = Spawn(w, At(side.From, add ? 6f : 0f), add ? 12f : 8f) as Creature;
                        if (c == null) continue;
                        s_Raiders.AddOrUpdate(c, new Tag { RaidId = r.Id, ProtectBelow = def.ProtectBelow });
                        r.All.Add(new WeakReference<Creature>(c));
                        r.Wave.Add(new WeakReference<Creature>(c));
                        made.Add(c);
                        if (add) madeAdds++;
                        Send(c, At(side.To, add ? 8f : 5f));
                    }
                    r.Spawned += made.Count;
                    r.WaveSize = made.Count;
                    double t = Now;
                    r.WaveAt = t;
                    r.LastHit = Math.Max(r.LastHit, t);          // each wave gets a full idle window
                    if (first) { r.SpawnAt = t; r.Phase = Phase.Active; }
                    r.WaveLog.Add($"{made.Count - madeAdds - (leader ? 1 + escorts : 0)}+{madeAdds}{(leader ? "+L" : "")}{(escorts > 0 ? $"+{escorts}e" : "")}");
                    if (made.Count == 0) { Mod.Log.Warn($"[RevivalGuard] TownRaid: #{r.Id} wave {no}: nothing spawned (weenies {string.Join(",", wcids.Select(x => x.wcid))})"); if (first) End(r, R.Outcome.Faded, Now, "nothing spawned"); return; }
                    if (leader)
                    {
                        var lead = made.FirstOrDefault(c => c.WeenieClassId == def.Leader) ?? made[0];
                        Tell(Around(), L("leader", Tokens(r, lead.Name)));
                        BossBroadcast.Mark(lead, $"the leader of the {s_Cfg.Town} raid");
                    }
                    Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} wave {no} of {r.Waves}: {made.Count} ({made.Count - madeAdds - (leader ? 1 + escorts : 0)} in the group, {madeAdds} adds{(leader ? $", the leader{(escorts > 0 ? $" and {escorts} escorts" : "")}" : "")}) x [{string.Join(", ", made.GroupBy(c => c.Name).Select(g => $"{g.Count()} {g.Key} ({g.First().WeenieClassId}, level {g.First().Level})"))}] at the {side.Key} ({side.From[0]:0},{side.From[1]:0}) running to ({side.To[0]:0},{side.To[1]:0}); {r.Spawned} in all, {Live(r.All).Count()} standing");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid: spawn: {e}"); }
                finally { r.Pending--; }
            }));
            if (leader && r.Big) Reinforce(r);
        }

        /// <summary>A Generic object entering the world, scattered on the ground as ACE's own generators place theirs
        /// (TthuunRaids.Spawn).</summary>
        static WorldObject Spawn(uint wcid, Position at, float radius)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] TownRaid: no weenie {wcid}"); return null; }
            wo.Location = new Position(at);
            wo.Location.PositionZ += 0.05f;
            // never rots: ACE's decay (WorldObject.IsDecayable) takes any dynamic object with no generator, lifespan or
            // TimeToRot after 5 minutes, silently: raiders and reserves vanished mid-raid on the box (2026-11-16).
            // The raid removes what it spawned itself (end, stop, stuck), and a raid lasts 25 minutes at most.
            wo.TimeToRot = -1;
            if (radius > 0) wo.ScatterPos = new SetPosition(new ACE.Server.Physics.Common.Position(wo.Location), SetPositionFlags.RandomScatter, radius);
            bool ok = wo.EnterWorld();
            wo.ScatterPos = null;
            if (!ok) { wo.Destroy(); return null; }
            return wo;
        }

        /// <summary>A raider's home is its point in town: it runs there now, and ACE walks it back there after each fight;
        /// HomeRadius pulls a chase back before it strays far.</summary>
        static void Send(Creature c, Position to)
        {
            var chain = new ActionChain();
            chain.AddAction(c, () =>
            {
                try
                {
                    if (c.IsDead || c.IsDestroyed) return;
                    c.Home = new Position(to);
                    c.HomeRadius = s_Cfg.HomeRadius;
                    // Creature.MoveTo runs the walk on the server for a sleeping monster too (MoveToHome leaves it to the
                    // awake monster's own tick); once it wakes on a player, ACE's monster tick takes over
                    c.MoveTo(new Position(to), Math.Max(1f, c.GetRunRate()));
                }
                catch (Exception e) { TickGuard.Report("TownRaid.Send", e); }
            });
            chain.EnqueueChain();
        }

        // ---------------------------------------------------------------- the fight

        static IEnumerable<Creature> Live(List<WeakReference<Creature>> l) =>
            l.Select(w => w.TryGetTarget(out var c) ? c : null).Where(c => c != null && !c.IsDead && !c.IsDestroyed);

        static void Watch(Raid r, double now)
        {
            // every raider not dead or destroyed counts, loaded or not (a block with nobody near can go quiet under it)
            var alive = Live(r.All).ToList();
            if (InTown().Count == 0) { if (r.EmptySince == 0) r.EmptySince = now; } else r.EmptySince = 0;
            foreach (var c in alive)
            {
                if (c.CurrentLandblock == null || c.Location == null)
                {
                    if (r.Detached.Add(c.Guid.Full)) Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} {c.Name} 0x{c.Guid.Full:X8} has no landblock (destroyed {c.IsDestroyed})");
                    continue;
                }
                // backstop: a raider on a protected player who has not hurt it lets go and goes back to its point
                if (c.AttackTarget is Creature tgt && Protected(tgt, c))
                {
                    var cc0 = c;
                    var ch0 = new ActionChain();
                    ch0.AddAction(cc0, () => { try { if (!cc0.IsDead && !cc0.IsDestroyed && cc0.AttackTarget is Creature t0 && Protected(t0, cc0)) { cc0.AttackTarget = null; cc0.MoveToHome(); } } catch (Exception e) { TickGuard.Report("TownRaid.Protect", e); } });
                    ch0.EnqueueChain();
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} {c.Name} let go of {tgt.Name} (under the protect level)");
                }
                // stuck outside the town (caught on the ground, unable to reach anyone): it slinks off after a while
                var here = Local(c.Location);
                if (R.InBox(s_Cfg, here.X, here.Y, 0)) r.Still.Remove(c.Guid.Full);
                else if (!r.Still.TryGetValue(c.Guid.Full, out var st) || Vector2.Distance(st.at, here) > 1.5f) r.Still[c.Guid.Full] = (here, now);
                else if (R.StuckOutside(now - st.since, c.AttackTarget != null || now - r.LastHit < s_Cfg.Schedule.FightGraceSeconds, s_Cfg.Schedule))
                {
                    r.Still.Remove(c.Guid.Full);
                    var gone = c;
                    var ch = new ActionChain();
                    ch.AddAction(gone, () => { try { if (!gone.IsDead && !gone.IsDestroyed) gone.Destroy(); } catch (Exception e) { TickGuard.Report("TownRaid.Stuck", e); } });
                    ch.EnqueueChain();
                    Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} {c.Name} 0x{c.Guid.Full:X8} stuck outside the town at {here.X:0},{here.Y:0} for {now - st.since:0} s: taken away");
                    continue;
                }
                var log = c.DamageHistory?.Log;
                if (log != null && log.Count > 0)
                {
                    double t = (log[log.Count - 1].Time - DateTime.UnixEpoch).TotalSeconds;
                    if (t > r.LastHit) r.LastHit = Math.Min(now, t);
                }
                // never inside a building: back to its point in town
                if (c.Location != null && c.Location.Indoors && c.MonsterState != Creature.State.Return)
                {
                    var cc = c;
                    var chain = new ActionChain();
                    chain.AddAction(cc, () => { try { if (!cc.IsDead && !cc.IsDestroyed) cc.MoveToHome(); } catch (Exception e) { TickGuard.Report("TownRaid.Indoors", e); } });
                    chain.EnqueueChain();
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} {c.Name} went indoors, sent back to its point");
                }
            }
            if (!r.Arrived && alive.Any(c => { if (c.Location == null) return false; var at = Local(c.Location); return R.InBox(s_Cfg, at.X, at.Y, 0); }))
            {
                r.Arrived = true;
                Tell(Around(), L("area.arrive", Tokens(r)));
                Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} the raiders reach the town ({now - r.SpawnAt:0} s after they appeared)");
            }
            if (Tracing)
                foreach (var c in alive.Where(x => x.Location != null)) { var at = Local(c.Location); Mod.Log.Info($"[RevivalGuard] TownRaid trace: #{r.Id} {c.Name} 0x{c.Guid.Full:X8} at {at.X:0},{at.Y:0} lb {(c.CurrentLandblock?.Id.Raw ?? 0) >> 16:X4}{(c.CurrentLandblock?.IsDormant == true ? " DORMANT" : "")} {c.MonsterState} hp {c.Health.Current}/{c.Health.MaxValue} target {c.AttackTarget?.Name ?? "-"}"); }

            bool moreToCome = r.WaveNo < r.Waves;
            if (moreToCome && r.Pending == 0
                && R.NextWaveDue(r.WaveSize, Live(r.Wave).Count(), now - r.WaveAt, r.LastHit > r.WaveAt, alive.Count, NextSize(r), s_Cfg.Waves))
            {
                SpawnWave(r);
                return;
            }
            if (r.Pending > 0) return;
            var outcome = R.Judge(r.Spawned, alive.Count, r.Killed, moreToCome, now - r.SpawnAt, now - r.LastHit, s_Cfg.Schedule, r.EmptySince > 0 ? now - r.EmptySince : 0);
            if (outcome != R.Outcome.Running) End(r, outcome, now, null);
        }

        /// <summary>The big wave: the lifestone guard calls, Royal Guards come in from the side's gate, and a placed guard
        /// is mustered to the fight.</summary>
        static void Reinforce(Raid r)
        {
            if (r.Reinforced) return;
            r.Reinforced = true;
            var side = r.Side;
            Shout(LIFESTONE_GUARD, L("guard.reinforce"));
            int n = Math.Min(s_Cfg.Big.Defenders, side.GuardsAt.Count);
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        var c = Spawn(s_Cfg.Big.DefenderWcid, At(side.GuardsFrom), 2f) as Creature;
                        if (c == null) continue;
                        r.Reserves.Add(new WeakReference<Creature>(c));
                        s_Reserves.AddOrUpdate(c, new Tag { RaidId = r.Id, Reserve = true });
                        var path = new List<Position> { At(side.GuardsAt[i]) };
                        var cc = c; int idx = i;
                        var chain = new ActionChain();
                        chain.AddAction(cc, () => { try { HoltburgGuards.AddReserve(cc, $"reserve {idx + 1}", path); } catch (Exception e) { TickGuard.Report("TownRaid.Reserve", e); } });
                        if (idx == 0) { chain.AddDelaySeconds(3); chain.AddAction(cc, () => HoltburgGuards.Say(cc, L("guard.reserve"), 60f)); }
                        chain.EnqueueChain();
                    }
                    Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} reinforcements: {r.Reserves.Count} Royal Guards from ({side.GuardsFrom[0]:0},{side.GuardsFrom[1]:0})");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid: reinforcements: {e.Message}"); }
            }));
            if (!string.IsNullOrEmpty(side.Muster) && side.MusterPath.Count > 0)
            {
                var path = side.MusterPath.Select(p => At(p)).ToList();
                r.Mustered = HoltburgGuards.Muster(side.Muster, path);
                Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} the {side.Muster} guard {(r.Mustered ? "is mustered" : "is not loaded; not mustered")}");
            }
        }

        // ---------------------------------------------------------------- the end

        static void End(Raid r, R.Outcome outcome, double now, string why)
        {
            if (r.Phase == Phase.Ending) return;
            r.Phase = Phase.Ending; r.Outcome = outcome; r.EndAt = now;
            var tok = Tokens(r);
            var around = Around();
            int defenders = 0;
            if (outcome == R.Outcome.Held)
            {
                Shout(LIFESTONE_GUARD, L("guard.held"));
                Tell(around, L("area.held", tok));
                defenders = Reward(r);
                tok["count"] = defenders.ToString();
                Post(L("discord.held", tok));
            }
            else
            {
                Tell(around, L(outcome == R.Outcome.Withdrew ? "area.withdrew" : "area.stopped", tok));
                if (outcome == R.Outcome.Withdrew) Post(L("discord.withdrew", tok));
            }
            // whatever raider is still standing goes (held at 75% leaves a few; withdrawn or stopped leaves them all)
            foreach (var c in Live(r.All).ToList())
            {
                var cc = c;
                var chain = new ActionChain();
                chain.AddAction(cc, () => { try { if (!cc.IsDead && !cc.IsDestroyed) cc.Destroy(); } catch (Exception e) { TickGuard.Report("TownRaid.Remove", e); } });
                chain.EnqueueChain();
            }
            // the reserves walk back out; the mustered guard back to its round
            foreach (var c in Live(r.Reserves))
            {
                var cc = c;
                var back = new List<Position> { At(r.Side.GuardsFrom) };
                var chain = new ActionChain();
                chain.AddAction(cc, () => { try { HoltburgGuards.SendOff(cc, back); } catch (Exception e) { TickGuard.Report("TownRaid.SendOff", e); } });
                chain.EnqueueChain();
            }
            if (r.Mustered) HoltburgGuards.Muster(r.Side.Muster, null);
            s_State.History.Add($"{DateTime.UtcNow:yyyy-MM-dd HH:mm}Z #{r.Id} {r.Kind.Key} t{r.Tier}{(r.Big ? " big" : "")}{(r.Theme != null ? $" +{r.Theme.Festival}" : "")} {r.Side.Key}: {outcome}, waves {string.Join(" ", r.WaveLog)} of {r.Waves}, {r.Killed}/{r.Spawned} killed, {defenders} rewarded{(r.Forced ? " (staff)" : "")}");
            SaveState();
            Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} ends {outcome}{(why != null ? $" ({why})" : "")}: {r.WaveNo} of {r.Waves} waves ({string.Join(" ", r.WaveLog)}), {r.Killed} of {r.Spawned} raiders killed in {(r.SpawnAt > 0 ? now - r.SpawnAt : 0):0} s, {r.Damage.Count} player(s) did damage, {defenders} rewarded");
        }

        static void Leave(Raid r, double now)
        {
            var reserves = Live(r.Reserves).ToList();
            bool timeUp = now - r.EndAt > 60;
            foreach (var c in reserves)
            {
                if (!timeUp && !HoltburgGuards.Gone(c)) continue;
                var cc = c;
                Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} reserve 0x{c.Guid.Full:X8} leaves at {Local(c.Location).X:0},{Local(c.Location).Y:0}{(timeUp ? " (time up)" : "")}");
                var chain = new ActionChain();
                chain.AddAction(cc, () => { try { HoltburgGuards.ForgetReserve(cc.Guid.Full); if (!cc.IsDestroyed) cc.Destroy(); } catch (Exception e) { TickGuard.Report("TownRaid.Leave", e); } });
                chain.EnqueueChain();
            }
            if (reserves.Count == 0 || timeUp) s_Raid = null;
        }

        /// <summary>A held town: each defender's rolls from the tier's treasure table, the counter, the title.</summary>
        static int Reward(Raid r)
        {
            var rolls = R.Rolls(r.Damage, s_Cfg.Rewards);
            var profile = r.Def.Treasure != 0 ? DatabaseManager.World.GetCachedDeathTreasure(r.Def.Treasure) : null;
            int n = 0;
            foreach (var kv in rolls)
            {
                var p = PlayerManager.GetOnlinePlayer(kv.Key);
                if (p == null) continue;
                n++;
                int count = kv.Value;
                var chain = new ActionChain();
                chain.AddAction(p, () =>
                {
                    try
                    {
                        for (int i = 0; i < count && profile != null; i++)
                        {
                            var item = LootGenerationFactory.CreateRandomLootObjects(profile, TreasureItemCategory.MagicItem)
                                       ?? LootGenerationFactory.CreateRandomLootObjects(profile, TreasureItemCategory.Item);
                            if (item == null) continue;
                            string name = item.NameWithMaterial ?? item.Name;
                            if (p.TryCreateInInventoryWithNetworking(item)) Say(p, L("reward", new Dictionary<string, string> { ["item"] = name }));
                            else
                            {
                                item.Location = new Position(p.Location);
                                item.Location.PositionZ += 0.1f;
                                if (item.EnterWorld()) Say(p, L("reward.full", new Dictionary<string, string> { ["item"] = name }));
                                else item.Destroy();
                            }
                        }
                        var rw = s_Cfg.Rewards;
                        if (!string.IsNullOrWhiteSpace(rw.Counter))
                        {
                            int before = p.QuestManager.GetCurrentSolves(rw.Counter);
                            p.QuestManager.Stamp(rw.Counter);
                            int now = p.QuestManager.GetCurrentSolves(rw.Counter);
                            Say(p, L("counter", new Dictionary<string, string> { ["n"] = now.ToString() }));
                            if (R.TitleDue(before, now, rw) && Enum.TryParse<CharacterTitle>(rw.Title, out var title))
                            {
                                p.AddTitle(title);
                                Say(p, L("title"));
                            }
                        }
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid: reward for {p.Name}: {e.Message}"); }
                });
                chain.EnqueueChain();
                Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} {p.Name}: {r.Damage[kv.Key]:0} damage of {r.Damage.Values.Sum():0}, {count} roll(s) from treasure {r.Def.Treasure}");
            }
            return n;
        }

        static void Say(Player p, string text)
        {
            if (!string.IsNullOrEmpty(text)) p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        // ---------------------------------------------------------------- hooks' questions

        /// <summary>Creature.OnDeath of a raider: the kill counts, and its damage log says who defended.</summary>
        internal static void OnDeath(Creature c)
        {
            if (!s_Raiders.TryGetValue(c, out var tag)) return;
            var r = s_Raid;
            if (r == null || r.Id != tag.RaidId) return;
            r.Killed++;
            r.LastHit = Now;
            foreach (var info in c.DamageHistory.TotalDamage.Values)
            {
                if (info.TotalDamage <= 0) continue;
                var p = info.TryGetAttacker() as Player ?? info.TryGetPetOwner();
                if (p == null) continue;
                r.Damage[p.Guid.Full] = (r.Damage.TryGetValue(p.Guid.Full, out var d) ? d : 0) + info.TotalDamage;
            }
            if (Tracing) Mod.Log.Info($"[RevivalGuard] TownRaid: #{r.Id} {c.Name} killed ({r.Killed} of {r.Spawned})");
        }

        /// <summary>Creature.GenerateTreasure of a raider: a quest item its retail list carries (the Robber Baron's head)
        /// stays in its dungeon.</summary>
        internal static void OnTreasure(Creature c, Corpse corpse)
        {
            if (corpse == null || !s_Raiders.TryGetValue(c, out _)) return;
            var strip = s_Cfg.Rewards.StripItems;
            if (strip == null || strip.Length == 0) return;
            foreach (var item in corpse.Inventory.Values.Where(i => Array.IndexOf(strip, i.WeenieClassId) >= 0).ToList())
                if (corpse.TryRemoveFromInventory(item.Guid, out var gone)) gone?.Destroy();
        }

        internal static bool IsRaider(Creature c) => s_Raiders.TryGetValue(c, out _);

        /// <summary>Creature.GetAttackTargets of a raider: players under its tier's protect level who have not hurt it are
        /// not targets.</summary>
        internal static void FilterTargets(Creature c, List<Creature> targets)
        {
            if (targets == null || targets.Count == 0 || !s_Raiders.TryGetValue(c, out var tag) || tag.ProtectBelow <= 0) return;
            targets.RemoveAll(t => t is Player p && !R.MayTarget(p.Level ?? 1, c.DamageHistory.HasDamager(p), tag.ProtectBelow));
        }

        /// <summary>WorldObject.Destroy of a raider or reserve: every way one leaves the world is logged (a death's
        /// corpse swap, our own removals, a landblock unload).</summary>
        internal static void OnDestroy(WorldObject wo, bool fromUnload)
        {
            if (!(wo is Creature c)) return;
            Tag tag;
            bool raider = s_Raiders.TryGetValue(c, out tag), reserve = !raider && s_Reserves.TryGetValue(c, out tag);
            if (!raider && !reserve) return;
            var at = c.Location != null ? Local(c.Location) : default;
            string why = fromUnload ? "its landblock unloaded" : c.IsDead ? "dead" : Caller();
            Mod.Log.Info($"[RevivalGuard] TownRaid: #{tag.RaidId} {(reserve ? "reserve" : "raider")} {c.Name} 0x{c.Guid.Full:X8} leaves the world ({why}) at {at.X:0},{at.Y:0}, block {(c.CurrentLandblock?.Id.Raw ?? 0) >> 16:X4}, location block {c.Location?.Landblock ?? 0:X4}");
        }

        /// <summary>The first method on the stack outside the Destroy patch (who removed it).</summary>
        static string Caller()
        {
            foreach (var f in new System.Diagnostics.StackTrace(1, false).GetFrames())
            {
                var m = f.GetMethod();
                if (m == null || m.DeclaringType == typeof(TownRaid) || m.Name.Contains("Destroy") || m.DeclaringType?.Name.Contains("TownRaidDestroy") == true) continue;
                return $"{m.DeclaringType?.Name}.{m.Name}";
            }
            return "?";
        }

        /// <summary>Landblock.Unload of a block round Holtburg while a raid runs: which, and how many raid objects in it.</summary>
        internal static void OnUnload(ACE.Server.Entity.Landblock lb)
        {
            var r = s_Raid;
            uint id = lb.Id.Raw >> 16;
            int x = (int)(id >> 8), y = (int)(id & 0xFF);
            if (Math.Abs(x - 0xA9) > 1 || Math.Abs(y - 0xB4) > 1) return;
            int mine = 0;
            foreach (var wo in lb.GetAllWorldObjectsForDiagnostics()) if (wo is Creature c && (s_Raiders.TryGetValue(c, out _) || s_Reserves.TryGetValue(c, out _))) mine++;
            Mod.Log.Info($"[RevivalGuard] TownRaid: landblock {id:X4} unloads{(r != null ? $" during raid #{r.Id}" : "")}, {mine} raid object(s) in it, players in town {InTown().Count}");
        }

        /// <summary>Creature.AlertMonster (a player moving near wakes a monster onto itself): a protected player does not
        /// wake a raider. Box 2026-11-16: without this, tier 3 raiders went for a level 10 at once.</summary>
        internal static bool Protected(Creature alerter, Creature monster)
        {
            if (!(alerter is Player p) || monster == null || !s_Raiders.TryGetValue(monster, out var tag) || tag.ProtectBelow <= 0) return false;
            return !R.MayTarget(p.Level ?? 1, monster.DamageHistory.HasDamager(p), tag.ProtectBelow);
        }

        // ---------------------------------------------------------------- staff

        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            void Reply(string t) { if (me != null) Say(me, "[raid] " + t); else Console.WriteLine("[raid] " + t); }
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            double now = Now;
            switch (verb)
            {
                case "start":
                {
                    int? tier = null; string kind = null, side = null, theme = null; bool? big = null;
                    foreach (var a in args.Skip(1).Select(x => x.ToLowerInvariant()))
                    {
                        if (int.TryParse(a, out var t)) tier = Math.Clamp(t, 1, s_Cfg.TierFloors.Length);
                        else if (a == "big") big = true;
                        else if (s_Cfg.KindBy(a) != null) kind = a;
                        else if (s_Cfg.SideBy(a) != null) side = a;
                        else if (a == "none" || s_Cfg.Themes.Any(t => string.Equals(t.Festival, a, StringComparison.OrdinalIgnoreCase))) theme = a;
                        else { Reply($"what is '{a}'? Kinds: {string.Join(", ", s_Cfg.Kinds.Select(k => k.Key))}; sides: {string.Join(", ", s_Cfg.Sides.Select(x => x.Key))}; themes: {string.Join(", ", s_Cfg.Themes.Select(t => t.Festival))}, none"); return; }
                    }
                    // never during a Holtburg director event (Bael'Zharon's visit, the snowmen): one fight in town at a time
                    var ev = EventDirector.CurrentEvent()?.key;
                    if (R.PausedForEvent(ev, s_Cfg.Schedule)) { Reply($"not now: {ev} is running in Holtburg (@eventdirector stop ends it)"); return; }
                    Reply(Begin(tier, kind, side, big ?? false, true, now, theme));
                    return;
                }
                case "stop":
                    if (s_Raid == null) { Reply("no raid is running"); return; }
                    End(s_Raid, R.Outcome.Faded, now, "stopped by staff");
                    Reply("stopped; the raiders are taken away");
                    return;
                case "reload":
                    Reply(args.Length > 1 && args[1].ToLowerInvariant() == "embedded" ? SyncSeed(true) + "; " + Load(true) : Load(true) ?? "unchanged");
                    return;
                default:
                {
                    var r = s_Raid;
                    var sch = s_Cfg.Schedule;
                    double gapLeft = sch.MinGapMinutes * 60 - (now - s_State.LastStart);
                    var town = InTown();
                    Reply($"{(On ? "on" : "OFF")}; Discord {(Discord ? "ON" : "off")}; {town.Count} in town (tier {R.TierFor(town.Select(p => p.Level ?? 1).ToList(), s_Cfg.TierFloors)}); "
                        + (gapLeft > 0 ? $"next roll in {gapLeft / 60:0} min" : "rolling each minute")
                        + (R.QuietForRestart(DateTime.UtcNow, sch) ? "; quiet for the restart" : ""));
                    if (r != null)
                        Reply($"#{r.Id} {r.Kind.Key} tier {r.Tier}{(r.Big ? " big" : "")}{(r.Theme != null ? $" + {r.Theme.Festival}: {r.Group}" : "")} from the {r.Side.Key}: {r.Phase}, wave {r.WaveNo} of {r.Waves} ({string.Join(" ", r.WaveLog)}), {Live(r.All).Count()} alive, {r.Killed} of {r.Spawned} killed, "
                            + $"{r.Damage.Count} defender(s), last hit {(r.SpawnAt > 0 ? now - r.LastHit : 0):0} s ago, reserves {Live(r.Reserves).Count()}");
                    foreach (var h in s_State.History.Skip(Math.Max(0, s_State.History.Count - 5))) Reply(h);
                    return;
                }
            }
        }
    }

    // ================= hooks =================

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class TownRaidTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done) { s_Done = true; try { TownRaid.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] TownRaid: register: {e.Message}"); } }
            return true;
        }
        static void Postfix()
        {
            try { TownRaid.Tick(); }
            catch (Exception e) { TickGuard.Report("TownRaid", e); }
        }
    }

    [HarmonyPatch]
    static class TownRaidDeath
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] TownRaid: Creature.OnDeath(DamageHistoryInfo, DamageType, bool) not found; raids never count a kill");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix(Creature __instance)
        {
            // a prefix: ACE's OnDeath hands the damage out and may clear the history
            try { TownRaid.OnDeath(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid death: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class TownRaidTreasure
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            if (m == null) Mod.Log.Error("[RevivalGuard] TownRaid: Creature.GenerateTreasure(DamageHistoryInfo, Corpse) not found; raid corpses keep quest items");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Creature __instance, Corpse corpse)
        {
            try { TownRaid.OnTreasure(__instance, corpse); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid treasure: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Destroy), new[] { typeof(bool), typeof(bool) })]
    static class TownRaidDestroy
    {
        static void Prefix(WorldObject __instance, bool fromLandblockUnload)
        {
            try { if (!__instance.IsDestroyed) TownRaid.OnDestroy(__instance, fromLandblockUnload); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid destroy log: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ACE.Server.Entity.Landblock), nameof(ACE.Server.Entity.Landblock.Unload))]
    static class TownRaidUnload
    {
        static void Prefix(ACE.Server.Entity.Landblock __instance)
        {
            try { TownRaid.OnUnload(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownRaid unload log: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.AlertMonster))]
    static class TownRaidAlert
    {
        static bool Prefix(Creature __instance, Creature monster, ref bool __result)
        {
            try { if (TownRaid.Protected(__instance, monster)) { __result = false; return false; } }
            catch (Exception e) { TickGuard.Report("TownRaid.Alert", e); }
            return true;
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.GetAttackTargets))]
    static class TownRaidTargets
    {
        static void Postfix(Creature __instance, List<Creature> __result)
        {
            try { TownRaid.FilterTargets(__instance, __result); }
            catch (Exception e) { TickGuard.Report("TownRaid.Targets", e); }
        }
    }
}
