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
    /// <summary>Player reports and support tickets.</summary>
    static class TicketDesk
    {
        const string MAGIC = "RVT1";
        static readonly HashSet<string> KINDS = new HashSet<string> { "bug", "help", "player", "auto", "crash", "idea", "answer", "usage", "route" };
        const int MAX_CHUNKS = 48, MAX_CHUNK_CHARS = 600, MAX_PENDING = 2, MAX_PENDING_STAFF = 16;
        static bool Staff(Session s) => s != null && s.AccessLevel >= AccessLevel.Sentinel;
        const double PENDING_SECONDS = 180;
        const int PLAYER_PER_10MIN = 5, PLAYER_PER_DAY = 30;      // tickets a person writes
        const int AUTO_PER_SESSION = 5, AUTO_PER_DAY = 40;         // reports the game writes for them
        const int USAGE_PER_SESSION = 40;                          // AcUsage flushes: every 15 min and at logout
        const int ASSIST_PER_SESSION = 120;                        // AcAssistUsage batches: hourly at least, a stop within a minute
        const int SHARD_PER_DAY = 3000;                            // a ceiling on disk use for the whole shard
        // Shared routes: "Share this route" sends a walked route for the atlas.
        const int ROUTE_PER_SESSION = 3, ROUTE_PER_DAY = 10, ROUTE_SHARD_PER_DAY = 2000, ROUTE_MAX_POINTS = 2000, ROUTE_MAX_PARTS = 4;

        static readonly string Root = Environment.GetEnvironmentVariable("REVIVAL_REPORTS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports");

        sealed class Pending { public string Kind; public int Count; public string[] Parts; public DateTime Started; }
        sealed class SessionState { public readonly Dictionary<string, Pending> Open = new Dictionary<string, Pending>(); public int Auto, Usage, Assist; public readonly HashSet<string> Routes = new HashSet<string>(); }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Session, SessionState> s_Sessions =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Session, SessionState>();
        static readonly Dictionary<uint, List<(DateTime at, bool auto)>> s_ByAccount = new Dictionary<uint, List<(DateTime, bool)>>();
        static readonly object s_Lock = new object();
        static int s_TodayCount; static string s_Today;
        static readonly Dictionary<uint, List<(DateTime at, string id)>> s_RoutesByAccount = new Dictionary<uint, List<(DateTime, string)>>();
        static int s_RouteTodayCount; static string s_RouteToday;

        /// <summary>Handle one AbuseLogRequest.</summary>
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
                    // Staff (Sentinel and up) file reports in bursts while reviewing: no per-account caps for them; the shard's daily ceiling still applies.
                    if (st.Open.Count >= (Staff(session) ? MAX_PENDING_STAFF : MAX_PENDING)) { Offences.Add(player, "ticket", "too many unfinished reports; chunk dropped"); return; }
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
                        // The client's feature counters (Assets/Streaming/AcUsage.cs): not a ticket, a line in the player event log (Telemetry).
                        if (Telemetry.IsAssist(done) ? ++st.Assist > ASSIST_PER_SESSION : ++st.Usage > USAGE_PER_SESSION) return;
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
            if (kind == "route") { FileRoute(session, player, st, done); return; }
            // Answers to the milestone questions carry the question they answer
            if (kind == "answer") done = $"Question: {Feedback.LastQuestion(player) ?? "(none asked this session)"}\n\n{done}";
            if (kind == "idea" || kind == "answer") Telemetry.Emit(kind, player, ",\"text\":" + Telemetry.J(done.Length > 4000 ? done.Substring(0, 4000) : done));
            File(session, player, kind, done);
        }

        /// <summary>A report the player did not write as a ticket</summary>
        internal static void FileQuiet(Session session, Player player, string kind, string body) => File(session, player, kind, body, quiet: true);

        const int BOT_AUTO_PER_DAY = 60;
        static readonly Dictionary<uint, (string day, int n)> s_BotDay = new Dictionary<uint, (string, int)>();

        static bool FileBot(Session session, Player player, string kind, string body)
        {
            var now = DateTime.UtcNow;
            string day = now.ToString("yyMMdd");
            lock (s_Lock)
            {
                s_BotDay.TryGetValue(session.AccountId, out var c);
                if (c.day != day) c = (day, 0);
                if (c.n >= BOT_AUTO_PER_DAY) return true;
                s_BotDay[session.AccountId] = (day, c.n + 1);
            }
            var rec = new Dictionary<string, object>
            {
                ["received_utc"] = now.ToString("o"),
                ["kind"] = kind,
                ["account"] = session.Account,
                ["character"] = player.Name,
                ["level"] = player.Level ?? 0,
                ["server_location"] = player.Location?.ToLOCString() ?? "",
                ["server_busy"] = SafeBusy(player),
                ["report"] = body.Length > 60000 ? body.Substring(0, 60000) : body,
            };
            try
            {
                string dir = Path.Combine(Root, "bots", now.ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                string name = $"{now:yyMMdd-HHmmss}-{session.AccountId}-{kind}.json";
                System.IO.File.WriteAllText(Path.Combine(dir, name),
                    JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true }));
                Mod.Log.Info($"[RevivalGuard] bot report: {kind} from {player.Name} -> reports/bots/{now:yyyy-MM}/{name}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] bot report from {player.Name} not kept: {e.Message}"); }
            return true;
        }

        static void File(Session session, Player player, string kind, string body, bool quiet = false)
        {
            bool auto = kind == "auto" || kind == "crash";
            if (auto && Mercenaries.IsMerc(player) && FileBot(session, player, kind, body))
                return;
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
                if (!Staff(session) && (auto ? day >= AUTO_PER_DAY : (day >= PLAYER_PER_DAY || recent >= PLAYER_PER_10MIN)))
                {
                    if (!auto && !quiet) player.SendTransientError("You have sent several reports recently. Please wait a while before sending another.");
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
                ["server_busy"] = SafeBusy(player),
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
            if (!auto && !quiet)
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Thank you. Your report has been received as ticket {id}. Our team reviews every ticket.", ChatMessageType.Broadcast));
        }

        /// <summary>A shared route part: kept only when it is the JSON the client's Share button writes</summary>
        static void FileRoute(Session session, Player player, SessionState st, string body)
        {
            string share; int part, of;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                string pts = r.TryGetProperty("pts", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                share = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                part = r.TryGetProperty("part", out var k) && k.TryGetInt32(out var kk) ? kk : 1;
                of = r.TryGetProperty("of", out var o) && o.TryGetInt32(out var oo) ? oo : 1;
                int n = pts == null ? 0 : pts.Count(c => c == ';') + 1;
                if (!r.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || n < 2 || n > ROUTE_MAX_POINTS
                    || share == null || share.Length == 0 || share.Length > 16 || of < 1 || of > ROUTE_MAX_PARTS || part < 1 || part > of)
                { Offences.Add(player, "ticket", "shared route not in the route format; dropped"); return; }
            }
            catch (JsonException) { Offences.Add(player, "ticket", "shared route was not JSON; dropped"); return; }
            lock (st)
            {
                if (!st.Routes.Contains(share) && st.Routes.Count >= ROUTE_PER_SESSION)
                { Offences.Add(player, "ticket", "shared route over the session limit; dropped"); return; }
                st.Routes.Add(share);
            }

            uint account = session.AccountId;
            var now = DateTime.UtcNow;
            string id;
            lock (s_Lock)
            {
                if (!s_RoutesByAccount.TryGetValue(account, out var hist)) s_RoutesByAccount[account] = hist = new List<(DateTime, string)>();
                hist.RemoveAll(h => (now - h.at).TotalHours > 24);
                bool known = hist.Any(h => h.id == share);
                string today = now.ToString("yyMMdd");
                if (s_RouteToday != today) { s_RouteToday = today; s_RouteTodayCount = CountRoutesToday(now); }
                if (s_RouteTodayCount >= ROUTE_SHARD_PER_DAY) { Mod.Log.Warn($"[RevivalGuard] route: shard daily ceiling reached; route from {player.Name} dropped"); return; }
                if (!Staff(session) && !known && hist.Select(h => h.id).Distinct().Count() >= ROUTE_PER_DAY)
                {
                    player.SendTransientError("You have shared several routes today. Please share more tomorrow.");
                    Offences.Add(player, "ticket", "shared route over the daily limit; dropped");
                    return;
                }
                hist.Add((now, share));
                s_RouteTodayCount++;
                id = $"r{today}-{s_RouteTodayCount:D4}";
            }
            var rec = new Dictionary<string, object>
            {
                ["id"] = id,
                ["received_utc"] = now.ToString("o"),
                ["kind"] = "route",
                ["account_id"] = account,
                ["character"] = player.Name,
                ["character_id"] = $"0x{player.Guid.Full:X8}",
                ["level"] = player.Level ?? 0,
                ["server_location"] = player.Location?.ToLOCString() ?? "",
                ["report"] = body,
            };
            try
            {
                string dir = Path.Combine(Root, "routes", now.ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(Path.Combine(dir, id + ".json"),
                    JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] route: could not file {id}: {e.Message}"); return; }
            Mod.Log.Info($"[RevivalGuard] route {id} from {player.Name}: share {share} part {part} of {of}, {body.Length} chars");
            if (part == of)   // one thank-you per share, on its last part
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    "Thank you. Your route has been received. It helps our map learn the best walking paths.", ChatMessageType.Broadcast));
        }

        static int CountRoutesToday(DateTime now)
        {
            try
            {
                string dir = Path.Combine(Root, "routes", now.ToString("yyyy-MM"));
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "r" + now.ToString("yyMMdd") + "-*.json").Length : 0;
            }
            catch { return 0; }
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

        // the cast/use state and its recent history (BusyLedger): a stuck cast or drink report carries its evidence
        static string SafeBusy(Player p) { try { return BusyLedger.Describe(p); } catch (Exception e) { return "? " + e.GetType().Name; } }

        static string Trim(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "...";
    }
}
