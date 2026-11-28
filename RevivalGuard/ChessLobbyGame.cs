using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Entity.Chess;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using R = RevivalGuard.ChessLobbyRule;

namespace RevivalGuard
{
    /// <summary>The chess lobby, in the game.</summary>
    public static partial class ChessLobby
    {
        /// <summary>The pieces' and the board's exit.</summary>
        static PlayScript s_FadeFx = PlayScript.Destroy;
        const double FADE_S = 1.5;

        sealed class Rematch_ { public Temp T; public uint White, Black; public bool Ai; public string Level; public int Clock; public DateTime At; }
        sealed class Offer { public ChessColor From; public DateTime At; }

        static readonly Dictionary<uint, ChessMatch> s_Live = new Dictionary<uint, ChessMatch>();              // by board
        static readonly Dictionary<uint, HashSet<uint>> s_Watch = new Dictionary<uint, HashSet<uint>>();       // board -> watchers
        static readonly Dictionary<uint, uint> s_Watching = new Dictionary<uint, uint>();                      // watcher -> board
        static readonly Dictionary<uint, string> s_Why = new Dictionary<uint, string>();                       // board -> why it ends
        static readonly Dictionary<uint, Offer> s_Offers = new Dictionary<uint, Offer>();                      // board -> draw offer
        static readonly Dictionary<(uint, int), DateTime> s_OfferRetry = new Dictionary<(uint, int), DateTime>();
        static readonly Dictionary<uint, Rematch_> s_Rematch = new Dictionary<uint, Rematch_>();               // player -> last game
        static readonly Dictionary<uint, DateTime> s_RematchAsked = new Dictionary<uint, DateTime>();
        static readonly Queue<string> s_News = new Queue<string>();
        static DateTime s_LastNews = DateTime.MinValue;

        [ThreadStatic] static bool t_Fade;
        [ThreadStatic] static string t_Why;

        internal static void Hello(Session session)
        {
            try { if (session?.Player != null) Send(session.Player); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby hello: {e.Message}"); }
        }

        static string NameOf(ChessSide sd, Temp t)
        {
            if (sd == null) return "nobody";
            if (sd.IsAi()) return t != null && t.Level == R.NORMAL ? "the computer (Normal)" : t != null ? "the computer (Easy)" : "the computer";
            return PlayerManager.FindByGuid(sd.PlayerGuid)?.Name ?? "a player";
        }

        static string Moves(ChessMatch m)
        {
            try { return string.Join(" ", (m.Log ?? new List<ChessMove>()).Select(mv => $"{(char)('a' + mv.From.X)}{mv.From.Y + 1}{(char)('a' + mv.To.X)}{mv.To.Y + 1}")); }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ the game, to our clients

        /// <summary>The game as one recipient sees it.</summary>
        static string GameJson(ChessMatch m, Temp t, uint to, string res, bool rematch)
        {
            var w = m.Sides[0]; var b = m.Sides[1];
            int you = w != null && w.PlayerGuid.Full == to ? 0 : b != null && b.PlayerGuid.Full == to ? 1 : -1;
            object clk = null;
            if (t != null && t.Clock > 0)
                lock (s_Lock)
                {
                    double el = t.Running >= 0 ? (DateTime.UtcNow - t.RunSince).TotalMilliseconds : 0;
                    clk = new { w = (long)R.ClockAfter(t.Left[0], t.Running == 0 ? el : 0), b = (long)R.ClockAfter(t.Left[1], t.Running == 1 ? el : 0), run = res != null ? -1 : t.Running };
                }
            int offer = 0;
            lock (s_Lock)
                if (res == null && s_Offers.TryGetValue(m.ChessBoard.Guid.Full, out var o) && (DateTime.UtcNow - o.At).TotalSeconds < R.DRAW_WAIT_S)
                    offer = (int)o.From == you ? 1 : you >= 0 ? 2 : 0;
            return JsonSerializer.Serialize(new
            {
                g = $"0x{m.ChessBoard.Guid.Full:X8}", st = res == null ? "play" : "over", w = NameOf(w, t), b = NameOf(b, t), you,
                turn = (int)m.Logic.Turn, mv = Moves(m), clk, res = res ?? "", rm = rematch && you >= 0 ? 1 : 0, offer,
                ai = (w?.IsAi() ?? false) || (b?.IsAi() ?? false) ? 1 : 0,
            });
        }

        static void SendGame(ChessMatch m, string res = null, bool rematch = false, uint only = 0)
        {
            if (m?.ChessBoard == null) return;
            var t = TempOf(m);
            var to = new List<uint>();
            if (only != 0) to.Add(only);
            else
            {
                foreach (var sd in m.Sides) if (sd != null && !sd.IsAi()) to.Add(sd.PlayerGuid.Full);
                lock (s_Lock) if (s_Watch.TryGetValue(m.ChessBoard.Guid.Full, out var ws)) to.AddRange(ws);
            }
            foreach (var g in to.Distinct())
            {
                var p = PlayerManager.GetOnlinePlayer(g);
                if (p?.Session == null || !ModChannel.Capable(p.Session)) continue;
                try { ModChannel.Send(p.Session, "chessgame", GameJson(m, t, g, res, rematch)); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: game to {p.Name}: {e.Message}"); }
            }
        }

        /// <summary>Running games within reach of me that it is not playing, for the lobby's Watch.</summary>
        static List<object> GamesNear(Player me)
        {
            var list = new List<object>();
            List<ChessMatch> live; lock (s_Lock) live = s_Live.Values.ToList();
            foreach (var m in live)
            {
                try
                {
                    if (m.State != ChessState.InProgress || m.ChessBoard?.Location == null || me.Location == null || m.InMatch(me.Guid)) continue;
                    if (me.Location.DistanceTo(m.ChessBoard.Location) > R.WATCH_M) continue;
                    var t = TempOf(m);
                    list.Add(new { g = $"0x{m.ChessBoard.Guid.Full:X8}", w = NameOf(m.Sides[0], t), b = NameOf(m.Sides[1], t), n = m.Log?.Count ?? 0 });
                }
                catch { }
            }
            return list;
        }

        // ------------------------------------------------------------------ seating (new games and rematches)

        /// <summary>Seat a temporary board's sides after delay s: White 0 is the computer moving first.</summary>
        static void Seat(Temp t, double delay)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(t.Board, () =>
            {
                try
                {
                    if (t.Board.IsDestroyed) { EndAndPutAway(t, Chess.ChessWinnerEndGame, "gone"); return; }
                    if (t.Ai)
                    {
                        var h = PlayerManager.GetOnlinePlayer(t.Human);
                        if (h == null) { EndAndPutAway(t, Chess.ChessWinnerEndGame, "left"); return; }
                        if (t.White == 0)
                        {
                            // the computer as White: ACE's AddAi would ask for the human's session first; seat it by hand
                            var m = t.Board.ChessMatch ?? (t.Board.ChessMatch = new ChessMatch(t.Board));
                            m.Sides[(int)ChessColor.White] = new ChessSide(new ObjectGuid(0), ChessColor.White);
                            m.Logic.WalkPieces(pc => { if (pc.Color == ChessColor.White) m.AddWeeniePiece(pc); });
                            t.Board.ActOnJoin(h);   // Black, and both seated: ACE queues Start (StartPatch moves the AI)
                        }
                        else { t.Board.ActOnJoin(h); t.Board.ChessMatch?.AddAi(); }
                        Mod.Log.Info($"[RevivalGuard] ChessLobby: {h.Name} plays the computer ({t.Level}) as {(t.White == 0 ? "Black" : "White")} on 0x{t.Guid:X8}");
                    }
                    else
                    {
                        var w = PlayerManager.GetOnlinePlayer(t.White); var b = PlayerManager.GetOnlinePlayer(t.Black);
                        if (w == null || b == null) { EndAndPutAway(t, Chess.ChessWinnerEndGame, "left"); return; }
                        t.Board.ActOnJoin(w);
                        t.Board.ActOnJoin(b);
                        Mod.Log.Info($"[RevivalGuard] ChessLobby: {w.Name} (White) v {b.Name} (Black) on 0x{t.Guid:X8}{(t.Clock > 0 ? $", {t.Clock} min clock" : "")}");
                    }
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ChessLobby: seats: {e}"); EndAndPutAway(t, Chess.ChessWinnerEndGame, "error"); }
            });
            chain.EnqueueChain();
        }

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.Start))]
        static class StartPatch
        {
            static void Postfix(ChessMatch __instance)
            {
                try
                {
                    if (!On || __instance.State != ChessState.InProgress) return;
                    var t = TempOf(__instance);
                    lock (s_Lock)
                    {
                        s_Live[__instance.ChessBoard.Guid.Full] = __instance;
                        if (t != null && t.Clock > 0) { t.Left[0] = t.Left[1] = t.Clock * 60000.0; t.Running = 0; t.RunSince = DateTime.UtcNow; }
                    }
                    // the computer as White moves first (ACE only ever gives it its turn in FinishTurn)
                    var side = __instance.Sides[(int)__instance.Logic.Turn];
                    if (side != null && side.IsAi() && __instance.AiState == ChessAiState.None) __instance.AiState = ChessAiState.WaitingToStart;
                    SendGame(__instance);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: start: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ moves and the clock

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.MoveEnqueue))]
        static class MovePatch
        {
            /// <summary>The mover's clock stops when the move is asked (the pieces' walk is nobody's time).</summary>
            static void Prefix(ChessMatch __instance, Player player)
            {
                try
                {
                    if (!On || player == null) return;
                    var t = TempOf(__instance);
                    if (t == null || t.Clock <= 0) return;
                    int c = (int)__instance.GetColor(player.Guid);
                    lock (s_Lock)
                        if (t.Running == c && c >= 0) { t.Left[c] = R.ClockAfter(t.Left[c], (DateTime.UtcNow - t.RunSince).TotalMilliseconds); t.Running = -1; }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.MoveDelayed))]
        static class MoveDelayedPatch
        {
            /// <summary>A refused move (no walk follows): the mover's clock runs again.</summary>
            static void Postfix(ChessMatch __instance) => ResumeClock(__instance, false);
        }

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.FinishTurn))]
        static class TurnPatch
        {
            static void Postfix(ChessMatch __instance)
            {
                try
                {
                    if (!On || __instance.State != ChessState.InProgress) return;
                    ResumeClock(__instance, true);
                    SendGame(__instance);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: turn: {e.Message}"); }
            }
        }

        static void ResumeClock(ChessMatch m, bool turnDone)
        {
            try
            {
                if (!On || m.State != ChessState.InProgress || m.WaitingForMotion) return;
                var t = TempOf(m);
                if (t == null || t.Clock <= 0) return;
                lock (s_Lock)
                {
                    if (!turnDone && t.Running >= 0) return;
                    t.Running = (int)m.Logic.Turn; t.RunSince = DateTime.UtcNow;
                }
                if (!turnDone) SendGame(m);
            }
            catch { }
        }

        /// <summary>Every world tick: a flag that fell.</summary>
        static void GameTick(DateTime now)
        {
            List<Temp> flagged = null;
            lock (s_Lock)
                foreach (var t in s_Temps.Values)
                    if (t.Clock > 0 && t.Running >= 0 && !t.Ending && R.Flagged(t.Left[t.Running], (now - t.RunSince).TotalMilliseconds))
                        (flagged ??= new List<Temp>()).Add(t);
            if (flagged == null) return;
            foreach (var t in flagged)
            {
                int loser;
                lock (s_Lock) { loser = t.Running; t.Left[loser] = 0; t.Running = -1; s_Why[t.Guid] = "time"; }
                Mod.Log.Info($"[RevivalGuard] ChessLobby: 0x{t.Guid:X8} {(loser == 0 ? "White" : "Black")} out of time");
                EndAndPutAway(t, 1 - loser, "time");
            }
        }

        /// <summary>Every 2 s: watchers who walked off or left, a stale offer, the news line.</summary>
        static void GameTick2(DateTime now)
        {
            List<(uint who, uint board)> drop = new List<(uint, uint)>();
            List<(uint who, uint board)> all;
            lock (s_Lock) all = s_Watching.Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (who, board) in all)
            {
                var p = PlayerManager.GetOnlinePlayer(who);
                ChessMatch m; lock (s_Lock) s_Live.TryGetValue(board, out m);
                if (p == null || m == null || m.State != ChessState.InProgress || p.Location == null || m.ChessBoard?.Location == null
                    || p.Location.DistanceTo(m.ChessBoard.Location) > R.WATCH_M + 5f)
                    drop.Add((who, board));
            }
            foreach (var (who, _) in drop) { var p = PlayerManager.GetOnlinePlayer(who); if (p != null) Unwatch(p, false); else lock (s_Lock) DropWatcher(who); }
            lock (s_Lock)
            {
                foreach (var k in s_Offers.Where(kv => (now - kv.Value.At).TotalSeconds > R.DRAW_WAIT_S + 30).Select(kv => kv.Key).ToList()) s_Offers.Remove(k);
                foreach (var k in s_Rematch.Where(kv => (now - kv.Value.At).TotalSeconds > R.REMATCH_S).Select(kv => kv.Key).ToList()) s_Rematch.Remove(k);
            }
            FlushNews(now);
        }

        // ------------------------------------------------------------------ resign once

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.QuitEnqueue))]
        static class QuitPatch
        {
            static bool Prefix(ChessMatch __instance, Player player)
            {
                try
                {
                    if (!On || player == null) return true;
                    var m = __instance;
                    if (m.State != ChessState.InProgress && m.State != ChessState.WaitingForPlayers) return false;
                    var color = m.GetColor(player.Guid);
                    if (color == ChessColor.None) return false;
                    uint board = m.ChessBoard.Guid.Full;
                    lock (s_Lock)
                    {
                        if (s_Why.TryGetValue(board, out var why) && why == "resign" || m.Actions.Any(a => a.Action == ChessDelayedActionType.Quit && a.Color == color))
                        { PkCommon.Say(player, R.ALREADY_RESIGNED); return false; }
                        if (m.State == ChessState.InProgress) s_Why[board] = "resign";
                    }
                    // ACE queues a Quit for the board's next heartbeat, after walking pieces and the computer's move
                    var chain = new ActionChain();
                    chain.AddAction(m.ChessBoard, () => { try { m.QuitDelayed(color); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: quit: {e.Message}"); } });
                    chain.EnqueueChain();
                    Mod.Log.Info($"[RevivalGuard] ChessLobby: {player.Name} resigns on 0x{board:X8}");
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: quit: {e.Message}"); return true; }
            }
        }

        // ------------------------------------------------------------------ draw offers

        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.StalemateEnqueue))]
        static class OfferPatch
        {
            static bool Prefix(ChessMatch __instance, Player player, bool stalemate)
            {
                try
                {
                    if (!On || player == null) return true;
                    var m = __instance;
                    if (m.State != ChessState.InProgress) return false;
                    var color = m.GetColor(player.Guid);
                    if (color == ChessColor.None) return false;
                    uint board = m.ChessBoard.Guid.Full;
                    var opp = m.Sides[(int)Chess.InverseColor(color)];
                    if (!stalemate)
                    {
                        // a retail client's toggle off: the offer is withdrawn
                        lock (s_Lock) if (s_Offers.TryGetValue(board, out var o0) && o0.From == color) s_Offers.Remove(board);
                        SendGame(m);
                        return false;
                    }
                    var now = DateTime.UtcNow;
                    lock (s_Lock)
                    {
                        if (s_Offers.TryGetValue(board, out var o) && (now - o.At).TotalSeconds < R.DRAW_WAIT_S)
                        { PkCommon.Say(player, o.From == color ? R.DRAW_WAITING : "Your opponent has offered a draw: answer it first."); return false; }
                        if (s_OfferRetry.TryGetValue((board, (int)color), out var at) && (now - at).TotalSeconds < R.DRAW_RETRY_S)
                        { PkCommon.Say(player, R.DRAW_TOO_SOON); return false; }
                        s_OfferRetry[(board, (int)color)] = now;
                    }
                    if (opp == null) return false;
                    if (opp.IsAi())
                    {
                        var chain = new ActionChain();
                        chain.AddAction(m.ChessBoard, () => AiAnswersDraw(m, player.Guid.Full, color));
                        chain.EnqueueChain();
                        return false;
                    }
                    var them = opp.GetPlayer();
                    if (them == null) return false;
                    lock (s_Lock) s_Offers[board] = new Offer { From = color, At = now };
                    if (!them.ConfirmationManager.EnqueueSend(new ChessAskConfirmation(them.Guid, (yes, timeout) => DrawAnswered(m, board, color, player.Guid.Full, them, yes, timeout)), R.DrawQuestion(player.Name)))
                    {
                        lock (s_Lock) s_Offers.Remove(board);
                        PkCommon.Say(player, $"{them.Name} is answering another question. Try again in a moment.");
                        return false;
                    }
                    PkCommon.Say(player, R.DrawOffered(them.Name));
                    SendGame(m);
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: draw offer: {e.Message}"); return true; }
            }
        }

        /// <summary>On the board's thread: the computer looks at the position and says yes or no.</summary>
        static void AiAnswersDraw(ChessMatch m, uint who, ChessColor from)
        {
            var p = PlayerManager.GetOnlinePlayer(who);
            try
            {
                if (m.State != ChessState.InProgress) return;
                var L = m.Logic;
                var ai = Chess.InverseColor(from);
                float ev = L.EvaluateBoard();
                float aiScore = ai == ChessColor.Black ? ev : -ev;
                var pieces = new List<(int, int)>();
                L.WalkPieces(pc => pieces.Add(((int)pc.Type, (int)pc.Color)));
                bool dead = R.Insufficient(pieces);
                bool yes = R.AiTakesDraw(aiScore, dead);
                Mod.Log.Info($"[RevivalGuard] ChessLobby: draw offer to the computer on 0x{m.ChessBoard.Guid.Full:X8}: score {aiScore:0} dead {dead}: {(yes ? "taken" : "declined")}");
                if (p != null) PkCommon.Say(p, yes ? R.AI_TAKES_DRAW : R.AI_DECLINES_DRAW);
                if (yes)
                {
                    lock (s_Lock) s_Why[m.ChessBoard.Guid.Full] = "agreed";
                    m.Finish(Chess.ChessWinnerStalemate);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: computer's draw answer: {e.Message}"); }
        }

        static void DrawAnswered(ChessMatch m, uint board, ChessColor from, uint offerer, Player them, bool yes, bool timeout)
        {
            lock (s_Lock) { if (!s_Offers.TryGetValue(board, out var o) || o.From != from) { if (yes && them != null) PkCommon.Say(them, "That draw offer was withdrawn."); return; } s_Offers.Remove(board); }
            var p = PlayerManager.GetOnlinePlayer(offerer);
            if (!yes)
            {
                if (p != null) PkCommon.Say(p, timeout ? R.DrawNoAnswer(them?.Name ?? "Your opponent") : R.DrawDeclined(them?.Name ?? "Your opponent"));
                SendGame(m);
                return;
            }
            var chain = new ActionChain();
            chain.AddAction(m.ChessBoard, () =>
            {
                try
                {
                    if (m.State != ChessState.InProgress || m.ChessBoard.ChessMatch != m) return;
                    lock (s_Lock) s_Why[board] = "agreed";
                    m.Finish(Chess.ChessWinnerStalemate);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: draw: {e.Message}"); }
            });
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ the end

        /// <summary>FinishPatch's Prefix, before ACE removes the pieces: why it ends, and the exit effect for a temporary board.</summary>
        static void GameEnding(ChessMatch m, int winner, Temp t)
        {
            uint board = m.ChessBoard.Guid.Full;
            string why;
            lock (s_Lock) { s_Why.TryGetValue(board, out why); s_Why.Remove(board); s_Offers.Remove(board); s_Live.Remove(board); if (t != null) t.Running = -1; }
            if (why == null)
            {
                why = winner == Chess.ChessWinnerStalemate ? "stalemate" : "";
                if (winner >= 0 && m.State == ChessState.InProgress)
                {
                    var turn = m.Logic.Turn;
                    ChessCheckmate.Quiet = true;
                    try { if (m.Logic.InCheckmate(Chess.InverseColor((ChessColor)winner), true)) why = "mate"; else why = "left"; }
                    catch { }
                    finally { ChessCheckmate.Quiet = false; m.Logic.Turn = turn; }
                }
            }
            t_Why = why;
            t_Fade = t != null;
        }

        /// <summary>FinishPatch's Postfix: tell both sides and the watchers, keep the rematch.</summary>
        static void GameEnded(ChessMatch m, int winner, Temp t)
        {
            if (t_Why == null) return;   // ACE's Finish of a game already over
            string why = t_Why; t_Why = null;
            var ws = m.Sides[0]; var bs = m.Sides[1];
            bool started = ws != null && bs != null;
            string res = R.ResultLine(winner, NameOf(ws, t), NameOf(bs, t), why);
            bool rematch = t != null && started;
            if (rematch)
            {
                var r = new Rematch_ { T = t, White = t.White, Black = t.Black, Ai = t.Ai, Level = t.Level, Clock = t.Clock, At = DateTime.UtcNow };
                lock (s_Lock) foreach (var g in new[] { t.White, t.Black }) if (g != 0) s_Rematch[g] = r;
            }
            SendGame(m, res, rematch);
            lock (s_Lock)
                if (s_Watch.TryGetValue(m.ChessBoard.Guid.Full, out var w))
                    foreach (var g in w.ToList()) DropWatcher(g);
        }

        /// <summary>A piece leaving at the end of a temporary board's game: the effect, then gone (ACE removes it at once).</summary>
        [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.RemoveWeeniePiece))]
        static class PiecePatch
        {
            static bool Prefix(ChessMatch __instance, BasePiece piece)
            {
                if (!t_Fade) return true;
                try
                {
                    var gp = __instance.ChessBoard.CurrentLandblock?.GetObject(piece.Guid) as GamePiece;
                    if (gp == null) return true;
                    piece.Guid = new ObjectGuid(0);
                    FadeAndDestroy(gp);
                    return false;
                }
                catch { return true; }
            }
        }

        static void FadeAndDestroy(WorldObject wo)
        {
            if (wo == null || wo.IsDestroyed) return;
            wo.EnqueueBroadcast(new GameMessageScript(wo.Guid, s_FadeFx));
            var chain = new ActionChain();
            chain.AddDelaySeconds(FADE_S);
            chain.AddAction(wo, () => { try { if (!wo.IsDestroyed) wo.Destroy(); } catch { } });
            chain.EnqueueChain();
        }

        static void StaffFx(Player me, string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) arg = arg.Substring(2);
            if (!uint.TryParse(arg, System.Globalization.NumberStyles.HexNumber, null, out var id)) { PkCommon.Say(me, $"/chess fx 0x<PlayScript>: now 0x{(uint)s_FadeFx:X}"); return; }
            s_FadeFx = (PlayScript)id;
            PkCommon.Say(me, $"Chess end effect: {s_FadeFx} (0x{id:X}) until the server restarts.");
        }

        // ------------------------------------------------------------------ rematch

        static void Rematch(Player me)
        {
            Rematch_ r;
            lock (s_Lock) s_Rematch.TryGetValue(me.Guid.Full, out r);
            if (r == null || (DateTime.UtcNow - r.At).TotalSeconds > R.REMATCH_S) { Say(me, R.NO_REMATCH); return; }
            if (InMatch(me)) { Say(me, R.YOU_BUSY); return; }
            if (r.Ai) { StartRematch(r, me); return; }
            uint other = r.White == me.Guid.Full ? r.Black : r.White;
            var them = PlayerManager.GetOnlinePlayer(other);
            if (them == null) { Say(me, "Your opponent is no longer here."); return; }
            if (InMatch(them)) { Say(me, R.Busy(them.Name)); return; }
            if (!Near(me, them)) { Say(me, R.FarLine(them.Name, DeathLog.WhereOf(them))); return; }
            lock (s_Lock)
            {
                if (s_RematchAsked.TryGetValue(me.Guid.Full, out var at) && (DateTime.UtcNow - at).TotalSeconds < R.CHALLENGE_S) { Say(me, $"You are still waiting for {them.Name}."); return; }
                s_RematchAsked[me.Guid.Full] = DateTime.UtcNow;
            }
            bool themWhite = r.Black == them.Guid.Full;   // colours swap
            if (!them.ConfirmationManager.EnqueueSend(new ChessAskConfirmation(them.Guid, (yes, timeout) =>
                {
                    lock (s_Lock) s_RematchAsked.Remove(me.Guid.Full);
                    var asker = PlayerManager.GetOnlinePlayer(me.Guid.Full);
                    if (!yes) { if (asker != null) Say(asker, timeout ? $"{them.Name} did not answer." : $"{them.Name} does not want a rematch."); return; }
                    if (asker == null) { Say(them, "Your opponent is no longer here."); return; }
                    if (!On) { Say(them, R.NOT_OPEN); return; }
                    StartRematch(r, asker);
                }), R.RematchQuestion(me.Name, themWhite, r.Clock)))
            {
                lock (s_Lock) s_RematchAsked.Remove(me.Guid.Full);
                Say(me, $"{them.Name} is answering another question. Try again in a moment.");
                return;
            }
            Say(me, R.RematchWaiting(them.Name));
        }

        /// <summary>The same two sides, colours swapped, the same clock; on the old board while it stands.</summary>
        static void StartRematch(Rematch_ r, Player asker)
        {
            uint white = r.Black, black = r.White;
            lock (s_Lock) { foreach (var g in new[] { r.White, r.Black }) if (g != 0) s_Rematch.Remove(g); }
            var others = new[] { white, black }.Where(g => g != 0 && g != asker.Guid.Full).Select(PlayerManager.GetOnlinePlayer).ToList();
            if (others.Any(o => o == null || InMatch(o))) { Say(asker, "Your opponent is not free now."); return; }
            Temp t = r.T;
            bool reuse;
            lock (s_Lock)
            {
                reuse = t != null && !t.Gone && !t.Board.IsDestroyed && t.Board.ChessMatch == null && s_Temps.ContainsKey(t.Guid);
                if (reuse)
                {
                    t.PutAwayAt = null; t.Ending = false; t.White = white; t.Black = black; t.Plies = 0;
                    t.Made = t.LastMoveAt = DateTime.UtcNow; t.MotionSince = default; t.Running = -1;
                }
                else if (s_Temps.Count >= R.MAX_BOARDS) { Say(asker, R.TOO_MANY); return; }
            }
            if (!reuse)
            {
                var spot = FindSpot(asker, asker.Location, 0f, 5.5f, out var why);
                if (spot == null) { Say(asker, R.SpotWhy(why)); return; }
                t = MakeBoard(spot, white, black, r.Ai, r.Level);
                if (t == null) { Say(asker, "The board could not be set here. Try a few steps away."); return; }
                t.Clock = r.Clock;
            }
            foreach (var g in new[] { white, black })
            {
                var p = g == 0 ? null : PlayerManager.GetOnlinePlayer(g);
                if (p == null) continue;
                Say(p, r.Ai ? R.AiStart(r.Level == R.NORMAL ? "Normal" : "Easy", g == white) : R.RematchStart(g == white));
            }
            Seat(t, reuse ? FADE_S + 0.5 : 1.0);   // the last game's pieces finish leaving first
        }

        // ------------------------------------------------------------------ watching

        /// <summary>Using a board whose game is full: watch it (ours; ACE answered "could not join").</summary>
        [HarmonyPatch(typeof(Game), nameof(Game.ActOnJoin))]
        static class JoinPatch
        {
            static bool Prefix(Game __instance, Player player)
            {
                try
                {
                    if (!On || player == null) return true;
                    var m = __instance.ChessMatch;
                    if (m == null || m.State != ChessState.InProgress || m.GetFreeColor() != ChessColor.None || m.InMatch(player.Guid)) return true;
                    Watch(player, m);
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby: watch by use: {e.Message}"); return true; }
            }
        }

        static void WatchCmd(Player me, string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) arg = arg.Substring(2);
            ChessMatch m = null;
            if (uint.TryParse(arg, System.Globalization.NumberStyles.HexNumber, null, out var g)) lock (s_Lock) s_Live.TryGetValue(g, out m);
            if (m == null || m.State != ChessState.InProgress) { Say(me, R.WATCH_NONE); Send(me); return; }
            Watch(me, m);
        }

        static void Watch(Player me, ChessMatch m)
        {
            if (InMatch(me)) { Say(me, R.YOU_BUSY); return; }
            if (me.Location == null || m.ChessBoard.Location == null || me.Location.DistanceTo(m.ChessBoard.Location) > R.WATCH_M) { Say(me, R.WATCH_FAR); return; }
            uint board = m.ChessBoard.Guid.Full;
            lock (s_Lock)
            {
                DropWatcher(me.Guid.Full);
                if (!s_Watch.TryGetValue(board, out var set)) s_Watch[board] = set = new HashSet<uint>();
                set.Add(me.Guid.Full);
                s_Watching[me.Guid.Full] = board;
            }
            var t = TempOf(m);
            PkCommon.Say(me, R.Watching(NameOf(m.Sides[0], t), NameOf(m.Sides[1], t)));
            SendGame(m, only: me.Guid.Full);
        }

        static void Unwatch(Player me, bool asked)
        {
            bool was;
            lock (s_Lock) was = DropWatcher(me.Guid.Full);
            if (was && ModChannel.Capable(me.Session)) ModChannel.Send(me.Session, "chessgame", "{\"st\":\"none\"}");
            if (asked && !was) PkCommon.Say(me, "You are not watching a game.");
        }

        /// <summary>Under s_Lock.</summary>
        static bool DropWatcher(uint who)
        {
            if (!s_Watching.TryGetValue(who, out var board)) return false;
            s_Watching.Remove(who);
            if (s_Watch.TryGetValue(board, out var set)) { set.Remove(who); if (set.Count == 0) s_Watch.Remove(board); }
            return true;
        }

        // ------------------------------------------------------------------ news in General

        static void News(string line)
        {
            lock (s_Lock)
            {
                if (s_News.Count >= R.NEWS_QUEUE) { Mod.Log.Info($"[RevivalGuard] ChessLobby: news dropped (queue full): {line}"); return; }
                s_News.Enqueue(line);
            }
        }

        static void FlushNews(DateTime now)
        {
            string line;
            lock (s_Lock)
            {
                if (s_News.Count == 0 || !R.NewsDue(s_LastNews, now)) return;
                line = s_News.Dequeue();
                s_LastNews = now;
            }
            try
            {
                if (PropertyManager.GetBool("chat_disable_general").Item) return;
                var msg = new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_EVENT_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME,
                                                     TurbineChatChannel.General, R.NEWS_SENDER, line, 0, ChatType.General);
                foreach (var p in PlayerManager.GetAllOnline())
                {
                    if (!p.GetCharacterOption(CharacterOption.ListenToGeneralChat) || Mercenaries.IsMerc(p)) continue;
                    p.Session?.Network.EnqueueSend(msg);
                }
                ChatLog.Write("General", R.NEWS_SENDER, line);
                Mod.Log.Info($"[RevivalGuard] ChessLobby: General: {line}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby news: {e.Message}"); }
        }
    }

    /// <summary>A Yes/No whose answer goes to a callback (draw offers, rematches).</summary>
    sealed class ChessAskConfirmation : Confirmation
    {
        readonly Action<bool, bool> m_Then;
        public ChessAskConfirmation(ObjectGuid target, Action<bool, bool> then) : base(target, ConfirmationType.Yes_No) { m_Then = then; }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            try { m_Then(response && !timeout, timeout); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChessLobby answer: {e.Message}"); }
        }
    }
}
