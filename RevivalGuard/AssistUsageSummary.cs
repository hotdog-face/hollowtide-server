using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ACE.Server.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE ASSISTANT'S DAILY USAGE SUMMARY (owner 2026-10-31, "Build assistant usage"). The client's anonymous Assistant
    /// batches (Assets/Streaming/AcAssistUsage.cs) land in the event log as "assist" lines (Telemetry.Assist; an older mod
    /// logged them as "usage" lines whose u.kind is "assist", read here too). Once a day this adds them up into counts only,
    /// with no account hashes, character names or anything per player, and files the result where the tickets are, as
    /// ~/ace/reports/summaries/asum-&lt;yyMMdd&gt;.json (kind "assist_summary", status "closed"), so the laptop's report
    /// tools fetch it through the same read as the ticket digest (tools/reports/assistant_usage.py) and never read the
    /// event log itself. Staff and mercenary lines are left out.
    ///
    /// When: checked every 30 minutes from PlayerManager.Tick, and so right after each start (the nightly restart); a
    /// day is summarised once it has ended (UTC), for the last DAYS_BACK days that have an event file and no summary yet.
    /// The work runs off the world thread; any failure is logged and tried again next time.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class AssistUsageSummary
    {
        const int DAYS_BACK = 8;
        const double CHECK_MINUTES = 30;

        static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static readonly string EventsDir = Path.Combine(Home, "ace", "reports", "events");
        static readonly string ReportsRoot = Environment.GetEnvironmentVariable("REVIVAL_REPORTS_DIR") ?? Path.Combine(Home, "ace", "reports");
        static DateTime s_Next = DateTime.MinValue;
        static int s_Running;

        static void Postfix()
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now < s_Next) return;
                s_Next = now.AddMinutes(CHECK_MINUTES);
                if (System.Threading.Interlocked.Exchange(ref s_Running, 1) == 1) return;
                Task.Run(() =>
                {
                    try { CatchUp(DateTime.UtcNow); }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] assist summary: {e.Message}"); }
                    finally { System.Threading.Interlocked.Exchange(ref s_Running, 0); }
                });
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] assist summary: {e.Message}"); }
        }

        static void CatchUp(DateTime now)
        {
            string outDir = Path.Combine(ReportsRoot, "summaries");
            for (int back = 1; back <= DAYS_BACK; back++)
            {
                var day = now.Date.AddDays(-back);
                string events = Path.Combine(EventsDir, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                string id = "asum-" + day.ToString("yyMMdd", CultureInfo.InvariantCulture);
                string outFile = Path.Combine(outDir, id + ".json");
                if (!File.Exists(events) || File.Exists(outFile)) continue;
                var summary = AssistUsageRollup.Summarise(File.ReadLines(events), day);
                var rec = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["received_utc"] = now.ToString("o"),
                    ["kind"] = "assist_summary",
                    ["status"] = "closed",
                    ["account"] = "",
                    ["character"] = "",
                    ["level"] = 0,
                    ["report"] = summary,
                };
                Directory.CreateDirectory(outDir);
                string tmp = outFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, outFile, true);
                Mod.Log.Info($"[RevivalGuard] assist summary {id} filed");
            }
        }

    }

    /// <summary>The pure part of AssistUsageSummary: one day's event lines to counts. No ACE types (tools/modcheck calls it).</summary>
    public static class AssistUsageRollup
    {
        const int OVERNIGHT_S = 6 * 3600;

        /// <summary>One day's event lines to the summary JSON (counts only).</summary>
        public static string Summarise(IEnumerable<string> lines, DateTime day)
        {
            var players = new HashSet<string>();
            var vers = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var stops = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var tabs = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var ends = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var overnightEnds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var lengths = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["a_under_1h"] = 0, ["b_1_to_3h"] = 0, ["c_3_to_6h"] = 0, ["d_6h_plus"] = 0 };
            var feat = new SortedDictionary<string, long[]>(StringComparer.Ordinal);   // s, on, off, quick
            var featPlayers = new Dictionary<string, HashSet<string>>();
            var lastSettings = new Dictionary<string, JsonObject>();                  // the newest snapshot per account
            int messages = 0, pk = 0, runs = 0, overnight = 0, overnightSurvived = 0, overnightCutByRestart = 0, restartsSurvived = 0;
            long runSecs = 0;

            foreach (var line in lines)
            {
                if (line.IndexOf("assist", StringComparison.Ordinal) < 0) continue;   // cheap: most lines are "where"
                JsonNode n;
                try { n = JsonNode.Parse(line); } catch { continue; }
                if (n is not JsonObject o) continue;
                string ev = Str(o, "ev");
                JsonObject x = null;
                if (ev == "assist") x = o["x"] as JsonObject;
                else if (ev == "usage" && o["u"] is JsonObject u && Str(u, "kind") == "assist") x = u;
                if (x == null) continue;
                if (o.ContainsKey("staff") || o.ContainsKey("merc")) continue;
                string who = Str(o, "a") ?? "?";
                messages++; players.Add(who);
                Inc(vers, Str(x, "ver") ?? "?");
                if (x["feat"] is JsonObject fs)
                    foreach (var kv in fs)
                    {
                        if (kv.Value is not JsonObject f || !Room(feat, kv.Key)) continue;
                        if (!feat.TryGetValue(kv.Key, out var a)) feat[kv.Key] = a = new long[4];
                        a[0] += Num(f, "s"); a[1] += Num(f, "on"); a[2] += Num(f, "off"); a[3] += Num(f, "quick");
                        if (Num(f, "s") > 0 || Num(f, "on") > 0)
                        {
                            if (!featPlayers.TryGetValue(kv.Key, out var ps)) featPlayers[kv.Key] = ps = new HashSet<string>();
                            ps.Add(who);
                        }
                    }
                AddCounts(stops, x["stops"] as JsonObject);
                AddCounts(tabs, x["tabs"] as JsonObject);
                pk += (int)Num(x, "pk");
                if (x["runs"] is JsonArray rs)
                    foreach (var rn in rs)
                    {
                        if (rn is not JsonObject r) continue;
                        long s = Num(r, "s"); int lived = (int)Num(r, "rs"); string end = Str(r, "end") ?? "other";
                        runs++; runSecs += s; restartsSurvived += lived; Inc(ends, end);
                        Inc(lengths, s < 3600 ? "a_under_1h" : s < 3 * 3600 ? "b_1_to_3h" : s < OVERNIGHT_S ? "c_3_to_6h" : "d_6h_plus");
                        if (s >= OVERNIGHT_S)
                        {
                            overnight++; Inc(overnightEnds, end);
                            if (lived > 0) overnightSurvived++;
                            if (end == "server_restart") overnightCutByRestart++;
                        }
                    }
                if (x["set"] is JsonObject set) lastSettings[who] = set;
            }

            var settings = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
            foreach (var set in lastSettings.Values)
                foreach (var kv in set)
                {
                    if (!Room(settings, kv.Key)) continue;
                    if (!settings.TryGetValue(kv.Key, out var b)) settings[kv.Key] = b = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    Inc(b, kv.Value?.ToString() ?? "?");
                }

            var res = new JsonObject
            {
                ["day"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["players"] = players.Count,
                ["messages"] = messages,
                ["versions"] = Obj(vers),
                ["stops"] = Obj(stops),
                ["tabs"] = Obj(tabs),
                ["pk_alerts"] = pk,
                ["settings_snapshots"] = lastSettings.Count,
            };
            var fo = new JsonObject();
            foreach (var kv in feat)
                fo[kv.Key] = new JsonObject
                {
                    ["s"] = kv.Value[0], ["on"] = kv.Value[1], ["off"] = kv.Value[2], ["quick"] = kv.Value[3],
                    ["players"] = featPlayers.TryGetValue(kv.Key, out var ps) ? ps.Count : 0,
                };
            res["feat"] = fo;
            var so = new JsonObject();
            foreach (var kv in settings) so[kv.Key] = Obj(kv.Value);
            res["settings"] = so;
            res["runs"] = new JsonObject
            {
                ["n"] = runs, ["s"] = runSecs, ["lengths"] = Obj(lengths), ["ends"] = Obj(ends),
                ["restarts_survived"] = restartsSurvived,
                ["overnight"] = overnight, ["overnight_survived_restart"] = overnightSurvived,
                ["overnight_cut_by_restart"] = overnightCutByRestart, ["overnight_ends"] = Obj(overnightEnds),
            };
            return res.ToJsonString();
        }

        static string Str(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

        static long Num(JsonObject o, string k)
        {
            if (!o.TryGetPropertyValue(k, out var v) || v is not JsonValue jv) return 0;
            if (jv.TryGetValue<long>(out var l)) return Math.Max(0, l);
            if (jv.TryGetValue<int>(out var i)) return Math.Max(0, i);
            if (jv.TryGetValue<double>(out var d)) return Math.Max(0, (long)d);
            if (jv.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var el)) return Math.Max(0, el);
            return 0;
        }

        // a client writes the keys: a hostile one cannot grow the summary past MAX_KEYS names of MAX_KEY characters
        const int MAX_KEYS = 64, MAX_KEY = 40;
        static bool Room<T>(IDictionary<string, T> d, string k) => k != null && k.Length <= MAX_KEY && (d.ContainsKey(k) || d.Count < MAX_KEYS);

        static void Inc(SortedDictionary<string, int> d, string k) { if (!Room(d, k)) return; d.TryGetValue(k, out int n); d[k] = n + 1; }

        static void AddCounts(SortedDictionary<string, int> d, JsonObject o)
        {
            if (o == null) return;
            foreach (var kv in o)
            {
                if (kv.Value is not JsonValue jv) continue;
                long n = jv.TryGetValue<long>(out var l) ? l : jv.TryGetValue<int>(out var i) ? i
                       : jv.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var el) ? el : 0;
                if (n <= 0 || !Room(d, kv.Key)) continue;
                d.TryGetValue(kv.Key, out int c); d[kv.Key] = c + (int)Math.Min(n, 100000);
            }
        }

        static JsonObject Obj(SortedDictionary<string, int> d)
        {
            var o = new JsonObject();
            foreach (var kv in d) o[kv.Key] = kv.Value;
            return o;
        }
    }
}
