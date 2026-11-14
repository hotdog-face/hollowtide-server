using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Microsoft.EntityFrameworkCore;
using R = RevivalGuard.LeaderboardRule;

namespace RevivalGuard
{
    /// <summary>
    /// LEADERBOARDS (ours, not retail; owner's pick 2026-11-14). `/top` shows each board's leader and your place;
    /// `/top level|xp|lum|kills|pk|quests` the top 10 and your rank; `/top hide` / `/top show` leave or rejoin the boards.
    /// Who is listed, the ranking and every line are in LeaderboardRule (pure, tested by nettest LeaderboardCase): staff,
    /// test and fleet accounts, the owner's accounts and the Hollow Company are left out; the Borg are listed as players.
    /// Only characters that logged in within rg_top_active_days (30) are listed.
    ///
    /// NEVER ON THE WORLD THREAD: a timer thread rebuilds the boards every rg_top_refresh_minutes (15) from PlayerManager
    /// (every character's properties are in memory) plus one query for logins and deletions and one for quest flags; the
    /// command only reads the last finished build. Each build also writes leaderboards.json beside the dll (names and
    /// numbers only) for a future website page.
    ///
    /// PK deaths: ACE counts PK kills (PlayerKillsPk) but no PK deaths, so this counts them from the day it is deployed
    /// (top-pkdeaths.json beside the dll), and the PK board says from when.
    ///
    /// DAILY DISCORD POST: ON by default (rg_top_discord; owner 2026-11-14: "Turn it on"). When on, once a day at
    /// or after rg_top_discord_hour_utc (17 = 9 or 10 AM Pacific) each board's top 3 goes to the Discord bridge's General
    /// channel (the bot posts only to #general).
    ///
    /// Switches: rg_top (on), rg_top_refresh_minutes (15, 5..120), rg_top_active_days (30), rg_top_json (on), rg_top_discord
    /// (ON), rg_top_discord_hour_utc (17). Extra names to leave out: top-exclude.txt beside the dll (account or character
    /// names, one per line). Staff: `@top refresh` rebuilds now, `@top why <name>` says why a character is not listed.
    /// </summary>
    static class Leaderboards
    {
        internal const string P_ON = "rg_top", P_REFRESH = "rg_top_refresh_minutes", P_DAYS = "rg_top_active_days",
            P_JSON = "rg_top_json", P_DISCORD = "rg_top_discord", P_HOUR = "rg_top_discord_hour_utc";
        const string HIDDEN_FILE = "top-hidden.txt", EXCLUDE_FILE = "top-exclude.txt", DEATHS_FILE = "top-pkdeaths.json",
            STATE_FILE = "top-state.txt", JSON_FILE = "leaderboards.json";

        sealed class Cache
        {
            public DateTime At;
            public int Days;
            public string PkSince;
            public Dictionary<string, List<R.Ranked>> Boards;
            public Dictionary<uint, string> Why;     // every character not listed, and why (staff only)
        }

        sealed class Deaths { public string Since { get; set; } public Dictionary<uint, int> Count { get; set; } = new Dictionary<uint, int>(); }

        static volatile Cache s_Cache;
        static readonly object s_Lock = new object();          // s_Hidden, s_Deaths
        static HashSet<uint> s_Hidden = new HashSet<uint>();
        static Deaths s_Deaths;
        static bool s_DeathsDirty;
        static int s_Busy;
        static DateTime s_NextBuild = DateTime.MinValue;
        static Timer s_Timer;

        static bool On => PropertyManager.GetBool(P_ON, true).Item;
        static int Days => (int)Math.Clamp(PropertyManager.GetLong(P_DAYS, 30).Item, 1, 3650);
        static int RefreshMinutes => (int)Math.Clamp(PropertyManager.GetLong(P_REFRESH, 15).Item, 5, 120);

        internal static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard Leaderboards: /top and the leaderboards.json snapshot");
                if (!b.ContainsKey(P_JSON)) b[P_JSON] = new Property<bool>(true, "RevivalGuard Leaderboards: write leaderboards.json beside the dll at each rebuild");
                if (!b.ContainsKey(P_DISCORD)) b[P_DISCORD] = new Property<bool>(true, "RevivalGuard Leaderboards: a daily post of each board's top 3 to the Discord bridge's General channel (needs the owner's yes)");
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_REFRESH)) l[P_REFRESH] = new Property<long>(15, "RevivalGuard Leaderboards: minutes between rebuilds (5..120)");
                if (!l.ContainsKey(P_DAYS)) l[P_DAYS] = new Property<long>(30, "RevivalGuard Leaderboards: only characters that logged in within this many days are listed");
                if (!l.ContainsKey(P_HOUR)) l[P_HOUR] = new Property<long>(17, "RevivalGuard Leaderboards: the daily Discord post goes at or after this UTC hour");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: switches not registered ({e.Message}); still read at their defaults"); }

            LoadHidden();
            LoadDeaths();
            CommandManager.TryAddCommand(Handle, "top", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The leaderboards: each board's leader and your place, or a board's top 10.", "[level | xp | lum | kills | pk | quests | hide | show]");
            // first build two minutes in (the world is still loading before that), then a check every minute
            s_Timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));
            Mod.Log.Info($"[RevivalGuard] Leaderboards: /top, rebuilt every {RefreshMinutes} min off the world thread; Discord post {(PropertyManager.GetBool(P_DISCORD, true).Item ? "ON" : "off")}");
        }

        // ------------------------------------------------------------------ the command

        static void Say(Session s, string line)
        {
            if (s == null) Console.WriteLine(line);
            else s.Network?.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        static void Handle(Session session, params string[] args)
        {
            try
            {
                var p = session?.Player;
                string word = args != null && args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
                bool staff = session == null || session.AccessLevel >= AccessLevel.Sentinel;
                if (!On && !staff) { Say(session, "The leaderboards are closed for now."); return; }

                if (staff && word == "refresh")
                {
                    Say(session, "Rebuilding the leaderboards now.");
                    ThreadPool.QueueUserWorkItem(_ => { Build(); Say(session, Status()); });
                    return;
                }
                if (staff && word == "status") { Say(session, Status()); return; }
                if (staff && word == "why")
                {
                    string name = string.Join(' ', args.Skip(1)).Trim();
                    var who = PlayerManager.FindByName(name);
                    var c = s_Cache;
                    if (who == null) Say(session, $"No character named {name}.");
                    else if (c == null) Say(session, "Not built yet.");
                    else Say(session, c.Why.TryGetValue(who.Guid.Full, out var why) ? $"{who.Name}: not listed ({why})." : $"{who.Name}: listed.");
                    return;
                }
                if (p == null && word is "hide" or "show") return;

                if (word == "hide" || word == "show")
                {
                    bool hide = word == "hide";
                    bool changed;
                    lock (s_Lock) { changed = hide ? s_Hidden.Add(p.Guid.Full) : s_Hidden.Remove(p.Guid.Full); if (changed) SaveHidden(); }
                    Say(session, hide ? "You leave the leaderboards at the next update, within a minute or two. /top show puts you back."
                                      : "You are back on the leaderboards from the next update, within a minute or two.");
                    if (changed) Mod.Log.Info($"[RevivalGuard] Leaderboards: {p.Name} {(hide ? "hidden" : "shown")}");
                    if (changed) Soon();
                    return;
                }

                var cache = s_Cache;
                if (cache == null)
                {
                    Say(session, "The leaderboards are still being counted. Try again in a minute.");
                    Soon();
                    return;
                }

                string note = p == null ? "(the server console has no place of its own)" : CallerNote(p);
                uint me = p?.Guid.Full ?? 0;
                double age = (DateTime.UtcNow - cache.At).TotalSeconds;
                List<string> lines;
                if (word == "")
                    lines = R.SummaryLines(cache.Boards, me, note, age, cache.Days, cache.PkSince);
                else
                {
                    var board = R.Find(word);
                    if (board == null || !cache.Boards.TryGetValue(board.Key, out var ranked))
                    {
                        Say(session, "Boards: level, xp, lum, kills, pk, quests. /top hide leaves the boards, /top show comes back.");
                        return;
                    }
                    lines = R.BoardLines(board, ranked, me, note, age, cache.Days, cache.PkSince);
                }
                foreach (var line in lines) Say(session, line);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Leaderboards: /top: {e}"); }
        }

        /// <summary>Why the caller is not on the boards, in words a player may see, or null when they are (or will be).</summary>
        static string CallerNote(Player p)
        {
            bool hidden;
            lock (s_Lock) hidden = s_Hidden.Contains(p.Guid.Full);
            if (hidden) return "You are hidden from the leaderboards. /top show puts you back.";
            var e = new R.Entry { Id = p.Guid.Full, Name = p.Name, Account = p.Account?.AccountName, AccessLevel = p.Account?.AccessLevel ?? 0,
                                  MercAccount = Mercenaries.IsMerc(p), Online = true };
            string why = R.Excluded(e, 0, Days, null, Extra());
            if (why == null) return null;
            return why == "staff" ? "Staff characters are not on the leaderboards." : "This character is not on the leaderboards.";
        }

        static string Status()
        {
            var c = s_Cache;
            if (c == null) return "[Leaderboards] not built yet.";
            int listed = c.Boards.Values.SelectMany(r => r).Select(r => r.Who.Id).Distinct().Count();
            var why = c.Why.Values.GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
            return $"[Leaderboards] built {R.Ago((DateTime.UtcNow - c.At).TotalSeconds)}: {listed} on a board; left out: {string.Join(", ", why)}. "
                + $"Every {RefreshMinutes} min, active {c.Days} days, Discord post {(PropertyManager.GetBool(P_DISCORD, true).Item ? "ON" : "off")}.";
        }

        /// <summary>A hide/show or an early ask brings the next build forward to the next minute's tick (so never more than
        /// one build a minute, however often it is asked).</summary>
        static void Soon()
        {
            var now = DateTime.UtcNow;
            if (s_NextBuild > now) s_NextBuild = now;
        }

        // ------------------------------------------------------------------ the build (timer thread)

        static void Tick()
        {
            try
            {
                if (!On) return;
                var now = DateTime.UtcNow;
                if (now >= s_NextBuild)
                {
                    s_NextBuild = now.AddMinutes(RefreshMinutes);
                    Build();
                }
                MaybePost(now);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Leaderboards: tick: {e.Message}"); }
        }

        static void Build()
        {
            if (Interlocked.Exchange(ref s_Busy, 1) == 1) return;
            try
            {
                var sw = Stopwatch.StartNew();
                var now = DateTime.UtcNow;
                double unixNow = (now - DateTime.UnixEpoch).TotalSeconds;
                int days = Days;

                // logins and deletions, and quest flags: one query each, here on the timer thread
                var chars = new Dictionary<uint, (bool gone, double last)>();
                var quests = new Dictionary<uint, long>();
                using (var ctx = new ShardDbContext())
                {
                    foreach (var c in ctx.Character.AsNoTracking().Select(c => new { c.Id, c.IsDeleted, c.DeleteTime, c.LastLoginTimestamp }).ToList())
                        chars[c.Id] = (c.IsDeleted || c.DeleteTime > 0, c.LastLoginTimestamp);
                    double since = unixNow - days * 86400.0;
                    var active = chars.Where(kv => !kv.Value.gone && kv.Value.last >= since).Select(kv => kv.Key).ToHashSet();
                    foreach (var op in PlayerManager.GetAllOnline()) active.Add(op.Guid.Full);
                    foreach (var q in ctx.CharacterPropertiesQuestRegistry.AsNoTracking().Where(q => q.NumTimesCompleted > 0)
                                 .Select(q => new { q.CharacterId, q.QuestName, q.NumTimesCompleted }).ToList())
                        if (active.Contains(q.CharacterId) && R.IsQuestMilestone(q.QuestName, q.NumTimesCompleted))
                            quests[q.CharacterId] = (quests.TryGetValue(q.CharacterId, out var n) ? n : 0) + 1;
                }

                HashSet<uint> hidden;
                string pkSince;
                lock (s_Lock) { hidden = new HashSet<uint>(s_Hidden); pkSince = s_Deaths.Since; }
                var extra = Extra();

                var listed = new List<R.Entry>();
                var why = new Dictionary<uint, string>();
                foreach (var ip in PlayerManager.GetAllPlayers())
                {
                    try
                    {
                        uint id = ip.Guid.Full;
                        bool online = ip is Player;
                        chars.TryGetValue(id, out var ch);
                        var e = new R.Entry
                        {
                            Id = id, Name = ip.Name, Account = ip.Account?.AccountName, AccessLevel = ip.Account?.AccessLevel ?? 0,
                            MercAccount = Mercenaries.IsMercAccount(ip.Account?.AccountName), Online = online,
                            LastLogin = ch.last, Deleted = ch.gone,
                        };
                        string w = R.Excluded(e, unixNow, days, hidden, extra);
                        if (w != null) { why[id] = w; continue; }
                        e.Level = ip.GetProperty(PropertyInt.Level) ?? 0;
                        e.Xp = ip.GetProperty(PropertyInt64.TotalExperience) ?? 0;
                        e.Lum = ip.GetProperty(PropertyInt64.AvailableLuminance) ?? 0;
                        e.Kills = ip.GetProperty(PropertyInt.CreatureKills) ?? 0;
                        e.PkKills = ip.GetProperty(PropertyInt.PlayerKillsPk) ?? 0;
                        lock (s_Lock) e.PkDeaths = s_Deaths.Count.TryGetValue(id, out var d) ? d : 0;
                        e.Quests = quests.TryGetValue(id, out var qn) ? qn : 0;
                        listed.Add(e);
                    }
                    catch (Exception ex) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: skipped 0x{ip?.Guid.Full:X8}: {ex.Message}"); }
                }

                var boards = R.Boards.ToDictionary(b => b.Key, b => R.Rank(listed, b));
                s_Cache = new Cache { At = now, Days = days, PkSince = pkSince, Boards = boards, Why = why };

                if (PropertyManager.GetBool(P_JSON, true).Item)
                {
                    var snap = R.ToSnapshot(boards, now, days, listed.Count, pkSince);
                    WriteAtomic(Path.Combine(ModFolder.Path(), JSON_FILE), JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
                }
                SaveDeathsIfDirty();
                Mod.Log.Info($"[RevivalGuard] Leaderboards: built in {sw.ElapsedMilliseconds} ms, {listed.Count} listed of {listed.Count + why.Count} characters");
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Leaderboards: build failed: {e}"); }
            finally { Interlocked.Exchange(ref s_Busy, 0); }
        }

        // ------------------------------------------------------------------ the daily Discord post (off by default)

        static void MaybePost(DateTime now)
        {
            if (!PropertyManager.GetBool(P_DISCORD, true).Item || !DiscordBridge.On) return;
            var c = s_Cache;
            if (c == null) return;
            string last = ReadState();
            if (!R.PostDue(now, last, (int)PropertyManager.GetLong(P_HOUR, 17).Item)) return;
            WriteState(R.DayKey(now));   // first, so a failed post is never repeated all day
            if (DiscordBridge.PostGeneral(R.DiscordText(c.Boards, c.Days, c.PkSince)))
                Mod.Log.Info("[RevivalGuard] Leaderboards: daily post queued for the Discord bridge");
        }

        static string ReadState()
        {
            try { var f = Path.Combine(ModFolder.Path(), STATE_FILE); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; }
            catch { return null; }
        }

        static void WriteState(string day)
        {
            try { WriteAtomic(Path.Combine(ModFolder.Path(), STATE_FILE), day + "\n"); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: {STATE_FILE}: {e.Message}"); }
        }

        // ------------------------------------------------------------------ PK deaths (ACE counts none)

        [HarmonyPatch(typeof(Player), nameof(Player.HandlePKDeathBroadcast))]
        static class OnPkDeath
        {
            static void Postfix(Player __instance, DamageHistoryInfo topDamager)
            {
                try
                {
                    // the same test ACE uses before it adds to the killer's PlayerKillsPk
                    if (topDamager == null || !topDamager.IsPlayer || !(topDamager.TryGetAttacker() is Player) || !__instance.IsPKDeath(topDamager)) return;
                    lock (s_Lock)
                    {
                        if (s_Deaths == null) return;
                        uint id = __instance.Guid.Full;
                        s_Deaths.Count[id] = (s_Deaths.Count.TryGetValue(id, out var n) ? n : 0) + 1;
                        s_DeathsDirty = true;
                    }
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Leaderboards: PK death of {__instance?.Name}: {e.Message}"); }
            }
        }

        static void LoadDeaths()
        {
            lock (s_Lock)
            {
                try
                {
                    var f = Path.Combine(ModFolder.Path(), DEATHS_FILE);
                    if (File.Exists(f)) s_Deaths = JsonSerializer.Deserialize<Deaths>(File.ReadAllText(f));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: {DEATHS_FILE} unreadable ({e.Message}); starting it again"); }
                if (s_Deaths == null || s_Deaths.Count == null)
                {
                    s_Deaths = new Deaths { Since = DateTime.UtcNow.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) };
                    s_DeathsDirty = true;
                }
            }
        }

        static void SaveDeathsIfDirty()
        {
            string text;
            lock (s_Lock)
            {
                if (!s_DeathsDirty) return;
                text = JsonSerializer.Serialize(s_Deaths);
                s_DeathsDirty = false;
            }
            try { WriteAtomic(Path.Combine(ModFolder.Path(), DEATHS_FILE), text); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: {DEATHS_FILE}: {e.Message}"); lock (s_Lock) s_DeathsDirty = true; }
        }

        // ------------------------------------------------------------------ files

        static void LoadHidden()
        {
            lock (s_Lock)
            {
                try
                {
                    var f = Path.Combine(ModFolder.Path(), HIDDEN_FILE);
                    if (!File.Exists(f)) return;
                    foreach (var raw in File.ReadAllLines(f))
                    {
                        var t = raw.Split('#')[0].Trim();
                        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                        if (uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var id)) s_Hidden.Add(id);
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: {HIDDEN_FILE}: {e.Message}"); }
            }
        }

        // under s_Lock
        static void SaveHidden()
        {
            try
            {
                var lines = s_Hidden.OrderBy(i => i).Select(i => $"0x{i:X8} # {PlayerManager.FindByGuid(i)?.Name}");
                WriteAtomic(Path.Combine(ModFolder.Path(), HIDDEN_FILE), string.Join("\n", lines) + "\n");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: {HIDDEN_FILE}: {e.Message}"); }
        }

        static HashSet<string> s_Extra;
        static DateTime s_ExtraAt;
        /// <summary>top-exclude.txt beside the dll, re-read at most once a minute.</summary>
        static HashSet<string> Extra()
        {
            var now = DateTime.UtcNow;
            if (s_Extra != null && now - s_ExtraAt < TimeSpan.FromMinutes(1)) return s_Extra;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var f = Path.Combine(ModFolder.Path(), EXCLUDE_FILE);
                if (File.Exists(f))
                    foreach (var raw in File.ReadAllLines(f))
                    {
                        var t = raw.Split('#')[0].Trim();
                        if (t.Length > 0) set.Add(t);
                    }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Leaderboards: {EXCLUDE_FILE}: {e.Message}"); }
            s_ExtraAt = now;
            return s_Extra = set;
        }

        static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
    }
}
