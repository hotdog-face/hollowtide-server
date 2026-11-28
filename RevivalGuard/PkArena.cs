using System.Text;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Mode = RevivalGuard.PkArenaRules.Mode;

namespace RevivalGuard
{
    /// <summary>The arena queue: ranked fights on demand.</summary>
    internal static class PkArena
    {
        internal const string P_ON = "pk_arena_enabled", P_BOTS = "pk_arena_bots", P_NEWS = "pk_arena_news";
        const string P_MIN_LEVEL = "pk_arena_min_level", P_BAND = "pk_arena_band", P_BAND_STEP = "pk_arena_band_step",
            P_BAND_CAP = "pk_arena_band_cap", P_MINUTES = "pk_arena_minutes";
        const int COUNTDOWN = 10, ENTER_TIMEOUT = 30, SEND_HOME_AFTER = 5, TICK_SECONDS = 2;

        internal static bool On => PkCommon.On(P_ON);
        static bool Bots => PkCommon.On(P_BOTS);

        // ------------------------------------------------------------------ the arenas

        sealed class Room
        {
            public string Name; public uint Landblock; public (uint cell, float x, float y, float z)[] Spots;
            public (uint cell, float x, float y, float z) Exit;
        }

        // Retail's PK arena and general arena (dungeons 0x02E3 / 0x02E2, portals 7891 / 7894): the sunken pit.
        static (uint, float, float, float)[] Pit(uint lb) => new[]
        {
            ((lb << 16) | 0x0100u, 49.5f, -47f, 0.005f), ((lb << 16) | 0x0103u, 60.5f, -63f, 0.005f),
            ((lb << 16) | 0x0102u, 60.5f, -47f, 0.005f), ((lb << 16) | 0x0101u, 49.5f, -63f, 0.005f),
        };

        static readonly Room[] ROOMS =
        {
            new Room { Name = "the PK Arena", Landblock = 0x02E3, Spots = Pit(0x02E3), Exit = (0x02E301A3u, 130f, -50f, 12.005f) },
            new Room { Name = "the General Arena", Landblock = 0x02E2, Spots = Pit(0x02E2), Exit = (0x02E201A3u, 130f, -50f, 12.005f) },
        };

        /// <summary>A spot as a position; `k` steps toward the pit's middle (y -55) for a second teammate.</summary>
        static Position At((uint cell, float x, float y, float z) s, int k = 0) =>
            new Position(s.cell, s.x, s.y + (s.y < -55f ? 2f : -2f) * k, s.z, 0f, 0f, 0f, 1f);

        // ------------------------------------------------------------------ state

        enum Phase { Countdown, Entering, Live, Ending }

        sealed class Fighter
        {
            public uint Guid; public string Name; public int Level, Side, Rating; public bool Arrived, Out, Home;
            public string Why;
        }

        sealed class Match
        {
            public int Id; public Mode Mode; public Room Room; public Phase Phase; public long PhaseAt, StartedAt;
            public List<Fighter> F = new List<Fighter>();
            public int Sides => F.Select(f => f.Side).Distinct().Count();
            public Fighter Of(uint g) => F.FirstOrDefault(f => f.Guid == g);
        }

        sealed class Result { public long At { get; set; } public string Mode { get; set; } public string Line { get; set; } }

        internal sealed class Rec
        {
            public string Name { get; set; } = "";
            public int[] R { get; set; } = { PkArenaRules.START_RATING, PkArenaRules.START_RATING, PkArenaRules.START_RATING };
            public int[] W { get; set; } = new int[3];
            public int[] L { get; set; } = new int[3];
            public int[] D { get; set; } = new int[3];
        }

        sealed class Ledger
        {
            public Dictionary<string, Dictionary<uint, Rec>> Seasons { get; set; } = new Dictionary<string, Dictionary<uint, Rec>>();
            public List<Result> Recent { get; set; } = new List<Result>();
        }

        sealed class HomeRec { public uint Cell { get; set; } public float X { get; set; } public float Y { get; set; } public float Z { get; set; }
            public float QX { get; set; } public float QY { get; set; } public float QZ { get; set; } public float QW { get; set; } = 1f; public long At { get; set; } }

        static readonly object s_Lock = new object();
        static readonly List<PkArenaRules.Waiter>[] s_Queue = { new List<PkArenaRules.Waiter>(), new List<PkArenaRules.Waiter>(), new List<PkArenaRules.Waiter>() };
        static readonly List<Match> s_Matches = new List<Match>();
        static Ledger s_Ledger = new Ledger();
        static Dictionary<uint, HomeRec> s_Homes = new Dictionary<uint, HomeRec>();
        static string s_LedgerPath, s_HomesPath;
        static int s_NextId = 1;
        static long s_LastTick;
        static bool s_WasOn;

        // ------------------------------------------------------------------ registration

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "Arena queue: ranked 1v1/2v2/ffa on demand in the PK Arena, no item loss or vitae (RevivalGuard PkArena; docs/PK-ARENA-QUEUE.md)");
            PkCommon.RegisterBool(P_BOTS, false, "Arena queue: let Borg and Hollow Company mercenaries queue (owner's switch; off = players only)");
            PkCommon.RegisterBool(P_NEWS, true, "Arena queue: tell everyone online the result of each match");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                void Add(string k, long d, string t) { if (!l.ContainsKey(k)) l[k] = new Property<long>(d, t); }
                Add(P_MIN_LEVEL, 10, "Arena queue: lowest level that may queue");
                Add(P_BAND, 10, "Arena queue: level gap allowed at once");
                Add(P_BAND_STEP, 5, "Arena queue: levels the gap widens per minute waited");
                Add(P_BAND_CAP, 40, "Arena queue: the widest level gap ever matched");
                Add(P_MINUTES, 10, "Arena queue: minutes a match may run before it is a draw");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkArena: longs not registered with @modifylong ({e.Message}); defaults used"); }
            Load();
            CommandManager.TryAddCommand(Command, "fight", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The arena queue: ranked fights on demand, no item loss.", "[join 1v1|2v2|ffa | leave | top]");
            Mod.Log.Info($"[RevivalGuard] PkArena: {(On ? "ON" : "off")} ({P_ON}), bots {(Bots ? "allowed" : "never")}, @fight; {s_Homes.Count} fighter(s) owed a trip home");
        }

        // ------------------------------------------------------------------ the command

        static void Command(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            string verb = parameters != null && parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            string arg = parameters != null && parameters.Length > 1 ? parameters[1].ToLowerInvariant() : "";
            bool admin = session.AccessLevel >= AccessLevel.Admin;

            if (admin && (verb == "on" || verb == "off"))
            {
                PropertyManager.ModifyBool(P_ON, verb == "on");
                Mod.Log.Info($"[RevivalGuard] PkArena: switched {verb} by {p.Name}");
                PkCommon.Say(p, $"Arena queue {(verb == "on" ? "ON: players can @fight join" : "OFF: queues cleared, running matches end with no result")}.");
                if (verb == "off") StopAll("The arena queue was closed.");
                return;
            }
            if (admin && verb == "bots" && (arg == "on" || arg == "off"))
            {
                PropertyManager.ModifyBool(P_BOTS, arg == "on");
                PkCommon.Say(p, arg == "on" ? "Borg and mercenaries may now queue for the arena." : "Only players may queue for the arena (Borg and mercenaries refused).");
                return;
            }
            if (admin && verb == "cancel") { StopAll("An admin ended the match. No result."); PkCommon.Say(p, "Every arena match ended, no result."); return; }
            if (admin && verb == "matches") { PkCommon.Say(p, Describe()); return; }

            if (verb == "top") { Top(p, arg); return; }

            if (!On) { PkCommon.Say(p, "The arena queue is not open on this world."); return; }

            if (verb == "join")
            {
                if (!PkArenaRules.TryMode(arg, out var m)) { PkCommon.Say(p, "Usage: @fight join 1v1, @fight join 2v2 or @fight join ffa"); return; }
                PkCommon.Say(p, Join(p, m));
                Refresh(p);
                return;
            }
            if (verb == "leave")
            {
                Mode? only = PkArenaRules.TryMode(arg, out var m) ? m : (Mode?)null;
                PkCommon.Say(p, Leave(p, only));
                Refresh(p);
                return;
            }
            if (ModChannel.Capable(session)) PkBoard.Send(session, true, "arena");
            else PkCommon.Say(p, StateLine(p) + " @fight join 1v1 | 2v2 | ffa, @fight leave, @fight top.");
        }

        static string Join(Player p, Mode m)
        {
            string why = Refusal(p);
            if (why != null) return why;
            lock (s_Lock)
            {
                if (FindMatch(p.Guid.Full) != null) return "You are already in an arena match.";
                if (s_Queue[(int)m].Any(w => w.Guid == p.Guid.Full)) return $"You are already in the {Word(m)} queue. ({QueueLine(m)})";
                s_Queue[(int)m].Add(WaiterOf(p, PkCommon.Now()));
            }
            Mod.Log.Info($"[RevivalGuard] PkArena: {p.Name} (L{p.Level}) joined {Word(m)}");
            return $"You join the {Word(m)} arena queue. ({QueueLine(m)}) No item loss, no vitae: you come back here after the fight. @fight leave to stop.";
        }

        static string Leave(Player p, Mode? only)
        {
            lock (s_Lock)
            {
                var match = FindMatch(p.Guid.Full);
                if (match != null && match.Phase == Phase.Countdown)
                {
                    Cancel(match, p.Guid.Full, $"{p.Name} backed out.");
                    return "You back out of the match. The others go back to the queue.";
                }
                if (match != null) return "Your match has begun: leaving the arena now forfeits it.";
                int n = 0;
                for (int i = 0; i < 3; i++)
                    if (only == null || (int)only.Value == i) n += s_Queue[i].RemoveAll(w => w.Guid == p.Guid.Full);
                return n > 0 ? "You leave the arena queue." : "You are not in the arena queue.";
            }
        }

        /// <summary>Why this character may not queue, or null.</summary>
        static string Refusal(Player p)
        {
            if (p.PlayerKillerStatus != PlayerKillerStatus.PK) return "Only a player killer may queue for the arena (after a death, wait out your respite).";
            long min = PkCommon.Long(P_MIN_LEVEL, 10);
            if ((p.Level ?? 1) < min) return $"The arena takes player killers of level {min} and up.";
            if (!Bots && (BorgSwitch.IsBorg(p) || Mercenaries.IsMerc(p))) return "The arena queue is for players.";
            return null;
        }

        static PkArenaRules.Waiter WaiterOf(Player p, long since)
        {
            string addr = "";
            try
            {
                if (!PkCommon.On(PkCommon.P_IGNORE_IP))
                {
                    var a = Gateway.RealAddress(p.Session?.EndPointC2S);
                    if (a != null && !Gateway.IsUnknown(a)) addr = (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
                }
            }
            catch { }
            return new PkArenaRules.Waiter
            {
                Guid = p.Guid.Full, Name = p.Name, Level = p.Level ?? 1, Account = p.Account?.AccountId ?? p.Guid.Full,
                Address = addr, Since = since,
            };
        }

        // ------------------------------------------------------------------ the world loop

        internal static void Tick()
        {
            long now = PkCommon.Now();
            if (now - s_LastTick < TICK_SECONDS) return;
            s_LastTick = now;
            bool on = On;
            if (!on)
            {
                if (s_WasOn) StopAll("The arena queue was closed.");
                s_WasOn = false;
                return;
            }
            s_WasOn = true;
            var touched = new HashSet<uint>();
            lock (s_Lock)
            {
                for (int i = 0; i < 3; i++)
                    s_Queue[i].RemoveAll(w =>
                    {
                        var p = PlayerManager.GetOnlinePlayer(w.Guid);
                        if (p == null) return true;
                        var why = Refusal(p);
                        if (why == null) { w.Level = p.Level ?? w.Level; return false; }
                        PkCommon.Say(p, "You leave the arena queue: " + why);
                        touched.Add(w.Guid);
                        return true;
                    });
                foreach (var m in s_Matches.ToList()) Advance(m, now, touched);
                s_Matches.RemoveAll(m => m.Phase == Phase.Ending && m.F.All(f => f.Home || f.Out));
                MakeMatches(now, touched);
            }
            foreach (var g in touched) Refresh(PlayerManager.GetOnlinePlayer(g));
        }

        static void MakeMatches(long now, HashSet<uint> touched)
        {
            for (int i = 0; i < 3; i++)
            {
                var room = FreeRoom();
                if (room == null) return;
                var mode = (Mode)i;
                var pick = PkArenaRules.Pick(s_Queue[i], mode, now, (int)PkCommon.Long(P_BAND, 10),
                    (int)PkCommon.Long(P_BAND_STEP, 5), (int)PkCommon.Long(P_BAND_CAP, 40));
                if (pick == null) continue;
                var match = new Match { Id = s_NextId++, Mode = mode, Room = room, Phase = Phase.Countdown, PhaseAt = now };
                var sides = PkArenaRules.Sides(pick, mode);
                for (int s = 0; s < sides.Count; s++)
                    foreach (var w in sides[s])
                        match.F.Add(new Fighter { Guid = w.Guid, Name = w.Name, Level = w.Level, Side = s, Rating = RatingOf(w.Guid, w.Name, mode) });
                foreach (var f in match.F)
                    for (int q = 0; q < 3; q++) s_Queue[q].RemoveAll(w => w.Guid == f.Guid);
                s_Matches.Add(match);
                Mod.Log.Info($"[RevivalGuard] PkArena: match {match.Id} {Word(mode)} in {room.Name}: {Teams(match)}");
                foreach (var f in match.F)
                {
                    touched.Add(f.Guid);
                    Tell(f, $"Arena match found: {Word(mode)}, {Against(match, f)}. You are taken to {room.Name} in {COUNTDOWN} seconds. @fight leave backs out.");
                }
                i--;   // the same mode may have a second match in another room
            }
        }

        static void Advance(Match m, long now, HashSet<uint> touched)
        {
            switch (m.Phase)
            {
                case Phase.Countdown:
                    if (now - m.PhaseAt < COUNTDOWN) return;
                    foreach (var f in m.F)
                    {
                        var p = PlayerManager.GetOnlinePlayer(f.Guid);
                        string why = p == null ? "logged off" : Refusal(p) ?? NotNow(p);
                        if (why == null) continue;
                        if (p != null) PkCommon.Say(p, $"You could not be taken into the arena ({why}); you leave the queue.");
                        Cancel(m, f.Guid, $"{f.Name} could not come ({why}).");
                        return;
                    }
                    EvictOutsiders(m);
                    var bySide = m.F.GroupBy(f => f.Side).ToList();
                    foreach (var g in bySide)
                    {
                        int k = 0;
                        foreach (var f in g)
                        {
                            var p = PlayerManager.GetOnlinePlayer(f.Guid);
                            SaveHome(p);
                            f.Home = false;
                            WorldManager.ThreadSafeTeleport(p, At(m.Room.Spots[f.Side % m.Room.Spots.Length], k++));
                        }
                    }
                    m.Phase = Phase.Entering; m.PhaseAt = now;
                    Mod.Log.Info($"[RevivalGuard] PkArena: match {m.Id} entering");
                    return;

                case Phase.Entering:
                    foreach (var f in m.F.Where(f => !f.Out && !f.Arrived))
                    {
                        var p = PlayerManager.GetOnlinePlayer(f.Guid);
                        if (p != null && InRoom(p, m.Room) && !p.Teleporting) { f.Arrived = true; p.SetMaxVitals(); }
                    }
                    if (now - m.PhaseAt >= ENTER_TIMEOUT)
                        foreach (var f in m.F.Where(f => !f.Out && !f.Arrived)) Out(m, f, "did not arrive", touched, sendHome: true);
                    if (m.F.Any(f => !f.Out && !f.Arrived)) return;
                    if (StandingSides(m) < 2) { Finish(m, now, touched); return; }
                    m.Phase = Phase.Live; m.PhaseAt = m.StartedAt = now;
                    foreach (var f in m.F.Where(f => !f.Out)) { touched.Add(f.Guid); Tell(f, $"Fight! {Against(m, f)}. Last side standing wins."); }
                    Mod.Log.Info($"[RevivalGuard] PkArena: match {m.Id} live");
                    return;

                case Phase.Live:
                    foreach (var f in m.F.Where(f => !f.Out))
                    {
                        var p = PlayerManager.GetOnlinePlayer(f.Guid);
                        if (p == null) Out(m, f, "logged off", touched, sendHome: false);
                        else if (!InRoom(p, m.Room) && !p.Teleporting && !p.IsInDeathProcess) { Out(m, f, "left the arena", touched, sendHome: false); ForgetHome(f.Guid); f.Home = true; }
                    }
                    EvictOutsiders(m);
                    if (StandingSides(m) < 2) { Finish(m, now, touched); return; }
                    if (now - m.StartedAt >= PkCommon.Long(P_MINUTES, 10) * 60) Finish(m, now, touched);
                    return;

                case Phase.Ending:
                    if (now - m.PhaseAt < SEND_HOME_AFTER) return;
                    foreach (var f in m.F.Where(f => !f.Out && !f.Home))
                    {
                        f.Home = true;
                        var p = PlayerManager.GetOnlinePlayer(f.Guid);
                        if (p != null && InRoom(p, m.Room)) SendHome(p, false);
                    }
                    return;
            }
        }

        /// <summary>Why a queued character cannot be taken in right now, or null.</summary>
        static string NotNow(Player p)
        {
            if (p.PKTimerActive) return "you were in a PK fight moments ago";
            if (p.IsDead || p.IsInDeathProcess) return "you were dead";
            if (p.Teleporting) return "you were in portal space";
            if (p.IsLoggingOut) return "you were logging off";
            return null;
        }

        static int StandingSides(Match m) => m.F.Where(f => !f.Out).Select(f => f.Side).Distinct().Count();

        static void Out(Match m, Fighter f, string why, HashSet<uint> touched, bool sendHome)
        {
            f.Out = true; f.Why = why;
            touched.Add(f.Guid);
            Mod.Log.Info($"[RevivalGuard] PkArena: match {m.Id}: {f.Name} {why}");
            foreach (var o in m.F.Where(o => o != f && !o.Out)) Tell(o, $"{f.Name} {why}.");
            if (sendHome)
            {
                var p = PlayerManager.GetOnlinePlayer(f.Guid);
                if (p != null && InRoom(p, m.Room)) SendHome(p, false);
                f.Home = true;
            }
        }

        /// <summary>The match is over: the side still standing wins; ratings, news, and the winners go home in a few seconds.</summary>
        static void Finish(Match m, long now, HashSet<uint> touched)
        {
            var standing = m.F.Where(f => !f.Out).Select(f => f.Side).Distinct().ToList();
            int winner = standing.Count == 1 ? standing[0] : -1;
            int sides = m.F.Max(f => f.Side) + 1;
            var place = Enumerable.Range(0, sides).Select(s => winner < 0 ? 0 : s == winner ? 0 : 1).ToArray();
            var rating = Enumerable.Range(0, sides).Select(s => m.F.Where(f => f.Side == s).Average(f => (double)f.Rating)).ToArray();
            var change = PkArenaRules.Changes(rating, place);
            string season = PkRenown.Season();
            string line;
            if (winner < 0) line = $"{Teams(m)}: a draw.";
            else
            {
                string win = string.Join(" and ", m.F.Where(f => f.Side == winner).Select(f => $"{f.Name} ({f.Level})"));
                string lose = string.Join(", ", m.F.Where(f => f.Side != winner).Select(f => $"{f.Name} ({f.Level})"));
                line = $"{win} defeated {lose}.";
            }
            lock (s_Lock)
            {
                if (!s_Ledger.Seasons.TryGetValue(season, out var recs)) s_Ledger.Seasons[season] = recs = new Dictionary<uint, Rec>();
                foreach (var f in m.F)
                {
                    if (!recs.TryGetValue(f.Guid, out var r)) recs[f.Guid] = r = new Rec();
                    r.Name = f.Name;
                    int i = (int)m.Mode;
                    r.R[i] = Math.Max(0, r.R[i] + change[f.Side]);
                    if (winner < 0) r.D[i]++; else if (f.Side == winner) r.W[i]++; else r.L[i]++;
                }
                s_Ledger.Recent.Insert(0, new Result { At = now, Mode = Word(m.Mode), Line = line });
                if (s_Ledger.Recent.Count > 10) s_Ledger.Recent.RemoveRange(10, s_Ledger.Recent.Count - 10);
                Save();
            }
            Mod.Log.Info($"[RevivalGuard] PkArena: match {m.Id} over: {line} changes [{string.Join(",", change)}]");
            foreach (var f in m.F)
            {
                touched.Add(f.Guid);
                int d = change[f.Side];
                string res = winner < 0 ? "A draw" : f.Side == winner ? "You win" : "You lose";
                Tell(f, $"{res}. Arena rating {(d >= 0 ? "+" : "")}{d} ({Word(m.Mode)}).{(!f.Out ? $" You go back in {SEND_HOME_AFTER} seconds." : "")}");
            }
            if (PkCommon.On(P_NEWS)) PkCommon.World($"Arena ({Word(m.Mode)}): {line}");
            m.Phase = Phase.Ending; m.PhaseAt = now;
        }

        /// <summary>Stop a match before it began: the one who could not come leaves the queue, the rest go back to it with their place kept.</summary>
        static void Cancel(Match m, uint culprit, string why)
        {
            long now = PkCommon.Now();
            foreach (var f in m.F.Where(f => f.Guid != culprit))
            {
                var p = PlayerManager.GetOnlinePlayer(f.Guid);
                if (p == null) continue;
                var w = WaiterOf(p, now - 30);   // a little ahead of anyone who joins now
                s_Queue[(int)m.Mode].Add(w);
                Tell(f, $"The match is off: {why} You are back in the {Word(m.Mode)} queue.");
            }
            s_Matches.Remove(m);
            Mod.Log.Info($"[RevivalGuard] PkArena: match {m.Id} cancelled: {why}");
        }

        /// <summary>Switch off or admin cancel: no result, everyone home, queues emptied.</summary>
        static void StopAll(string why)
        {
            List<Match> all;
            lock (s_Lock)
            {
                all = s_Matches.ToList();
                s_Matches.Clear();
                foreach (var q in s_Queue)
                {
                    foreach (var w in q) PkCommon.Say(PlayerManager.GetOnlinePlayer(w.Guid), "You leave the arena queue: " + why);
                    q.Clear();
                }
            }
            foreach (var m in all)
                foreach (var f in m.F)
                {
                    var p = PlayerManager.GetOnlinePlayer(f.Guid);
                    if (p == null) continue;
                    PkCommon.Say(p, why);
                    if (InRoom(p, m.Room) && !f.Home) SendHome(p, false);
                }
        }

        // ------------------------------------------------------------------ the arena death

        /// <summary>Player.Die for a fighter in a running match, inside its arena: true when the arena took the death.</summary>
        internal static bool ArenaDeath(Player p, DamageHistoryInfo topDamager)
        {
            if (p == null) return false;
            Match m; Fighter f;
            var touched = new HashSet<uint>();
            lock (s_Lock)
            {
                m = FindMatch(p.Guid.Full);
                f = m?.Of(p.Guid.Full);
                if (m == null || f == null || f.Out || m.Phase != Phase.Live || !InRoom(p, m.Room)) return false;
                string by = topDamager?.Name;
                Out(m, f, string.IsNullOrEmpty(by) || topDamager.Guid == p.Guid ? "fell" : $"fell to {by}", touched, sendHome: false);
                f.Home = true;
                if (StandingSides(m) < 2) Finish(m, PkCommon.Now(), touched);
            }
            Mod.Log.Info($"[RevivalGuard] PkArena: arena death of {p.Name} (no corpse, no vitae)");
            p.IsInDeathProcess = true;
            p.IsBusy = true;
            p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Dead));
            var chain = new ActionChain();
            chain.AddDelaySeconds(2.0);
            chain.AddAction(p, () => SendHome(p, true));
            chain.EnqueueChain();
            foreach (var g in touched) Refresh(PlayerManager.GetOnlinePlayer(g));
            return true;
        }

        /// <summary>Back to where the match found them, at full vitals with every harmful spell lifted.</summary>
        static void SendHome(Player p, bool fromDeath)
        {
            if (p == null) return;
            var to = TakeHome(p.Guid.Full) ?? p.Sanctuary ?? p.Instantiation;
            if (to == null) { Mod.Log.Warn($"[RevivalGuard] PkArena: no home for {p.Name}"); return; }
            WorldManager.ThreadSafeTeleport(p, new Position(to), new ActionEventDelegate(() =>
            {
                try
                {
                    if (fromDeath) p.SetCombatMode(CombatMode.NonCombat);
                    p.EnchantmentManager.RemoveAllBadEnchantments();
                    p.Session?.Network.EnqueueSend(new GameEventMagicPurgeBadEnchantments(p.Session));
                    p.SetMaxVitals();
                    p.DamageHistory.Reset();
                    p.OnHealthUpdate();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkArena: restoring {p.Name}: {e.Message}"); }
                finally { p.IsInDeathProcess = false; p.IsBusy = false; }
            }));
        }

        /// <summary>Anyone in a running match's arena who is not one of its fighters is shown out</summary>
        static void EvictOutsiders(Match m)
        {
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (p?.Location == null || p.Location.Landblock != m.Room.Landblock || m.Of(p.Guid.Full) != null) continue;
                if (p.Session != null && p.Session.AccessLevel >= AccessLevel.Sentinel) continue;   // staff may watch
                if (p.Teleporting || p.IsInDeathProcess) continue;
                PkCommon.Say(p, $"A ranked arena match holds {m.Room.Name}; you are shown out.");
                bool owed;
                lock (s_Lock) owed = s_Homes.ContainsKey(p.Guid.Full);
                if (owed) SendHome(p, false);
                else WorldManager.ThreadSafeTeleport(p, At(m.Room.Exit));
            }
        }

        /// <summary>At login: a character inside one of our arenas who is owed a trip home and is not fighting there now.</summary>
        internal static void OnEnterWorld(Player p)
        {
            if (p?.Location == null || !ROOMS.Any(r => r.Landblock == p.Location.Landblock)) return;
            lock (s_Lock)
            {
                if (!s_Homes.ContainsKey(p.Guid.Full)) return;
                var m = FindMatch(p.Guid.Full);
                var f = m?.Of(p.Guid.Full);
                if (f != null && !f.Out && m.Phase != Phase.Ending) return;
            }
            var chain = new ActionChain();
            chain.AddDelaySeconds(3.0);
            chain.AddAction(p, () =>
            {
                PkCommon.Say(p, "Your arena match is over; you go back to where you were.");
                SendHome(p, false);
            });
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ helpers

        static Match FindMatch(uint g) => s_Matches.FirstOrDefault(m => m.Of(g) != null);

        /// <summary>True for a fighter in a match that has not ended (exposed for other PK features: an arena kill).</summary>
        public static bool InMatch(Player p)
        {
            if (p == null) return false;
            lock (s_Lock)
            {
                var m = FindMatch(p.Guid.Full);
                return m != null && m.Phase == Phase.Live && m.Of(p.Guid.Full) is Fighter f && !f.Out;
            }
        }

        static Room FreeRoom() => ROOMS.FirstOrDefault(r => !s_Matches.Any(m => m.Room == r));

        static bool InRoom(Player p, Room r) => p?.Location != null && p.Location.Landblock == r.Landblock;

        static string Word(Mode m) => PkArenaRules.MODE_WORD[(int)m];

        static string QueueLine(Mode m)
        {
            int n = s_Queue[(int)m].Count;
            return n == 1 ? "1 waiting" : $"{n} waiting";
        }

        static string Teams(Match m) =>
            string.Join(" vs ", m.F.GroupBy(f => f.Side).Select(g => string.Join(" and ", g.Select(f => $"{f.Name} ({f.Level})"))));

        static string Against(Match m, Fighter f)
        {
            var mates = m.F.Where(o => o.Side == f.Side && o != f).Select(o => o.Name).ToList();
            var foes = m.F.Where(o => o.Side != f.Side).Select(o => $"{o.Name} ({o.Level})");
            return (mates.Count > 0 ? $"with {string.Join(" and ", mates)} " : "") + $"against {string.Join(", ", foes)}";
        }

        static void Tell(Fighter f, string line) => PkCommon.Say(PlayerManager.GetOnlinePlayer(f.Guid), line);

        static void Refresh(Player p)
        {
            var s = p?.Session;
            if (s != null && ModChannel.Capable(s)) PkBoard.Send(s, false, null);
        }

        static int RatingOf(uint g, string name, Mode m)
        {
            string season = PkRenown.Season();
            return s_Ledger.Seasons.TryGetValue(season, out var recs) && recs.TryGetValue(g, out var r) ? r.R[(int)m] : PkArenaRules.START_RATING;
        }

        static string StateLine(Player p)
        {
            lock (s_Lock)
            {
                var m = FindMatch(p.Guid.Full);
                if (m != null) return $"You are in a {Word(m.Mode)} arena match ({m.Phase.ToString().ToLowerInvariant()}), {Against(m, m.Of(p.Guid.Full))}.";
                var modes = Enumerable.Range(0, 3).Where(i => s_Queue[i].Any(w => w.Guid == p.Guid.Full)).Select(i => Word((Mode)i)).ToList();
                return modes.Count > 0 ? $"You are queued for {string.Join(", ", modes)}." : "You are not queued.";
            }
        }

        static void Top(Player p, string arg)
        {
            var m = PkArenaRules.TryMode(arg, out var mm) ? mm : Mode.Duel;
            string season = PkRenown.Season();
            List<Rec> rows;
            lock (s_Lock)
                rows = s_Ledger.Seasons.TryGetValue(season, out var recs)
                    ? recs.Values.Where(r => r.W[(int)m] + r.L[(int)m] + r.D[(int)m] > 0).OrderByDescending(r => r.R[(int)m]).Take(10).ToList()
                    : new List<Rec>();
            if (rows.Count == 0) { PkCommon.Say(p, $"No {Word(m)} arena fights yet this season ({season})."); return; }
            var sb = new StringBuilder($"Arena {Word(m)}, {season}:");
            int i = 0;
            foreach (var r in rows) sb.Append($"\n{++i}. {r.Name} {r.R[(int)m]} ({r.W[(int)m]}-{r.L[(int)m]}{(r.D[(int)m] > 0 ? "-" + r.D[(int)m] : "")})");
            PkCommon.Say(p, sb.ToString());
        }

        static string Describe()
        {
            lock (s_Lock)
            {
                var sb = new StringBuilder($"[Arena queue] {(On ? "ON" : "off")}, bots {(Bots ? "allowed" : "never")}; queued: "
                    + string.Join(", ", Enumerable.Range(0, 3).Select(i => $"{Word((Mode)i)} {s_Queue[i].Count}")) + $"; homes owed {s_Homes.Count}");
                foreach (var m in s_Matches)
                    sb.Append($"\n#{m.Id} {Word(m.Mode)} in {m.Room.Name}, {m.Phase.ToString().ToLowerInvariant()}: {Teams(m)}"
                        + (m.F.Any(f => f.Out) ? " (out: " + string.Join(", ", m.F.Where(f => f.Out).Select(f => $"{f.Name} {f.Why}")) + ")" : ""));
                if (s_Matches.Count == 0) sb.Append("\nNo match running.");
                return sb.ToString();
            }
        }

        /// <summary>The Arena view of the PK board (Assets/Streaming/AcPkBoardHud.Arena.cs).</summary>
        internal static void AppendJson(StringBuilder sb, Player p)
        {
            string season = PkRenown.Season();
            long now = PkCommon.Now();
            lock (s_Lock)
            {
                sb.Append(",\"arena\":{\"min\":").Append(PkCommon.Long(P_MIN_LEVEL, 10)).Append(",\"q\":[");
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) sb.Append(',');
                    var mine = s_Queue[i].FirstOrDefault(w => w.Guid == p.Guid.Full);
                    sb.Append("{\"n\":").Append(s_Queue[i].Count).Append(",\"in\":").Append(mine != null ? 1 : 0)
                      .Append(",\"wait\":").Append(mine != null ? now - mine.Since : 0).Append('}');
                }
                sb.Append(']');
                var m = FindMatch(p.Guid.Full);
                if (m != null)
                {
                    var f = m.Of(p.Guid.Full);
                    sb.Append(",\"match\":{\"mode\":").Append(PkCommon.Q(Word(m.Mode))).Append(",\"phase\":").Append(PkCommon.Q(m.Phase.ToString().ToLowerInvariant()))
                      .Append(",\"vs\":").Append(PkCommon.Q(Against(m, f))).Append(",\"where\":").Append(PkCommon.Q(m.Room.Name))
                      .Append(",\"out\":").Append(f.Out ? 1 : 0).Append('}');
                }
                sb.Append(",\"running\":").Append(s_Matches.Count(x => x.Phase == Phase.Live));
                s_Ledger.Seasons.TryGetValue(season, out var recs);
                sb.Append(",\"me\":");
                if (recs != null && recs.TryGetValue(p.Guid.Full, out var me))
                    sb.Append("{\"r\":[").Append(string.Join(",", me.R)).Append("],\"w\":[").Append(string.Join(",", me.W))
                      .Append("],\"l\":[").Append(string.Join(",", me.L)).Append("],\"d\":[").Append(string.Join(",", me.D)).Append("]}");
                else sb.Append("null");
                sb.Append(",\"top\":[");
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('[');
                    int n = 0;
                    if (recs != null)
                        foreach (var r in recs.Values.Where(r => r.W[i] + r.L[i] + r.D[i] > 0).OrderByDescending(r => r.R[i]).Take(10))
                        {
                            if (n++ > 0) sb.Append(',');
                            sb.Append("{\"n\":").Append(PkCommon.Q(r.Name)).Append(",\"r\":").Append(r.R[i]).Append(",\"w\":").Append(r.W[i])
                              .Append(",\"l\":").Append(r.L[i]).Append(",\"d\":").Append(r.D[i]).Append('}');
                        }
                    sb.Append(']');
                }
                sb.Append("],\"recent\":[");
                int k = 0;
                foreach (var r in s_Ledger.Recent.Take(5))
                {
                    if (k++ > 0) sb.Append(',');
                    sb.Append("{\"at\":").Append(r.At).Append(",\"m\":").Append(PkCommon.Q(r.Mode)).Append(",\"t\":").Append(PkCommon.Q(r.Line)).Append('}');
                }
                sb.Append("]}");
            }
        }

        // ------------------------------------------------------------------ the files

        static void SaveHome(Player p)
        {
            if (p?.Location == null) return;
            var l = p.Location;
            lock (s_Lock)
            {
                s_Homes[p.Guid.Full] = new HomeRec { Cell = l.Cell, X = l.PositionX, Y = l.PositionY, Z = l.PositionZ,
                    QX = l.RotationX, QY = l.RotationY, QZ = l.RotationZ, QW = l.RotationW, At = PkCommon.Now() };
                SaveHomes();
            }
        }

        static Position TakeHome(uint g)
        {
            lock (s_Lock)
            {
                if (!s_Homes.TryGetValue(g, out var h)) return null;
                s_Homes.Remove(g);
                SaveHomes();
                return new Position(h.Cell, h.X, h.Y, h.Z, h.QX, h.QY, h.QZ, h.QW);
            }
        }

        static void ForgetHome(uint g) { lock (s_Lock) if (s_Homes.Remove(g)) SaveHomes(); }

        static void Load()
        {
            try
            {
                var dir = ModFolder.Sub("PkLedger");
                if (dir == null) return;
                s_LedgerPath = Path.Combine(dir, "arena.json");
                s_HomesPath = Path.Combine(dir, "arena-homes.json");
                if (File.Exists(s_LedgerPath)) s_Ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(s_LedgerPath)) ?? new Ledger();
                if (File.Exists(s_HomesPath)) s_Homes = JsonSerializer.Deserialize<Dictionary<uint, HomeRec>>(File.ReadAllText(s_HomesPath)) ?? new Dictionary<uint, HomeRec>();
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkArena: cannot read its files: {e.Message}; starting empty"); }
        }

        static void Save() => Write(s_LedgerPath, JsonSerializer.Serialize(s_Ledger));
        static void SaveHomes() => Write(s_HomesPath, JsonSerializer.Serialize(s_Homes));

        static void Write(string path, string text)
        {
            if (path == null) return;
            try { var tmp = path + ".tmp"; File.WriteAllText(tmp, text); File.Move(tmp, path, true); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkArena: cannot write {path}: {e.Message}"); }
        }
    }

    /// <summary>The queue and the matches, from the world loop.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class PkArenaTick
    {
        static void Postfix()
        {
            try { PkArena.Tick(); }
            catch (Exception e) { TickGuard.Report("PkArena", e); }
        }
    }

    /// <summary>A fighter's death inside the arena: the arena's, not ACE's (no corpse, no vitae, home).</summary>
    [HarmonyPatch(typeof(Player), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
    static class PkArenaDie
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Player __instance, DamageHistoryInfo topDamager)
        {
            try { return !PkArena.ArenaDeath(__instance, topDamager); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkArena death of {__instance?.Name}: {e}"); return true; }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class PkArenaLogin
    {
        static void Postfix(Player __instance)
        {
            try { PkArena.OnEnterWorld(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkArena login of {__instance?.Name}: {e.Message}"); }
        }
    }
}
