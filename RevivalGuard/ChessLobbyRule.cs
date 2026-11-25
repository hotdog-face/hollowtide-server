using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of the chess lobby (ChessLobby.cs; no ACE types, tools/nettest compiles it by path, ChessLobbyCase.cs):
    /// the record kept per character, which games count, the rating, the titles and when they are due, the ladder, the
    /// spot a temporary board may stand on, and every player line. Ours, not retail (docs/DIVERGENCES.md "Chess lobby"):
    /// ACE's chess (ChessMatch, ChessLogic, its rank) stays ACE's. Player text: plain, no em dashes.
    /// </summary>
    public static class ChessLobbyRule
    {
        /// <summary>Challenges are in person (owner 2026-11-25, like the allegiance swear): both within this many metres.</summary>
        public const float NEAR_M = 20f;
        /// <summary>A challenge waits this long for its answer (ACE's own Yes/No times out sooner, and that is a No).</summary>
        public const int CHALLENGE_S = 60;
        /// <summary>After a No, the same pair waits this long before another challenge.</summary>
        public const int DECLINED_WAIT_S = 120;
        /// <summary>One temporary board per player at a time; a new one no sooner than this after the last.</summary>
        public const int SPAWN_GAP_S = 20;
        /// <summary>The whole shard's cap on temporary boards standing at once.</summary>
        public const int MAX_BOARDS = 24;
        /// <summary>A temporary board's game with no move for this long ends (PvP: the side to move loses; AI: no result).</summary>
        public const int IDLE_MIN = 10;
        /// <summary>No temporary board stands longer than this, game or not.</summary>
        public const int MAX_LIFE_MIN = 120;
        /// <summary>A finished game's board stays this long so both sides see the end, then goes.</summary>
        public const int PUT_AWAY_S = 8;
        /// <summary>A game counts for the record, ladder and titles only with at least this many moves (both sides', plies):
        /// an instant resign between friends is not a win.</summary>
        public const int MIN_PLIES = 10;
        /// <summary>Wins over the same opponent that count in one day (UTC): two friends cannot trade titles.</summary>
        public const int PAIR_WINS_PER_DAY = 3;
        public const int RATING_START = 1400, RATING_K = 32;
        public const int LADDER_ROWS = 10;
        /// <summary>The board's footprint: ACE lays the 8x8 pieces 1 m apart around its centre (ChessMatch.CalculateWeeniePosition),
        /// so the ground must be clear and flat this far out, and the board this far inside its landblock.</summary>
        public const float HALF_M = 5f, EDGE_M = 7f, FLAT_M = 1.0f, CLEAR_M = 6.5f, BUILDING_M = 16f, OTHER_BOARD_M = 14f;

        public const string EASY = "easy", NORMAL = "normal";

        /// <summary>Retail titles nothing in the world grants (checked on the test shard's ace_world 2026-11-25: no
        /// AddCharacterTitle emote carries them). Wins over players unlock the four; a first win over Normal, the fifth.
        /// Ids are ACE.Entity.Enum.CharacterTitle (checked at startup, ChessLobby.CheckTitleIds).</summary>
        public const uint T_TACTICAL_AID = 664, T_TRUE_TACTICIAN = 665, T_STRATEGIST = 427, T_TACTICAL_FANTASTICAL = 666, T_PUZZLEMASTER = 828;

        public static readonly (int wins, uint title, string name)[] MILESTONES =
        {
            (1, T_TACTICAL_AID, "Tactical Aid"),
            (10, T_TRUE_TACTICIAN, "True Tactician"),
            (25, T_STRATEGIST, "Strategist"),
            (50, T_TACTICAL_FANTASTICAL, "Tactical Fantastical"),
        };
        public const string AI_TITLE_NAME = "Puzzlemaster";

        public static string TitleName(uint id) =>
            id == T_PUZZLEMASTER ? AI_TITLE_NAME : MILESTONES.FirstOrDefault(m => m.title == id).name ?? ("title " + id);

        public static string ParseLevel(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s == "" || s == "easy" || s == "e" || s == "1") return EASY;
            if (s == "normal" || s == "n" || s == "2" || s == "medium" || s == "hard") return NORMAL;
            return null;
        }

        // ------------------------------------------------------------------ the record

        public sealed class Record
        {
            public string Name { get; set; }
            /// <summary>Games against players that counted.</summary>
            public int W { get; set; }
            public int L { get; set; }
            public int D { get; set; }
            /// <summary>Games against the computer (any board), kept apart: never the ladder, never the player titles.</summary>
            public int AiW { get; set; }
            public int AiL { get; set; }
            public int AiD { get; set; }
            public int AiNormalW { get; set; }
            public int Rating { get; set; } = RATING_START;
            public List<uint> Titles { get; set; } = new List<uint>();
            public DateTime LastAt { get; set; }
            /// <summary>Wins that counted today, per opponent guid (UTC date in Day).</summary>
            public string Day { get; set; }
            public Dictionary<uint, int> WinsToday { get; set; } = new Dictionary<uint, int>();
            public int Games => W + L + D;
        }

        public enum Result { Win, Loss, Draw }

        public enum Skip { None, SameAccount, SameAddress, TooShort, PairCap }

        /// <summary>Whether a game between players counts for the record, the ladder and the titles. A draw or a loss is
        /// never capped (only wins could be farmed).</summary>
        public static Skip Counts(bool sameAccount, bool sameAddress, int plies, Result r, int winsOverThisOpponentToday)
        {
            if (sameAccount) return Skip.SameAccount;
            if (sameAddress) return Skip.SameAddress;
            if (plies < MIN_PLIES) return Skip.TooShort;
            if (r == Result.Win && winsOverThisOpponentToday >= PAIR_WINS_PER_DAY) return Skip.PairCap;
            return Skip.None;
        }

        public static string SkipWhy(Skip s) => s switch
        {
            Skip.SameAccount => "both sides are on one account",
            Skip.SameAddress => "both sides play from one connection",
            Skip.TooShort => $"it ended before {MIN_PLIES} moves",
            Skip.PairCap => $"only {PAIR_WINS_PER_DAY} wins over the same player count each day",
            _ => "",
        };

        /// <summary>The Elo step (ACE's own formula, ChessMatch.ExpectationToWin, K 32).</summary>
        public static int Delta(int rating, int opp, Result r)
        {
            double expect = 1.0 / (1.0 + Math.Pow(10, (opp - rating) / 400.0));
            double score = r == Result.Win ? 1 : r == Result.Draw ? 0.5 : 0;
            return (int)Math.Round(RATING_K * (score - expect));
        }

        public static int WinsToday(Record rec, uint opp, DateTime now)
        {
            if (rec.Day != now.ToString("yyyy-MM-dd")) return 0;
            return rec.WinsToday != null && rec.WinsToday.TryGetValue(opp, out var n) ? n : 0;
        }

        /// <summary>A counted game against a player: the record, the rating and today's pair count.</summary>
        public static void AddPvp(Record rec, Result r, int ratingDelta, uint opp, DateTime now)
        {
            if (r == Result.Win) rec.W++; else if (r == Result.Loss) rec.L++; else rec.D++;
            rec.Rating = Math.Max(100, rec.Rating + ratingDelta);
            string day = now.ToString("yyyy-MM-dd");
            if (rec.Day != day) { rec.Day = day; rec.WinsToday = new Dictionary<uint, int>(); }
            if (rec.WinsToday == null) rec.WinsToday = new Dictionary<uint, int>();
            if (r == Result.Win) rec.WinsToday[opp] = (rec.WinsToday.TryGetValue(opp, out var n) ? n : 0) + 1;
            rec.LastAt = now;
        }

        public static void AddAi(Record rec, Result r, bool normal, DateTime now)
        {
            if (r == Result.Win) { rec.AiW++; if (normal) rec.AiNormalW++; }
            else if (r == Result.Loss) rec.AiL++; else rec.AiD++;
            rec.LastAt = now;
        }

        /// <summary>Titles the record has earned and not yet been given, in milestone order.</summary>
        public static List<uint> TitlesDue(Record rec)
        {
            var due = new List<uint>();
            foreach (var m in MILESTONES)
                if (rec.W >= m.wins && !rec.Titles.Contains(m.title)) due.Add(m.title);
            if (rec.AiNormalW >= 1 && !rec.Titles.Contains(T_PUZZLEMASTER)) due.Add(T_PUZZLEMASTER);
            return due;
        }

        /// <summary>The next player-win title and how many more wins it needs, or null when all four are had.</summary>
        public static (string name, int more)? NextTitle(Record rec)
        {
            foreach (var m in MILESTONES)
                if (rec.W < m.wins) return (m.name, m.wins - rec.W);
            return null;
        }

        /// <summary>The ladder: characters with a counted game against a player, by rating, then wins, then fewer losses,
        /// then name.</summary>
        public static List<(int rank, Record rec)> Ladder(IEnumerable<Record> all, int rows = LADDER_ROWS)
        {
            var list = all.Where(r => r != null && r.Games > 0 && !string.IsNullOrEmpty(r.Name))
                .OrderByDescending(r => r.Rating).ThenByDescending(r => r.W).ThenBy(r => r.L)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var outp = new List<(int, Record)>();
            for (int i = 0; i < list.Count && i < rows; i++) outp.Add((i + 1, list[i]));
            return outp;
        }

        /// <summary>The 1-based place of one record on the full ladder, or 0 when it is not on it.</summary>
        public static int PlaceOf(IEnumerable<Record> all, Record rec)
        {
            if (rec == null || rec.Games == 0) return 0;
            var l = Ladder(all, int.MaxValue);
            foreach (var (rank, r) in l) if (ReferenceEquals(r, rec)) return rank;
            return 0;
        }

        // ------------------------------------------------------------------ where a board may stand

        public enum Spot { Ok, Indoors, Edge, Uneven, Water, Building, Clutter, OtherBoard }

        /// <summary>A temporary board's spot: outdoors, far enough inside the landblock that all 64 squares are in it, flat
        /// (the 3x3 ground heights across the board within FLAT_M), dry, no building within BUILDING_M, no tree or rock
        /// within CLEAR_M, no other chessboard within OTHER_BOARD_M.</summary>
        public static Spot Check(bool indoors, float x, float y, IList<float> groundZ, bool water,
            float nearestBuilding, float nearestClutter, float nearestBoard)
        {
            if (indoors) return Spot.Indoors;
            if (x < EDGE_M || y < EDGE_M || x > 192f - EDGE_M || y > 192f - EDGE_M) return Spot.Edge;
            if (water) return Spot.Water;
            if (groundZ == null || groundZ.Count == 0 || groundZ.Max() - groundZ.Min() > FLAT_M) return Spot.Uneven;
            if (nearestBuilding < BUILDING_M) return Spot.Building;
            if (nearestClutter < CLEAR_M) return Spot.Clutter;
            if (nearestBoard < OTHER_BOARD_M) return Spot.OtherBoard;
            return Spot.Ok;
        }

        /// <summary>Where to try a board, as offsets from a point: the point itself, then rings at 6 and 11 m.</summary>
        public static IEnumerable<(float dx, float dy)> Candidates()
        {
            yield return (0f, 0f);
            foreach (float r in new[] { 6f, 11f })
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    yield return ((float)Math.Round(r * Math.Cos(a), 2), (float)Math.Round(r * Math.Sin(a), 2));
                }
        }

        public static string SpotWhy(Spot s) => s switch
        {
            Spot.Indoors => "Chess boards need open ground outdoors. Step outside and try again, or use one of the boards in the world (Boards).",
            Spot.Edge or Spot.Uneven or Spot.Water or Spot.Building or Spot.Clutter or Spot.OtherBoard =>
                "There is no flat, open ground here for a board. Move to an open field and try again.",
            _ => "",
        };

        // ------------------------------------------------------------------ player lines

        public static string ChallengeQuestion(string from, int level) =>
            $"{from} (level {level}) challenges you to a game of chess. Play now? A board will appear between you.";
        public static string FarLine(string name, string where) =>
            $"{name} is not here: challenges are in person. {name} is {where ?? "somewhere far away"}. Meet them, then challenge again.";
        public const string NOT_OPEN = "The chess lobby is not open on this server. Use a chessboard in the world to play (Boards lists them).";
        public static string Declined(string name) => $"{name} declined your challenge.";
        public static string NoAnswer(string name) => $"{name} did not answer your challenge.";
        public static string Waiting(string name) => $"You challenge {name} to a game of chess. Waiting for an answer...";
        public static string Busy(string name) => $"{name} is already playing a game.";
        public const string YOU_BUSY = "You are already playing a game. Finish or resign it first.";
        public static string Accepted(string name) => $"{name} accepts. The board is set; you play White, {name} Black. Good luck!";
        public static string YouAccepted(string name) => $"You accept. The board is set; {name} plays White, you Black. Good luck!";
        public static string AiStart(string level) =>
            $"A board is set beside you. You play White against the computer ({level}). Walk away more than 40 m or resign to end it.";
        public const string IDLE_END = "The chess game sat with no move for 10 minutes and has been put away.";
        public static string IdleLoss(string name) => $"{name} made no move for 10 minutes and loses the game on time.";
        public const string TOO_MANY = "Too many temporary boards stand right now. Try again in a minute, or use a board in the world.";
        public const string TOO_SOON = "Your last board was only just put away. Try again in a few seconds.";
        public static string TitleLine(string title) => $"You have earned the title {title}!";
        public static string NotCounted(string why) => $"This game does not count on the chess ladder: {why}.";
        public static string RatingLine(int rating, int delta) =>
            $"Your chess ladder rating is now {rating} ({(delta >= 0 ? "+" : "")}{delta}).";
    }
}
