using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// PLAYER REPORTS AND SUPPORT TICKETS. Owner, 2026-09-18: capture the bugs real players hit, and let
    /// players send support tickets for staff to review.
    ///
    /// The wire is retail's own AbuseLogRequest 0x0140 (String16L target, uint status, String16L
    /// complaint), which ACE lists but never handles. Retail's Report Abuse and Urgent Assistance
    /// buttons opened Turbine's support site instead; ours send this message, so the report arrives
    /// over the player's own authenticated session and no new port is opened.
    ///
    /// Our client (Assets/Streaming/AcTickets.cs) sends one report as numbered chunks, because one
    /// game message is one fragment and the bridge's strings are ASCII:
    ///     target    = "RVT1|&lt;client id&gt;|&lt;index&gt;|&lt;count&gt;|&lt;kind&gt;"
    ///     complaint = one slice of the report's UTF-8 text in base64
    /// Any other 0x0140 (a retail client's own abuse report) is filed as it arrives, kind "abuse".
    ///
    /// Each finished report is one JSON file under ~/ace/reports/&lt;yyyy-MM&gt;/&lt;id&gt;.json, with what
    /// the SERVER knows (account, character, level, location) beside what the client wrote, since
    /// the client's text is the player's word and the server's fields are not.
    /// tools/reports/tickets.py lists, shows and closes them.
    ///
    /// A report desk is also an attack surface (disk filling, log spam), so it is limited per account
    /// and per session, and anything malformed is dropped.
    /// </summary>
    static class TicketDesk
    {
        const string MAGIC = "RVT1";
        static readonly HashSet<string> KINDS = new HashSet<string> { "bug", "help", "player", "auto", "crash", "idea", "answer", "usage" };
        const int MAX_CHUNKS = 48, MAX_CHUNK_CHARS = 600, MAX_PENDING = 2;
        const double PENDING_SECONDS = 180;
        const int PLAYER_PER_10MIN = 5, PLAYER_PER_DAY = 30;      // tickets a person writes
        const int AUTO_PER_SESSION = 5, AUTO_PER_DAY = 40;         // reports the game writes for them
        const int USAGE_PER_SESSION = 40;                          // AcUsage flushes: every 15 min and at logout
        const int SHARD_PER_DAY = 3000;                            // a ceiling on disk use for the whole shard

        static readonly string Root = Environment.GetEnvironmentVariable("REVIVAL_REPORTS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports");

        sealed class Pending { public string Kind; public int Count; public string[] Parts; public DateTime Started; }
        sealed class SessionState { public readonly Dictionary<string, Pending> Open = new Dictionary<string, Pending>(); public int Auto, Usage; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Session, SessionState> s_Sessions =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Session, SessionState>();
        static readonly Dictionary<uint, List<(DateTime at, bool auto)>> s_ByAccount = new Dictionary<uint, List<(DateTime, bool)>>();
        static readonly object s_Lock = new object();
        static int s_TodayCount; static string s_Today;

        /// <summary>Handle one AbuseLogRequest. Always consumes it (ACE has no handler of its own).</summary>
        public static void Take(ClientMessage message, Session session)
        {
            var player = session?.Player;
            if (player == null) return;
            string target, complaint;
            try
            {
                target = message.Payload.ReadString16L();
                message.Payload.ReadUInt32();   // status: retail's, unused
                complaint = message.Payload.ReadString16L();
            }
            catch { Offences.Add(player, "ticket", "malformed AbuseLogRequest dropped"); return; }

            if (target == null || !target.StartsWith(MAGIC + "|"))
            {
                // A retail client's own report: file it whole.
                File(session, player, "abuse", $"Reported: {target}\n\n{complaint}");
                return;
            }

            var f = target.Split('|');
            if (f.Length != 5 || f[1].Length == 0 || f[1].Length > 16
                || !int.TryParse(f[2], out int index) || !int.TryParse(f[3], out int count)
                || count < 1 || count > MAX_CHUNKS || index < 0 || index >= count
                || !KINDS.Contains(f[4]) || complaint == null || complaint.Length > MAX_CHUNK_CHARS)
            {
                Offences.Add(player, "ticket", $"bad report chunk dropped: {Trim(target, 60)}");
                return;
            }

            string done = null, kind = f[4];
            var st = s_Sessions.GetOrCreateValue(session);
            lock (st)
            {
                var now = DateTime.UtcNow;
                foreach (var stale in st.Open.Where(p => (now - p.Value.Started).TotalSeconds > PENDING_SECONDS).Select(p => p.Key).ToList())
                    st.Open.Remove(stale);
                if (!st.Open.TryGetValue(f[1], out var p))
                {
                    if (st.Open.Count >= MAX_PENDING) { Offences.Add(player, "ticket", "too many unfinished reports; chunk dropped"); return; }
                    p = new Pending { Kind = kind, Count = count, Parts = new string[count], Started = now };
                    st.Open[f[1]] = p;
                }
                if (p.Count != count || p.Kind != kind) { st.Open.Remove(f[1]); Offences.Add(player, "ticket", "report chunks disagree; dropped"); return; }
                p.Parts[index] = complaint;
                if (p.Parts.All(x => x != null))
                {
                    st.Open.Remove(f[1]);
                    try { done = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(p.Parts))); }
                    catch { Offences.Add(player, "ticket", "report was not valid base64; dropped"); return; }
                    if (kind == "usage")
                    {
                        // The client's feature counters (Assets/Streaming/AcUsage.cs): not a ticket, a line in
                        // the player event log (Telemetry). Capped per session, silently.
                        if (++st.Usage > USAGE_PER_SESSION) return;
                        Telemetry.Usage(player, done);
                        return;
                    }
                    if (kind == "auto" || kind == "crash")
                    {
                        if (st.Auto >= AUTO_PER_SESSION) return;   // silently: the player did not write it
                        st.Auto++;
                    }
                }
            }
            if (done == null) return;
            // Answers to the milestone questions (Feedback) carry the question they answer; ideas and
            // answers also go into the event log beside what the player was doing.
            if (kind == "answer") done = $"Question: {Feedback.LastQuestion(player) ?? "(none asked this session)"}\n\n{done}";
            if (kind == "idea" || kind == "answer") Telemetry.Emit(kind, player, ",\"text\":" + Telemetry.J(done.Length > 4000 ? done.Substring(0, 4000) : done));
            File(session, player, kind, done);
        }

        static void File(Session session, Player player, string kind, string body)
        {
            bool auto = kind == "auto" || kind == "crash";
            uint account = session.AccountId;
            var now = DateTime.UtcNow;
            string id;
            lock (s_Lock)
            {
                if (!s_ByAccount.TryGetValue(account, out var hist)) s_ByAccount[account] = hist = new List<(DateTime, bool)>();
                hist.RemoveAll(h => (now - h.at).TotalHours > 24);
                int day = hist.Count(h => h.auto == auto);
                int recent = hist.Count(h => !h.auto && (now - h.at).TotalMinutes < 10);
                string today = now.ToString("yyMMdd");
                if (s_Today != today) { s_Today = today; s_TodayCount = CountToday(now); }
                if (s_TodayCount >= SHARD_PER_DAY) { Mod.Log.Warn($"[RevivalGuard] ticket: shard daily ceiling reached; {kind} from {player.Name} dropped"); return; }
                if (auto ? day >= AUTO_PER_DAY : (day >= PLAYER_PER_DAY || recent >= PLAYER_PER_10MIN))
                {
                    if (!auto) player.SendTransientError("You have sent several reports recently. Please wait a while before sending another.");
                    Offences.Add(player, "ticket", $"{kind} report over the limit; dropped");
                    return;
                }
                hist.Add((now, auto));
                s_TodayCount++;
                id = $"{today}-{s_TodayCount:D4}";
            }

            var rec = new Dictionary<string, object>
            {
                ["id"] = id,
                ["received_utc"] = now.ToString("o"),
                ["kind"] = kind,
                ["status"] = "open",
                ["account"] = session.Account,
                ["account_id"] = account,
                ["access_level"] = session.AccessLevel.ToString(),
                ["character"] = player.Name,
                ["character_id"] = $"0x{player.Guid.Full:X8}",
                ["level"] = player.Level ?? 0,
                ["server_location"] = player.Location?.ToLOCString() ?? "",
                ["report"] = body.Length > 60000 ? body.Substring(0, 60000) : body,
            };
            try
            {
                string dir = Path.Combine(Root, now.ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(Path.Combine(dir, id + ".json"),
                    JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ticket: could not file {id}: {e.Message}"); return; }

            Mod.Log.Info($"[RevivalGuard] ticket {id}: {kind} from {player.Name} ({session.Account}), {body.Length} chars");
            if (!auto)
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Thank you. Your report has been received as ticket {id}. Our team reviews every ticket.", ChatMessageType.Broadcast));
        }

        static int CountToday(DateTime now)
        {
            try
            {
                string dir = Path.Combine(Root, now.ToString("yyyy-MM"));
                return Directory.Exists(dir) ? Directory.GetFiles(dir, now.ToString("yyMMdd") + "-*.json").Length : 0;
            }
            catch { return 0; }
        }

        static string Trim(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "...";
    }
}
