using System;
using ACE.Entity.Enum;
using ACE.Server.Entity.Chess;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(ChessLogic), nameof(ChessLogic.FinalizeMove))]
    public static class ChessCheckmate
    {
        [ThreadStatic] static bool t_Searching;
        /// <summary>ChessLobby's Normal AI sets this around its own search, which only wants ACE's cheap answer.</summary>
        internal static bool Quiet { get => t_Searching; set => t_Searching = value; }

        static void Postfix(ChessLogic __instance, ref ChessMoveResult __result)
        {
            // Inside our own search: hand back ACE's cheap answer, which is what the search reads.
            if (t_Searching) return;
            // Not a check, so it cannot be mate -- and this is the branch almost every move takes.
            if (!__result.HasFlag(ChessMoveResult.OKMoveCheck)) return;
            // ACE got there on its own (the no-generated-moves case); nothing to add.
            if (__result.HasFlag(ChessMoveResult.OKMoveCheckmate)) return;

            // `Turn` is the side that must now move, which FinalizeMove has already flipped to the opponent -- the same value line 618 passes.
            var turn = __instance.Turn;
            t_Searching = true;
            try
            {
                if (__instance.InCheckmate(turn, true))
                {
                    __result |= ChessMoveResult.OKMoveCheckmate;
                    Mod.Log.Info("[RevivalGuard] chess: checkmate that ACE reported as a bare check "
                        + $"(result now 0x{(int)__result:X}) -- ChessLogic.FinalizeMove asks "
                        + "InCheckmate with fullCheck false, which only catches a side with no "
                        + "generated moves at all.");
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Error($"[RevivalGuard] chess: checkmate search threw, leaving ACE's own "
                    + $"result 0x{(int)__result:X} alone: {ex}");
            }
            finally
            {
                t_Searching = false;
                __instance.Turn = turn;
            }
        }
    }

    /// <summary>A two-player game ends when a side is mated.</summary>
    [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.FinishTurn))]
    public static class ChessMateEndsGame
    {
        static void Postfix(ChessMatch __instance)
        {
            // Same rule as the search above: a throw here aborts the shard, so nothing escapes.
            try
            {
                if (__instance?.Logic == null) return;
                if (__instance.State != ChessState.InProgress) return;
                if (!__instance.MoveResult.HasFlag(ChessMoveResult.OKMoveCheckmate)) return;
                var winner = Chess.InverseColor(__instance.Logic.Turn);
                Mod.Log.Info($"[RevivalGuard] chess: {winner} mates; finishing the match on board "
                    + $"0x{__instance.ChessBoard?.Guid.Full:X8}, which ACE only does for its AI.");
                __instance.Finish((int)winner);
            }
            catch (Exception ex)
            {
                Mod.Log.Error($"[RevivalGuard] chess: finishing a mated match threw; the game is "
                    + $"left running rather than taking the world down: {ex}");
            }
        }
    }

    /// <summary>One player, one seat.</summary>
    [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.Join))]
    public static class ChessJoinOnce
    {
        static bool Prefix(ChessMatch __instance, Player player)
        {
            // A throw from a PREFIX is worse than from a postfix: it aborts the shard before ACE's own Join has run at all.
            try
            {
                if (player == null || __instance?.ChessBoard == null) return true;
                var color = __instance.GetColor(player.Guid);
                if (color == ChessColor.None) return true;
                player.ChessMatch = __instance;
                player.Session?.Network.EnqueueSend(
                    new ACE.Server.Network.GameEvent.Events.GameEventJoinGameResponse(player.Session, __instance.ChessBoard.Guid, color));
                Mod.Log.Info($"[RevivalGuard] chess: {player.Name} used board 0x{__instance.ChessBoard.Guid.Full:X8} "
                    + $"while already seated as {color}; seat kept, second seat not given.");
                return false;
            }
            catch (Exception ex)
            {
                Mod.Log.Error($"[RevivalGuard] chess: the seat check threw; letting ACE's own Join "
                    + $"run instead of taking the world down: {ex}");
                return true;
            }
        }
    }
}
