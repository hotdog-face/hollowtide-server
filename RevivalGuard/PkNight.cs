using System.Numerics;
using System.Text;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// PK NIGHT: HOLD THE SPOT (owner 2026-11-09, docs/proposals/pk-night-decisions.md; design docs/proposals/pk-night.md).
    /// Ours. Off until `rg_pk_night` is true.
    ///
    /// Every Sunday at 7 PM Pacific, for one hour, the allegiance that holds a known outdoor landmark longest wins.
    /// The spot rotates weekly (PkNightRules.SPOTS) and is announced a day, an hour and ten minutes ahead. During
    /// the hour, every TICK seconds, the player killers within pk_night_radius metres of the spot are counted by
    /// allegiance (PkCommon.Side: the monarch, or a lone player): one allegiance alone there holds it and earns
    /// the tick, two or more are a contest and nobody earns it. Mercenaries never count; the Borg count like any
    /// allegiance. Normal Hollowtide PK death rules apply, this changes nothing about dying.
    ///
    /// The reward is glory: a title (pk_night_title, a retail CharacterTitle) to the winners who stood there, a
    /// world line, a line for the Town Crier (CrierLine), a weekly leaderboard, and a small token (pk_night_token,
    /// a weenie; 0 = none until the owner picks one). State: PkLedger/pknight.json beside the dll (ModFolder).
    ///
    ///   @pknight                          what is next, or the standings now (any player; a retail client's board)
    ///   @pknight start [minutes] [spot] [record]   staff: a round now (default 10 minutes, this week's spot; `record`
    ///                                     counts it on the leaderboard; without it the round is a rehearsal)
    ///   @pknight stop                     staff: end the running round now
    ///   @pknight say                      staff: every line players are told, to you only
    ///   @pknight reset                    staff: forget every round (test shards)
    ///
    /// The board: PkBoard sends "night" JSON with the rest of the PK board, which our client draws on the Social
    /// panel's PK Boards tab (AcPkBoardHud) and a Borg client reads the same way.
    /// </summary>
    public static partial class PkNight
    {
        internal const string P_ON = "rg_pk_night", P_RADIUS = "pk_night_radius", P_TOKEN = "pk_night_token",
            P_TITLE = "pk_night_title", P_DISCORD = "pk_night_discord";
        const double TICK = TICK_SECONDS, SAVE_EVERY = 30;
        const double LOGIN_DELAY_S = 8;   // after the login's own lines and Mail (FestivalNotices)

        internal static bool On => PkCommon.On(P_ON);
        static double Radius => Math.Clamp(PkCommon.Long(P_RADIUS, DEFAULT_RADIUS), 5, 200);

        sealed class RoundRec
        {
            public string Key { get; set; }
            public string SpotKey { get; set; }
            public long StartedAt { get; set; }
            public long EndedAt { get; set; }
            public uint WinnerSide { get; set; }
            public string Winner { get; set; }
            public double Held { get; set; }
            public bool Forced { get; set; }
            public List<string> Members { get; set; } = new List<string>();
            public Dictionary<uint, Standing> Sides { get; set; } = new Dictionary<uint, Standing>();
        }

        sealed class State
        {
            public Live Live { get; set; }
            public List<RoundRec> Rounds { get; set; } = new List<RoundRec>();
            public Dictionary<string, HashSet<string>> Said { get; set; } = new Dictionary<string, HashSet<string>>();
            public Dictionary<uint, List<string>> Pending { get; set; } = new Dictionary<uint, List<string>>();   // titles owed, by guid
            public string CrierLine { get; set; }
        }

        static readonly object s_Lock = new object();
        static State s_State = new State();
        static string s_Path;
        static double s_Next, s_SavedAt;
        static ACE.Entity.Position s_Centre;   // of the live round's spot
        static string s_CentreKey;

        internal static void Register()
        {
            Load();
            CommandManager.TryAddCommand(Command, "pknight", AccessLevel.Player, CommandHandlerFlag.None,
                "PK Night: hold the spot. What is next, or the standings now.", "[start [minutes] [spot] [record] | stop | say | reset]");
            Mod.Log.Info($"[RevivalGuard] PK Night: {(On ? "ON" : "off")} ({P_ON}); {SPOTS.Length} spots, first run {WhenWords(StartUtcOf(FIRST_SUNDAY))}"
                + (s_State.Live != null ? $"; a round was running at shutdown ({s_State.Live.Key}), resumed" : ""));
        }

        // ------------------------------------------------------------------ the world loop

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        static class OnTick
        {
            static void Postfix()
            {
                var now = ACE.Common.Time.GetUnixTime();
                if (now < s_Next) return;
                s_Next = now + TICK;
                try { if (On) Step(now); }
                catch (Exception e) { TickGuard.Report("PkNight", e); }
            }
        }

        static void Step(double now)
        {
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            Live live;
            lock (s_Lock) live = s_State.Live;
            if (live == null)
            {
                var next = Next(utc);
                if (next == null) return;
                // a scheduled round that was ended early (@pknight stop) is not opened again inside its own hour
                if (next.Running(utc)) { if (!Played(next.Key)) Begin(next, now); return; }
                Notice? due;
                lock (s_Lock)
                {
                    if (!s_State.Said.TryGetValue(next.Key, out var said))
                    {
                        s_State.Said.Clear();   // only the coming round's notices matter
                        s_State.Said[next.Key] = said = new HashSet<string>();
                    }
                    due = DueBefore(next, utc, said);
                    if (due != null) Save();
                }
                if (due == Notice.DayBefore) World(DayBeforeLine(next.Spot));
                else if (due == Notice.HourBefore) World(HourBeforeLine(next.Spot));
                else if (due == Notice.TenMinutes) World(TenMinutesLine(next.Spot));
                return;
            }
            if (now >= live.EndsAt) { End(live, now, "the hour is up"); return; }
            var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
            var centre = Centre(spot);
            if (centre == null) return;
            double dt = live.LastTickAt > 0 ? Math.Min(TICK * 2, now - live.LastTickAt) : TICK;
            var present = new List<Presence>();
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (!Eligible(p) || Dist(p.Location, centre) > Radius) continue;
                present.Add(new Presence { Side = PkCommon.Side(p), Label = PkCommon.SideLabel(p), Guid = p.Guid.Full });
            }
            uint newLeader;
            bool standings;
            lock (s_Lock)
            {
                newLeader = PkNight.Tick(live, present, now, dt);
                standings = StandingsDue(live, now);
                if (now - s_SavedAt >= SAVE_EVERY || newLeader != 0 || standings) Save();
            }
            if (newLeader != 0 && now - live.LeadSaidAt >= LEAD_NOTE_GAP && live.Sides.Count > 1)
            {
                live.LeadSaidAt = now;
                World(LeadLine(live.Sides[newLeader].Name, spot));
            }
            if (standings) World(StandingsLine(live, spot, now));
        }

        static bool Eligible(Player p)
        {
            try
            {
                return p != null && p.IsPK && !p.IsDead && !p.Teleporting && p.Location != null
                    && p.CloakStatus != CloakStatus.On && !Mercenaries.IsMerc(p);
            }
            catch { return false; }
        }

        static ACE.Entity.Position Centre(Spot spot)
        {
            if (s_Centre != null && s_CentreKey == spot.Key) return s_Centre;
            try
            {
                var pos = new ACE.Entity.Position(new Vector2((float)spot.Ew, (float)spot.Ns));
                pos.AdjustMapCoords();
                s_Centre = pos; s_CentreKey = spot.Key;
                Mod.Log.Info($"[RevivalGuard] PkNight: {spot.Name} centre 0x{pos.Cell:X8} ({pos.PositionX:0.0}, {pos.PositionY:0.0}, {pos.PositionZ:0.0})");
                return pos;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkNight: no position for {spot.Name}: {e.Message}"); return null; }
        }

        /// <summary>Horizontal distance in metres (the spot is open ground; a dungeon under it is not the spot).</summary>
        static float Dist(ACE.Entity.Position a, ACE.Entity.Position b)
        {
            if (a == null || b == null) return float.MaxValue;
            if (a.Indoors && (a.Cell >> 16) != (b.Cell >> 16)) return float.MaxValue;   // a dungeon elsewhere; a building on the spot still counts
            var ga = a.ToGlobal(); var gb = b.ToGlobal();
            float dx = ga.X - gb.X, dy = ga.Y - gb.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        // ------------------------------------------------------------------ begin and end

        /// <summary>A recorded round with this key has already ended (stopped early by staff, or the hour ran out and
        /// the tick came round again before the clock left the hour).</summary>
        static bool Played(string key)
        {
            lock (s_Lock)
            {
                if (s_State.Rounds == null) return false;
                foreach (var r in s_State.Rounds) if (r.Key == key && !r.Forced) return true;
                return false;
            }
        }

        static void Begin(Scheduled s, double now)
        {
            var live = new Live { Key = s.Key, SpotKey = s.Spot.Key, StartedAt = ToUnix(s.StartUtc), EndsAt = ToUnix(s.EndUtc), Record = true };
            Open(live, s.Spot, now);
        }

        static void Open(Live live, Spot spot, double now)
        {
            lock (s_Lock) { s_State.Live = live; Save(); }
            s_Centre = null;
            Centre(spot);
            int minutes = (int)Math.Round((live.EndsAt - live.StartedAt) / 60.0);
            World(StartLine(spot, minutes));
            if (PkCommon.On(P_DISCORD) && !live.Forced) DiscordBridge.Announce(DiscordStartLine(spot));
            Mod.Log.Info($"[RevivalGuard] PkNight: round {live.Key} open at {spot.Name}, {minutes} min{(live.Forced ? ", forced" : "")}{(live.Record ? "" : ", not recorded")}");
        }

        static void End(Live live, double now, string why)
        {
            var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            var next = Next(utc);                                        // at a scheduled round's end this is next week's
            if (next != null && next.Running(utc)) next = After(next);   // a forced round inside the live hour
            var w = Leader(live);
            var rec = new RoundRec
            {
                Key = live.Key, SpotKey = live.SpotKey, StartedAt = (long)live.StartedAt, EndedAt = (long)now, Forced = live.Forced,
                WinnerSide = w?.Side ?? 0, Winner = w?.Name, Held = w?.Held ?? 0, Sides = live.Sides,
            };
            var winners = new List<Player>();
            if (w != null)
                foreach (var g in w.Present)
                {
                    var p = PlayerManager.GetOnlinePlayer(g);
                    if (p != null) { winners.Add(p); rec.Members.Add(p.Name); }
                    else { var off = PlayerManager.FindByGuid(g); if (off != null) rec.Members.Add(off.Name); }
                }
            lock (s_Lock)
            {
                s_State.Live = null;
                if (live.Record) s_State.Rounds.Add(rec);
                if (live.Record) s_State.CrierLine = CrierLine(w?.Name, spot, w?.Held ?? 0, next);
                if (live.Record && w != null)
                    foreach (var g in w.Present)
                        if (PlayerManager.GetOnlinePlayer(g) == null)
                        {
                            if (!s_State.Pending.TryGetValue(g, out var l)) s_State.Pending[g] = l = new List<string>();
                            l.Add(TitleKey());
                        }
                Save();
            }
            World(EndLine(live, spot, live.Forced ? null : next));
            if (PkCommon.On(P_DISCORD) && !live.Forced) DiscordBridge.Announce(DiscordEndLine(live, spot));
            if (live.Record) foreach (var p in winners) Reward(p);
            Mod.Log.Info($"[RevivalGuard] PkNight: round {live.Key} over ({why}): {(w == null ? "no holder" : $"{w.Name} {Clock(w.Held)}")}, "
                + $"{live.Sides.Count} sides, winners present {winners.Count}{(live.Record ? "" : ", not recorded")}");
        }

        static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        // ------------------------------------------------------------------ the reward

        static string TitleKey() => PkCommon.Str(P_TITLE, "KingofDarktide").Trim();

        static CharacterTitle? Title()
        {
            var key = TitleKey();
            if (key.Length == 0 || key == "none") return null;
            return Enum.TryParse<CharacterTitle>(key, true, out var t) ? t : null;
        }

        /// <summary>"KingofDarktide" to "King of Darktide", as the title list shows it.</summary>
        internal static string TitleWords(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var s = System.Text.RegularExpressions.Regex.Replace(key, "([a-z])(of|the)([A-Z])", "$1 $2 $3");
            s = System.Text.RegularExpressions.Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
            return s.Replace("Derethe", "Dereth").Replace("Darktides", "Darktide's");
        }

        static void Reward(Player p)
        {
            var t = Title();
            if (t != null)
            {
                try { p.AddTitle(t.Value); PkCommon.Say(p, $"You have earned the title {TitleWords(t.ToString())}."); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkNight: title {t} for {p.Name}: {e.Message}"); }
            }
            long wcid = PkCommon.Long(P_TOKEN, 0);
            if (wcid > 0)
            {
                try
                {
                    var wo = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject((uint)wcid);
                    if (wo != null && p.TryCreateInInventoryWithNetworking(wo)) PkCommon.Say(p, $"{wo.Name} is yours for the night.");
                    else Mod.Log.Warn($"[RevivalGuard] PkNight: token {wcid} for {p.Name} did not fit or does not exist");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkNight: token {wcid} for {p.Name}: {e.Message}"); }
            }
        }

        /// <summary>Titles owed to winners who had left before the end, and the login line for the night.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class AtLogin
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    if (!On) return;
                    List<string> owed = null;
                    lock (s_Lock)
                        if (s_State.Pending.TryGetValue(__instance.Guid.Full, out var l) && l.Count > 0) { owed = l.ToList(); s_State.Pending.Remove(__instance.Guid.Full); Save(); }
                    if (owed != null)
                        foreach (var key in owed.Distinct())
                            if (Enum.TryParse<CharacterTitle>(key, true, out var t))
                            {
                                try { __instance.AddTitle(t); PkCommon.Say(__instance, $"You have earned the title {TitleWords(key)}."); }
                                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkNight: owed title {key} for {__instance.Name}: {e.Message}"); }
                            }
                    string line = LoginLine();
                    if (line == null) return;
                    var chain = new ACE.Server.Entity.Actions.ActionChain();
                    chain.AddDelaySeconds(LOGIN_DELAY_S);
                    chain.AddAction(__instance, () => { if (__instance.Session != null) PkCommon.Say(__instance, line); });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkNight: login of {__instance?.Name}: {e.Message}"); }
            }
        }

        static string LoginLine()
        {
            var now = ACE.Common.Time.GetUnixTime();
            Live live;
            lock (s_Lock) live = s_State.Live;
            if (live != null) return LoginLiveLine(SpotNamed(live.SpotKey) ?? SPOTS[0], Math.Max(0, live.EndsAt - now));
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            var next = Next(utc);
            if (next != null && (next.StartUtc - utc).TotalHours <= 24) return LoginSoonLine(next);
            return null;
        }

        // ------------------------------------------------------------------ what the crier and the calendar read

        /// <summary>The Town Crier's line about last week's night (null until a round has been recorded).</summary>
        internal static string CrierLine() { lock (s_Lock) return On ? s_State.CrierLine : null; }

        /// <summary>One line for @festival's "what is next" (SeasonalCalendar).</summary>
        internal static string FestivalLine()
        {
            if (!On) return null;
            var now = ACE.Common.Time.GetUnixTime();
            Live live;
            lock (s_Lock) live = s_State.Live;
            if (live != null) return LoginLiveLine(SpotNamed(live.SpotKey) ?? SPOTS[0], Math.Max(0, live.EndsAt - now));
            var next = Next(DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime);
            return next == null ? null : $"PK Night is {WhenWords(next.StartUtc)} at {next.Spot.Name} ({next.Spot.Where}).";
        }

        // ------------------------------------------------------------------ the command

        static void Out(Session s, string text)
        {
            if (s?.Network != null) PkCommon.Say(s.Player, text);
            else Console.WriteLine(text);
        }

        static bool Staff(Session s) => s == null || s.AccessLevel >= AccessLevel.Admin;

        static void Command(Session session, params string[] parameters)
        {
            var args = parameters ?? new string[0];
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (!On) { Out(session, "PK Night is not held on this world."); return; }
            var now = ACE.Common.Time.GetUnixTime();
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            if (verb == "start" || verb == "stop" || verb == "say" || verb == "reset")
            {
                if (!Staff(session)) { Out(session, "Only staff can do that."); return; }
                if (verb == "say") { foreach (var l in AllLines()) Out(session, "  " + l); return; }
                if (verb == "reset")
                {
                    lock (s_Lock) { s_State = new State(); Save(); }
                    s_Centre = null;
                    Out(session, "PK Night: every round forgotten.");
                    return;
                }
                Live live; lock (s_Lock) live = s_State.Live;
                if (verb == "stop")
                {
                    if (live == null) { Out(session, "No PK Night round is running."); return; }
                    End(live, now, $"stopped by {session?.Player?.Name ?? "the console"}");
                    Out(session, "PK Night: the round is over.");
                    return;
                }
                if (live != null) { Out(session, "A PK Night round is already running (@pknight stop ends it)."); return; }
                int minutes = 10; Spot spot = null; bool record = false;
                foreach (var a in args.Skip(1))
                {
                    if (int.TryParse(a, out int m) && m >= 1 && m <= 180) minutes = m;
                    else if (a.Equals("record", StringComparison.OrdinalIgnoreCase)) record = true;
                    else spot ??= SpotNamed(a);
                }
                spot ??= (Next(utc) ?? new Scheduled { Spot = SPOTS[0] }).Spot;
                var forced = new Live { Key = $"forced-{(long)now}", SpotKey = spot.Key, StartedAt = now, EndsAt = now + minutes * 60, Forced = true, Record = record };
                Open(forced, spot, now);
                Out(session, $"PK Night: a {minutes} minute round at {spot.Name} ({spot.Where}){(record ? ", recorded on the leaderboard" : ", a rehearsal, not recorded")}. Centre: {Mansions.Where(s_Centre)}.");
                return;
            }
            // any player: the text form of the board (a retail client, a bot, the console)
            if (session != null && ModChannel.Capable(session) && verb != "text") { PkBoard.Send(session, true, "night"); return; }
            foreach (var l in StatusLines(session?.Player, now)) Out(session, l);
        }

        internal static List<string> StatusLines(Player asker, double now)
        {
            var lines = new List<string>();
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            Live live; List<RoundRec> rounds;
            lock (s_Lock) { live = s_State.Live; rounds = s_State.Rounds.ToList(); }   // only recorded rounds are kept (a forced round with `record` counts)
            uint mySide = asker != null ? PkCommon.Side(asker) : 0;
            if (live != null)
            {
                var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
                lines.Add($"PK Night is on: hold {spot.Name} ({spot.Where}). {Clock(Math.Max(0, live.EndsAt - now))} left"
                    + (live.Contested ? ", contested now." : live.Holding != 0 && live.Sides.TryGetValue(live.Holding, out var h) ? $", {h.Name} holds it now." : ", no one holds it now."));
                var ranked = Ranked(live);
                if (ranked.Count == 0) lines.Add("No allegiance has held it yet.");
                int i = 0;
                foreach (var s in ranked.Take(10)) lines.Add($"{++i}. {s.Name}: {Clock(s.Held)}{(s.Side == mySide ? " (yours)" : "")}");
            }
            else
            {
                var next = Next(utc);
                if (next != null) lines.Add($"PK Night is {WhenWords(next.StartUtc)}: hold {next.Spot.Name} ({next.Spot.Where}) for the hour. The allegiance that holds it longest takes the night.");
            }
            var last = rounds.LastOrDefault();
            if (last != null)
            {
                var spot = SpotNamed(last.SpotKey);
                lines.Add(last.Winner != null ? $"Last PK Night: {last.Winner} held {spot?.Name ?? last.SpotKey} for {Clock(last.Held)}."
                                              : $"Last PK Night: no one held {spot?.Name ?? last.SpotKey}.");
            }
            var wins = Wins(rounds);
            if (wins.Count > 0) lines.Add("Wins: " + string.Join(", ", wins.Take(5).Select(w => $"{w.name} {w.wins}")));
            lines.Add($"Stand within {Radius:0} metres of the spot with no other allegiance there to hold it. Dying there counts as it always does.");
            return lines;
        }

        static List<(uint side, string name, int wins, double held)> Wins(List<RoundRec> rounds) =>
            rounds.Where(r => r.WinnerSide != 0)
                  .GroupBy(r => r.WinnerSide)
                  .Select(g => (g.Key, g.Last().Winner, g.Count(), g.Sum(r => r.Held)))
                  .OrderByDescending(x => x.Item3).ThenByDescending(x => x.Item4).ThenBy(x => x.Item2).ToList();

        // ------------------------------------------------------------------ the board's data

        internal static void AppendJson(StringBuilder sb, Player asker)
        {
            var now = ACE.Common.Time.GetUnixTime();
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            Live live; List<RoundRec> rounds;
            lock (s_Lock) { live = s_State.Live; rounds = s_State.Rounds.ToList(); }   // only recorded rounds are kept (a forced round with `record` counts)
            uint mySide = PkCommon.Side(asker);
            sb.Append(",\"night\":{");
            if (live != null)
            {
                var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
                sb.Append("\"state\":\"live\",\"start\":").Append((long)live.StartedAt).Append(",\"end\":").Append((long)live.EndsAt)
                  .Append(",\"forced\":").Append(live.Forced ? 1 : 0).Append(",\"spot\":"); SpotJson(sb, spot);
                sb.Append(",\"holding\":").Append(live.Holding != 0 && live.Sides.TryGetValue(live.Holding, out var h) ? PkCommon.Q(h.Name) : "\"\"")
                  .Append(",\"contested\":").Append(live.Contested ? 1 : 0).Append(",\"present\":").Append(live.PresentNow)
                  .Append(",\"standings\":[");
                int n = 0;
                foreach (var s in Ranked(live))
                {
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"n\":").Append(PkCommon.Q(s.Name)).Append(",\"held\":").Append((long)Math.Round(s.Held))
                      .Append(",\"mine\":").Append(s.Side == mySide ? 1 : 0).Append(",\"holding\":").Append(s.Side == live.Holding ? 1 : 0)
                      .Append(",\"players\":").Append(s.Present.Count).Append('}');
                }
                sb.Append(']');
            }
            else
            {
                var next = Next(utc);
                sb.Append("\"state\":").Append(next != null ? "\"next\"" : "\"off\"");
                if (next != null)
                {
                    sb.Append(",\"start\":").Append(ToUnix(next.StartUtc)).Append(",\"end\":").Append(ToUnix(next.EndUtc))
                      .Append(",\"when\":").Append(PkCommon.Q(WhenWords(next.StartUtc))).Append(",\"spot\":"); SpotJson(sb, next.Spot);
                }
            }
            sb.Append(",\"radius\":").Append(Radius.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"title\":").Append(PkCommon.Q(TitleWords(Title()?.ToString() ?? "")))
              .Append(",\"recent\":[");
            int k = 0;
            foreach (var r in rounds.AsEnumerable().Reverse().Take(8))
            {
                if (k++ > 0) sb.Append(',');
                var spot = SpotNamed(r.SpotKey);
                sb.Append("{\"at\":").Append(r.StartedAt).Append(",\"spot\":").Append(PkCommon.Q(spot?.Name ?? r.SpotKey))
                  .Append(",\"winner\":").Append(PkCommon.Q(r.Winner ?? "")).Append(",\"held\":").Append((long)Math.Round(r.Held))
                  .Append(",\"mine\":").Append(r.WinnerSide != 0 && r.WinnerSide == mySide ? 1 : 0).Append('}');
            }
            sb.Append("],\"wins\":[");
            k = 0;
            foreach (var w in Wins(rounds).Take(10))
            {
                if (k++ > 0) sb.Append(',');
                sb.Append("{\"n\":").Append(PkCommon.Q(w.name)).Append(",\"wins\":").Append(w.wins).Append(",\"held\":").Append((long)Math.Round(w.held))
                  .Append(",\"mine\":").Append(w.side == mySide ? 1 : 0).Append('}');
            }
            sb.Append("]}");
        }

        static void SpotJson(StringBuilder sb, Spot s) =>
            sb.Append("{\"name\":").Append(PkCommon.Q(s.Name)).Append(",\"where\":").Append(PkCommon.Q(s.Where))
              .Append(",\"near\":").Append(PkCommon.Q(s.Near)).Append(",\"levels\":").Append(PkCommon.Q(s.Levels))
              .Append(",\"ns\":").Append(s.Ns.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"ew\":").Append(s.Ew.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('}');

        // ------------------------------------------------------------------ small helpers

        static void World(string line)
        {
            PlayerManager.BroadcastToAll(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(line, ChatMessageType.WorldBroadcast));
            Mod.Log.Info("[RevivalGuard] PkNight: " + line);
        }

        // ------------------------------------------------------------------ the file

        static void Load()
        {
            try
            {
                var dir = ModFolder.Sub("PkLedger");
                if (dir == null) return;
                s_Path = Path.Combine(dir, "pknight.json");
                if (File.Exists(s_Path)) s_State = JsonSerializer.Deserialize<State>(File.ReadAllText(s_Path)) ?? new State();
                s_State.Rounds ??= new List<RoundRec>(); s_State.Said ??= new Dictionary<string, HashSet<string>>(); s_State.Pending ??= new Dictionary<uint, List<string>>();
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkNight: cannot read {s_Path}: {e.Message}; starting empty"); s_State = new State(); }
        }

        static void Save()
        {
            if (s_Path == null) return;
            try
            {
                var tmp = s_Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_State));
                File.Move(tmp, s_Path, true);
                s_SavedAt = ACE.Common.Time.GetUnixTime();
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkNight: cannot write {s_Path}: {e.Message}"); }
        }
    }
}
