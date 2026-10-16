using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Handlers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE DISCORD BRIDGE (docs/CHAT-IMPROVEMENTS-01.md piece 6, ours): the in-game General channel and one Discord
    /// channel, mirrored both ways, so friends can talk to the game from their phone.
    ///
    ///   Out: every line said on General (TurbineChatHandler.LogTurbineChat runs once per line actually sent) is
    ///        posted to the Discord channel as "**Name**: text". Mercenaries' lines never leave.
    ///   In:  the bot reads the channel every few seconds; each new message from a person (not a bot, not ours)
    ///        is spoken on General in game as "Name (Discord)", to everyone who listens to General.
    ///
    /// One Discord bot does both (REST only, no gateway): POST and GET /channels/{id}/messages with "Bot <token>".
    /// The bot needs the Message Content intent and, in that channel, View Channel, Send Messages and Read Message
    /// History. Mentions never ping (allowed_mentions parse none); lines are capped in length; at most one post every
    /// 1.2 s (lines in between are joined) and 429s are honoured.
    ///
    /// CONFIG: discord-bridge.json beside the mod, {"token": "...", "channelId": "..."}. No file, no bridge. The file
    /// is never published (the public source takes only the .cs files).
    /// </summary>
    [HarmonyPatch]
    static class DiscordBridge
    {
        const string FILE = "discord-bridge.json";
        const string API = "https://discord.com/api/v10";
        const int OUT_MAX = 400, IN_MAX = 240;
        const int POLL_MS = 2000, POST_GAP_MS = 700;   // Discord allows 5 posts in 5 s per channel

        sealed class Config { public string token { get; set; } public string channelId { get; set; } }

        static Config s_Cfg;
        static HttpClient s_Http;
        static string s_BotId, s_LastId, s_GuildId, s_OwnerId;
        // THE SERVER NICKNAME, not the account name (owner 2026-10-14: the bridge showed his account name in game, not
        // his nickname on the server). A REST message carries no member, so each author's nickname is looked up once
        // and kept 10 minutes.
        static readonly Dictionary<string, (string nick, DateTime at)> s_Nicks = new Dictionary<string, (string, DateTime)>();
        static readonly ConcurrentQueue<string> s_Out = new ConcurrentQueue<string>();
        static CancellationTokenSource s_Stop;
        static int s_Sent, s_Received;

        public static bool On => s_Cfg != null;

        public static void Register()
        {
            try
            {
                var path = Path.Combine(Folder(), FILE);
                if (!File.Exists(path)) { Mod.Log.Info($"[RevivalGuard] DiscordBridge: off (no {FILE})"); return; }
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
                if (string.IsNullOrWhiteSpace(cfg?.token) || string.IsNullOrWhiteSpace(cfg?.channelId))
                { Mod.Log.Warn($"[RevivalGuard] DiscordBridge: off ({FILE} needs token and channelId)"); return; }
                s_Cfg = cfg;
                s_Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                s_Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bot " + cfg.token.Trim());
                s_Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DiscordBot (https://github.com/an admin account-face/hollowtide-server, 1)");
                s_Stop = new CancellationTokenSource();
                Task.Run(() => Run(s_Stop.Token));
                Mod.Log.Info($"[RevivalGuard] DiscordBridge: on, channel {cfg.channelId}");
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] DiscordBridge: off ({e.GetType().Name}: {e.Message})"); s_Cfg = null; }
        }

        static string Folder()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            return string.IsNullOrEmpty(folder) ? Path.Combine(ModManager.ModPath, "RevivalGuard") : folder;
        }

        // ---- out: General -> Discord ----------------------------------------------------------------------------

        static MethodBase TargetMethod() => AccessTools.Method(typeof(TurbineChatHandler), "LogTurbineChat");
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Postfix(uint channelID, string name, string message, uint senderID)
        {
            try
            {
                if (s_Cfg == null || channelID != TurbineChatChannel.General || string.IsNullOrWhiteSpace(message)) return;
                if (name != null && name.EndsWith(" (Discord)", StringComparison.Ordinal)) return;   // our own echo
                var p = PlayerManager.GetOnlinePlayer(senderID);
                if (p != null && Mercenaries.IsMerc(p)) return;
                string text = message.Length > OUT_MAX ? message.Substring(0, OUT_MAX) + "..." : message;
                s_Out.Enqueue($"**{Escape((name ?? "?").TrimStart('+'))}**: {Escape(text)}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DiscordBridge out: {e.Message}"); }
        }

        /// <summary>Discord markdown in a game line would format or hide parts of it; escape what matters.</summary>
        static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s) { if ("*_~`|>\\".IndexOf(c) >= 0) sb.Append('\\'); sb.Append(c); }
            return sb.ToString();
        }

        // ---- the loop: post what is queued, read what is new ----------------------------------------------------

        static async Task Run(CancellationToken stop)
        {
            try
            {
                using (var me = await s_Http.GetAsync(API + "/users/@me", stop))
                {
                    if (!me.IsSuccessStatusCode) { Mod.Log.Error($"[RevivalGuard] DiscordBridge: the token was refused ({(int)me.StatusCode}); off"); s_Cfg = null; return; }
                    using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
                    s_BotId = doc.RootElement.GetProperty("id").GetString();
                }
                using (var ch = await s_Http.GetAsync($"{API}/channels/{s_Cfg.channelId}", stop))
                    if (ch.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await ch.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("guild_id", out var gid)) s_GuildId = gid.GetString();
                    }
                // THE SERVER'S OWNER is the one Discord member allowed to appear under a protected name (NameGuard)
                if (s_GuildId != null)
                    using (var gr = await s_Http.GetAsync($"{API}/guilds/{s_GuildId}", stop))
                        if (gr.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await gr.Content.ReadAsStringAsync());
                            if (doc.RootElement.TryGetProperty("owner_id", out var oid)) s_OwnerId = oid.GetString();
                        }
                // start from the channel's newest message, so a restart never replays the backlog into the game
                using (var last = await s_Http.GetAsync($"{API}/channels/{s_Cfg.channelId}/messages?limit=1", stop))
                {
                    if (last.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
                        foreach (var m in doc.RootElement.EnumerateArray()) s_LastId = m.GetProperty("id").GetString();
                    }
                    else Mod.Log.Warn($"[RevivalGuard] DiscordBridge: cannot read the channel ({(int)last.StatusCode}); check the bot's permissions there");
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DiscordBridge start: {e.Message}"); }

            var nextPoll = DateTime.UtcNow;
            while (!stop.IsCancellationRequested && s_Cfg != null)
            {
                try
                {
                    await PostQueued(stop);
                    if (DateTime.UtcNow >= nextPoll) { nextPoll = DateTime.UtcNow.AddMilliseconds(POLL_MS); await Poll(stop); }
                }
                catch (TaskCanceledException) { }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DiscordBridge: {e.GetType().Name}: {e.Message}"); await Task.Delay(5000, stop).ContinueWith(_ => { }); }
                await Task.Delay(POST_GAP_MS, stop).ContinueWith(_ => { });
            }
        }

        static async Task PostQueued(CancellationToken stop)
        {
            if (s_Out.IsEmpty) return;
            var sb = new StringBuilder();
            while (sb.Length < 1600 && s_Out.TryDequeue(out var line)) { if (sb.Length > 0) sb.Append('\n'); sb.Append(line); }
            var body = JsonSerializer.Serialize(new { content = sb.ToString(), allowed_mentions = new { parse = Array.Empty<string>() } });
            using var r = await s_Http.PostAsync($"{API}/channels/{s_Cfg.channelId}/messages", new StringContent(body, Encoding.UTF8, "application/json"), stop);
            if ((int)r.StatusCode == 429) { await Task.Delay(RetryAfter(await r.Content.ReadAsStringAsync()), stop); return; }
            if (!r.IsSuccessStatusCode) Mod.Log.Warn($"[RevivalGuard] DiscordBridge: post refused ({(int)r.StatusCode})");
            else s_Sent++;
        }

        static async Task Poll(CancellationToken stop)
        {
            string url = $"{API}/channels/{s_Cfg.channelId}/messages?limit=20" + (s_LastId != null ? "&after=" + s_LastId : "");
            using var r = await s_Http.GetAsync(url, stop);
            if ((int)r.StatusCode == 429) { await Task.Delay(RetryAfter(await r.Content.ReadAsStringAsync()), stop); return; }
            if (!r.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var fresh = new List<(string id, string who, string text)>();
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                string id = m.GetProperty("id").GetString();
                var author = m.GetProperty("author");
                bool bot = author.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.True;
                string aid = author.GetProperty("id").GetString();
                string who = author.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : author.GetProperty("username").GetString();
                string text = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (m.TryGetProperty("attachments", out var at) && at.GetArrayLength() > 0) text = (text + " [picture]").Trim();
                if (!bot && aid != s_BotId) who = await Nick(aid, stop) ?? who;
                // NO ONE BUT THE OWNER UNDER THE OWNER'S NAME (owner 2026-10-16, impersonation): a nickname that looks
                // like a protected name falls back to the Discord username, and to "Discord user" if that does too
                if (!bot && aid != s_OwnerId && NameGuard.IsLookalike(who))
                {
                    string un = author.TryGetProperty("username", out var u) ? u.GetString() : null;
                    who = !string.IsNullOrEmpty(un) && !NameGuard.IsLookalike(un) ? un : "Discord user";
                }
                fresh.Add((id, bot || aid == s_BotId ? null : who, text));
            }
            fresh.Sort((a, b2) => ulong.Parse(a.id).CompareTo(ulong.Parse(b2.id)));   // Discord answers newest first
            foreach (var (id, who, text) in fresh)
            {
                s_LastId = id;
                if (who == null || string.IsNullOrWhiteSpace(text)) continue;
                SayInGame(Clean(who, 24), Clean(text, IN_MAX));
            }
        }

        static async Task<string> Nick(string userId, CancellationToken stop)
        {
            if (s_GuildId == null)
            {
                try
                {
                    using var ch = await s_Http.GetAsync($"{API}/channels/{s_Cfg.channelId}", stop);
                    if (ch.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await ch.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("guild_id", out var gid)) s_GuildId = gid.GetString();
                    }
                }
                catch { }
                if (s_GuildId == null) return null;
            }
            if (s_Nicks.TryGetValue(userId, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(10)) return c.nick;
            string nick = null;
            try
            {
                using var r = await s_Http.GetAsync($"{API}/guilds/{s_GuildId}/members/{userId}", stop);
                if (r.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("nick", out var n) && n.ValueKind == JsonValueKind.String) nick = n.GetString();
                }
            }
            catch { }
            s_Nicks[userId] = (nick, DateTime.UtcNow);
            return nick;
        }

        static int RetryAfter(string json)
        {
            try { using var d = JsonDocument.Parse(json); return (int)Math.Ceiling(d.RootElement.GetProperty("retry_after").GetDouble() * 1000) + 100; }
            catch { return 5000; }
        }

        /// <summary>One line, printable, capped: a Discord message can be many lines and emoji the game font lacks.</summary>
        static string Clean(string s, int max)
        {
            var sb = new StringBuilder();
            foreach (char ch in s.Replace("\r", " ").Replace("\n", " "))
                if (ch >= ' ' && ch < 0xD800) sb.Append(ch);
            var t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) + "..." : t;
        }

        // ---- in: Discord -> General -----------------------------------------------------------------------------

        static void SayInGame(string who, string text)
        {
            string sender = who + " (Discord)";
            ChatLog.Write("Discord", who, text);
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (PropertyManager.GetBool("chat_disable_general").Item) return;
                    var msg = new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_EVENT_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME,
                                                         TurbineChatChannel.General, sender, text, 0, ChatType.General);
                    foreach (var p in PlayerManager.GetAllOnline())
                    {
                        if (!p.GetCharacterOption(CharacterOption.ListenToGeneralChat) || Mercenaries.IsMerc(p)) continue;
                        p.Session?.Network.EnqueueSend(msg);
                    }
                    s_Received++;
                    Mod.Log.Info($"[RevivalGuard] DiscordBridge: in from {who} ({text.Length} chars)");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DiscordBridge in: {e.Message}"); }
            }));
        }

        public static string Status() => s_Cfg == null ? "off" : $"on, channel {s_Cfg.channelId}, {s_Sent} posts out, {s_Received} lines in";
    }
}
