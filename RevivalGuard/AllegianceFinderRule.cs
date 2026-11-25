using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of the allegiance finder (Recruiting.Finder.cs; no ACE types, tools/nettest compiles it by path,
    /// AllegianceFinderCase.cs): what a listing may say, when it goes stale, how the list is sorted for the reader, the
    /// join request's states and timers, the asker's limits, and the one newcomer hint. Ours, not retail: swearing,
    /// passup and ranks stay ACE's; this only adds a way to find an allegiance and ask it (docs/DIVERGENCES.md
    /// "Finding an allegiance"). Player text: plain, no em dashes, never the Borg.
    /// </summary>
    public static class AllegianceFinderRule
    {
        public const int LEVEL_MIN = 1, LEVEL_MAX = 275;
        /// <summary>"auto-accept below rank X": X from 2 (only the unsworn with no vassals) to 10.</summary>
        public const int AUTO_RANK_MIN = 2, AUTO_RANK_MAX = 10;
        /// <summary>A listing whose monarch has not logged in for this long is taken down.</summary>
        public const int STALE_DAYS = 14;
        /// <summary>"Active this week": logged in within this many days.</summary>
        public const int ACTIVE_DAYS = 7;
        /// <summary>A request nobody answered ends after PENDING_HOURS; an accepted one nobody swore on in person after
        /// ACCEPTED_HOURS (owner 2026-11-25: meet and swear in person, "Accepted, meet X to swear" for about an hour).</summary>
        public const int PENDING_HOURS = 24, ACCEPTED_HOURS = 1;
        /// <summary>The asker's limits: a gap between asks, a day's cap, and a day before asking an allegiance that said no.</summary>
        public const int ASK_GAP_S = 60, ASKS_PER_DAY = 10, DECLINED_WAIT_HOURS = 24;
        /// <summary>The hint: a character in its first week, level 10 or higher, with no patron.</summary>
        public const int HINT_LEVEL = 10, HINT_DAYS = 7;
        public const string HINT_FLAG = "rg_allegiance_hint";

        public const string PK = "pk", PVE = "pve", ASK = "ask", AUTO = "auto";

        // ------------------------------------------------------------------ the listing

        public enum Problem { None, Pitch, Levels, Style, Join, Rank, Hours }

        /// <summary>What a recruiter set. Pitch and Hours are already through RecruitRule.Clean (null = empty).</summary>
        public sealed class Settings
        {
            public string Pitch, Hours, Style = PVE, Join = ASK;
            public int LevelMin = LEVEL_MIN, LevelMax = LEVEL_MAX, AutoRank;
        }

        /// <summary>The first thing wrong with a listing, or None.</summary>
        public static Problem Check(Settings s)
        {
            if (s == null || string.IsNullOrEmpty(s.Pitch) || s.Pitch.Length > RecruitRule.PITCH_MAX) return Problem.Pitch;
            if (s.LevelMin < LEVEL_MIN || s.LevelMax > LEVEL_MAX || s.LevelMin > s.LevelMax) return Problem.Levels;
            if (s.Style != PK && s.Style != PVE) return Problem.Style;
            if (s.Join != ASK && s.Join != AUTO) return Problem.Join;
            if (s.Join == AUTO && (s.AutoRank < AUTO_RANK_MIN || s.AutoRank > AUTO_RANK_MAX)) return Problem.Rank;
            if (s.Hours != null && s.Hours.Length > RecruitRule.HOURS_MAX) return Problem.Hours;
            return Problem.None;
        }

        /// <summary>"10-60", "10 60", "10", "any": (lo, hi), or null when it is not a range.</summary>
        public static (int lo, int hi)? ParseLevels(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s == "any" || s == "all") return (LEVEL_MIN, LEVEL_MAX);
            var parts = s.Replace('-', ' ').Replace("to", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && parts[0].EndsWith("+") && int.TryParse(parts[0].TrimEnd('+'), out int from)) return Ok(from, LEVEL_MAX);
            if (parts.Length == 1 && int.TryParse(parts[0], out int one)) return Ok(one, one);
            if (parts.Length == 2 && int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi)) return Ok(lo, hi);
            return null;
            static (int, int)? Ok(int lo, int hi) => lo >= LEVEL_MIN && hi <= LEVEL_MAX && lo <= hi ? (lo, hi) : null;
        }

        public static string ParseStyle(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return s == "pk" || s == "pvp" ? PK : s == "pve" || s == "npk" || s == "peaceful" ? PVE : null;
        }

        /// <summary>"ask" or "auto N": (mode, rank), or null.</summary>
        public static (string mode, int rank)? ParseJoin(string s)
        {
            var parts = (s ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && parts[0] == ASK) return (ASK, 0);
            if (parts.Length == 2 && parts[0] == AUTO && int.TryParse(parts[1], out int r) && r >= AUTO_RANK_MIN && r <= AUTO_RANK_MAX) return (AUTO, r);
            return null;
        }

        /// <summary>Who may change a listing: RecruitRule.CanToggle, with the officer rank the monarch allows (1 Speaker,
        /// 2 Seneschal, 3 Castellan; 4 = the monarch alone). Default Seneschal, as before.</summary>
        public static RecruitRule.Toggle CanManage(bool hasAllegiance, bool isMonarch, int? officerRank, int level, bool botOrStaff, int officerMin)
        {
            var t = RecruitRule.CanToggle(hasAllegiance, isMonarch, officerRank, level, botOrStaff);
            if (t == RecruitRule.Toggle.NotLeader && !isMonarch && (officerRank ?? 0) >= Math.Max(1, officerMin) && officerMin <= 3)
                t = level < RecruitRule.RECRUITER_LEVEL ? RecruitRule.Toggle.TooLow : RecruitRule.Toggle.Ok;
            else if (t == RecruitRule.Toggle.Ok && !isMonarch && (officerRank ?? 0) < Math.Max(1, officerMin))
                t = RecruitRule.Toggle.NotLeader;
            return t;
        }

        /// <summary>The monarch has not logged in for STALE_DAYS (an online monarch is never stale; no record at all is).</summary>
        public static bool Stale(DateTime? monarchLastLogin, bool monarchOnline, DateTime now) =>
            !monarchOnline && (monarchLastLogin == null || (now - monarchLastLogin.Value).TotalDays > STALE_DAYS);

        public static bool ActiveThisWeek(DateTime? lastLogin, bool online, DateTime now) =>
            online || (lastLogin != null && (now - lastLogin.Value).TotalDays <= ACTIVE_DAYS);

        // ------------------------------------------------------------------ the list, for one reader

        public sealed class Row
        {
            public uint Monarch; public string Name; public int Members, Active, LevelMin, LevelMax;
        }

        /// <summary>How well a level fits a range: 1 inside, 0.5 within 10 levels of it, 0.2 farther.</summary>
        public static double Fit(int level, int lo, int hi)
        {
            if (level >= lo && level <= hi) return 1.0;
            int d = level < lo ? lo - level : level - hi;
            return d <= 10 ? 0.5 : 0.2;
        }

        /// <summary>Active members weighted by fit: an allegiance that is busy and wants your level comes first.</summary>
        public static double Score(Row r, int level) => (r.Active + 1) * Fit(level, r.LevelMin, r.LevelMax);

        /// <summary>The order a reader of this level sees: score, then active, then size, then name.</summary>
        public static List<Row> Sort(IEnumerable<Row> rows, int level) =>
            (rows ?? Enumerable.Empty<Row>()).Where(r => r != null)
                .OrderByDescending(r => Score(r, level)).ThenByDescending(r => r.Active).ThenByDescending(r => r.Members)
                .ThenBy(r => r.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Monarch).ToList();

        // ------------------------------------------------------------------ the request

        public enum State { None, Pending, Accepted, Declined, Expired, Withdrawn, Sworn }
        public enum Event { Ask, Yes, No, Timeout, Withdraw, Swore, ListingGone }

        /// <summary>Pending and accepted requests are open: a player has at most one.</summary>
        public static bool Open(State s) => s == State.Pending || s == State.Accepted;

        /// <summary>The request's next state, or the same state when the event does not apply to it.</summary>
        public static State Next(State s, Event e)
        {
            switch (s)
            {
                case State.Pending:
                    return e switch
                    {
                        Event.Yes => State.Accepted, Event.No => State.Declined, Event.Timeout => State.Expired,
                        Event.Withdraw => State.Withdrawn, Event.ListingGone => State.Expired, Event.Swore => State.Withdrawn,
                        _ => s,
                    };
                case State.Accepted:
                    return e switch
                    {
                        Event.Swore => State.Sworn, Event.Timeout => State.Expired, Event.Withdraw => State.Withdrawn, _ => s,
                    };
                default:
                    return e == Event.Ask ? State.Pending : s;
            }
        }

        /// <summary>The state after the clock: pending past PENDING_HOURS from the ask, accepted past ACCEPTED_HOURS from
        /// the yes, become Expired.</summary>
        public static State Aged(State s, DateTime askedAt, DateTime? answeredAt, DateTime now)
        {
            if (s == State.Pending && (now - askedAt).TotalHours >= PENDING_HOURS) return Next(s, Event.Timeout);
            if (s == State.Accepted && (now - (answeredAt ?? askedAt)).TotalHours >= ACCEPTED_HOURS) return Next(s, Event.Timeout);
            return s;
        }

        public enum AskRefusal { None, AlreadyOpen, TooSoon, DailyCap, DeclinedRecently, Sworn, NotPlayer }

        /// <summary>May this player ask now? <paramref name="asksLastDay"/> counts asks in the last 24 hours;
        /// <paramref name="declinedByThisAt"/> is when this allegiance last said no to them.</summary>
        public static AskRefusal CanAsk(bool hasPatron, bool botOrStaff, bool hasOpen, DateTime? lastAsk, int asksLastDay,
                                        DateTime? declinedByThisAt, DateTime now)
        {
            if (botOrStaff) return AskRefusal.NotPlayer;
            if (hasPatron) return AskRefusal.Sworn;
            if (hasOpen) return AskRefusal.AlreadyOpen;
            if (lastAsk != null && (now - lastAsk.Value).TotalSeconds < ASK_GAP_S) return AskRefusal.TooSoon;
            if (asksLastDay >= ASKS_PER_DAY) return AskRefusal.DailyCap;
            if (declinedByThisAt != null && (now - declinedByThisAt.Value).TotalHours < DECLINED_WAIT_HOURS) return AskRefusal.DeclinedRecently;
            return AskRefusal.None;
        }

        /// <summary>"auto-accept below rank X": the asker's own allegiance rank (1 with no vassals) is under X.</summary>
        public static bool AutoAccepts(string join, int autoRank, int askerRank) => join == AUTO && askerRank < autoRank;

        // ------------------------------------------------------------------ the newcomer hint

        public static bool HintDue(int level, double ageDays, bool hasPatron, bool alreadyHinted, bool botOrStaff, int listings) =>
            !botOrStaff && !alreadyHinted && !hasPatron && level >= HINT_LEVEL && ageDays >= 0 && ageDays <= HINT_DAYS && listings > 0;

        // ------------------------------------------------------------------ lines

        public const string HINT = "Allegiances give XP and friends: find one in the Allegiance panel.";
        /// <summary>The same hint for a client without the panel (a retail client).</summary>
        public const string HINT_CHAT = "Allegiances give XP and friends: type /recruit to see who is recruiting.";

        public static string Question(string asker, int level, string allegiance) =>
            $"{asker} (level {level}) asks to join {allegiance} as your vassal. Accept?";

        public static string Refused(AskRefusal why, string allegiance) => why switch
        {
            AskRefusal.AlreadyOpen => "You already have a request open. Cancel it first to ask another allegiance.",
            AskRefusal.TooSoon => "Wait a minute before asking again.",
            AskRefusal.DailyCap => "That is enough asking for today. Try again tomorrow.",
            AskRefusal.DeclinedRecently => $"{RecruitRule.Cap(allegiance)} said no to you recently. Try another allegiance.",
            AskRefusal.Sworn => "You already have a patron. Break allegiance first to join another.",
            AskRefusal.NotPlayer => "This is for players only.",
            _ => "",
        };

        public static string ProblemLine(Problem p) => p switch
        {
            Problem.Pitch => $"Add a line about your allegiance, at most {RecruitRule.PITCH_MAX} characters.",
            Problem.Levels => $"Levels are a range from {LEVEL_MIN} to {LEVEL_MAX}, e.g. 10-60.",
            Problem.Style => "Say pk or pve.",
            Problem.Join => "Join is ask, or auto and a rank, e.g. auto 3.",
            Problem.Rank => $"Auto-accept takes a rank from {AUTO_RANK_MIN} to {AUTO_RANK_MAX}.",
            Problem.Hours => $"Hours: at most {RecruitRule.HOURS_MAX} characters.",
            _ => "",
        };

        public const string PITCH_REFUSED = "That line names someone it shouldn't. Write it another way.";

        public static string SentNow(string allegiance, int told) =>
            $"Request sent to {allegiance}: {told} {(told == 1 ? "recruiter is" : "recruiters are")} on and will answer.";
        public static string SentLater(string allegiance) =>
            $"Nobody from {allegiance} who can accept you is on. Your request waits {PENDING_HOURS} hours; they will be asked when they log in.";
        // Owner 2026-11-25: "players have to get together and do the swearing into the allegiance in person so the kneeling
        // animation plays". A Yes only matches the two players; the swear is retail's, side by side (ACE's walk-to and
        // distance), never at a distance. <where> is DeathLog's place words ("at 42.1N, 33.6E near Holtburg", "in a dungeon").
        const string HOW = "Stand next to them, select them and press Swear on the Allegiance panel.";

        public static string AcceptedLine(string patron, string where) =>
            $"{patron} accepted you. Meet {patron} in person to swear: {(string.IsNullOrEmpty(where) ? patron + " is not on right now" : patron + " is " + where)}. {HOW} This stays open {Hours(ACCEPTED_HOURS)}.";
        public static string AcceptedToRecruiter(string asker, string where) =>
            $"You accepted {asker}. Meet {asker} in person: they swear to you standing next to you. {MeetPlace(where)}";
        public static string AutoToRecruiter(string asker, string allegiance, string where) =>
            $"{asker} asked to join {allegiance}, and your listing accepts them. Meet {asker} in person: they swear to you standing next to you. {MeetPlace(where)}";
        static string Hours(int h) => h == 1 ? "1 hour" : h + " hours";
        /// <summary>The asker's place for the patron, or a line saying the asker is not on.</summary>
        static string MeetPlace(string askerWhere) => string.IsNullOrEmpty(askerWhere) ? "They are not on right now." : $"They are {askerWhere}.";
        /// <summary>The "@recruit where" and login reminder for the patron who accepted.</summary>
        public static string MeetReminderToPatron(string asker, string where) =>
            $"{asker} is coming to swear to you in person. {MeetPlace(where)}";
        public static string AcceptedExpiredLine(string patron) =>
            $"You did not meet {patron} to swear within {Hours(ACCEPTED_HOURS)}, so your request closed. You can ask again.";
        public static string DeclinedLine(string allegiance) => $"{RecruitRule.Cap(allegiance)} declined your request.";
        public static string AnsweredByOther(string asker, string who) => $"{who} already answered {asker}'s request.";
        public static string ExpiredLine(string allegiance) => $"Your request to {allegiance} ran out. You can ask again.";
        public const string WITHDRAWN = "Request cancelled.";

        /// <summary>No em or en dash, no Borg: checked over every fixed line by the nettest.</summary>
        public static IEnumerable<string> AllLines()
        {
            yield return HINT; yield return HINT_CHAT; yield return PITCH_REFUSED; yield return WITHDRAWN;
            yield return Question("Ann", 12, "the allegiance of Bo");
            foreach (AskRefusal a in Enum.GetValues(typeof(AskRefusal))) yield return Refused(a, "the allegiance of Bo");
            foreach (Problem p in Enum.GetValues(typeof(Problem))) yield return ProblemLine(p);
            yield return SentNow("X", 1); yield return SentNow("X", 2); yield return SentLater("X");
            yield return AcceptedLine("Bo", "at 12.3N, 41.2E near Holtburg"); yield return AcceptedLine("Bo", null);
            yield return AcceptedToRecruiter("Ann", "at 12.3N, 41.2E"); yield return AcceptedToRecruiter("Ann", null);
            yield return AutoToRecruiter("Ann", "X", "in a dungeon"); yield return DeclinedLine("X"); yield return AnsweredByOther("Ann", "Bo");
            yield return ExpiredLine("X"); yield return MeetReminderToPatron("Ann", "at 12.3N, 41.2E"); yield return AcceptedExpiredLine("Bo");
        }
    }
}
