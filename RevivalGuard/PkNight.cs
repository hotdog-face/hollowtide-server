using System.Numerics;
using System.Runtime.CompilerServices;
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
    /// SIDES (owner 2026-11-22, docs/proposals/pk-night-fellowship.md): pk_night_sides = fellowship (the default, and what an
    /// unset value means) or allegiance. In fellowship mode a side is a fellowship (FellowKey: the Fellowship object, kept
    /// through a leader change) or a player alone, with the anti-hop lock (PkNightRules.Admit), and the banner goes to the
    /// winning fellowship's leader once per player. Only PK Night: bounties, renown and the ladder keep PkCommon.Side.
    /// What follows says "allegiance"; read "side".
    ///
    /// Every other Sunday at 7 PM Pacific (PkNightRules.INTERVAL_DAYS), for ROUND_MINUTES (40), the allegiance that holds a known outdoor landmark longest wins.
    /// The spot rotates once per round (PkNightRules.SPOTS) and is announced a day, 30 minutes and ten minutes ahead. During
    /// the round, every TICK seconds, the player killers within pk_night_radius metres of the hill are counted by
    /// allegiance (PkCommon.Side: the monarch, or a lone player): the side with more of its members there than every
    /// other side holds it and earns the tick ("outnumber to score", owner 2026-11-25); a tie for most is a contest. Mercenaries never count; the Borg count like any
    /// allegiance (Mercenaries.IsHollowCompany). v2 (owner 2026-11-22): the hill moves within the spot every 10 minutes,
    /// torches mark it, and players near it hear the standings every 15 s (PkNightHill.cs, PkNightHillRules.cs). Normal Hollowtide PK death rules apply, this changes nothing about dying.
    ///
    /// The reward is glory: a title (pk_night_title, a retail CharacterTitle) to the winners who stood there, a
    /// world line, a line for the Town Crier (CrierLine), a leaderboard of nights, and a small token (pk_night_token,
    /// a weenie; 0 = none until the owner picks one). State: PkLedger/pknight.json beside the dll (ModFolder).
    ///
    ///   @pknight                          what is next, or the standings now (any player; a retail client's board)
    ///   @pknight start [minutes] [spot] [record]   staff: a round now (default 10 minutes, the next round's spot; `record`
    ///                                     counts it on the leaderboard; without it the round is a rehearsal)
    ///   @pknight stop                     staff: end the running round now
    ///   @pknight say                      staff: every line players are told, to you only
    ///   @pknight gate                     staff: the event portals now (PkNightGate.cs)
    ///   @pknight reset                    staff: forget every round (test shards)
    ///
    /// The board: PkBoard sends "night" JSON with the rest of the PK board, which our client draws on the Social
    /// panel's PK Boards tab (AcPkBoardHud) and a Borg client reads the same way.
    /// </summary>
    public static partial class PkNight
    {
        internal const string P_ON = "rg_pk_night", P_RADIUS = "pk_night_radius", P_TOKEN = "pk_night_token",
            P_TITLE = "pk_night_title", P_DISCORD = "pk_night_discord", P_BANNER = "pk_night_banner", P_SIDES = "pk_night_sides";
        const double TICK = TICK_SECONDS, SAVE_EVERY = 30;
        const double LOGIN_DELAY_S = 8;   // after the login's own lines and Mail (FestivalNotices)

        internal static bool On => PkCommon.On(P_ON);
        /// <summary>pk_night_sides now (an unset or unknown value is fellowship mode). A running round keeps the mode it
        /// opened with (Live.Sides).</summary>
        static bool FellowshipNow => IsFellowshipMode(PkCommon.Str(P_SIDES, SIDES_FELLOWSHIP));
        static bool ByFellowship(Live live) => live != null ? live.ByFellowship : FellowshipNow;
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
            public Dictionary<uint, List<string>> Pending { get; set; } = new Dictionary<uint, List<string>>();   // titles (and "item:<wcid>") owed, by guid
            public HashSet<uint> Banners { get; set; } = new HashSet<uint>();   // sides given a Banner of the Held Ground, ever
            public HashSet<uint> BannerPlayers { get; set; } = new HashSet<uint>();   // fellowship mode: leaders given one, ever
            public string CrierLine { get; set; }
        }

        static readonly object s_Lock = new object();
        static State s_State = new State();
        static string s_Path;
        static double s_Next, s_SavedAt;
        static ACE.Entity.Position s_Centre;   // of the live round's spot
        static string s_CentreKey;

        // ------------------------------------------------------------------ fellowship sides

        /// <summary>Each fellowship's side key, by object: ACE keeps one Fellowship object from its creation to its disband,
        /// through a leader leaving (QuitFellowship assigns a new leader), so the key holds for the fellowship's lifetime
        /// (FellowPassword keys its passwords the same way). The leader's guid would change with the leader, and ACE keeps
        /// no creation time. A restart dissolves every fellowship and forgets this table: s_FirstKey marks the first key of
        /// this process, and a fellowship formed again takes back its members' old key (AdoptKey).</summary>
        static readonly ConditionalWeakTable<Fellowship, StrongBox<uint>> s_FellowKeys = new ConditionalWeakTable<Fellowship, StrongBox<uint>>();
        static readonly HashSet<uint> s_Adopted = new HashSet<uint>();
        static readonly object s_KeyLock = new object();
        static uint s_NextKey, s_FirstKey;

        static void SeedKeys()
        {
            lock (s_KeyLock)
            {
                var used = KeysOf(s_State.Live).Concat(s_State.Rounds.SelectMany(r => (r.Sides?.Keys ?? Enumerable.Empty<uint>()).Append(r.WinnerSide)));
                s_NextKey = NextFellowKey(used, s_NextKey);
                if (s_FirstKey == 0) s_FirstKey = s_NextKey;
            }
        }

        static uint FellowKey(Fellowship f, Live live)
        {
            lock (s_KeyLock)
            {
                if (s_FellowKeys.TryGetValue(f, out var box)) return box.Value;
                uint k = 0;
                Dictionary<uint, Player> members = null;
                try { members = f.GetFellowshipMembers(); } catch { }
                if (live != null && members != null) lock (s_Lock) k = AdoptKey(live, members.Keys, s_Adopted, s_FirstKey);
                if (k != 0) s_Adopted.Add(k);
                else k = s_NextKey++;
                s_FellowKeys.Add(f, new StrongBox<uint>(k));
                return k;
            }
        }

        /// <summary>The side a player counts for at PK Night: in fellowship mode the fellowship (label: its name, else its
        /// leader's) or the player alone; in allegiance mode PkCommon.Side. Leader: the fellowship's leader (alone: the player).</summary>
        static Presence NightSide(Player p, Live live, bool byFellowship)
        {
            if (!byFellowship) return new Presence { Side = PkCommon.Side(p), Label = PkCommon.SideLabel(p), Guid = p.Guid.Full };
            var f = p.Fellowship;
            if (f == null) return new Presence { Side = p.Guid.Full, Label = p.Name, Guid = p.Guid.Full, Leader = p.Guid.Full };
            uint leader = f.FellowshipLeaderGuid;
            string label = string.IsNullOrWhiteSpace(f.FellowshipName)
                ? (PlayerManager.FindByGuid(leader)?.Name ?? p.Name) : f.FellowshipName.Trim();
            return new Presence { Side = FellowKey(f, live), Label = label, Guid = p.Guid.Full, Leader = leader };
        }

        static uint MySide(Player asker, Live live)
        {
            if (asker == null) return 0;
            try { return NightSide(asker, live, ByFellowship(live)).Side; } catch { return 0; }
        }

        internal static void Register()
        {
            Load();
            CommandManager.TryAddCommand(Command, "pknight", AccessLevel.Player, CommandHandlerFlag.None,
                "PK Night: hold the spot. What is next, or the standings now.", "[start [minutes] [spot] [record] | stop | say | gate | reset]");
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
                try
                {
                    if (On) Step(now);
                    else { if (s_GateKey != null) CloseGate("PK Night switched off"); if (s_MarkKey != null) ClearMarkers("PK Night switched off"); }
                }
                catch (Exception e) { TickGuard.Report("PkNight", e); }
            }
        }

        static void Step(double now)
        {
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            Live live;
            lock (s_Lock) live = s_State.Live;
            try { GateStep(now, utc, live); }
            catch (Exception e) { TickGuard.Report("PkNight.Gate", e); }
            if (live == null)
            {
                if (s_MarkKey != null) ClearMarkers("no round running");
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
                if (due == Notice.DayBefore) World(DayBeforeLine(next.Spot, FellowshipNow));
                else if (due == Notice.PrepBefore) World(PrepBeforeLine(next.Spot, FellowshipNow));
                else if (due == Notice.TenMinutes) World(TenMinutesLine(next.Spot));
                return;
            }
            if (now >= live.EndsAt) { End(live, now, "the time is up"); return; }
            var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
            var centre = Centre(spot, HillIndex(live, spot, now));   // the moving hill (PkNightHillRules)
            if (centre == null) return;
            double dt = live.LastTickAt > 0 ? Math.Min(TICK * 2, now - live.LastTickAt) : TICK;
            var present = new List<Presence>();
            bool fs = live.ByFellowship;
            Dictionary<uint, uint> locked;
            lock (s_Lock) locked = fs ? new Dictionary<uint, uint>(live.Locked) : null;
            foreach (var p in PlayerManager.GetAllOnline())
            {
                bool here = Eligible(p) && Dist(p.Location, centre) <= Radius;
                // fellowship mode looks at every player already locked this round, in the circle or not, so a fellowship
                // changed away from the spot is seen too (the anti-hop rule)
                if (!here && (!fs || !locked.ContainsKey(p.Guid.Full))) continue;
                var x = NightSide(p, live, fs);
                x.Away = !here;
                x.Regrouping = fs && p.Fellowship == null && LockedToGone(live, x.Guid, s_FirstKey);
                present.Add(x);
            }
            uint newLeader;
            bool standings;
            var benched = new List<uint>();
            lock (s_Lock)
            {
                newLeader = PkNight.Tick(live, present, now, dt, benched);
                if (benched.Count > 0) Save();
                standings = StandingsDue(live, now);
                if (now - s_SavedAt >= SAVE_EVERY || newLeader != 0 || standings) Save();
            }
            if (newLeader != 0 && now - live.LeadSaidAt >= LEAD_NOTE_GAP && live.Sides.Count > 1)
            {
                live.LeadSaidAt = now;
                World(LeadLine(live.Sides[newLeader].Name, spot));
            }
            if (standings) World(StandingsLine(live, spot, now));
            HillStep(live, spot, centre, now);
            foreach (var g in benched)
            {
                var p = PlayerManager.GetOnlinePlayer(g);
                Mod.Log.Info($"[RevivalGuard] PkNight: {p?.Name ?? g.ToString("X8")} changed sides; counts for no one until the end");
                if (p != null) PkCommon.Say(p, BenchedLine(spot));
            }
        }

        static bool Eligible(Player p)
        {
            try
            {
                return p != null && p.IsPK && !p.IsDead && !p.Teleporting && p.Location != null
                    && p.CloakStatus != CloakStatus.On && !Mercenaries.IsHollowCompany(p);   // the Borg count (owner 2026-11-22)
            }
            catch { return false; }
        }

        /// <summary>The hill's centre: point `hill` of the spot's list (HillPoints; 0 is the spot's own centre).</summary>
        static ACE.Entity.Position Centre(Spot spot, int hill = 0)
        {
            string key = spot.Key + "/" + hill;
            if (s_Centre != null && s_CentreKey == key) return s_Centre;
            try
            {
                // the exact map point (CellOfMap), the one the corpse sites, the landing, the quiet ground and the markers use;
                // ACE's map constructor lands 12 m south-west of it (box 2026-11-20: 84.72N 58.42E gave C8E9 88.8, 64.8)
                var at = HillPoints(spot)[Math.Clamp(hill, 0, HillPoints(spot).Length - 1)];
                var (cell, x, y) = CellOfMap(at.Ns, at.Ew);
                var pos = new ACE.Entity.Position(cell, x, y, 0f, 0f, 0f, 0f, 1f);
                pos.PositionZ = pos.GetTerrainZ();
                s_Centre = pos; s_CentreKey = key;
                Mod.Log.Info($"[RevivalGuard] PkNight: {spot.Name} hill {hill + 1} centre 0x{pos.Cell:X8} ({pos.PositionX:0.0}, {pos.PositionY:0.0}, {pos.PositionZ:0.0})");
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
            live.SideRule ??= SidesOf(PkCommon.Str(P_SIDES, SIDES_FELLOWSHIP));
            lock (s_Lock) { s_State.Live = live; Save(); }
            s_Centre = null;
            Centre(spot);
            int minutes = (int)Math.Round((live.EndsAt - live.StartedAt) / 60.0);
            World(StartLine(spot, minutes, live.ByFellowship));
            if (PkCommon.On(P_DISCORD) && !live.Forced) DiscordBridge.Announce(DiscordStartLine(spot, live.ByFellowship));
            Mod.Log.Info($"[RevivalGuard] PkNight: round {live.Key} open at {spot.Name}, {minutes} min, sides by {live.SideRule}{(live.Forced ? ", forced" : "")}{(live.Record ? "" : ", not recorded")}");
        }

        static void End(Live live, double now, string why)
        {
            var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
            var utc = DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime;
            var next = Next(utc);                                        // at a scheduled round's end this is the next round
            if (next != null && next.Running(utc)) next = After(next);   // a forced round inside the live hour
            var w = Leader(live);
            var rec = new RoundRec
            {
                Key = live.Key, SpotKey = live.SpotKey, StartedAt = (long)live.StartedAt, EndedAt = (long)now, Forced = live.Forced,
                WinnerSide = w?.Side ?? 0, Winner = w?.Name, Held = w?.Held ?? 0, Sides = live.Sides,
            };
            var winners = new List<Player>();
            uint bannerTo = 0;
            uint leaderNow = 0;   // fellowship mode: the winning fellowship's leader now, if any member is still in it
            if (w != null && live.ByFellowship && IsFellowKey(w.Side))
                foreach (var p in PlayerManager.GetAllOnline())
                    if (p.Fellowship != null && FellowKey(p.Fellowship, live) == w.Side) { leaderNow = p.Fellowship.FellowshipLeaderGuid; break; }
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
                if (live.Record) s_State.CrierLine = CrierLine(w?.Name, spot, w?.Held ?? 0, next, live.ByFellowship);
                if (live.Record && w != null)
                {
                    long token = PkCommon.Long(P_TOKEN, 0);
                    foreach (var g in w.Present)
                        if (PlayerManager.GetOnlinePlayer(g) == null)
                        {
                            Owe(g, TitleKey());
                            if (token > 0) Owe(g, HeldGroundBannerRule.ItemKey((uint)token));
                        }
                    if (PkCommon.On(P_BANNER))
                    {
                        // allegiance mode: the monarch, once per allegiance; fellowship mode: the leader, once per player
                        s_State.BannerPlayers ??= new HashSet<uint>();
                        bannerTo = BannerTo(live.ByFellowship, w, leaderNow, s_State.Banners, s_State.BannerPlayers);
                        if (bannerTo != 0) (live.ByFellowship ? s_State.BannerPlayers : s_State.Banners).Add(bannerTo);
                        else Mod.Log.Info($"[RevivalGuard] PkNight: {w.Name} already has a Banner of the Held Ground, none given");
                    }
                }
                Save();
            }
            ClearMarkers("the night is over");
            s_LineAt = 0;
            World(EndLine(live, spot, live.Forced ? null : next));
            if (live.Record) PkRenown.SeasonFinale();   // Darktide Season: each PK Night closes a round of the season
            if (PkCommon.On(P_DISCORD) && !live.Forced) DiscordBridge.Announce(DiscordEndLine(live, spot));
            if (live.Record) foreach (var p in winners) Reward(p);
            if (bannerTo != 0) GiveBanner(bannerTo, w.Name);
            if (live.Record && w != null) GiveRunePrize(w);
            Mod.Log.Info($"[RevivalGuard] PkNight: round {live.Key} over ({why}): {(w == null ? "no holder" : $"{w.Name} {Clock(w.Held)}")}, "
                + $"{live.Sides.Count} sides by {live.SideRule ?? SIDES_ALLEGIANCE}, {live.Benched.Count} benched, winners present {winners.Count}{(live.Record ? "" : ", not recorded")}");
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
            if (wcid > 0 && !Give(p, (uint)wcid)) { lock (s_Lock) { Owe(p.Guid.Full, HeldGroundBannerRule.ItemKey((uint)wcid)); Save(); } }
        }

        /// <summary>The night's one Cragstone Guise piece (rg_rune_skins, off: nothing; owner 2026-11-25): to the winner who
        /// stood on the hill longest (RuneSkinsRule.PkWinner, proposed), now if online, else at their login.</summary>
        static void GiveRunePrize(Standing w)
        {
            uint wcid = RuneSkins.PkNightPrize();
            uint g = wcid == 0 ? 0 : RuneSkinsRule.PkWinner(w.Present, w.Ticks);
            if (g == 0) return;
            var p = PlayerManager.GetOnlinePlayer(g);
            if (p != null && Give(p, wcid)) { World($"{p.Name} held the ground longest and takes a piece of the Cragstone Guise."); return; }
            lock (s_Lock) { Owe(g, HeldGroundBannerRule.ItemKey(wcid)); Save(); }
            Mod.Log.Info($"[RevivalGuard] PkNight: Cragstone piece {wcid} for {g:X8} waits for their login");
        }

        /// <summary>Add a title key or an "item:" key to what a guid is owed at login. Call under s_Lock.</summary>
        static void Owe(uint guid, string key)
        {
            if (!s_State.Pending.TryGetValue(guid, out var l)) s_State.Pending[guid] = l = new List<string>();
            l.Add(key);
        }

        /// <summary>One item into the player's pack, with its line. False when it does not exist or did not fit.</summary>
        static bool Give(Player p, uint wcid)
        {
            try
            {
                var wo = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null) { Mod.Log.Warn($"[RevivalGuard] PkNight: item {wcid} for {p.Name} does not exist"); return false; }
                if (!p.TryCreateInInventoryWithNetworking(wo))
                {
                    wo.Destroy();
                    Mod.Log.Warn($"[RevivalGuard] PkNight: item {wcid} for {p.Name} did not fit, kept for the next login");
                    PkCommon.Say(p, $"Your pack is too full for the {wo.Name} you won. It will wait for you at your next login.");
                    return false;
                }
                PkCommon.Say(p, wcid == HeldGroundBannerRule.WCID ? BannerLine : $"{wo.Name} is yours for the night.");
                Mod.Log.Info($"[RevivalGuard] PkNight: {wo.Name} ({wcid}) given to {p.Name}");
                return true;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkNight: item {wcid} for {p.Name}: {e.Message}"); return false; }
        }

        const string BannerLine = "The Banner of the Held Ground is yours. Hang it on a floor or yard hook in your house; "
            + "your allegiance may draw on it once a day.";

        /// <summary>The winning side's banner: to its monarch (or the lone winner; in fellowship mode the leader) now if
        /// online, else at their login.</summary>
        static void GiveBanner(uint side, string sideName)
        {
            var p = PlayerManager.GetOnlinePlayer(side);
            if (p != null && Give(p, HeldGroundBannerRule.WCID)) return;
            lock (s_Lock) { Owe(side, HeldGroundBannerRule.ItemKey(HeldGroundBannerRule.WCID)); Save(); }
            Mod.Log.Info($"[RevivalGuard] PkNight: Banner of the Held Ground for {sideName} ({side:X8}) waits for their login");
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
                    {
                        var items = owed.Where(k => HeldGroundBannerRule.TryItem(k, out _)).ToList();
                        if (items.Count > 0)
                        {
                            var p = __instance;
                            var give = new ACE.Server.Entity.Actions.ActionChain();
                            give.AddDelaySeconds(LOGIN_DELAY_S);
                            give.AddAction(p, () =>
                            {
                                foreach (var key in items)
                                    if (HeldGroundBannerRule.TryItem(key, out var wcid) && (p.Session == null || !Give(p, wcid)))
                                        lock (s_Lock) { Owe(p.Guid.Full, key); Save(); }
                            });
                            give.EnqueueChain();
                        }
                    }
                    if (owed != null)
                        foreach (var key in owed.Where(k => !HeldGroundBannerRule.TryItem(k, out _)).Distinct())
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
            if (next != null && (next.StartUtc - utc).TotalHours <= 24) return LoginSoonLine(next, FellowshipNow);
            return null;
        }

        // ------------------------------------------------------------------ what the crier and the calendar read

        /// <summary>The Town Crier's line about the last night (null until a round has been recorded).</summary>
        internal static string CrierLine() { lock (s_Lock) return On ? s_State.CrierLine : null; }

        /// <summary>The Town Crier's answer to "when is PK Night": the night running now, or the next one with its way.</summary>
        internal static string CrierNightLine()
        {
            var line = FestivalLine();
            if (line == null) return null;
            var now = ACE.Common.Time.GetUnixTime();
            Live live; lock (s_Lock) live = s_State.Live;
            var spot = live != null ? SpotNamed(live.SpotKey) : Next(DateTimeOffset.FromUnixTimeSeconds((long)now).UtcDateTime)?.Spot;
            return "Hear ye! " + line + (spot != null && !string.IsNullOrEmpty(spot.Way) ? " " + spot.Way : "");
        }

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
            if (verb == "start" || verb == "stop" || verb == "say" || verb == "reset" || verb == "gate")
            {
                if (!Staff(session)) { Out(session, "Only staff can do that."); return; }
                if (verb == "gate") { Out(session, GateStatus()); Out(session, MarkerStatus()); return; }
                if (verb == "say") { foreach (var l in AllLines(FellowshipNow)) Out(session, "  " + l); return; }
                if (verb == "reset")
                {
                    lock (s_Lock) { s_State = new State(); Save(); }
                    s_Centre = null;
                    SeedKeys();
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
                Out(session, $"PK Night: a {minutes} minute round at {spot.Name} ({spot.Where}){(record ? ", recorded on the leaderboard" : ", a rehearsal, not recorded")}. Centre: {Mansions.Where(s_Centre)}."
                    + (spot.HasGate ? " Its portals open on the next tick (@pknight gate)." : ""));
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
            uint mySide = MySide(asker, live);
            bool fs = ByFellowship(live);
            if (live != null)
            {
                var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
                lines.Add($"PK Night is on: hold {spot.Name} ({spot.Where}). {Clock(Math.Max(0, live.EndsAt - now))} left"
                    + (live.Contested ? ", contested now." : live.Holding != 0 && live.Sides.TryGetValue(live.Holding, out var h) ? $", {h.Name} holds it now." : ", no one holds it now."));
                double move = NextMoveAt(live, spot, now);
                lines.Add($"The hill stands at {MapWords(HillAt(live, spot, now))}" + (move > 0
                    ? $"; it moves to {MapWords(HillPoints(spot)[(HillIndex(live, spot, now) + 1) % HillPoints(spot).Length])} in {MinSec(move - now)}."
                    : " until the end."));
                var ranked = Ranked(live);
                if (ranked.Count == 0) lines.Add(NoneHeldYetLine(fs));
                int i = 0;
                foreach (var s in ranked.Take(10)) lines.Add($"{++i}. {s.Name}: {Clock(s.Held)}{(s.Side == mySide ? " (yours)" : "")}");
            }
            else
            {
                var next = Next(utc);
                if (next != null) lines.Add(NextRoundLine(next, fs));
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
            lines.Add(HowToHoldLine(Radius, fs));
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
            uint mySide = MySide(asker, live);
            sb.Append(",\"night\":{");
            // the board's words (AcPkBoardHud, from the client after 0.1.125; an older client ignores it and says "allegiance")
            sb.Append("\"sides\":").Append(ByFellowship(live) ? "\"fellowship\"," : "\"allegiance\",");
            if (live != null)
            {
                var spot = SpotNamed(live.SpotKey) ?? SPOTS[0];
                sb.Append("\"state\":\"live\",\"start\":").Append((long)live.StartedAt).Append(",\"end\":").Append((long)live.EndsAt)
                  .Append(",\"forced\":").Append(live.Forced ? 1 : 0).Append(",\"spot\":"); SpotJson(sb, spot);
                sb.Append(",\"holding\":").Append(live.Holding != 0 && live.Sides.TryGetValue(live.Holding, out var h) ? PkCommon.Q(h.Name) : "\"\"")
                  .Append(",\"contested\":").Append(live.Contested ? 1 : 0).Append(",\"present\":").Append(live.PresentNow)
                  .Append(",\"left\":").Append((long)Math.Max(0, live.EndsAt - now));
                AppendHillJson(sb, live, spot, asker, now);
                sb.Append(",\"standings\":[");
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
                s_State.Banners ??= new HashSet<uint>(); s_State.BannerPlayers ??= new HashSet<uint>();
                if (s_State.Live != null) { s_State.Live.Locked ??= new Dictionary<uint, uint>(); s_State.Live.Benched ??= new HashSet<uint>(); }
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkNight: cannot read {s_Path}: {e.Message}; starting empty"); s_State = new State(); }
            SeedKeys();
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
