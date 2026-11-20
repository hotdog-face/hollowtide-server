using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevivalGuard
{
    /// <summary>
    /// NewcomerWelcome's rule, pure (no ACE types), so tools/nettest compiles and checks it by path: who counts as a
    /// brand-new player, how arrivals are batched under the rate limit, and the words. No em dashes (player text).
    /// </summary>
    public static class NewcomerWelcomeRule
    {
        /// <summary>At most one welcome line per this many seconds; arrivals in between wait and share the next line.</summary>
        public const double GAP_S = 600;
        /// <summary>A newcomer must still be in the world this long after entering (bounces and relogs say nothing).</summary>
        public const double SETTLE_S = 30;
        /// <summary>An account older than this is not new, whatever its characters.</summary>
        public const double ACCOUNT_NEW_H = 24;
        /// <summary>Names in one line; more are counted ("and 3 others").</summary>
        public const int MAX_NAMES = 4;
        /// <summary>The in-game sender of the line on General (and its name on Discord).</summary>
        public const string SENDER = "Hollowtide";

        /// <summary>Why this login is not a brand-new player's first, from what the login itself shows (no database),
        /// or null when it may be one. Staff, the shard's own accounts (hcmerc*: Hollow Company and Borg), the dev and
        /// test fleet's accounts and the owner's are never welcomed.</summary>
        public static string NotNew(string accountName, uint accessLevel, string characterName, int totalLogins, bool firstEnterWorldDone)
        {
            string acct = (accountName ?? "").Trim();
            if (acct.Length == 0) return "no account";
            if (string.IsNullOrWhiteSpace(characterName)) return "no name";
            if (accessLevel > 0 || characterName.StartsWith("+")) return "staff";
            if (acct.StartsWith("hcmerc", StringComparison.OrdinalIgnoreCase)) return "merc/borg";   // every Borg is on one (BorgRule)
            if (LeaderboardRule.OWNER_ACCOUNTS.Any(o => string.Equals(o, acct, StringComparison.OrdinalIgnoreCase))) return "owner";
            if (LeaderboardRule.TEST_ACCOUNT_PREFIXES.Any(p => acct.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return "test account";
            // ACE counts the login before PlayerEnterWorld's postfix runs: 1 = this is the character's first time in
            if (totalLogins != 1 || firstEnterWorldDone) return "not the first login";
            return null;
        }

        /// <summary>Why this is not a new player, from the account's history (the database), or null when it is one.
        /// <paramref name="otherCharacters"/>: the account's other characters, deleted ones included (a second character,
        /// or a remake, is an existing player). <paramref name="knownFromAddress"/>: other accounts from the same address
        /// that already have a character in the world (another browser tab's guest, a lost-browser guest, a player's
        /// second account); -1 when the address cannot tell (local, a shared relay, unknown).</summary>
        public static string NotNewAccount(double accountAgeHours, int otherCharacters, int knownFromAddress)
        {
            if (accountAgeHours > ACCOUNT_NEW_H) return "account not new";
            if (otherCharacters > 0) return "account has other characters";
            if (knownFromAddress > 0) return "another account from the same address";
            return null;
        }

        /// <summary>The rate limit: when the next line may be said, given the last one (NaN or 0 = never said).</summary>
        public static double NextAt(double lastPostAt, double now) =>
            double.IsNaN(lastPostAt) || lastPostAt <= 0 ? now : Math.Max(now, lastPostAt + GAP_S);

        /// <summary>"Bob", "Bob and Alice", "Bob, Alice and Cara", "Bob, Alice, Cara, Dee and 2 others". Repeats dropped.</summary>
        public static string Names(IEnumerable<string> names)
        {
            var list = (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return null;
            if (list.Count == 1) return list[0];
            if (list.Count <= MAX_NAMES) return string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
            int more = list.Count - MAX_NAMES;
            return string.Join(", ", list.Take(MAX_NAMES)) + $" and {more} other{(more == 1 ? "" : "s")}";
        }

        public const int STYLES = 3;

        /// <summary>The line said on General, in wording <paramref name="style"/> (1-3, rg_newcomer_welcome_style; anything
        /// else is 1). Null when there is nobody to welcome.</summary>
        public static string Line(IEnumerable<string> names, long style)
        {
            var list = (names ?? Enumerable.Empty<string>()).ToList();
            string who = Names(list);
            if (who == null) return null;
            int count = list.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            bool many = count > 1;
            switch (style)
            {
                case 2: return $"{who} just stepped into Dereth for the first time. Give them a wave in General!";
                case 3: return many ? $"New faces in Dereth: {who}. Say hello and lend a hand if you can!"
                                    : $"A new face in Dereth: {who}. Say hello and lend a hand if you can!";
                default: return $"Welcome {who} to Dereth! Say hi in General.";
            }
        }

        /// <summary>The Discord post for the line (the bridge's own "**Name**: text" form). Markdown is escaped and "@"
        /// is dropped, so a name can never format the post or look like a mention (the bridge also posts with
        /// allowed_mentions none, so nothing ever pings).</summary>
        public static string Discord(string line) =>
            string.IsNullOrWhiteSpace(line) ? null : $"**{SENDER}**: {Escape(line.Replace("@", ""))}";

        public static string Escape(string s)
        {
            var sb = new StringBuilder((s ?? "").Length + 8);
            foreach (char c in s ?? "") { if ("*_~`|>\\".IndexOf(c) >= 0) sb.Append('\\'); sb.Append(c); }
            return sb.ToString();
        }
    }
}
