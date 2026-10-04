using System;
using ACE.Entity.Enum;
using ACE.Server.Entity.Chess;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CHESS GAME COULD NOT BE WON: ACE NEVER REPORTS CHECKMATE.
    ///
    /// Found 2026-09-21 playing the first complete two-player game this project has managed, once
    /// the client gained a CHESS command verb. Two bots joined one board as White and Black and
    /// played 1.e4 e5 2.Bc4 Nf6 3.Qh5 d6 4.Qxf7 -- Scholar's Mate, and mate it is: the queen on f7
    /// is defended by the bishop on c4, so Kxf7 is illegal; Ke7 is attacked by the same queen;
    /// nothing on the board can capture her; and the check is adjacent, so it cannot be blocked.
    ///
    /// **The client and the server disagreed about it.** Our client returned its own move result
    /// `0x802` -- `MR_OK_OCCUPIED | MR_CHECKMATE`, a capture that mates. ACE answered `0x402` --
    /// `OKMoveToOccupiedSquare | OKMoveCheck`, a capture that merely checks. Black's client was
    /// handed the turn and the game carried on from a position with no legal moves in it.
    ///
    /// ACE IS THE ONE THAT IS WRONG, and the reason is one argument:
    ///
    ///     // ChessLogic.FinalizeMove, Entity/Chess/ChessLogic.cs:618
    ///     if (InCheckmate(Turn, false))
    ///         result |= ChessMoveResult.OKMoveCheckmate;
    ///
    /// with `fullCheck: false`, `InCheckmate` degrades to:
    ///
    ///     hasMove = storage.Count > 0;            // ANY generated move at all
    ///     return InCheck(color) &amp;&amp; !hasMove;
    ///
    /// which asks whether the mated side has any move *whatsoever*, not whether it has one that
    /// escapes check. A mated king almost always has pawns and knights that can legally push into
    /// a losing position, so `storage.Count` is never 0 and mate is never reported. It fires only
    /// on the stalemate-shaped case where a side has no generated moves at all.
    ///
    /// WHY IT IS WRITTEN THAT WAY, AND WHY THE ONE-WORD FIX IS WRONG. `InCheckmate(color, true)`
    /// evaluates each candidate by calling **`FinalizeMove`** and undoing it. Passing `true` at
    /// line 618 would therefore have `FinalizeMove` call `InCheckmate` call `FinalizeMove`, without
    /// bound. The `false` is not an oversight about chess; it is ACE avoiding its own recursion, and
    /// the cost is that the game cannot be won.
    ///
    /// SO THIS RUNS THE REAL SEARCH FROM OUTSIDE, ONCE, with a re-entry guard. The postfix only
    /// bothers when ACE has already said `OKMoveCheck` without `OKMoveCheckmate` -- a position that
    /// is not check cannot be mate, so the expensive search never runs on an ordinary move -- and
    /// the guard makes every nested `FinalizeMove` inside the search return ACE's own answer
    /// untouched, which is exactly the answer the search wants from it.
    ///
    /// The guard is `[ThreadStatic]` because landblocks tick on several threads and two boards in
    /// different landblocks must not share it; this is the same shape `EnterRescue` uses.
    ///
    /// COST. One full legal-move search per checking move, on a board that is by then already in
    /// check. Chess games on this shard are rare, small and turn-based, and the alternative is a
    /// game that cannot end.
    ///
    /// THE SEARCH LEAVES `Turn` ON THE WRONG SIDE, AND THAT WAS OPEN ITEM 46 (2026-09-22). After the
    /// first mate this patch flagged, the WHITE client logged `[chess] opponent h5-f7` for its own
    /// move and the game ended "Black wins!". Not a colour mix-up in the client: the server had put
    /// White back on move. `InCheckmate(color, true)` runs FinalizeMove and UndoMove over every
    /// candidate, and ACE's UndoMove (ChessLogic.cs:718) sets
    ///
    ///     Turn = Chess.InverseColor(move.Color);
    ///
    /// which is the value InternalMove (line 708) had ALREADY set when the move was made, not the
    /// value from before it. ACE's own AI path never notices, because the next FinalizeMove assigns
    /// Turn unconditionally and GenerateMoves takes its colour as an argument. Here the search runs
    /// AFTER the real move, so it hands `Turn` back flipped: the side that just mated is "to move"
    /// again. ChessMatch.FinishTurn then reads that Turn and routes the two events backwards, the
    /// MoveResponse to the mated side and the OpponentTurn (carrying the mating move) to the mover,
    /// and every later move from the mated side is dropped by MoveDelayed's `Logic.Turn != color`.
    /// The game hung until White resigned or walked off the board, and QuitDelayed credited Black.
    /// The character records agree: +Dev devbot3 (White) lost, +Hollowtide Academy AB (Black) won.
    /// So the postfix saves Turn before the search and puts it back afterwards.
    ///
    /// AND ACE NEVER ENDS A TWO-PLAYER GAME ON MATE. FinishAiMove calls Finish() when the AI has no
    /// move; MoveDelayed/FinishTurn never look at OKMoveCheckmate at all. The flag this patch sets
    /// only reached the client's "You have checkmated your opponent!" line. <see cref="ChessMateEndsGame"/>
    /// finishes the match from FinishTurn, once the mated side has been shown the move, with the
    /// mover as the winner, which is what retail's GameOver (0x028C) carried.
    /// </summary>
    [HarmonyPatch(typeof(ChessLogic), nameof(ChessLogic.FinalizeMove))]
    public static class ChessCheckmate
    {
        [ThreadStatic] static bool t_Searching;

        static void Postfix(ChessLogic __instance, ref ChessMoveResult __result)
        {
            // Inside our own search: hand back ACE's cheap answer, which is what the search reads.
            if (t_Searching) return;
            // Not a check, so it cannot be mate -- and this is the branch almost every move takes.
            if (!__result.HasFlag(ChessMoveResult.OKMoveCheck)) return;
            // ACE got there on its own (the no-generated-moves case); nothing to add.
            if (__result.HasFlag(ChessMoveResult.OKMoveCheckmate)) return;

            // `Turn` is the side that must now move, which FinalizeMove has already flipped to
            // the opponent -- the same value line 618 passes. Kept, because the search's UndoMove
            // does not restore it (class note).
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
                // NEVER LET THIS REACH THE WORLD THREAD. ACE runs chess on the World Manager
                // thread, where an unhandled exception aborts the whole process with SIGABRT and
                // takes the shard down; ACE's own UnhandledException handler only log.Errors into
                // an async forwarder that has not drained by then, so the stack is lost too. The
                // shard aborted three times on 2026-09-22 with no cause established, and this
                // postfix ran unprotected on the world thread at the time. Leaving ACE's own
                // answer in place loses a mate detection; it does not lose the world.
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

    /// <summary>
    /// A two-player game ends when a side is mated. ChessMatch.FinishTurn has just sent the mover
    /// its MoveResponse and the mated side the OpponentTurn with the mating move on it; this runs
    /// after both, so the mated board is shown before GameOver arrives, and hands Finish() the mover
    /// (the inverse of the side to move) as ACE's FinishAiMove does for the AI. Finish() records the
    /// win, the loss and the rank change and clears the board, so nothing here repeats that.
    /// </summary>
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

    /// <summary>
    /// ONE PLAYER, ONE SEAT. ChessMatch.Join hands the next free colour to whoever uses the board,
    /// with no check that the player is already seated, so a second use by the same character fills
    /// the other seat and one player holds both sides of the game (the owner's WORLD-ACT-01,
    /// "allows playing both sides", on the Holtburg board). Retail's client never sent a second
    /// join for a board it was already on; ours sends the board a plain Use (0x0036) each time,
    /// which ACE routes to the same Join. A seated player using the board again keeps their seat:
    /// the match is re-attached to the current Player object (a relog makes a new one with
    /// ChessMatch null, which is why moves after a relog went nowhere) and the JoinGameResponse is
    /// re-sent with their colour, so the client's feed shows the seat they hold.
    /// </summary>
    [HarmonyPatch(typeof(ChessMatch), nameof(ChessMatch.Join))]
    public static class ChessJoinOnce
    {
        static bool Prefix(ChessMatch __instance, Player player)
        {
            // A throw from a PREFIX is worse than from a postfix: it aborts the shard before ACE's
            // own Join has run at all. On any failure, fall through to ACE (return true) rather
            // than propagate. ChessBoard was dereferenced here without a null check, and a board
            // that has been destroyed or not yet attached would have taken the world with it.
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
