using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ACE.Common;
using ACE.Database.Models.Shard;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// CHARACTERS ON TREESTATS, SENT BY THE SERVER (owner 2026-11-06: "make it opt out only not opt in. we need our numbers
    /// to be higher to rise up the ranks"; amoeba's token uploads, treestats.net docs/direct-upload.md). Ours.
    /// docs/TREESTATS-UPLOADS.md.
    ///
    /// WHAT: the Decal plugin's JSON (github.com/amoeba/treestats Character.cs) for each online character, POSTed to
    /// https://treestats.net/characters with header X-TreeStats-Server-Token. Built here from ACE's own values, so it
    /// cannot be faked by a client and the token never leaves the server. Left out on purpose: monarch, patron and
    /// vassals (the site makes a page for every name in them, which would publish players who opted out),
    /// server_population (TreeStats polls hollowtide.org/population.json), location, account_name.
    ///
    /// WHO: everyone at Player level, on by default. Never: a character that opted out (PropertyBool 29160, saved on the
    /// character, so it holds on every device and before the client says anything), staff (above Player, or a "+"
    /// name), browser guests that were never kept (account "guest..."), Hollow Company mercenaries.
    ///
    /// WHEN: a minute after entering the world, then hourly while online, and on a level-up (no more than once a minute
    /// per character). At most 900 uploads an hour in all (the token allows 1,000).
    ///
    /// OPT-OUT: "/treestats off|on" for players on any client (our client turns an unknown slash command into "@treestats",
    /// so it works on every version, before 0.1.98's Options row), answered in chat. "@rgtreestats 0|1" is the silent form
    /// the Options > Character switch sends. Both set the same saved property. The answer
    /// goes on the mod channel as "treestats" {"on":bool}, also sent at every hello, which is how the client knows the
    /// feature is here and shows the switch. THE NOTICE: once per character, before its first upload, a system line
    /// (PropertyBool 29161 marks it said).
    ///
    /// CONFIG: treestats.json beside the mod turns it on: {"on": true} (optional "url", a test's mock; optional "tokenFile";
    /// optional "token" for a test, never the real one). The real token is read from ~/ace/secrets/treestats-token (OVH,
    /// mode 600, never in git, logs or the published source). No file, or "on" false: nothing is sent and the switch stays
    /// hidden, so placing the token alone never starts uploads (owner's go first).
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class TreeStatsUpload
    {
        const string FILE = "treestats.json";
        const string DEFAULT_URL = "https://treestats.net/characters";
        internal const PropertyBool OFF = (PropertyBool)29160, TOLD = (PropertyBool)29161;
        const double SCAN_SECONDS = 15, SETTLE_SECONDS = 60, HOURLY_MINUTES = 60, MIN_GAP_SECONDS = 60;
        const int PER_HOUR_CAP = 900, PER_SCAN = 4;
        public const string NOTICE =
            "Your character is shared on treestats.net (level, skills, titles). To turn it off, type /treestats off or use Options > Character.";

        sealed class Config
        {
            public bool on { get; set; }
            public string token { get; set; }
            public string tokenFile { get; set; }
            public string url { get; set; }
        }
        static readonly string DEFAULT_TOKEN_FILE =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "secrets", "treestats-token");
        sealed class Seen { public DateTime FirstSeen, LastSent; public int Level; public bool Sent; }

        static Config s_Cfg;
        static HttpClient s_Http;
        static DateTime s_NextScan, s_PausedUntil;
        static readonly Dictionary<uint, Seen> s_Seen = new Dictionary<uint, Seen>();
        static readonly Queue<DateTime> s_Hour = new Queue<DateTime>();
        static int s_Ok, s_Fail, s_InFlight, s_FailsToAlert;
        static DateTime s_LogAt, s_Started = DateTime.UtcNow, s_AlertAt, s_FailWindowStart;
        static string s_LastFail;
        /// <summary>A failure alert is an ERROR line (the bug alarm reads those), so never in the first minutes after a
        /// start: nightly-restart.sh rolls the mod back on any RevivalGuard ERROR right after the restart.</summary>
        const double ALERT_AFTER_UPTIME_MIN = 10, ALERT_EVERY_MIN = 15;

        internal static bool On => s_Cfg != null;

        internal static void Register()
        {
            CommandManager.TryAddCommand(Option, "rgtreestats", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Share this character on treestats.net: 1 = on (the default), 0 = off. No argument: say which.", "[1|0]");
            CommandManager.TryAddCommand(Typed, "treestats", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Share this character on treestats.net: /treestats off stops it, /treestats on starts it again.", "[on|off]");
            try
            {
                var path = Path.Combine(Folder(), FILE);
                if (!File.Exists(path)) { Mod.Log.Info($"[RevivalGuard] TreeStats: off (no {FILE})"); return; }
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
                if (cfg == null || !cfg.on) { Mod.Log.Info($"[RevivalGuard] TreeStats: off ({FILE} does not say \"on\": true)"); return; }
                if (string.IsNullOrWhiteSpace(cfg.token))
                {
                    string tf = string.IsNullOrWhiteSpace(cfg.tokenFile) ? DEFAULT_TOKEN_FILE : cfg.tokenFile;
                    cfg.token = File.Exists(tf) ? File.ReadAllText(tf).Trim() : null;
                }
                if (string.IsNullOrWhiteSpace(cfg.token)) { Mod.Log.Warn($"[RevivalGuard] TreeStats: off (no token)"); return; }
                if (string.IsNullOrWhiteSpace(cfg.url)) cfg.url = DEFAULT_URL;
                s_Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                s_Http.DefaultRequestHeaders.UserAgent.ParseAdd("Hollowtide-RevivalGuard/1");
                s_Cfg = cfg;
                Mod.Log.Info($"[RevivalGuard] TreeStats: ON, uploads to {new Uri(cfg.url).GetLeftPart(UriPartial.Path)} as "
                    + $"\"{ConfigManager.Config.Server.WorldName}\"");
            }
            catch (Exception e) { s_Cfg = null; Mod.Log.Error($"[RevivalGuard] TreeStats: off, {FILE} unreadable ({e.Message})"); }
        }

        static string Folder()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            return string.IsNullOrEmpty(folder) ? Path.Combine(ModManager.ModPath, "RevivalGuard") : folder;
        }

        // ---------------------------------------------------------------- the switch

        static bool Shares(Player p) => p.GetProperty(OFF) != true;

        /// <summary>"@rgtreestats 0|1": the Options switch's silent form (state back on the mod channel; chat only for a
        /// client without the channel).</summary>
        static void Option(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            string a = parameters != null && parameters.Length > 0 ? parameters[0] : "";
            if (a == "0" || a == "1") Set(p, a == "1");
            if (ModChannel.Capable(session)) Hello(session);
            else Say(session, p);
        }

        /// <summary>"/treestats on|off" typed by a player: always answered in chat, and the Options switch follows.</summary>
        static void Typed(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            string a = (parameters != null && parameters.Length > 0 ? parameters[0] : "").Trim().ToLowerInvariant();
            if (a == "off" || a == "0") Set(p, false);
            else if (a == "on" || a == "1") Set(p, true);
            else if (a.Length > 0) { session.Network.EnqueueSend(new GameMessageSystemChat("Type /treestats off or /treestats on.", ChatMessageType.Broadcast)); return; }
            Say(session, p);
            if (ModChannel.Capable(session)) Hello(session);
        }

        static void Set(Player p, bool on)
        {
            if (on) p.RemoveProperty(OFF); else p.SetProperty(OFF, true);
            p.CharacterChangesDetected = true;
            lock (s_Seen) if (s_Seen.TryGetValue(p.Guid.Full, out var s)) s.Sent = false;   // back on: send at the next scan
            Mod.Log.Info($"[RevivalGuard] TreeStats: {p.Name} turned sharing {(on ? "on" : "off")}");
        }

        static void Say(Session session, Player p) =>
            session.Network.EnqueueSend(new GameMessageSystemChat(
                !On ? (Shares(p) ? "Sharing on treestats.net has not started yet. Type /treestats off now to keep this character off it."
                                 : "Saved: this character will not be shared on treestats.net.")
                : Excluded(p) != null ? "This character is not shared on treestats.net."
                : Shares(p) ? "This character is shared on treestats.net. Type /treestats off to stop."
                : "This character is not shared on treestats.net. Type /treestats on to share it.", ChatMessageType.Broadcast));

        /// <summary>From ModChannel's hello: the switch's state, which is also how the client learns the feature is here.</summary>
        internal static void Hello(Session session)
        {
            var p = session?.Player;
            if (!On || p == null || Excluded(p) != null) return;
            ModChannel.Send(session, "treestats", "{\"on\":" + (Shares(p) ? "true" : "false") + "}");
        }

        // ---------------------------------------------------------------- who and when

        /// <summary>Why this character is never sent, or null.</summary>
        internal static string Excluded(Player p)
        {
            if (p.Account == null || p.Session == null) return "no session";
            if (p.Account.AccessLevel > (uint)AccessLevel.Player || p.Session.AccessLevel > AccessLevel.Player) return "staff";
            if ((p.Name ?? "").StartsWith("+")) return "staff";
            if ((p.Account.AccountName ?? "").StartsWith("guest", StringComparison.OrdinalIgnoreCase)) return "guest";
            if (Mercenaries.IsMerc(p)) return "mercenary";
            return null;
        }

        static void Postfix()
        {
            if (!On) return;
            try
            {
                var now = DateTime.UtcNow;
                if (now < s_NextScan) return;
                s_NextScan = now.AddSeconds(SCAN_SECONDS);
                while (s_Hour.Count > 0 && (now - s_Hour.Peek()).TotalHours >= 1) s_Hour.Dequeue();
                if (now >= s_LogAt) { s_LogAt = now.AddHours(1); if (s_Ok + s_Fail > 0) Mod.Log.Info($"[RevivalGuard] TreeStats: {s_Ok} sent, {s_Fail} failed since start"); }
                // FAILED UPLOADS ARE SURFACED (amoeba 2026-11-07: "let me know if you see any failed uploads"): one ERROR per
                // 15 minutes at most, with the count and the last refusal.
                int pending = Volatile.Read(ref s_FailsToAlert);
                if (pending > 0 && now >= s_AlertAt && (now - s_Started).TotalMinutes >= ALERT_AFTER_UPTIME_MIN)
                {
                    Interlocked.Add(ref s_FailsToAlert, -pending);
                    s_AlertAt = now.AddMinutes(ALERT_EVERY_MIN);
                    Mod.Log.Error($"[RevivalGuard] TreeStats ALERT: {pending} upload(s) failed since {s_FailWindowStart:HH:mm} UTC; last: {s_LastFail}");
                }
                if (now < s_PausedUntil) return;

                var online = PlayerManager.GetAllOnline();
                var here = new HashSet<uint>();
                int built = 0;
                foreach (var p in online)
                {
                    uint g = p.Guid.Full;
                    here.Add(g);
                    Seen s;
                    lock (s_Seen) if (!s_Seen.TryGetValue(g, out s)) s_Seen[g] = s = new Seen { FirstSeen = now, Level = p.Level ?? 0 };
                    if (built >= PER_SCAN || s_Hour.Count >= PER_HOUR_CAP) continue;
                    if ((now - s.FirstSeen).TotalSeconds < SETTLE_SECONDS || (now - s.LastSent).TotalSeconds < MIN_GAP_SECONDS) continue;
                    int level = p.Level ?? 0;
                    bool due = !s.Sent || (now - s.LastSent).TotalMinutes >= HOURLY_MINUTES || level > s.Level;
                    if (!due || !Shares(p) || Excluded(p) != null) continue;

                    string json = Build(p);
                    if (json == null) continue;
                    if (p.GetProperty(TOLD) != true)
                    {
                        p.SetProperty(TOLD, true);
                        p.CharacterChangesDetected = true;
                        p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(NOTICE, ChatMessageType.Broadcast));
                    }
                    s.Sent = true; s.LastSent = now; s.Level = level;
                    s_Hour.Enqueue(now);
                    built++;
                    Post(p.Name, json);
                }
                lock (s_Seen) foreach (var g in s_Seen.Keys.Where(k => !here.Contains(k)).ToList()) s_Seen.Remove(g);   // logged off
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TreeStats: {e.Message}"); }
        }

        static void Post(string name, string json)
        {
            var cfg = s_Cfg;
            Interlocked.Increment(ref s_InFlight);
            Task.Run(async () =>
            {
                try
                {
                    using var rq = new HttpRequestMessage(HttpMethod.Post, cfg.url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                    rq.Headers.Add("X-TreeStats-Server-Token", cfg.token);
                    using var rs = await s_Http.SendAsync(rq).ConfigureAwait(false);
                    int code = (int)rs.StatusCode;
                    string body = OneLine(await rs.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (code >= 200 && code < 300)
                    {
                        Interlocked.Increment(ref s_Ok);
                        Mod.Log.Info($"[RevivalGuard] TreeStats: {name} sent, {code} {body}");
                        return;
                    }
                    Failed($"{name} {code} {body}");
                    if (code == 429)
                    {
                        int wait = rs.Headers.TryGetValues("RateLimit-Reset", out var v) && int.TryParse(v.FirstOrDefault(), out var r) ? r : 600;
                        s_PausedUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(wait, 60, 3600));
                    }
                    else if (code == 401 || code == 403) s_PausedUntil = DateTime.UtcNow.AddHours(1);   // a bad token: try again in an hour, not every scan
                    Mod.Log.Warn($"[RevivalGuard] TreeStats FAILED: {name} refused, {code} {body}");
                }
                catch (Exception e)
                {
                    Failed($"{name} not sent ({OneLine(e.Message)})");
                    Mod.Log.Warn($"[RevivalGuard] TreeStats FAILED: {name} not sent ({OneLine(e.Message)})");
                }
                finally { Interlocked.Decrement(ref s_InFlight); }
            });
        }

        static void Failed(string what)
        {
            Interlocked.Increment(ref s_Fail);
            if (Interlocked.Increment(ref s_FailsToAlert) == 1) s_FailWindowStart = DateTime.UtcNow;
            s_LastFail = what;
        }

        /// <summary>The site's reply for a log line: one line, at most 200 characters (it never contains the token).</summary>
        static string OneLine(string s)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        // ---------------------------------------------------------------- the payload (the plugin's, a subset)

        /// <summary>The plugin's PropertyInt blacklist (Character.cs Init): never sent.</summary>
        static readonly HashSet<int> BLACKLIST = new HashSet<int>
        {
            2,5,7,10,17,19,20,24,25,26,28,30,33,35,36,38,43,45,86,87,88,89,90,91,92,98,105,106,107,108,109,110,111,
            113,114,115,117,125,129,131,134,158,159,160,166,170,171,172,174,175,176,177,178,179,188,193,270,271,272,293,
        };

        /// <summary>TreeStats' race names (treestats.net RaceHelper), by HeritageGroup.</summary>
        static string Race(int h) => h switch
        {
            1 => "Aluvian", 2 => "Gharu'ndim", 3 => "Sho", 4 => "Viamontian", 5 => "Shadowbound",
            6 => "Gearknight", 7 => "Tumerok", 8 => "Lugian", 9 => "Empyrean", 10 => "Penumbraen",
            11 => "Undead", 12 => "Olthoi", 13 => "OlthoiAcid", _ => null,
        };

        static readonly (PropertyAttribute Id, string Name)[] ATTRS =
        {
            (PropertyAttribute.Strength, "Strength"), (PropertyAttribute.Endurance, "Endurance"),
            (PropertyAttribute.Quickness, "Quickness"), (PropertyAttribute.Coordination, "Coordination"),
            (PropertyAttribute.Focus, "Focus"), (PropertyAttribute.Self, "Self"),
        };

        static readonly (PropertyAttribute2nd Id, string Name)[] VITALS =
        {
            (PropertyAttribute2nd.MaxHealth, "Health"), (PropertyAttribute2nd.MaxStamina, "Stamina"), (PropertyAttribute2nd.MaxMana, "Mana"),
        };

        /// <summary>The upload, or null if the character is not ready (no name, no heritage). World thread only.</summary>
        internal static string Build(Player p)
        {
            string race = Race((int)p.HeritageGroup);
            int gender = p.Gender ?? 0;
            if (string.IsNullOrEmpty(p.Name) || race == null || (gender != 1 && gender != 2)) return null;

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("version", "2");
                w.WriteString("name", p.Name);
                w.WriteString("race", race);
                w.WriteString("gender", gender == 1 ? "Male" : "Female");
                w.WriteNumber("level", p.Level ?? 1);
                var allegName = p.Allegiance?.AllegianceName;
                if (!string.IsNullOrEmpty(allegName)) w.WriteString("allegiance_name", allegName);
                w.WriteNumber("rank", p.AllegianceRank ?? 0);
                w.WriteNumber("followers", p.AllegianceNode?.TotalFollowers ?? 0);
                w.WriteString("server", ConfigManager.Config.Server.WorldName);
                w.WriteNumber("deaths", p.NumDeaths);
                if ((p.CreationTimestamp ?? 0) > 0) w.WriteString("birth", Birth(p.CreationTimestamp.Value));
                w.WriteNumber("total_xp", p.TotalExperience ?? 0);
                w.WriteNumber("unassigned_xp", p.AvailableExperience ?? 0);
                w.WriteNumber("skill_credits", p.AvailableSkillCredits ?? 0);
                if (p.AvailableLuminance.HasValue) w.WriteNumber("luminance_earned", p.AvailableLuminance.Value);
                if (p.MaximumLuminance.HasValue) w.WriteNumber("luminance_total", p.MaximumLuminance.Value);

                w.WriteStartObject("attribs");
                foreach (var (id, name) in ATTRS)
                {
                    if (!p.Attributes.TryGetValue(id, out var a)) continue;
                    w.WriteStartObject(name.ToLowerInvariant());
                    w.WriteString("name", name); w.WriteNumber("base", a.Base); w.WriteNumber("creation", a.StartingValue); w.WriteNumber("buffed", a.Current);
                    w.WriteEndObject();
                }
                w.WriteEndObject();

                w.WriteStartObject("vitals");
                foreach (var (id, name) in VITALS)
                {
                    if (!p.Vitals.TryGetValue(id, out var v)) continue;
                    w.WriteStartObject(name.ToLowerInvariant());
                    w.WriteString("name", name); w.WriteNumber("creation", v.StartingValue); w.WriteNumber("base", v.Base); w.WriteNumber("buffed", v.MaxValue);
                    w.WriteEndObject();
                }
                w.WriteEndObject();

                // The plugin names skills from the portal SkillTable, lower case with underscores ("melee_defense").
                var table = DatManager.PortalDat?.SkillTable?.SkillBaseHash;
                w.WriteStartObject("skills");
                foreach (var kv in p.Skills.OrderBy(k => (int)k.Key))
                {
                    if (table == null || !table.TryGetValue((uint)kv.Key, out var sb) || string.IsNullOrEmpty(sb.Name)) continue;
                    string key = sb.Name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
                    w.WriteStartObject(key);
                    w.WriteString("name", key); w.WriteNumber("base", kv.Value.Base); w.WriteNumber("buffed", kv.Value.Current); w.WriteString("training", Training(kv.Value.AdvancementClass));
                    w.WriteEndObject();
                }
                w.WriteEndObject();

                var titles = p.Character?.GetTitles(p.CharacterDatabaseLock);
                if (titles != null && titles.Count > 0)
                {
                    int current = p.CharacterTitleId ?? (int)titles[0].TitleId;
                    w.WriteNumber("current_title", current);
                    w.WriteStartArray("titles");
                    foreach (var t in titles) w.WriteNumberValue(t.TitleId);
                    w.WriteEndArray();
                }

                w.WriteStartObject("properties");
                foreach (var kv in p.GetAllPropertyInt().OrderBy(k => (int)k.Key))
                {
                    int id = (int)kv.Key;
                    if (id >= 1000 || BLACKLIST.Contains(id)) continue;   // ours (29xxx) and the plugin's blacklist never go
                    w.WriteNumber(id.ToString(CultureInfo.InvariantCulture), kv.Value);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>Decal's eTrainingType names without "eTrain", as the plugin sends them.</summary>
        static string Training(SkillAdvancementClass c) => c switch
        {
            SkillAdvancementClass.Untrained => "Untrained", SkillAdvancementClass.Trained => "Trained",
            SkillAdvancementClass.Specialized => "Specialized", _ => "Unusable",
        };

        /// <summary>The birth string as treestats.net's direct-upload example ("01/15/2000 08:30:00 PM"), parsed there as EST.</summary>
        static string Birth(int unix) =>
            DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(TimeSpan.FromHours(-5))
                .ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.GetCultureInfo("en-US"));
    }
}
