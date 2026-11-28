using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>The pure part of the chess lobby</summary>
    public static class ChessLobbyRule
    {
        /// <summary>Challenges are in person: both within this many metres.</summary>
        public const float NEAR_M = 20f;
        /// <summary>A challenge waits this long for its answer (ACE's own Yes/No times out sooner, and that is a No).</summary>
        public const int CHALLENGE_S = 60;
        /// <summary>How long an open "looking for a match" request stands.</summary>
        public const int OPEN_S = 600;
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
        /// <summary>A game counts for the record, ladder and titles only with at least this many moves</summary>
        public const int MIN_PLIES = 10;
        /// <summary>Wins over the same opponent that count in one day (UTC): two friends cannot trade titles.</summary>
        public const int PAIR_WINS_PER_DAY = 3;
        public const int RATING_START = 1400, RATING_K = 32;
        public const int LADDER_ROWS = 10;
        /// <summary>The board's footprint: ACE lays the 8x8 pieces 1 m apart around its centre</summary>
        public const float HALF_M = 5f, EDGE_M = 7f, FLAT_M = 1.0f, CLEAR_M = 6.5f, BUILDING_M = 16f, OTHER_BOARD_M = 14f;

        public const string EASY = "easy", NORMAL = "normal";

        /// <summary>Retail titles nothing in the world grants.</summary>
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

        /// <summary>Whether a game between players counts for the record, the ladder and the titles.</summary>
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

        /// <summary>The ladder: characters with a counted game against a player, by rating, then wins, then fewer losses, then name.</summary>
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

        /// <summary>A temporary board's spot: outdoors, far enough inside the landblock that all 64 squares are in it, flat, dry</summary>
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

        // ------------------------------------------------------------------ in the game

        /// <summary>Retail's Stalemate button is a draw offer.</summary>
        public const float AI_DRAW_BEHIND = 30f;
        /// <summary>A draw offer to a player waits this long; after a No (or to the computer) the same side waits this long.</summary>
        public const int DRAW_WAIT_S = 30, DRAW_RETRY_S = 20;
        /// <summary>After a game on a temporary board, either side may ask for a rematch this long.</summary>
        public const int REMATCH_S = 90;
        /// <summary>Watchers: within this of the board (ACE's own leash for the players is 40 m).</summary>
        public const float WATCH_M = 40f;
        /// <summary>General chat news (a win in a player game, a chess title): one line per gap at most, a few waiting.</summary>
        public const int NEWS_GAP_S = 60, NEWS_QUEUE = 4;
        public const string NEWS_SENDER = "Chess";
        /// <summary>The clocks a challenge may pick, in minutes per side (0: none).</summary>
        public static readonly int[] CLOCKS = { 0, 10, 3 };

        /// <summary>"c10" / "c3" / "c0" at the end of "/chess challenge 0x... c10": the clock, else 0. -1: not a clock word.</summary>
        public static int ParseClock(string word)
        {
            if (string.IsNullOrEmpty(word) || word.Length < 2 || (word[0] != 'c' && word[0] != 'C')) return -1;
            return int.TryParse(word.Substring(1), out int m) && CLOCKS.Contains(m) ? m : -1;
        }

        /// <summary>Neither side can ever mate: kings alone, or a king and one bishop or knight against a bare king.</summary>
        public static bool Insufficient(IEnumerable<(int type, int color)> pieces)
        {
            int[] minors = new int[2];
            foreach (var (type, color) in pieces)
            {
                if (type == 1 || type == 2 || type == 5) return false;
                if (type == 3 || type == 4) { if (color < 0 || color > 1) return false; minors[color]++; }
            }
            return minors[0] + minors[1] <= 1;
        }

        /// <summary>Does the computer take a draw?</summary>
        public static bool AiTakesDraw(float aiScore, bool insufficient) => insufficient || aiScore <= -AI_DRAW_BEHIND;

        public static double ClockAfter(double leftMs, double elapsedMs) => Math.Max(0, leftMs - Math.Max(0, elapsedMs));
        public static bool Flagged(double leftMs, double elapsedMs) => leftMs - elapsedMs <= 0;

        /// <summary>The news queue: may a line go now, given the last one's time.</summary>
        public static bool NewsDue(DateTime last, DateTime now) => (now - last).TotalSeconds >= NEWS_GAP_S;

        public static string ClockName(int minutes) => minutes <= 0 ? "" : minutes == 3 ? "3 minute blitz clock" : $"{minutes} minute clock";

        public static string ChallengeQuestion(string from, int level, int clock) =>
            clock <= 0 ? ChallengeQuestion(from, level)
                       : $"{from} (level {level}) challenges you to a game of chess with a {ClockName(clock)} each. Play now? A board will appear between you.";
        public static string OpenPosted(int clock) =>
            $"Your request is up: anyone online can accept it from the Game Center{(clock > 0 ? " (" + ClockName(clock) + " each)" : "")}. It stands {OPEN_S / 60} minutes. /chess cancel withdraws it.";
        public const string OPEN_GONE = "That request is gone.";
        public static string OpenAccepted(string name) => $"{name} accepted your request.";
        public static string DrawQuestion(string name) => $"{name} offers a draw. Accept? The game ends as a draw.";
        public static string DrawOffered(string name) => $"You offer {name} a draw. Waiting for an answer...";
        public static string DrawDeclined(string name) => $"{name} declines the draw. Play on.";
        public static string DrawNoAnswer(string name) => $"{name} did not answer your draw offer. Play on.";
        public const string DRAW_WAITING = "Your draw offer is still waiting for an answer.";
        public const string DRAW_TOO_SOON = "You offered a draw only just now. Play a little first.";
        public const string AI_TAKES_DRAW = "The computer accepts your draw offer.";
        public const string AI_DECLINES_DRAW = "The computer declines your draw offer. Play on.";
        public const string ALREADY_RESIGNED = "You have already resigned. The game is ending.";
        public static string RematchQuestion(string name, bool youWhite, int clock) =>
            $"{name} asks for a rematch: you play {(youWhite ? "White" : "Black")}{(clock > 0 ? ", " + ClockName(clock) + " each" : "")}. Play again?";
        public static string RematchWaiting(string name) => $"You ask {name} for a rematch. Waiting for an answer...";
        public const string NO_REMATCH = "There is no game to play again. Start one from the Game Center.";
        public static string RematchStart(bool youWhite) => $"Rematch. You play {(youWhite ? "White" : "Black")}. Good luck!";
        public static string AiStart(string level, bool youWhite) => youWhite ? AiStart(level)
            : $"A board is set beside you. You play Black against the computer ({level}), which moves first. Walk away more than 40 m or resign to end it.";
        public static string TimeLoss(string name) => $"{name} ran out of time.";
        public const string WATCH_FAR = "Walk closer to that game to watch it.";
        public const string WATCH_NONE = "That game has ended.";
        public static string Watching(string white, string black) => $"You are watching {white} (White) and {black} (Black). Close the Game Center to stop.";

        /// <summary>The finished game in one line, as both players and watchers see it.</summary>
        public static string ResultLine(int winner, string white, string black, string why)
        {
            if (winner == -1) return why == "agreed" ? "Draw agreed." : why == "stalemate" ? "Stalemate. The game is a draw." : "The game is a draw.";
            if (winner < 0) return "The game was ended with no result.";
            string w = Cap(winner == 0 ? white : black), l = Cap(winner == 0 ? black : white);
            return why switch
            {
                "mate" => $"Checkmate. {w} wins.",
                "resign" => $"{l} resigns. {w} wins.",
                "time" => $"{l} ran out of time. {w} wins.",
                "idle" => $"{l} made no move for {IDLE_MIN} minutes. {w} wins.",
                "left" => $"{l} left the game. {w} wins.",
                _ => $"{w} wins.",
            };
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static string NewsWin(string winner, string loser, string why) =>
            why == "mate" ? $"{winner} beat {loser} at chess by checkmate." : $"{winner} beat {loser} at chess.";
        public static string NewsTitle(string name, string title) => $"{name} has earned the chess title {title}.";
    }
}
