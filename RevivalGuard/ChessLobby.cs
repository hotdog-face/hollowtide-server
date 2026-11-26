using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using ACE.Database.Models.World;
using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Entity.Chess;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;
using R = RevivalGuard.ChessLobbyRule;
using PLandblock = ACE.Server.Physics.Common.Landblock;

namespace RevivalGuard
{
    /// <summary>
    /// THE CHESS LOBBY (owner 2026-11-25: "i also want to make it easier for players to play chess in the game. the top left
    /// chess icon currently does nothing"). Our client's Game Center icon opens a lobby when no game runs (ChessHud.Lobby.cs);
    /// this is its server half, all behind rg_chess_lobby (default OFF: /chess says it is not open, nothing is spawned or
    /// recorded).
    ///
    ///   Play the computer: "/chess ai easy|normal" sets a TEMPORARY chessboard (weenie 14341, as the 12 placed in the world)
    ///   beside the player on open flat ground (ChessLobbyRule.Check) and starts ACE's own match with its AI as Black. Easy
    ///   is ACE's AI (one move deep); Normal is ours (NormalAi: also looks at the reply), because ACE's minimax
    ///   (AsyncCalculateAiComplexMove) is broken and never called by ACE (its min side takes the max, and it can pick an
    ///   illegal move).
    ///   Challenge: "/chess challenge &lt;name|0xguid&gt;": in person only, both within NEAR_M (owner: like the allegiance
    ///   swear); far, the challenger is told where the other is. The other gets ACE's Yes/No; on Yes a temporary board
    ///   appears between them and both are seated (challenger White).
    ///   Boards: the 12 placed boards, named by the nearest town, with map coordinates for the client's pins.
    ///   Record, titles, ladder: every finished game with a player (any board) counts unless ChessLobbyRule.Counts says
    ///   not (one account, one connection, under 10 moves, a pair's 4th win in a day); games with the computer are kept
    ///   apart. Retail titles nothing else grants at 1/10/25/50 wins and a first win over Normal. Our ladder rating is
    ///   our own (players only); ACE's ChessRank is left alone except that a lobby game with the computer no longer moves it
    ///   (one click anywhere would make it farmable).
    ///
    /// TEMPORARY BOARDS NEVER LIE AROUND: never saved to the shard (PersistPatch), never rot-timed by ACE (TimeToRot -1, so
    /// it is ours to remove), put away PUT_AWAY_S after the game ends, after IDLE_MIN with no move, after MAX_LIFE_MIN in
    /// any case, and when the landblock unloads they go with it. One per player, MAX_BOARDS on the shard. A board is not
    /// loot (stuck, ACE's Game) and the pieces are ACE's own game pieces. A piece that cannot walk to its square (a tree
    /// root, a slope) used to hang a match forever; for temporary boards a 30 s watchdog ends the motion and plays on.
    ///
    /// Client: "/chess lobby" answers on the mod channel ("chess", JSON). State: Chess/records.json beside the dll.
    /// Staff: "/chess temp" lists the temporary boards, "/chess clear" puts them all away.
    /// </summary>
    public static partial class ChessLobby
    {
        internal const string P_ON = "rg_chess_lobby";
        const uint WCID_BOARD = 14341;
        const uint BOARD_FX_TABLE = 0x3400001A;
        const double WATCHDOG_S = 30, JOIN_TIMEOUT_S = 60;

        internal static bool On { get { try { return PkCommon.On(P_ON); } catch { return false; } } }

        sealed class Temp
        {
            public Game Board;
            public uint Guid, White, Black;
            public bool Ai; public string Level;
            public DateTime Made, LastMoveAt, MotionSince;
            public int Plies;
            public DateTime? PutAwayAt;
            public bool Ending, Gone;
            public float Lx, Ly; public uint Lb;
            // the clock (player games, picked in the challenge; ChessLobbyGame.cs): minutes each, ms left, the side running
            public int Clock; public double[] Left = new double[2]; public int Running = -1; public DateTime RunSince;
            /// <summary>The human in a game with the computer (White or Black).</summary>
            public uint Human => White != 0 ? White : Black;
        }
        sealed class Challenge { public uint From, To; public string FromName, ToName; public DateTime At; public int Clock; }
        sealed class PlacedBoard { public string Name, Where; public float Ns, Ew; public uint Lb; public float X, Y; }

        static readonly object s_Lock = new object();
        static readonly Dictionary<uint, Temp> s_Temps = new Dictionary<uint, Temp>();
        static readonly Dictionary<uint, Challenge> s_Challenges = new Dictionary<uint, Challenge>();   // by challenger
        static readonly Dictionary<(uint, uint), DateTime> s_Declined = new Dictionary<(uint, uint), DateTime>();
        static readonly Dictionary<uint, DateTime> s_LastBoard = new Dictionary<uint, DateTime>();
        static readonly Dictionary<uint, string> s_Msg = new Dictionary<uint, string>();
        static Dictionary<uint, R.Record> s_Records = new Dictionary<uint, R.Record>();
        static List<PlacedBoard> s_Placed = new List<PlacedBoard>();
        static string s_File;
        static DateTime s_NextTick;

        [ThreadStatic] static bool t_SkipRank;

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard ChessLobby: the Game Center lobby (/chess): play the computer or challenge a player in person on a temporary board, the board list, chess records, titles and the ladder (false: /chess says it is not open, nothing is spawned or recorded)");
            var dir = ModFolder.Sub("Chess");
            s_File = dir == null ? null : Path.Combine(dir, "records.json");
            Load();
            CheckTitleIds();
            Task.Run(LoadPlaced);
            CommandManager.TryAddCommand(Command, "chess", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Chess: play the computer, challenge a player near you, find a board, your record.",
                "[lobby | ai easy|normal | challenge <name> | cancel | boards | record]");
            Mod.Log.Info($"[RevivalGuard] ChessLobby: {(On ? "ON" : "off")} ({P_ON}), {s_Records.Count} records; /chess");
        }

        static void CheckTitleIds()
        {
            var want = new (uint id, CharacterTitle t)[]
            {
                (R.T_TACTICAL_AID, CharacterTitle.TacticalAid), (R.T_TRUE_TACTICIAN, CharacterTitle.TrueTactician),
                (R.T_STRATEGIST, CharacterTitle.Strategist), (R.T_TACTICAL_FANTASTICAL, CharacterTitle.TacticalFantastical),
                (R.T_PUZZLEMASTER, CharacterTitle.Puzzlemaster),
            };
            foreach (var (id, t) in want)
                if ((uint)t != id) Mod.Log.Warn($"[RevivalGuard] ChessLobby: title {t} is {(uint)t} in ACE, {id} in ChessLobbyRule.cs");
        }

        // ------------------------------------------------------------------ state

        static void Load()
        {
            try
            {
                if (s_File == null || !File.Exists(s_File)) return;
                var d = JsonSerializer.Deserialize<Dictionary<uint, R.Record>>(File.ReadAllText(s_File));
                if (d != null) lock (s_Lock) s_Records = d;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: cannot read {s_File}: {e.Message}"); }
        }

        static void Save()
        {
            if (s_File == null) return;
            string text;
            lock (s_Lock) text = JsonSerializer.Serialize(s_Records);
            try { var tmp = s_File + ".tmp"; File.WriteAllText(tmp, text); File.Move(tmp, s_File, true); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: cannot write {s_File}: {e.Message}"); }
        }

        static R.Record Rec(uint guid, string name)
        {
            if (!s_Records.TryGetValue(guid, out var r)) s_Records[guid] = r = new R.Record { Name = name };
            if (!string.IsNullOrEmpty(name)) r.Name = name;
            return r;
        }

        /// <summary>The 12 placed boards, once, off the world thread.</summary>
        static void LoadPlaced()
        {
            try
            {
                using var ctx = new WorldDbContext();
                var rows = ctx.LandblockInstance.Where(i => i.WeenieClassId == WCID_BOARD).ToList();
                var towns = DeathLog.Towns;
                var list = new List<PlacedBoard>();
                foreach (var i in rows)
                {
                    uint lb = i.ObjCellId >> 16;
                    float gx = ((lb >> 8) & 0xFF) * 192f + i.OriginX, gy = (lb & 0xFF) * 192f + i.OriginY;
                    string c = DeathLogRule.Coords(gx, gy, out float ns, out float ew);
                    string town = NearestTown(ns, ew, towns, out float d);
                    string name = town == null ? "A chessboard" : d <= 3f ? town : "Near " + town;
                    list.Add(new PlacedBoard { Name = name, Where = c, Ns = ns, Ew = ew, Lb = lb, X = i.OriginX, Y = i.OriginY });
                }
                // two boards in one town get a "(2)"
                foreach (var g in list.GroupBy(b => b.Name).Where(g => g.Count() > 1))
                {
                    int n = 1;
                    foreach (var b in g.OrderBy(b => b.Ew)) b.Name += $" ({n++})";
                }
                lock (s_Lock) s_Placed = list.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
                Mod.Log.Info($"[RevivalGuard] ChessLobby: {list.Count} placed boards");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: placed boards: {e.Message}"); }
        }

        static string NearestTown(float ns, float ew, IList<(string name, float ns, float ew)> towns, out float dist)
        {
            dist = float.MaxValue; string best = null;
            if (towns == null) return null;
            foreach (var t in towns)
            {
                float dn = t.ns - ns, de = t.ew - ew, d = (float)Math.Sqrt(dn * dn + de * de);
                if (d < dist) { dist = d; best = t.name; }
            }
            return best;
        }

        // ------------------------------------------------------------------ the command

        static void Say(Player p, string line) { PkCommon.Say(p, line); lock (s_Lock) s_Msg[p.Guid.Full] = line; }

        static void Command(Session s, params string[] args)
        {
            var me = s?.Player;
            if (me == null) return;
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "lobby";
            string rest = string.Join(" ", args.Skip(1)).Trim();
            try
            {
                if (verb == "fx")
                {
                    if (s.AccessLevel < AccessLevel.Sentinel) { PkCommon.Say(me, "Staff only."); return; }
                    StaffFx(me, rest);
                    return;
                }
                if (verb == "temp" || verb == "clear")
                {
                    if (s.AccessLevel < AccessLevel.Sentinel) { PkCommon.Say(me, "Staff only."); return; }
                    StaffTemp(me, verb == "clear");
                    return;
                }
                if (!On)
                {
                    if (verb == "lobby") Send(me); else PkCommon.Say(me, R.NOT_OPEN);
                    return;
                }
                switch (verb)
                {
                    case "lobby": case "boards": case "record": Send(me); break;
                    case "ai": case "computer": PlayAi(me, rest); Send(me); break;
                    case "challenge": Challenge_(me, rest); Send(me); break;
                    case "cancel": Cancel(me); Send(me); break;
                    case "rematch": Rematch(me); Send(me); break;
                    case "watch": WatchCmd(me, rest); break;
                    case "unwatch": Unwatch(me, true); break;
                    default: PkCommon.Say(me, "/chess ai easy|normal, /chess challenge <name>, /chess cancel. The Game Center icon opens the lobby."); break;
                }
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ChessLobby /chess {verb} by {me.Name}: {e}"); }
        }

        static void StaffTemp(Player me, bool clear)
        {
            List<Temp> l; lock (s_Lock) l = s_Temps.Values.ToList();
            if (l.Count == 0) { PkCommon.Say(me, "No temporary chessboards stand."); return; }
            foreach (var t in l)
            {
                PkCommon.Say(me, $"0x{t.Guid:X8} {(t.Ai ? "vs computer " + t.Level : "player game")} white {PlayerManager.FindByGuid(t.White)?.Name} black {(t.Ai ? "-" : PlayerManager.FindByGuid(t.Black)?.Name)} "
                    + $"made {(DateTime.UtcNow - t.Made).TotalMinutes:0} min ago, {t.Plies} moves{(t.Ending ? ", ending" : "")}");
                if (clear) EndAndPutAway(t, Chess.ChessWinnerEndGame, "staff");
            }
        }

        static bool InMatch(Player p) => p.ChessMatch != null && (p.ChessMatch.State == ChessState.InProgress || p.ChessMatch.State == ChessState.WaitingForPlayers);

        static bool HasTemp(uint guid)
        {
            lock (s_Lock) return s_Temps.Values.Any(t => !t.Gone && (t.White == guid || t.Black == guid));
        }

        static string CanSpawn(Player p)
        {
            if (InMatch(p) || HasTemp(p.Guid.Full)) return R.YOU_BUSY;
            lock (s_Lock)
            {
                if (s_Temps.Count >= R.MAX_BOARDS) return R.TOO_MANY;
                if (s_LastBoard.TryGetValue(p.Guid.Full, out var at) && (DateTime.UtcNow - at).TotalSeconds < R.SPAWN_GAP_S) return R.TOO_SOON;
            }
            return null;
        }

        static void PlayAi(Player me, string level)
        {
            var lv = R.ParseLevel(level);
            if (lv == null) { Say(me, "Pick easy or normal."); return; }
            var why = CanSpawn(me);
            if (why != null) { Say(me, why); return; }
            // You play White, and ACE lays White on the board's south side: try a centre 5.5 m north of you first.
            var spot = FindSpot(me, me.Location, 0f, 5.5f, out var spotWhy);
            if (spot == null) { Say(me, R.SpotWhy(spotWhy)); return; }
            var t = MakeBoard(spot, me.Guid.Full, 0, true, lv);
            if (t == null) { Say(me, "The board could not be set here. Try a few steps away."); return; }
            Say(me, R.AiStart(lv == R.NORMAL ? "Normal" : "Easy"));
            Seat(t, 1.0);
        }

        static Player FindTarget(Player me, string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length == 0) return null;
            if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(arg.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var g))
                return PlayerManager.GetOnlinePlayer(g);
            return PlayerManager.GetOnlinePlayer(arg);
        }

        static bool Near(Player a, Player b) =>
            a.Location != null && b.Location != null && a.Location.DistanceTo(b.Location) <= R.NEAR_M
            && (a.CurrentLandblock?.IsDungeon ?? false) == (b.CurrentLandblock?.IsDungeon ?? false);

        static void Challenge_(Player me, string arg)
        {
            // "0x50000001 c10": the clock the challenger picked (ChessLobbyRule.CLOCKS), last
            int clock = 0;
            var words = (arg ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && R.ParseClock(words[^1]) >= 0) { clock = R.ParseClock(words[^1]); arg = string.Join(" ", words.Take(words.Length - 1)); }
            var them = FindTarget(me, arg);
            if (them == null) { Say(me, $"No one called {arg} is online."); return; }
            if (them == me) { Say(me, "You cannot challenge yourself. Try the computer."); return; }
            if (Mercenaries.IsMerc(them)) { Say(me, $"{them.Name} does not play chess."); return; }
            if (InMatch(me) || HasTemp(me.Guid.Full)) { Say(me, R.YOU_BUSY); return; }
            if (InMatch(them) || HasTemp(them.Guid.Full)) { Say(me, R.Busy(them.Name)); return; }
            lock (s_Lock)
            {
                if (s_Challenges.TryGetValue(me.Guid.Full, out var open) && (DateTime.UtcNow - open.At).TotalSeconds < R.CHALLENGE_S)
                { Say(me, $"You are still waiting for {open.ToName}. /chess cancel withdraws it."); return; }
                if (s_Declined.TryGetValue((me.Guid.Full, them.Guid.Full), out var no) && (DateTime.UtcNow - no).TotalSeconds < R.DECLINED_WAIT_S)
                { Say(me, $"{them.Name} said no just now. Give it a couple of minutes."); return; }
            }
            if (!Near(me, them)) { Say(me, R.FarLine(them.Name, DeathLog.WhereOf(them))); return; }
            var c = new Challenge { From = me.Guid.Full, To = them.Guid.Full, FromName = me.Name, ToName = them.Name, At = DateTime.UtcNow, Clock = clock };
            if (!them.ConfirmationManager.EnqueueSend(new ChessConfirmation(them.Guid, me.Guid.Full), R.ChallengeQuestion(me.Name, me.Level ?? 1, clock)))
            { Say(me, $"{them.Name} is answering another question. Try again in a moment."); return; }
            lock (s_Lock) s_Challenges[me.Guid.Full] = c;
            Say(me, R.Waiting(them.Name));
        }

        static void Cancel(Player me)
        {
            Challenge c;
            lock (s_Lock) { s_Challenges.TryGetValue(me.Guid.Full, out c); s_Challenges.Remove(me.Guid.Full); }
            Say(me, c == null ? "You have no challenge waiting." : $"Challenge to {c.ToName} withdrawn.");
        }

        /// <summary>The challenged player's Yes/No (ChessConfirmation).</summary>
        internal static void Answered(Player them, uint from, bool yes, bool timeout)
        {
            Challenge c;
            lock (s_Lock)
            {
                if (!s_Challenges.TryGetValue(from, out c) || c.To != them?.Guid.Full) c = null;
                if (c != null) s_Challenges.Remove(from);
            }
            var me = PlayerManager.GetOnlinePlayer(from);
            if (c == null || them == null) { if (them != null && yes) Say(them, "That challenge was withdrawn."); return; }
            if (me == null) { if (yes) Say(them, $"{c.FromName} is no longer here."); return; }
            if (!yes)
            {
                lock (s_Lock) s_Declined[(from, them.Guid.Full)] = DateTime.UtcNow;
                Say(me, timeout ? R.NoAnswer(them.Name) : R.Declined(them.Name));
                return;
            }
            if (!On) { Say(them, R.NOT_OPEN); Say(me, R.NOT_OPEN); return; }
            if (!Near(me, them)) { Say(them, R.FarLine(me.Name, DeathLog.WhereOf(me))); Say(me, R.FarLine(them.Name, DeathLog.WhereOf(them))); return; }
            var why = CanSpawn(me) ?? CanSpawn(them);
            if (why != null) { Say(me, why == R.YOU_BUSY ? R.Busy(InMatch(me) ? me.Name : them.Name) : why); Say(them, why); return; }
            // between them, on the challenger's landblock
            var mid = new ACE.Entity.Position(me.Location);
            var off = me.Location.GetOffset(them.Location);
            var spot = FindSpot(me, mid, off.X / 2f, off.Y / 2f, out var spotWhy);
            if (spot == null) { Say(me, R.SpotWhy(spotWhy)); Say(them, R.SpotWhy(spotWhy)); return; }
            var t = MakeBoard(spot, me.Guid.Full, them.Guid.Full, false, null);
            if (t == null) { Say(me, "The board could not be set here. Try a few steps away."); Say(them, "The board could not be set here."); return; }
            t.Clock = c.Clock;
            Say(me, R.Accepted(them.Name));
            Say(them, R.YouAccepted(me.Name));
            Seat(t, 1.0);
        }

        // ------------------------------------------------------------------ the board

        /// <summary>A spot near <paramref name="basePos"/> + (dx, dy) on its landblock where a board may stand, or null.</summary>
        static ACE.Entity.Position FindSpot(Player p, ACE.Entity.Position basePos, float dx, float dy, out R.Spot why)
        {
            why = R.Spot.Indoors;
            if (basePos == null || (p.CurrentLandblock?.IsDungeon ?? true) || (basePos.Cell & 0xFFFF) >= 0x100) return null;
            var lb = ACE.Server.Physics.Common.LScape.get_landblock(basePos.Cell);
            if (lb == null) return null;
            uint lbid = basePos.Cell >> 16;
            List<(float x, float y)> boards;
            lock (s_Lock)
                boards = s_Placed.Where(b => b.Lb == lbid).Select(b => (b.X, b.Y))
                    .Concat(s_Temps.Values.Where(t => t.Lb == lbid).Select(t => (t.Lx, t.Ly))).ToList();
            R.Spot first = R.Spot.Ok; bool any = false;
            foreach (var (cx, cy) in R.Candidates())
            {
                float x = basePos.PositionX + dx + cx, y = basePos.PositionY + dy + cy;
                var s = Judge(lb, x, y, basePos.PositionZ, boards, out float z);
                if (!any) { first = s; any = true; }
                if (s != R.Spot.Ok) continue;
                var pos = new ACE.Entity.Position(basePos.Cell, x, y, z, 0f, 0f, 0f, 1f);
                pos.LandblockId = new LandblockId(pos.GetCell());
                why = R.Spot.Ok;
                return pos;
            }
            why = first == R.Spot.Ok ? R.Spot.Uneven : first;
            return null;
        }

        static R.Spot Judge(PLandblock lb, float x, float y, float zGuess, List<(float x, float y)> boards, out float z)
        {
            z = zGuess;
            if (x < R.EDGE_M || y < R.EDGE_M || x > 192f - R.EDGE_M || y > 192f - R.EDGE_M)
                return R.Check(false, x, y, null, false, 999, 999, 999);
            var zs = new List<float>(); bool water = false;
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    var pt = new Vector3(x + i * R.HALF_M, y + j * R.HALF_M, zGuess + 50f);
                    zs.Add(lb.GetZ(pt));
                    var cell = lb.GetCell(pt);
                    if (cell != null && cell.WaterType != ACE.Server.Physics.Common.LandDefs.WaterType.NotWater) water = true;
                }
            z = zs[4];
            float Dist(Vector3 o) { float ddx = o.X - x, ddy = o.Y - y; return (float)Math.Sqrt(ddx * ddx + ddy * ddy); }
            float bld = 999, clut = 999, brd = 999;
            foreach (var b in lb.Buildings ?? new List<ACE.Server.Physics.Common.BuildingObj>())
                if (b?.Position != null) bld = Math.Min(bld, Dist(b.Position.Frame.Origin));
            foreach (var o in (lb.StaticObjects ?? new List<ACE.Server.Physics.PhysicsObj>()).Concat(lb.Scenery ?? new List<ACE.Server.Physics.PhysicsObj>()))
                if (o?.Position != null) clut = Math.Min(clut, Dist(o.Position.Frame.Origin));
            foreach (var (bx, by) in boards) brd = Math.Min(brd, Dist(new Vector3(bx, by, 0)));
            return R.Check(false, x, y, zs, water, bld, clut, brd);
        }

        static Temp MakeBoard(ACE.Entity.Position at, uint white, uint black, bool ai, string level)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(WCID_BOARD) as Game;
            if (wo == null) return null;
            // the placed boards stand 0.45 m above the ground (landblock_instance origin Z)
            at.PositionZ += 0.45f;
            wo.Location = at;
            wo.TimeToRot = -1;
            // the board's setup has no effect table, so its exit (ChessLobbyGame.FadeAndDestroy) would play nothing: the
            // drudge pieces' own table (0x3400001A, ace_world drudgepawn), whose Destroy fades the object out
            wo.SetProperty(ACE.Entity.Enum.Properties.PropertyDataId.PhysicsEffectTable, BOARD_FX_TABLE);
            var t = new Temp
            {
                Board = wo, Guid = wo.Guid.Full, White = white, Black = black, Ai = ai, Level = level,
                Made = DateTime.UtcNow, LastMoveAt = DateTime.UtcNow, Lx = at.PositionX, Ly = at.PositionY, Lb = at.Cell >> 16,
            };
            lock (s_Lock)
            {
                s_Temps[t.Guid] = t;
                s_LastBoard[white] = DateTime.UtcNow;
                if (black != 0) s_LastBoard[black] = DateTime.UtcNow;
            }
            if (!wo.EnterWorld())
            {
                lock (s_Lock) s_Temps.Remove(t.Guid);
                wo.Destroy();
                return null;
            }
            Mod.Log.Info($"[RevivalGuard] ChessLobby: temporary board 0x{t.Guid:X8} at {at.ToLOCString()}");
            return t;
        }

        /// <summary>End the match (if it still runs) and put the board away soon. Safe from any thread: runs on the board.</summary>
        static void EndAndPutAway(Temp t, int winner, string why)
        {
            if (t == null) return;
            lock (s_Lock) { if (t.Ending) return; t.Ending = true; }
            var chain = new ActionChain();
            chain.AddAction(t.Board, () =>
            {
                try
                {
                    var m = t.Board.ChessMatch;
                    if (m != null && (m.State == ChessState.InProgress || m.State == ChessState.WaitingForPlayers))
                    {
                        // a game that never started has no winner
                        m.Finish(m.State == ChessState.WaitingForPlayers ? Chess.ChessWinnerEndGame : winner);
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: ending 0x{t.Guid:X8} ({why}): {e.Message}"); }
                lock (s_Lock) t.PutAwayAt = DateTime.UtcNow.AddSeconds(R.PUT_AWAY_S);
            });
            chain.EnqueueChain();
        }

        static void Destroy(Temp t)
        {
            lock (s_Lock) { s_Temps.Remove(t.Guid); t.Gone = true; s_PutAway.Add(t.Guid); }
            if (t.Board.IsDestroyed) return;
            var chain = new ActionChain();
            chain.AddAction(t.Board, () =>
            {
                try
                {
                    var m = t.Board.ChessMatch;
                    if (m != null && m.State != ChessState.Finished) m.Finish(Chess.ChessWinnerEndGame);
                    // F7 261125-0057: the board goes with an effect everyone near sees, then is removed
                    if (!t.Board.IsDestroyed) FadeAndDestroy(t.Board);
                    Mod.Log.Info($"[RevivalGuard] ChessLobby: temporary board 0x{t.Guid:X8} put away");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: put away 0x{t.Guid:X8}: {e.Message}"); }
            });
            chain.EnqueueChain();
        }

        /// <summary>From the world loop, every 2 s: finished, idle, too old, stuck pieces.</summary>
        internal static void Tick()
        {
            var now = DateTime.UtcNow;
            GameTick(now);   // clocks every world tick (ChessLobbyGame.cs)
            if (now < s_NextTick) return;
            s_NextTick = now.AddSeconds(2);
            GameTick2(now);
            List<Temp> list;
            lock (s_Lock)
            {
                list = s_Temps.Values.ToList();
                foreach (var k in s_Challenges.Where(kv => (now - kv.Value.At).TotalSeconds > R.CHALLENGE_S + 30).Select(kv => kv.Key).ToList())
                    s_Challenges.Remove(k);
            }
            foreach (var t in list)
            {
                if (t.Board.IsDestroyed || t.Board.CurrentLandblock == null && (now - t.Made).TotalSeconds > 10)
                { lock (s_Lock) { s_Temps.Remove(t.Guid); t.Gone = true; } continue; }
                if (t.PutAwayAt.HasValue) { if (now >= t.PutAwayAt.Value) Destroy(t); continue; }
                if (t.Ending) continue;
                var m = t.Board.ChessMatch;
                if ((now - t.Made).TotalMinutes > R.MAX_LIFE_MIN) { EndAndPutAway(t, Chess.ChessWinnerEndGame, "too old"); continue; }
                if (m == null || m.State == ChessState.Finished)
                {
                    if (m != null || (now - t.Made).TotalSeconds > JOIN_TIMEOUT_S) EndAndPutAway(t, Chess.ChessWinnerEndGame, "finished");
                    continue;
                }
                if (m.State == ChessState.WaitingForPlayers)
                {
                    if ((now - t.Made).TotalSeconds > JOIN_TIMEOUT_S) EndAndPutAway(t, Chess.ChessWinnerEndGame, "nobody sat");
                    continue;
                }
                int plies = m.Log?.Count ?? 0;
                if (plies != t.Plies) { t.Plies = plies; t.LastMoveAt = now; }
                if ((now - t.LastMoveAt).TotalMinutes > R.IDLE_MIN)
                {
                    lock (s_Lock) s_Why[t.Guid] = "idle";
                    if (t.Ai) { foreach (var g in new[] { t.Human }) { var p = PlayerManager.GetOnlinePlayer(g); if (p != null) Say(p, R.IDLE_END); } EndAndPutAway(t, Chess.ChessWinnerEndGame, "idle"); }
                    else
                    {
                        var loser = m.Logic.Turn;
                        var name = PlayerManager.FindByGuid(loser == ChessColor.White ? t.White : t.Black)?.Name ?? "A player";
                        foreach (var g in new[] { t.White, t.Black }) { var p = PlayerManager.GetOnlinePlayer(g); if (p != null) Say(p, R.IdleLoss(name)); }
                        EndAndPutAway(t, (int)Chess.InverseColor(loser), "idle");
                    }
                    continue;
                }
                if (m.WaitingForMotion)
                {
                    if (t.MotionSince == default) t.MotionSince = now;
                    else if ((now - t.MotionSince).TotalSeconds > WATCHDOG_S) { t.MotionSince = default; Unstick(t); }
                }
                else t.MotionSince = default;
            }
        }

        /// <summary>A piece that never reached its square (ACE's MoveTo failed, so PieceReady never came): stop its walk,
        /// remove a captured piece still standing, and play on as PieceReady would have.</summary>
        static void Unstick(Temp t)
        {
            var chain = new ActionChain();
            chain.AddAction(t.Board, () =>
            {
                try
                {
                    var m = t.Board.ChessMatch;
                    if (m == null || !m.WaitingForMotion || t.Board.CurrentLandblock == null) return;
                    var lb = t.Board.CurrentLandblock;
                    var last = m.Logic.GetLastMove();
                    if (last != null && last.CapturedGuid.Full != 0 && lb.GetObject(last.CapturedGuid) is GamePiece victim && !victim.IsDestroyed)
                        victim.Destroy();
                    foreach (var g in m.Motions.ToList())
                    {
                        if (lb.GetObject(g) is GamePiece gp) { gp.GamePieceState = GamePieceState.None; gp.AttackTarget = null; }
                        m.PieceReady(g);
                    }
                    Mod.Log.Info($"[RevivalGuard] ChessLobby: board 0x{t.Guid:X8}: a piece could not reach its square in {WATCHDOG_S} s; play goes on");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: unstick 0x{t.Guid:X8}: {e.Message}"); }
            });
            chain.EnqueueChain();
        }

        static Temp TempOf(ChessMatch m)
        {
            uint g = m?.ChessBoard?.Guid.Full ?? 0;
            if (g == 0) return null;
            lock (s_Lock) return s_Temps.TryGetValue(g, out var t) ? t : null;
        }

        internal static bool IsTemp(uint guid) { lock (s_Lock) return s_Temps.ContainsKey(guid) || s_PutAway.Contains(guid); }
        /// <summary>Boards put away: still never saved while their exit effect plays (ChessLobbyGame.FadeAndDestroy).</summary>
        static readonly HashSet<uint> s_PutAway = new HashSet<uint>();

        // ------------------------------------------------------------------ Normal: our AI

        /// <summary>Normal: every legal move, each answered by the opponent's best legal reply (ACE's own board score; a reply
        /// that mates scores worst of all), the move whose worst reply leaves us best; a move that mates scores best. Black or
        /// White alike. About 900 positions a move, so it stays on the board's own tick.</summary>
        internal static bool NormalMove(ChessMatch m)
        {
            var L = m.Logic;
            var me = L.Turn; var opp = Chess.InverseColor(me);
            float Score() => me == ChessColor.Black ? L.EvaluateBoard() : -L.EvaluateBoard();
            ChessMove best = null; float bestScore = float.MinValue;
            var bests = new List<ChessMove>();
            ChessCheckmate.Quiet = true;
            try
            {
                var mine = new List<ChessMove>();
                L.GenerateMoves(me, mine);
                foreach (var mv in mine)
                {
                    var r = L.FinalizeMove(mv);
                    if (L.InCheck(me)) { L.UndoMove(1); continue; }   // illegal: leaves our king attacked
                    float worst = float.MaxValue; bool replies = false;
                    var theirs = new List<ChessMove>();
                    L.GenerateMoves(opp, theirs);
                    foreach (var rp in theirs)
                    {
                        L.FinalizeMove(rp);
                        if (!L.InCheck(opp))
                        {
                            replies = true;
                            // a reply that mates us is the worst there is, whatever the material says
                            worst = Math.Min(worst, L.InCheck(me) && !HasLegal(L, me) ? -100000f : Score());
                        }
                        L.UndoMove(1);
                    }
                    if (!replies) worst = L.InCheck(opp) ? 100000f : 0f;   // mate for us, or a stalemate
                    L.UndoMove(1);
                    if (worst > bestScore + 0.01f) { bestScore = worst; bests.Clear(); bests.Add(mv); }
                    else if (Math.Abs(worst - bestScore) <= 0.01f) bests.Add(mv);
                }
            }
            finally { ChessCheckmate.Quiet = false; L.Turn = me; }

            var res = new ChessAiMoveResult();
            m.AiFuture = res;
            if (bests.Count == 0) { res.SetResult(ChessMoveResult.NoMoveResult, null, null); return true; }
            best = bests[ThreadSafeRandom.Next(0, bests.Count - 1)];
            var result = L.FinalizeMove(best);   // not quiet: ChessCheckmate may add the mate
            res.SetResult(result, best.From, best.To);
            return true;
        }

        /// <summary>Whether <paramref name="c"/> has a move that leaves its king safe (only asked when it is in check).</summary>
        static bool HasLegal(ChessLogic L, ChessColor c)
        {
            var l = new List<ChessMove>();
            L.GenerateMoves(c, l);
            foreach (var mv in l)
            {
                L.FinalizeMove(mv);
                bool ok = !L.InCheck(c);
                L.UndoMove(1);
                if (ok) return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.StartAiMove))]
        static class NormalAiPatch
        {
            static bool Prefix(ChessMatch __instance)
            {
                try
                {
                    var t = TempOf(__instance);
                    if (t == null || t.Level != R.NORMAL) return true;
                    __instance.AiState = ChessAiState.InProgress;
                    NormalMove(__instance);
                    __instance.AiState = ChessAiState.WaitingForFinish;
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[RevivalGuard] ChessLobby: Normal AI threw, ACE's own AI moves instead: {e}");
                    __instance.AiState = ChessAiState.WaitingToStart;
                    return true;
                }
            }
        }

        // ------------------------------------------------------------------ the result

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.Finish))]
        static class FinishPatch
        {
            static void Prefix(ChessMatch __instance, int winner)
            {
                try
                {
                    if (__instance.State != ChessState.InProgress && __instance.State != ChessState.WaitingForPlayers) return;
                    var t = TempOf(__instance);
                    bool ai = __instance.Sides.Any(sd => sd != null && sd.IsAi());
                    if (t != null && ai) t_SkipRank = true;
                    if (On) GameEnding(__instance, winner, t);
                    if (__instance.State == ChessState.InProgress && On && winner != Chess.ChessWinnerEndGame)
                        Record(__instance, winner, ai, t);
                    // the lobby's last line ("...Good luck!") is over with the game
                    lock (s_Lock) foreach (var sd in __instance.Sides) if (sd != null && !sd.IsAi()) s_Msg.Remove(sd.PlayerGuid.Full);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ChessLobby: recording a finished game: {e}"); }
            }

            static void Postfix(ChessMatch __instance, int winner)
            {
                t_SkipRank = false;
                t_Fade = false;
                try
                {
                    var t = TempOf(__instance);
                    if (t != null) lock (s_Lock) { t.Ending = true; t.PutAwayAt ??= DateTime.UtcNow.AddSeconds(R.PUT_AWAY_S); }
                    if (On) GameEnded(__instance, winner, t);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: after a game: {e.Message}"); }
            }
        }

        /// <summary>A lobby game with the computer leaves ACE's ChessRank alone (class note).</summary>
        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.AdjustPlayerRanks))]
        static class RankPatch
        {
            static bool Prefix() => !t_SkipRank;
        }

        static void Record(ChessMatch m, int winner, bool ai, Temp t)
        {
            var now = DateTime.UtcNow;
            int plies = m.Log?.Count ?? 0;
            R.Result Res(ChessColor c) => winner == Chess.ChessWinnerStalemate ? R.Result.Draw : winner == (int)c ? R.Result.Win : R.Result.Loss;
            var players = m.Sides.Where(sd => sd != null && !sd.IsAi()).ToList();
            bool changed = false, counted = false;
            if (ai)
            {
                if (plies < 2) return;   // nobody moved: a board set and left is no game
                bool normal = t != null ? t.Level == R.NORMAL : false;
                foreach (var sd in players)
                {
                    var ip = PlayerManager.FindByGuid(sd.PlayerGuid);
                    if (ip == null) continue;
                    lock (s_Lock) R.AddAi(Rec(sd.PlayerGuid.Full, ip.Name), Res(sd.Color), normal, now);
                    changed = true;
                    GiveTitles(sd.PlayerGuid.Full, true);
                }
            }
            else if (players.Count == 2)
            {
                var a = players[0]; var b = players[1];
                var pa = PlayerManager.FindByGuid(a.PlayerGuid); var pb = PlayerManager.FindByGuid(b.PlayerGuid);
                if (pa == null || pb == null) return;
                var oa = PlayerManager.GetOnlinePlayer(a.PlayerGuid); var ob = PlayerManager.GetOnlinePlayer(b.PlayerGuid);
                bool sameAccount = pa.Account?.AccountId == pb.Account?.AccountId;
                // the PK board's own test aid (pk_honor_ignore_ip, leave false) lets a test shard's local clients count
                bool sameAddress = oa != null && ob != null && !PkCommon.On(PkCommon.P_IGNORE_IP) && PkCommon.SameAddress(oa, ob);
                lock (s_Lock)
                {
                    var ra = Rec(a.PlayerGuid.Full, pa.Name); var rb = Rec(b.PlayerGuid.Full, pb.Name);
                    var resA = Res(a.Color); var resB = Res(b.Color);
                    var skip = R.Counts(sameAccount, sameAddress, plies, resA, R.WinsToday(ra, b.PlayerGuid.Full, now));
                    if (skip == R.Skip.None) skip = R.Counts(sameAccount, sameAddress, plies, resB, R.WinsToday(rb, a.PlayerGuid.Full, now));
                    if (skip != R.Skip.None)
                    {
                        foreach (var o in new[] { oa, ob }) if (o != null) PkCommon.Say(o, R.NotCounted(R.SkipWhy(skip)));
                        Mod.Log.Info($"[RevivalGuard] ChessLobby: {pa.Name} v {pb.Name} not counted ({skip})");
                    }
                    else
                    {
                        int da = R.Delta(ra.Rating, rb.Rating, resA), db = R.Delta(rb.Rating, ra.Rating, resB);
                        R.AddPvp(ra, resA, da, b.PlayerGuid.Full, now);
                        R.AddPvp(rb, resB, db, a.PlayerGuid.Full, now);
                        changed = counted = true;
                        if (oa != null) PkCommon.Say(oa, R.RatingLine(ra.Rating, da));
                        if (ob != null) PkCommon.Say(ob, R.RatingLine(rb.Rating, db));
                        Mod.Log.Info($"[RevivalGuard] ChessLobby: {pa.Name} {resA} v {pb.Name} {resB} in {plies} plies; ratings {ra.Rating}/{rb.Rating}");
                    }
                }
                if (counted && winner >= 0)
                {
                    var (pw, pl) = winner == (int)a.Color ? (pa, pb) : (pb, pa);
                    News(R.NewsWin(pw.Name, pl.Name, t_Why));
                }
                GiveTitles(a.PlayerGuid.Full, true); GiveTitles(b.PlayerGuid.Full, true);
            }
            if (changed) Task.Run(Save);
        }

        /// <summary>Titles earned and not yet given, to an online character (at login otherwise).</summary>
        internal static void GiveTitles(uint guid, bool news = false)
        {
            var p = PlayerManager.GetOnlinePlayer(guid);
            if (p == null) return;
            List<uint> due;
            lock (s_Lock) { if (!s_Records.TryGetValue(guid, out var r)) return; due = R.TitlesDue(r); if (due.Count > 0) r.Titles.AddRange(due); }
            if (due.Count == 0) return;
            foreach (var id in due)
            {
                try { p.AddTitle(id); PkCommon.Say(p, R.TitleLine(R.TitleName(id))); if (news) News(R.NewsTitle(p.Name, R.TitleName(id))); Mod.Log.Info($"[RevivalGuard] ChessLobby: {p.Name} earns {R.TitleName(id)}"); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: title {id} to {p.Name}: {e.Message}"); }
            }
            Task.Run(Save);
        }

        // ------------------------------------------------------------------ the lobby, for our client

        static void Send(Player me)
        {
            if (!ModChannel.Capable(me.Session)) return;
            ModChannel.Send(me.Session, "chess", LobbyJson(me));
        }

        static string LobbyJson(Player me)
        {
            if (!On) return "{\"on\":0}";
            var now = DateTime.UtcNow;
            object meRec; List<object> ladder; List<object> boards; string msg; object pending = null;
            lock (s_Lock)
            {
                s_Records.TryGetValue(me.Guid.Full, out var r);
                r ??= new R.Record { Name = me.Name };
                var next = R.NextTitle(r);
                meRec = new
                {
                    w = r.W, l = r.L, d = r.D, aiw = r.AiW, ail = r.AiL, aid = r.AiD, rating = r.Rating,
                    place = R.PlaceOf(s_Records.Values, s_Records.TryGetValue(me.Guid.Full, out var rr) ? rr : null),
                    titles = r.Titles.Select(R.TitleName).ToList(),
                    next = next?.name ?? "", more = next?.more ?? 0, ainext = r.AiNormalW > 0 ? "" : R.AI_TITLE_NAME,
                };
                ladder = R.Ladder(s_Records.Values).Select(x => (object)new { r = x.rank, n = x.rec.Name, rt = x.rec.Rating, w = x.rec.W, l = x.rec.L, d = x.rec.D }).ToList();
                boards = s_Placed.Select(b => (object)new { n = b.Name, at = b.Where, ns = Math.Round(b.Ns, 2), ew = Math.Round(b.Ew, 2) }).ToList();
                s_Msg.TryGetValue(me.Guid.Full, out msg);
                if (s_Challenges.TryGetValue(me.Guid.Full, out var c) && (now - c.At).TotalSeconds < R.CHALLENGE_S)
                    pending = new { n = c.ToName, left = (int)(R.CHALLENGE_S - (now - c.At).TotalSeconds) };
            }
            var near = new List<object>();
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (p == me || p.Location == null || Mercenaries.IsMerc(p) || p.CloakStatus == CloakStatus.On) continue;
                if (!Near(me, p)) continue;
                near.Add(new { n = p.Name, g = $"0x{p.Guid.Full:X8}", l = p.Level ?? 1, busy = InMatch(p) ? 1 : 0, dist = (int)Math.Round(me.Location.DistanceTo(p.Location)) });
            }
            var friends = new List<object>();
            try
            {
                foreach (var f in me.Character?.CharacterPropertiesFriendList?.ToList() ?? new List<ACE.Database.Models.Shard.CharacterPropertiesFriendList>())
                {
                    var p = PlayerManager.GetOnlinePlayer(f.FriendId);
                    if (p == null || p == me || p.CloakStatus == CloakStatus.On) continue;
                    bool nr = Near(me, p);
                    friends.Add(new { n = p.Name, g = $"0x{p.Guid.Full:X8}", l = p.Level ?? 1, near = nr ? 1 : 0, busy = InMatch(p) ? 1 : 0, where = nr ? "" : DeathLog.WhereOf(p) ?? "" });
                }
            }
            catch (Exception) { }
            var o = new
            {
                on = 1, busy = InMatch(me) || HasTemp(me.Guid.Full) ? 1 : 0, near_m = (int)R.NEAR_M, msg = msg ?? "",
                pending, me = meRec, near = near.Take(20).ToList(), friends, boards, ladder, games = GamesNear(me),
            };
            return JsonSerializer.Serialize(o);
        }

        // ------------------------------------------------------------------ hooks

        /// <summary>A temporary board is never saved to the shard (a dynamic Game would be, on a landblock save).</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsDynamicThatShouldPersistToShard))]
        static class PersistPatch
        {
            static void Postfix(WorldObject __instance, ref bool __result)
            {
                if (__result && __instance is Game && IsTemp(__instance.Guid.Full)) __result = false;
            }
        }

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        static class TickPatch
        {
            static void Postfix()
            {
                try { Tick(); }
                catch (Exception e) { TickGuard.Report("ChessLobby", e); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class LoginPatch
        {
            static void Postfix(Player __instance)
            {
                try { if (On) GiveTitles(__instance.Guid.Full); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby login of {__instance?.Name}: {e.Message}"); }
            }
        }
    }

    /// <summary>The challenged player's Yes/No (ChessLobby.Answered).</summary>
    sealed class ChessConfirmation : Confirmation
    {
        readonly uint m_From;
        public ChessConfirmation(ObjectGuid target, uint from) : base(target, ConfirmationType.Yes_No) { m_From = from; }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            try { ChessLobby.Answered(Player, m_From, response && !timeout, timeout); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby answer: {e.Message}"); }
        }
    }
}
