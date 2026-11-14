using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of Leaderboards (no ACE types; tools/nettest compiles it by path, LeaderboardCase): who may be listed,
    /// the boards, ranking, the caller's own rank, and every line players or the website see. Ours, not retail.
    /// The Borg are listed as players (owner 2026-11-08: they count as players); nothing here marks them.
    /// </summary>
    public static class LeaderboardRule
    {
        /// <summary>One character's numbers, gathered by Leaderboards off the world thread.</summary>
        public sealed class Entry
        {
            public uint Id;
            public string Name;
            public string Account;
            public uint AccessLevel;      // the account's; above 0 is staff
            public bool MercAccount;      // an allowlisted hcmerc* account (Hollow Company or Borg)
            public bool Online;
            public double LastLogin;      // unix seconds
            public bool Deleted;          // deleted or pending deletion
            public long Level, Xp, Lum, Kills, PkKills, PkDeaths, Quests;
        }

        public sealed class Board
        {
            public string Key, Title, Unit;
            public string[] Aliases;
            public Func<Entry, long> Value;
        }

        // the order /top lists them in; Title is said in "Top 10 by <Title>"
        public static readonly Board[] Boards =
        {
            new Board { Key = "level",  Title = "level",              Unit = "",                Aliases = new[] { "lvl", "levels" },                 Value = e => e.Level },
            new Board { Key = "xp",     Title = "total experience",   Unit = " XP",             Aliases = new[] { "exp", "experience" },             Value = e => e.Xp },
            new Board { Key = "lum",    Title = "luminance",          Unit = " luminance",      Aliases = new[] { "luminance" },                     Value = e => e.Lum },
            new Board { Key = "kills",  Title = "creatures slain",    Unit = " kills",          Aliases = new[] { "kill", "creatures", "monsters" }, Value = e => e.Kills },
            new Board { Key = "pk",     Title = "player kills",       Unit = " player kills",   Aliases = new[] { "pkkills", "pvp" },                Value = e => e.PkKills },
            new Board { Key = "quests", Title = "quest milestones",   Unit = " quest milestones", Aliases = new[] { "quest", "q" },                  Value = e => e.Quests },
        };

        public static Board Find(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            word = word.Trim().ToLowerInvariant();
            return Boards.FirstOrDefault(b => b.Key == word || b.Aliases.Contains(word));
        }

        // ---- who is listed --------------------------------------------------------------------------------------

        /// <summary>Test and fleet accounts by prefix: the same list tools/reports/funnel.py and player_stats.py use,
        /// plus DnDefaultOn's loadbot. The owner's own accounts (viewer, an admin account) by exact name.</summary>
        public static readonly string[] TEST_ACCOUNT_PREFIXES =
            { "devbot", "harness", "loadbot", "playtest", "locfix", "fleetbot", "probe", "smoke", "soak", "newcomer", "kt" };
        public static readonly string[] OWNER_ACCOUNTS = { "viewer", "an admin account" };

        public static bool IsBorgName(string name) =>
            name != null && name.TrimEnd().EndsWith(" of Borg", StringComparison.OrdinalIgnoreCase);

        /// <summary>Why this character is not listed, or null when it is. For staff logs only: never shown to players.</summary>
        public static string Excluded(Entry e, double now, int activeDays, ISet<uint> hidden, ISet<string> extraNames)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Name)) return "no name";
            if (e.Deleted) return "deleted";
            if (e.AccessLevel > 0 || e.Name.StartsWith("+")) return "staff";
            string acct = (e.Account ?? "").Trim();
            if (acct.Length == 0) return "no account";
            if (OWNER_ACCOUNTS.Any(o => string.Equals(o, acct, StringComparison.OrdinalIgnoreCase))) return "owner";
            if (TEST_ACCOUNT_PREFIXES.Any(p => acct.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return "test account";
            // the hcmerc prefix is reserved (no player can make one), so every such account is the shard's: its "... of Borg"
            // characters are listed as players, the Hollow Company (and anything else on it) never
            bool shardAccount = e.MercAccount || acct.StartsWith("hcmerc", StringComparison.OrdinalIgnoreCase);
            if (shardAccount && !IsBorgName(e.Name)) return "mercenary";
            if (extraNames != null && (extraNames.Contains(acct) || extraNames.Contains(e.Name))) return "excluded by list";
            if (hidden != null && hidden.Contains(e.Id)) return "hidden";
            if (!e.Online && (now - e.LastLogin) > activeDays * 86400.0) return "inactive";
            return null;
        }

        /// <summary>A quest registry flag that is a player's own milestone: completed at least once, and not one of the
        /// flags the shard keeps for itself (RevivalGuard's, deliveries, kill-task counters still running).</summary>
        public static bool IsQuestMilestone(string flag, int timesCompleted)
        {
            if (timesCompleted < 1 || string.IsNullOrWhiteSpace(flag)) return false;
            string f = flag.Trim();
            foreach (var p in NOT_QUESTS)
                if (f.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        static readonly string[] NOT_QUESTS = { "RevivalGuard", "rgdelivery", "rg_", "rg-", "AnniversaryPackDoll", "LegendaryQuestCounter" };

        // ---- ranking --------------------------------------------------------------------------------------------

        public struct Ranked
        {
            public int Rank;
            public Entry Who;
            public long Value;
        }

        /// <summary>Everyone listed with a value above 0, best first; equal values share a rank (1, 2, 2, 4) and are
        /// ordered by name so the list is the same every time.</summary>
        public static List<Ranked> Rank(IEnumerable<Entry> listed, Board b)
        {
            var sorted = listed.Select(e => (e, v: b.Value(e))).Where(t => t.v > 0)
                .OrderByDescending(t => t.v).ThenBy(t => t.e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.e.Id).ToList();
            var outp = new List<Ranked>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                int rank = i > 0 && sorted[i].v == sorted[i - 1].v ? outp[i - 1].Rank : i + 1;
                outp.Add(new Ranked { Rank = rank, Who = sorted[i].e, Value = sorted[i].v });
            }
            return outp;
        }

        public static Ranked? RankOf(List<Ranked> ranked, uint id)
        {
            foreach (var r in ranked) if (r.Who.Id == id) return r;
            return null;
        }

        // ---- the lines ------------------------------------------------------------------------------------------

        public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        public static string Ago(double seconds)
        {
            if (seconds < 90) return "just now";
            int m = (int)Math.Round(seconds / 60);
            if (m < 90) return $"{m} minutes ago";
            int h = (int)Math.Round(seconds / 3600);
            return h == 1 ? "an hour ago" : $"{h} hours ago";
        }

        /// <summary>The PK board's line shows deaths beside kills when the shard counts them.</summary>
        public static string ValueText(Board b, Entry e, long v, string pkDeathsSince)
        {
            if (b.Key == "pk" && pkDeathsSince != null) return $"{N(v)} kills, {N(e.PkDeaths)} deaths";
            if (b.Key == "level") return $"level {N(v)}";
            return N(v) + b.Unit;
        }

        /// <summary>`/top &lt;board&gt;`: the top 10 and the caller's own place.</summary>
        public static List<string> BoardLines(Board b, List<Ranked> ranked, uint caller, string callerNote, double ageSeconds, int activeDays, string pkDeathsSince, int shown = 10)
        {
            var lines = new List<string> { $"Top {shown} by {b.Title} (characters active in the last {activeDays} days, updated {Ago(ageSeconds)}):" };
            if (ranked.Count == 0) lines.Add("  Nobody yet.");
            foreach (var r in ranked.Take(shown))
                lines.Add($"  {r.Rank}. {r.Who.Name}, {ValueText(b, r.Who, r.Value, pkDeathsSince)}");
            if (b.Key == "pk" && pkDeathsSince != null) lines.Add($"  Deaths are counted from {pkDeathsSince}.");
            lines.Add(YouLine(b, ranked, caller, callerNote, pkDeathsSince));
            return lines;
        }

        public static string YouLine(Board b, List<Ranked> ranked, uint caller, string callerNote, string pkDeathsSince)
        {
            if (callerNote != null) return callerNote;
            var me = RankOf(ranked, caller);
            if (me == null) return "You are not on this board yet.";
            return $"You: #{me.Value.Rank} of {ranked.Count}, {ValueText(b, me.Value.Who, me.Value.Value, pkDeathsSince)}.";
        }

        /// <summary>`/top`: each board's leader and the caller's place on it.</summary>
        public static List<string> SummaryLines(IReadOnlyDictionary<string, List<Ranked>> boards, uint caller, string callerNote, double ageSeconds, int activeDays, string pkDeathsSince)
        {
            var lines = new List<string> { $"Leaderboards (characters active in the last {activeDays} days, updated {Ago(ageSeconds)}):" };
            foreach (var b in Boards)
            {
                if (!boards.TryGetValue(b.Key, out var ranked)) continue;
                string lead = ranked.Count == 0 ? "nobody yet" : $"{ranked[0].Who.Name}, {ValueText(b, ranked[0].Who, ranked[0].Value, pkDeathsSince)}";
                string mine = "";
                if (callerNote == null)
                {
                    var me = RankOf(ranked, caller);
                    mine = me == null ? "" : $" (you: #{me.Value.Rank})";
                }
                lines.Add($"  {Cap(b.Title)}: {lead}{mine}");
            }
            if (callerNote != null) lines.Add(callerNote);
            lines.Add("/top level, /top xp, /top lum, /top kills, /top pk or /top quests for the top 10. /top hide leaves the boards, /top show comes back.");
            return lines;
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>The daily Discord post (off by default; the owner's yes turns it on): each board's top 3.</summary>
        public static string DiscordText(IReadOnlyDictionary<string, List<Ranked>> boards, int activeDays, string pkDeathsSince, int each = 3)
        {
            var sb = new StringBuilder();
            sb.Append("**Hollowtide leaderboards today** (characters active in the last ").Append(activeDays).Append(" days; /top in game)\n");
            foreach (var b in Boards)
            {
                if (!boards.TryGetValue(b.Key, out var ranked) || ranked.Count == 0) continue;
                sb.Append("**").Append(Cap(b.Title)).Append("**: ");
                sb.Append(string.Join(", ", ranked.Take(each).Select(r => $"{r.Rank}. {Escape(r.Who.Name)} ({ValueText(b, r.Who, r.Value, pkDeathsSince)})")));
                if (b.Key == "pk" && pkDeathsSince != null) sb.Append(" (deaths counted from ").Append(pkDeathsSince).Append(')');
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        static string Escape(string s) => (s ?? "").Replace("*", "\\*").Replace("_", "\\_").Replace("~", "\\~").Replace("`", "\\`").Replace("|", "\\|");

        /// <summary>The daily post is due once a UTC day, at or after the hour, and never twice in one day.</summary>
        public static bool PostDue(DateTime nowUtc, string lastPostedDay, int hourUtc) =>
            nowUtc.Hour >= Math.Clamp(hourUtc, 0, 23) && lastPostedDay != DayKey(nowUtc);

        public static string DayKey(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // ---- the website snapshot -------------------------------------------------------------------------------

        public sealed class SnapshotRow { public int rank { get; set; } public string name { get; set; } public long value { get; set; } public long? deaths { get; set; } }
        public sealed class SnapshotBoard { public string key { get; set; } public string title { get; set; } public int listed { get; set; } public List<SnapshotRow> top { get; set; } }
        public sealed class Snapshot { public string generated { get; set; } public int activeDays { get; set; } public string pkDeathsSince { get; set; } public int listed { get; set; } public List<SnapshotBoard> boards { get; set; } }

        /// <summary>What leaderboards.json holds: names and numbers only (no accounts, ids, or who is online).</summary>
        public static Snapshot ToSnapshot(IReadOnlyDictionary<string, List<Ranked>> boards, DateTime nowUtc, int activeDays, int listed, string pkDeathsSince, int shown = 10)
        {
            var s = new Snapshot { generated = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), activeDays = activeDays, pkDeathsSince = pkDeathsSince, listed = listed, boards = new List<SnapshotBoard>() };
            foreach (var b in Boards)
            {
                if (!boards.TryGetValue(b.Key, out var ranked)) continue;
                s.boards.Add(new SnapshotBoard
                {
                    key = b.Key, title = Cap(b.Title), listed = ranked.Count,
                    top = ranked.Take(shown).Select(r => new SnapshotRow
                    {
                        rank = r.Rank, name = r.Who.Name, value = r.Value,
                        deaths = b.Key == "pk" && pkDeathsSince != null ? r.Who.PkDeaths : (long?)null,
                    }).ToList(),
                });
            }
            return s;
        }
    }
}
