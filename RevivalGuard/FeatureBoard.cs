using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>
    /// THE FEATURE REQUEST BOARD (owner 2026-10-22: "a feature request list button that opens a window that allows
    /// players to write feature requests. then the ones with the most upvotes rise ... id want to pre-populate the
    /// list"). Ours. The rules are FeatureBoardRules (pure, tested in NetTest); this is the ACE side.
    ///
    ///     @features                         the board (our client: the mod channel "features"; retail: chat lines)
    ///     @features vote &lt;id&gt; on|off [n]     one vote per ACCOUNT; n is the client's counter, so a quick
    ///                                       on/off/on is never refused by ChatGuard as a repeated line
    ///     @features add &lt;base64url&gt;         a new request from our client: UTF-8 "title\ntext"
    ///     @features suggest &lt;title&gt; | &lt;text&gt;  the same, typed (a retail client)
    ///   staff (Sentinel and up), from the window's staff buttons:
    ///     @features status &lt;id&gt; open|planned|done|declined, delete &lt;id&gt;, merge &lt;from&gt; &lt;into&gt;, edit &lt;id&gt; &lt;base64url&gt;
    ///
    /// The board is one JSON file, ~/ace/reports/features/board.json (REVIVAL_REPORTS_DIR moves it with the reports),
    /// written whole after every change (tmp + move), so it survives restarts and mod deploys. A shard with no file
    /// starts from the seed list built into the dll (FeatureRequestsSeed.json). Each new request is also filed as a
    /// "feature" report beside the tickets (tools/reports/tickets.py --kind feature), where the owner reviews reports.
    /// Changes are pushed to every connection that looked at the board in the last WATCH_SECONDS.
    /// </summary>
    public static class FeatureBoard
    {
        const string CHANNEL = "features";
        const int WATCH_SECONDS = 15 * 60;

        static readonly object s_Lock = new object();
        static FeatureBoardRules.Store s_Store;
        sealed class Watch { public long At; }
        static readonly ConditionalWeakTable<Session, Watch> s_Watching = new ConditionalWeakTable<Session, Watch>();

        static readonly string Dir = Path.Combine(Environment.GetEnvironmentVariable("REVIVAL_REPORTS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports"), "features");
        static string FilePath => Path.Combine(Dir, "board.json");

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "features", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The feature request board: vote for what you want added.", "[vote <id> on|off | suggest <title> | <text>]");
            try { lock (s_Lock) Load(); Mod.Log.Info($"[RevivalGuard] feature board: {s_Store.Items.Count} requests ({FilePath})"); }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] feature board: " + e.Message); }
        }

        static bool Staff(Session s) => s.AccessLevel >= AccessLevel.Sentinel;
        static long Now() => (long)ACE.Common.Time.GetUnixTime();

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            var a = parameters ?? Array.Empty<string>();
            string verb = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            long now = Now();
            uint account = session.AccountId;
            bool staff = Staff(session);
            string say = null;
            bool changed = false;
            try
            {
                lock (s_Lock)
                {
                    Load();
                    var st = s_Store;
                    switch (verb)
                    {
                        case "":
                        case "list":
                        case "refresh":
                            break;
                        case "vote":
                        {
                            int id = a.Length > 1 && int.TryParse(a[1].TrimStart('#'), out var v) ? v : 0;
                            bool on = a.Length < 3 || a[2].ToLowerInvariant() != "off";
                            var res = FeatureBoardRules.Vote(st, id, account, on, now);
                            if (res == FeatureBoardRules.VoteResult.NoSuchRequest) say = "That request is no longer on the list.";
                            else if (res == FeatureBoardRules.VoteResult.Closed) say = "That request is closed to votes.";
                            changed = res == FeatureBoardRules.VoteResult.Ok;
                            break;
                        }
                        case "add":
                        case "suggest":
                        {
                            (string title, string text)? post;
                            if (verb == "add") post = FeatureBoardRules.DecodePost(a.Length > 1 ? a[1] : "");
                            else
                            {
                                string all = string.Join(" ", a.Skip(1));
                                int bar = all.IndexOf('|');
                                post = bar < 0 ? (all, "") : (all.Substring(0, bar), all.Substring(bar + 1));
                            }
                            if (post == null) { say = "That request could not be read. Please try again."; break; }
                            var req = FeatureBoardRules.Add(st, account, p.Name, post.Value.title, post.Value.text, now, staff, out var why);
                            if (req == null) { say = why; break; }
                            say = $"Thanks! \"{req.Title}\" is on the list as #{req.Id}, with your vote.";
                            changed = true;
                            Mod.Log.Info($"[RevivalGuard] feature #{req.Id} from {p.Name} ({session.Account}): {req.Title}");
                            try { TicketDesk.FileQuiet(session, p, "feature", $"Feature request #{req.Id}: {req.Title}\n\n{req.Text}"); }
                            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] feature report: " + e.Message); }
                            break;
                        }
                        case "status":
                        case "delete":
                        case "merge":
                        case "edit":
                        {
                            if (!staff) { say = "Only staff can do that."; break; }
                            int id = a.Length > 1 && int.TryParse(a[1].TrimStart('#'), out var v) ? v : 0;
                            if (verb == "status")
                            {
                                string s = a.Length > 2 ? a[2].ToLowerInvariant() : "";
                                changed = FeatureBoardRules.SetStatus(st, id, s, now);
                                say = changed ? $"#{id} is now {FeatureBoardRules.StatusWord(s)}." : "Usage: status <id> open|planned|done|declined";
                            }
                            else if (verb == "delete")
                            {
                                changed = FeatureBoardRules.Delete(st, id);
                                say = changed ? $"#{id} was removed." : $"There is no #{id}.";
                            }
                            else if (verb == "merge")
                            {
                                int into = a.Length > 2 && int.TryParse(a[2].TrimStart('#'), out var w) ? w : 0;
                                changed = FeatureBoardRules.Merge(st, id, into, now);
                                say = changed ? $"#{id} was merged into #{into}." : "Usage: merge <from id> <into id>";
                            }
                            else
                            {
                                var post = FeatureBoardRules.DecodePost(a.Length > 2 ? a[2] : "");
                                changed = post != null && FeatureBoardRules.Edit(st, id, post.Value.title, post.Value.text, now);
                                say = changed ? $"#{id} was edited." : "That edit could not be saved.";
                            }
                            if (changed) Mod.Log.Info($"[RevivalGuard] feature board: {p.Name} {verb} {string.Join(" ", a.Skip(1).Take(2))}");
                            break;
                        }
                        default:
                            say = "Usage: @features [vote <id> on|off | suggest <title> | <text>]";
                            break;
                    }
                    if (changed && !Save()) say = "The board could not be saved. Please tell the staff.";
                }
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] feature board: " + e.Message); say = "The board is unavailable right now."; }

            if (ModChannel.Capable(session))
            {
                s_Watching.Remove(session);
                s_Watching.Add(session, new Watch { At = now });
                Send(session, now, say);
            }
            else Chat(session, say, verb == "" || verb == "list" || verb == "refresh");
            if (changed) Push(session, now);
        }

        static void Send(Session s, long now, string say)
        {
            string json;
            lock (s_Lock)
            {
                if (s_Store == null) return;
                json = FeatureBoardRules.Json(s_Store, s.AccountId, Staff(s), now, say);
            }
            ModChannel.Send(s, CHANNEL, json);
        }

        /// <summary>The changed board to everyone else who has it open (or looked lately).</summary>
        static void Push(Session except, long now)
        {
            foreach (var o in PlayerManager.GetAllOnline())
            {
                var s = o?.Session;
                if (s == null || s == except || !ModChannel.Capable(s)) continue;
                if (!s_Watching.TryGetValue(s, out var w) || now - w.At > WATCH_SECONDS) continue;
                Send(s, now, null);
            }
        }

        /// <summary>A retail client's board: the top of the list as chat lines.</summary>
        static void Chat(Session s, string say, bool list)
        {
            if (!string.IsNullOrEmpty(say)) Say(s, say);
            if (!list) return;
            List<FeatureBoardRules.Request> items;
            lock (s_Lock) items = s_Store != null ? FeatureBoardRules.Sorted(s_Store.Items) : new List<FeatureBoardRules.Request>();
            Say(s, $"--- Feature requests: {items.Count} ---");
            foreach (var r in items.Take(15))
                Say(s, $"#{r.Id} [{FeatureBoardRules.StatusWord(r.Status)}] {FeatureBoardRules.Votes(r)} votes: {r.Title}");
            Say(s, "@features vote <number> to vote, @features suggest <title> | <text> to add one.");
        }

        static void Say(Session s, string line) =>
            s.Network?.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

        // ---- the file ---------------------------------------------------------------------------------

        static void Load()   // under s_Lock; once
        {
            if (s_Store != null) return;
            if (File.Exists(FilePath))
            {
                // An unreadable board must not be replaced by the seed: refuse changes until it is fixed by hand.
                s_Store = JsonSerializer.Deserialize<FeatureBoardRules.Store>(File.ReadAllText(FilePath))
                    ?? throw new InvalidDataException("empty board file");
                return;
            }
            string seed = "{}";
            using (var rs = typeof(FeatureBoard).Assembly.GetManifestResourceStream("RevivalGuard.FeatureRequestsSeed.json"))
                if (rs != null) using (var rd = new StreamReader(rs)) seed = rd.ReadToEnd();
            s_Store = FeatureBoardRules.FromSeed(seed, Now());
            Save();
        }

        static bool Save()   // under s_Lock
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_Store, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] feature board: could not write {FilePath}: {e.Message}"); return false; }
        }
    }
}
