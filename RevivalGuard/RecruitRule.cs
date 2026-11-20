using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of Recruiting (no ACE types; tools/nettest compiles it by path, RecruitCase.cs): who may list an
    /// allegiance, who is told when a new player asks, when the offer is made, the pitch text checks, the measured
    /// "usually on" hours, and every line a player reads. Player text: short, plain, no em dashes, never the Borg,
    /// nothing pushy (owner 2026-11-17). Combined from two drafts on the owner's word (2026-11-18, "Combine both").
    /// </summary>
    public static class RecruitRule
    {
        /// <summary>The starter path's end (starter-path.json until_level): the offer comes when this level is reached.</summary>
        public const int OFFER_LEVEL = 10;
        /// <summary>Whoever lists an allegiance, and everyone told of a request, is at least this level.</summary>
        public const int RECRUITER_LEVEL = 20;
        /// <summary>Officers who may list: Seneschal (2) and up. Every officer is told of a request.</summary>
        public const int OFFICER_RANK = 2;
        public const int PITCH_MAX = 120, MEET_MAX = 60, HOURS_MAX = 40;
        /// <summary>Samples (one each 10 minutes) before the hours are worked out: about two days.</summary>
        public const int HOURS_MIN_SAMPLES = 288;
        public const int REQUEST_DAYS = 7, MAX_REQUESTS = 20;
        public const double JOIN_GAP_S = 60, JOIN_SAME_MIN = 30;

        public enum Toggle { Ok, NoAllegiance, NotLeader, TooLow, NotPlayer }

        /// <summary>Whether this character may list its allegiance or change the listing: an allegiance with at least one
        /// vassal, its monarch or an officer of Seneschal rank or higher, level RECRUITER_LEVEL+, a player (never a bot,
        /// merc or staff, and never in an allegiance whose monarch is one: <paramref name="botOrStaff"/> covers both).</summary>
        public static Toggle CanToggle(bool hasAllegiance, bool isMonarch, int? officerRank, int level, bool botOrStaff)
        {
            if (botOrStaff) return Toggle.NotPlayer;
            if (!hasAllegiance) return Toggle.NoAllegiance;
            if (!isMonarch && (officerRank ?? 0) < OFFICER_RANK) return Toggle.NotLeader;
            if (level < RECRUITER_LEVEL) return Toggle.TooLow;
            return Toggle.Ok;
        }

        /// <summary>The level-up that crosses the offer level, once: from below it to it or past it.</summary>
        public static bool OfferNow(int before, int now, int at) => before < at && now >= at;

        /// <summary>Who may be offered allegiances and ask to join: no patron, nobody sworn to them, a player.</summary>
        public static bool Seeker(bool hasPatron, bool hasVassals, bool botOrStaff) => !hasPatron && !hasVassals && !botOrStaff;

        public sealed class Member
        {
            public uint Guid; public bool Online, IsMonarch, BotOrStaff; public int OfficerRank, Level;
        }

        /// <summary>The members told of a request: the named contact first, then the monarch and the officers; online,
        /// level RECRUITER_LEVEL+, players only. Each once.</summary>
        public static List<uint> Contacts(IEnumerable<Member> members, uint contact)
        {
            var ok = (members ?? Enumerable.Empty<Member>())
                .Where(m => m != null && m.Online && !m.BotOrStaff && m.Level >= RECRUITER_LEVEL
                            && (m.Guid == contact || m.IsMonarch || m.OfficerRank > 0)).ToList();
            var list = new List<uint>();
            foreach (var m in ok.Where(m => m.Guid == contact).Concat(ok)) if (!list.Contains(m.Guid)) list.Add(m.Guid);
            return list;
        }

        // ------------------------------------------------------------------ text a leader types

        /// <summary>Plain printable ASCII, single spaces, trimmed; null when nothing is left or it is too long.</summary>
        public static string Clean(string s, int max)
        {
            if (s == null) return null;
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c0 in s)
            {
                char c = c0 == '—' || c0 == '–' ? '-' : c0 == '‘' || c0 == '’' ? '\'' : c0 == '“' || c0 == '”' ? '"' : c0;
                if (c == ' ' || c == '\t') { space = sb.Length > 0; continue; }
                if (c < 0x21 || c > 0x7E) continue;
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            var t = sb.ToString();
            return t.Length == 0 || t.Length > max ? null : t;
        }

        /// <summary>"6 PM to 11 PM Pacific" from per-hour sample counts and summed online members (Pacific hours 0..23);
        /// null when there are too few samples or nobody was ever seen on. The hours are those where at least one member
        /// was on on average, or (a small allegiance) at least half its busiest hour; the longest run wins, wrapping past
        /// midnight.</summary>
        public static string Hours(int[] samples, long[] online)
        {
            if (samples == null || online == null || samples.Length != 24 || online.Length != 24) return null;
            if (samples.Sum() < HOURS_MIN_SAMPLES) return null;
            var avg = new double[24];
            double peak = 0;
            for (int h = 0; h < 24; h++) { avg[h] = samples[h] > 0 ? (double)online[h] / samples[h] : 0; peak = Math.Max(peak, avg[h]); }
            if (peak <= 0.05) return null;
            double bar = peak >= 1.0 ? 1.0 : peak * 0.5;
            var on = new bool[24];
            for (int h = 0; h < 24; h++) on[h] = avg[h] >= bar;
            if (on.All(x => x)) return "at all hours";
            int bestStart = -1, bestLen = 0;
            for (int s = 0; s < 24; s++)
            {
                if (!on[s] || on[(s + 23) % 24]) continue;
                int len = 0;
                while (len < 24 && on[(s + len) % 24]) len++;
                if (len > bestLen) { bestLen = len; bestStart = s; }
            }
            if (bestStart < 0) return null;
            return $"{Clock(bestStart)} to {Clock((bestStart + bestLen) % 24)} Pacific";
        }

        /// <summary>First letter up, for a name that starts a sentence ("the allegiance of X" becomes "The allegiance of X").</summary>
        public static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>"6 PM", "12 AM", "12 PM".</summary>
        public static string Clock(int hour)
        {
            hour = ((hour % 24) + 24) % 24;
            int h12 = hour % 12 == 0 ? 12 : hour % 12;
            return h12 + (hour < 12 ? " AM" : " PM");
        }

        /// <summary>"just now", "12 minutes ago", "3 hours ago", "2 days ago".</summary>
        public static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 2) return "just now";
            if (t.TotalHours < 2) return (int)t.TotalMinutes + " minutes ago";
            if (t.TotalDays < 2) return (int)t.TotalHours + " hours ago";
            return (int)t.TotalDays + " days ago";
        }

        // ------------------------------------------------------------------ the list

        public sealed class Listing
        {
            public string Name;        // the allegiance's name, or "the allegiance of <monarch>"
            public string Monarch;
            public int Members, OnlineNow;
            public string Hours;       // the measured hours, the leader's own words, or null
            public string Pitch, Meet;
            public string Contact;     // who newcomers ask
            public bool ContactOnline;
        }

        /// <summary>One numbered entry, two lines: what it is, then how to reach it.</summary>
        public static string[] Entry(int n, Listing l)
        {
            var a = new StringBuilder();
            a.Append(n).Append(". ").Append(Cap(l.Name)).Append(": ").Append(l.Members).Append(l.Members == 1 ? " member" : " members");
            if (l.OnlineNow > 0) a.Append(", ").Append(l.OnlineNow).Append(" on now");
            a.Append(l.Hours != null ? ", usually on " + l.Hours : ", hours not known yet").Append('.');
            if (!string.IsNullOrEmpty(l.Pitch)) a.Append(" \"").Append(l.Pitch).Append('"');
            var b = new StringBuilder("   ");
            b.Append("Monarch ").Append(l.Monarch).Append('.');
            if (!string.IsNullOrEmpty(l.Meet)) b.Append(" Meet: ").Append(l.Meet).Append('.');
            b.Append(" Ask: ").Append(l.Contact).Append(l.ContactOnline ? " (on now)" : " (away)").Append(", or type /recruit join ").Append(n).Append('.');
            return new[] { a.ToString(), b.ToString() };
        }

        // ------------------------------------------------------------------ lines a new player reads

        public const string OFFER_HEAD = "You have finished your first steps. Many players join an allegiance next: a patron to ask for help, and an allegiance chat to talk in. These allegiances welcome new players:";
        public const string LIST_HEAD = "Allegiances that welcome new players:";
        public const string LIST_EMPTY = "No allegiance is on the list right now. You can still ask any player to be your patron, or ask in General chat.";
        public const string LIST_FOOT = "Swearing costs you no experience: your patron earns a little extra as you level, and you gain someone who can help. You can break allegiance any time. /recruit shows this list again.";
        public const string NOT_OPEN = "Allegiance recruiting is not open yet.";
        public const string ALREADY_SWORN = "You already have a patron or vassals. Break allegiance first to join another.";
        public const string WAIT_A_MINUTE = "Wait a minute before asking again.";

        public static string JoinUsage(int count) =>
            count == 0 ? LIST_EMPTY : $"Type /recruit join and a number from 1 to {count} (see /recruit).";

        public static string AlreadyAsked(string label) => $"{Cap(label)} already has your request.";

        /// <summary>What the new player reads after asking: who was told now, or that they will be told later.</summary>
        public static string Asked(string label, int told, string contact, string meet, string hours)
        {
            if (told > 0)
                return $"{Cap(label)} has your request: {told} of its members on now {(told == 1 ? "was" : "were")} told and should send you a tell soon. "
                       + $"You can also /tell {contact} yourself. When you meet, select your patron-to-be and press Swear on the Allegiance panel.";
            var s = $"Nobody from {label} is on right now. They will see your request when they log in.";
            if (!string.IsNullOrEmpty(hours)) s += $" They are usually on {hours}.";
            if (!string.IsNullOrEmpty(meet)) s += $" Meet: {meet}.";
            return s;
        }

        // ------------------------------------------------------------------ lines a recruiter reads

        /// <summary>To each one told, the moment a new player asks, or at their next login.</summary>
        public static string ToRecruiter(string newcomer, int level, string allegiance, string ago) =>
            $"{newcomer}, a new player (level {level}), would like to join {allegiance}{(string.IsNullOrEmpty(ago) || ago == "just now" ? "" : " (asked " + ago + ")")}. Send them a tell if you can take them on.";

        public static string Refused(Toggle why) => why switch
        {
            Toggle.NoAllegiance => "You need an allegiance with at least one vassal to list it.",
            Toggle.NotLeader => "Only the monarch, or an officer of Seneschal rank or higher, can do that.",
            Toggle.TooLow => $"You need to be level {RECRUITER_LEVEL} to take on new players.",
            Toggle.NotPlayer => "This is for player allegiances only.",
            _ => "",
        };

        public const string USAGE = "Usage: /recruit [join <n>]. Monarchs and officers: /recruit on <a line about you> | off | meet <place> | contact <name> | hours <text> | status";
        public static string NotListed(string label) => $"{Cap(label)} is not on the list. /recruit on <a line about your allegiance> lists it.";
        public static string NotListedYet(string label) => $"{Cap(label)} is not on the list yet: /recruit on <a line about your allegiance> first.";
        public static string Listed(string label, bool noMeet) =>
            $"{Cap(label)} is on the list. New players see it when they finish their first steps, and with /recruit. When one asks, your contact, monarch and officers who are on are told."
            + (noMeet ? " Add where to meet: /recruit meet <place>." : "");
        public static string Unlisted(string label) => $"{Cap(label)} is off the list.";
        public const string PITCH_NEEDED = "Add a line about your allegiance: /recruit on <why a new player might like it>";
        public static string PitchTooLong() => $"Keep it to one line of at most {PITCH_MAX} characters.";
        public const string PITCH_SET = "Line set.";
        public static string MeetBad() => $"Say where to meet in at most {MEET_MAX} characters, e.g. /recruit meet Holtburg lifestone.";
        public static string MeetSet(string t) => "Meeting place set: " + t;
        public const string CONTACT_BAD = "Name a member of your allegiance, level 20 or higher: /recruit contact <character name>.";
        public static string ContactSet(string name) => $"Requests from new players now go to {name} first.";
        public const string HOURS_AUTO = "Hours: measured from when your members are on.";
        public static string HoursBad() => $"At most {HOURS_MAX} characters, e.g. /recruit hours 6 PM to 11 PM Pacific.";
        public static string HoursSet(string t) => "Hours set: " + t;
        public static string Waiting(IReadOnlyCollection<string> lines) =>
            lines.Count == 0 ? "No requests waiting." : "Requests waiting: " + string.Join(", ", lines) + ".";

        /// <summary>No em or en dash, no Borg: the owner's text rules, checked over every fixed line by the nettest.</summary>
        public static bool TextOk(string s) => s != null && s.IndexOf('—') < 0 && s.IndexOf('–') < 0
                                               && s.IndexOf("borg", StringComparison.OrdinalIgnoreCase) < 0
                                               && s.IndexOf("collective", StringComparison.OrdinalIgnoreCase) < 0;
    }
}
