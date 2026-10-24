using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevivalGuard
{
    /// <summary>
    /// THE FEATURE REQUEST BOARD'S RULES, pure C# (no ACE types) so NetTest runs them (tools/nettest/FeatureBoard.cs).
    /// Owner 2026-10-22: "a feature request list button ... players write feature requests, the ones with the most
    /// upvotes rise ... pre-populate the list." The store, one vote per account, the daily posting limit, the order
    /// players see, the staff verbs and the wire forms both ways. FeatureBoard.cs is the ACE side (commands, file, push).
    /// </summary>
    public static class FeatureBoardRules
    {
        public const int TITLE_MIN = 4, TITLE_MAX = 60, TEXT_MAX = 200, POSTS_PER_DAY = 3;
        /// <summary>UTF-8 bytes of title + newline + text: base64 of this fits one chat message, one packet fragment
        /// (the ticket desk's 360-character chunks are the same budget).</summary>
        public const int MAX_BYTES = 270;
        public static readonly string[] STATUSES = { "open", "planned", "done", "declined" };

        public sealed class Request
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public string Text { get; set; } = "";
            public string Status { get; set; } = "open";
            public long Created { get; set; }
            public long Updated { get; set; }
            /// <summary>The account that wrote it (0 for the seed list). Never sent to players.</summary>
            public uint Author { get; set; }
            /// <summary>The character that wrote it, for staff (the report file). Never sent to players.</summary>
            public string AuthorName { get; set; } = "";
            /// <summary>Votes carried in from the seed list (players who asked before the board existed) or a merge.</summary>
            public int BaseVotes { get; set; }
            public List<uint> Voters { get; set; } = new List<uint>();
            /// <summary>Where it came from: a report id for a seeded request, empty for one posted on the board.</summary>
            public string Source { get; set; } = "";
        }

        public sealed class Store
        {
            public int NextId { get; set; } = 1;
            public List<Request> Items { get; set; } = new List<Request>();
            /// <summary>Unix times of each account's posts in the last day (the POSTS_PER_DAY limit survives a restart).</summary>
            public Dictionary<string, List<long>> Posts { get; set; } = new Dictionary<string, List<long>>();
        }

        public static int Votes(Request r) => Math.Max(0, r.BaseVotes) + (r.Voters?.Count ?? 0);

        /// <summary>Open and Planned share the top (votes decide), then Done, then Declined.</summary>
        public static int Group(string status) => status == "done" ? 1 : status == "declined" ? 2 : 0;

        public static bool IsStatus(string s) => Array.IndexOf(STATUSES, s) >= 0;

        /// <summary>The order players see: group, then most votes, then the older request first (it waited longer), then id.</summary>
        public static List<Request> Sorted(IEnumerable<Request> items) =>
            items.OrderBy(r => Group(r.Status)).ThenByDescending(Votes).ThenBy(r => r.Created).ThenBy(r => r.Id).ToList();

        public static Request Find(Store st, int id) => st.Items.FirstOrDefault(r => r.Id == id);

        // ---- votes ---------------------------------------------------------------------------------

        public enum VoteResult { Ok, Unchanged, NoSuchRequest, Closed }

        /// <summary>One vote per account: on adds this account once, off takes it away. Done and Declined are closed.</summary>
        public static VoteResult Vote(Store st, int id, uint account, bool on, long now)
        {
            var r = Find(st, id);
            if (r == null) return VoteResult.NoSuchRequest;
            if (Group(r.Status) != 0) return VoteResult.Closed;
            bool has = r.Voters.Contains(account);
            if (has == on) return VoteResult.Unchanged;
            if (on) r.Voters.Add(account); else r.Voters.RemoveAll(v => v == account);
            return VoteResult.Ok;
        }

        // ---- posting -------------------------------------------------------------------------------

        /// <summary>How many more this account may post in the rolling day (drops the older stamps).</summary>
        public static int PostsLeft(Store st, uint account, long now)
        {
            if (!st.Posts.TryGetValue(account.ToString(), out var list)) return POSTS_PER_DAY;
            list.RemoveAll(t => now - t >= 86400);
            return Math.Max(0, POSTS_PER_DAY - list.Count);
        }

        /// <summary>A new request, or null with the reason a player reads. Staff are not limited.</summary>
        public static Request Add(Store st, uint account, string author, string title, string text, long now, bool staff, out string why)
        {
            title = Clean(title, TITLE_MAX); text = Clean(text, TEXT_MAX);
            why = null;
            if (title.Length < TITLE_MIN) { why = $"Give it a title of at least {TITLE_MIN} letters."; return null; }
            if (!staff && PostsLeft(st, account, now) <= 0) { why = $"You can suggest {POSTS_PER_DAY} features a day. Try again tomorrow."; return null; }
            string key = Key(title);
            var same = st.Items.FirstOrDefault(r => Key(r.Title) == key);
            if (same != null)
            {
                why = Group(same.Status) == 2 ? $"That one was considered and declined (#{same.Id})."
                    : Group(same.Status) == 1 ? $"That one is already done (#{same.Id})."
                    : $"That is already on the list (#{same.Id}). Vote for it instead.";
                return null;
            }
            var req = new Request
            {
                Id = st.NextId++, Title = title, Text = text, Status = "open", Created = now, Updated = now,
                Author = account, AuthorName = author ?? "",
            };
            req.Voters.Add(account);   // you want what you asked for
            st.Items.Add(req);
            if (!staff)
            {
                string a = account.ToString();
                if (!st.Posts.TryGetValue(a, out var list)) st.Posts[a] = list = new List<long>();
                list.Add(now);
            }
            return req;
        }

        /// <summary>Two titles that differ only in case, spaces and punctuation are the same request.</summary>
        public static string Key(string title)
        {
            var sb = new StringBuilder();
            foreach (char c in title ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>One line of player text: no control characters, runs of spaces collapsed, long dashes made plain, capped.</summary>
        public static string Clean(string t, int max)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c0 in t ?? "")
            {
                char c = c0 == '—' || c0 == '–' ? '-' : c0;
                if (char.IsWhiteSpace(c) || c < ' ' || c == 127) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            var s = sb.ToString();
            return s.Length > max ? s.Substring(0, max).TrimEnd() : s;
        }

        // ---- staff ---------------------------------------------------------------------------------

        public static bool SetStatus(Store st, int id, string status, long now)
        {
            var r = Find(st, id);
            if (r == null || !IsStatus(status)) return false;
            r.Status = status; r.Updated = now;
            return true;
        }

        public static bool Delete(Store st, int id) => st.Items.RemoveAll(r => r.Id == id) > 0;

        /// <summary>Fold one request into another: its voters join (each account still once), its carried votes add,
        /// and it leaves the list.</summary>
        public static bool Merge(Store st, int from, int into, long now)
        {
            if (from == into) return false;
            var a = Find(st, from); var b = Find(st, into);
            if (a == null || b == null) return false;
            foreach (var v in a.Voters) if (!b.Voters.Contains(v)) b.Voters.Add(v);
            b.BaseVotes += Math.Max(0, a.BaseVotes);
            b.Updated = now;
            st.Items.Remove(a);
            return true;
        }

        public static bool Edit(Store st, int id, string title, string text, long now)
        {
            var r = Find(st, id);
            title = Clean(title, TITLE_MAX); text = Clean(text, TEXT_MAX);
            if (r == null || title.Length < TITLE_MIN) return false;
            r.Title = title; r.Text = text; r.Updated = now;
            return true;
        }

        // ---- drop files (owner 2026-10-24: "lets make it reflect a changelist ... so its not manual") ----

        /// <summary>One status update from the release pipeline (tools/features/mark.sh, ship.sh), a file in
        /// reports/features/updates/: {"id":14,"status":"done","why":"shipped in 0.1.80"}.</summary>
        public sealed class Drop
        {
            public int id { get; set; }
            public string status { get; set; } = "";
            public string why { get; set; } = "";
        }

        /// <summary>A drop applied like the staff verb, with two limits a script must not cross: a Declined request
        /// stays Declined (only staff reopen it), and "planned" never takes a Done request back. Returns null when it
        /// was applied (changed = whether the board changed, note = the audit line), else the reason it failed.
        /// Drops never delete or merge.</summary>
        public static string ApplyDrop(Store st, string json, long now, out bool changed, out string note)
        {
            changed = false; note = null;
            Drop d;
            try { d = System.Text.Json.JsonSerializer.Deserialize<Drop>(json ?? ""); }
            catch (Exception e) { return "not JSON: " + e.Message; }
            if (d == null) return "empty";
            string s = (d.status ?? "").Trim().ToLowerInvariant();
            if (!IsStatus(s)) return $"status \"{d.status}\" is not one of {string.Join("|", STATUSES)}";
            var r = Find(st, d.id);
            if (r == null) return $"there is no #{d.id}";
            string why = Clean(d.why, TEXT_MAX);
            string tail = why.Length > 0 ? " (" + why + ")" : "";
            if (r.Status == s) { note = $"#{r.Id} already {StatusWord(s)}{tail}"; return null; }
            if (r.Status == "declined") return $"#{r.Id} is Declined; only staff can reopen it";
            if (r.Status == "done" && s == "planned") { note = $"#{r.Id} kept Done (drop said Planned){tail}"; return null; }
            string was = r.Status;
            SetStatus(st, r.Id, s, now);
            changed = true;
            note = $"#{r.Id} {StatusWord(was)} -> {StatusWord(s)}{tail}";
            return null;
        }

        // ---- the seed ------------------------------------------------------------------------------

        public sealed class SeedItem
        {
            public string title { get; set; } = "";
            public string text { get; set; } = "";
            public string status { get; set; } = "open";
            public int votes { get; set; }
            public string source { get; set; } = "";
        }
        public sealed class SeedFile { public List<SeedItem> items { get; set; } = new List<SeedItem>(); }

        /// <summary>A new store from the seed file (server-mods/RevivalGuard/FeatureRequestsSeed.json, the list the owner
        /// edits): used once, when the shard has no board yet. Items that do not clean to a title are skipped.</summary>
        public static Store FromSeed(string json, long now)
        {
            var st = new Store();
            var seed = System.Text.Json.JsonSerializer.Deserialize<SeedFile>(json ?? "{}") ?? new SeedFile();
            foreach (var it in seed.items ?? new List<SeedItem>())
            {
                string title = Clean(it.title, TITLE_MAX);
                if (title.Length < TITLE_MIN || st.Items.Any(r => Key(r.Title) == Key(title))) continue;
                st.Items.Add(new Request
                {
                    Id = st.NextId++, Title = title, Text = Clean(it.text, TEXT_MAX),
                    Status = IsStatus(it.status) ? it.status : "open", BaseVotes = Math.Max(0, it.votes),
                    Created = now, Updated = now, Source = it.source ?? "",
                });
            }
            return st;
        }

        // ---- the wire --------------------------------------------------------------------------------

        /// <summary>The client's "@features add|edit" payload: base64url (no padding) of UTF-8 "title\ntext". Null when
        /// it does not decode or is over MAX_BYTES.</summary>
        public static (string title, string text)? DecodePost(string b64)
        {
            if (string.IsNullOrEmpty(b64) || b64.Length > (MAX_BYTES * 4 + 2) / 3 + 4) return null;
            try
            {
                string s = b64.Replace('-', '+').Replace('_', '/');
                s += new string('=', (4 - s.Length % 4) % 4);
                var bytes = Convert.FromBase64String(s);
                if (bytes.Length > MAX_BYTES) return null;
                string all = new UTF8Encoding(false, true).GetString(bytes);
                int nl = all.IndexOf('\n');
                return nl < 0 ? (all, "") : (all.Substring(0, nl), all.Substring(nl + 1));
            }
            catch { return null; }
        }

        /// <summary>The board for one viewer, as the mod channel's "features" payload (JSON):
        /// {"now","staff","left","say","items":[{"id","t","x","s","v","me","mine","at"}]} in display order.
        /// "me" = this account voted, "mine" = this account wrote it. Authors are never sent.</summary>
        public static string Json(Store st, uint viewer, bool staff, long now, string say = null)
        {
            var sb = new StringBuilder("{\"now\":").Append(now).Append(",\"staff\":").Append(staff ? 1 : 0)
                .Append(",\"left\":").Append(staff ? POSTS_PER_DAY : PostsLeft(st, viewer, now))
                .Append(",\"per\":").Append(POSTS_PER_DAY);
            if (!string.IsNullOrEmpty(say)) sb.Append(",\"say\":").Append(Q(say));
            sb.Append(",\"items\":[");
            int n = 0;
            foreach (var r in Sorted(st.Items))
            {
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(r.Id).Append(",\"t\":").Append(Q(r.Title)).Append(",\"x\":").Append(Q(r.Text))
                  .Append(",\"s\":").Append(Q(r.Status)).Append(",\"v\":").Append(Votes(r))
                  .Append(",\"me\":").Append(r.Voters.Contains(viewer) ? 1 : 0)
                  .Append(",\"mine\":").Append(r.Author != 0 && r.Author == viewer ? 1 : 0)
                  .Append(",\"at\":").Append(r.Created).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static string StatusWord(string s) =>
            s == "planned" ? "Planned" : s == "done" ? "Done" : s == "declined" ? "Declined" : "Open";

        static string Q(string v)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in v ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
