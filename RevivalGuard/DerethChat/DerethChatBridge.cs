// Dereth cross-server chat: the ACE side. One file, shared by the drop-in mod any ACE server can install
// (cross-server-chat/ace-mod) and Hollowtide's RevivalGuard. AGPL-3.0. Design: docs/proposals/cross-server-chat.md.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Handlers;
using ACE.Server.WorldObjects;

namespace DerethChat
{
    /// <summary>
    /// THE DERETH CHANNEL: one chat line shared by every Asheron's Call server that joins the hub.
    ///
    ///   Out: "@dereth hi" is filtered, shown on this server as "Name (ThisServer): hi", and sent to the hub, which
    ///        passes it to every other server.
    ///   In:  lines from the hub are shown to everyone here who has the channel on, as "Name (Coldeve): hi".
    /// ONE channel (owner 2026-11-08): no global room carries it. A host mod may deliver lines its own way (DeliverHook:
    /// Hollowtide's client puts them in a tab of their own); everyone else gets a plain chat line.
    ///
    /// Only the character name, the server and the text cross. The hub checks the server's signature (an ECDSA key made
    /// here on first start; the hub holds only its public half), and filters, rate-limits and bans; this side does the
    /// same filters again and adds its own mutes. No config file, no bridge.
    /// </summary>
    public static class Bridge
    {
        public const string CONFIG = "dereth-chat.json", KEY = "dereth-chat-key.pem", STATE = "dereth-chat-state.json";

        public sealed class Config
        {
            public string hub { get; set; } = "wss://hollowtide.org/dereth/v1";
            public string serverId { get; set; }
            public bool enabled { get; set; } = true;
            public string command { get; set; } = "dereth";
            /// <summary>How lines show (ACE's ChatMessageType name).</summary>
            public string chatType { get; set; } = "Channel";
            /// <summary>Other servers' automated characters: "tag" (shown as "Name (bot)"), "hide" or "show".</summary>
            public string bots { get; set; } = "tag";
            /// <summary>This server's own automated characters: by character or account name. They never send unless sendBots.</summary>
            public string[] botCharacters { get; set; } = Array.Empty<string>();
            public string[] botAccounts { get; set; } = Array.Empty<string>();
            public bool sendBots { get; set; } = false;
            public int minLevel { get; set; } = 1;
        }

        sealed class State
        {
            public HashSet<uint> off { get; set; } = new HashSet<uint>();               // characters who turned it off
            public HashSet<uint> banned { get; set; } = new HashSet<uint>();            // local characters who may not send
            public HashSet<string> mutedServers { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> mutedPlayers { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // "Name@Server"
            public Dictionary<uint, HashSet<string>> ignore { get; set; } = new Dictionary<uint, HashSet<string>>();     // per character: "Server" or "Name@Server"
            public HashSet<uint> told { get; set; } = new HashSet<uint>();              // has seen the first-use notice
            public bool paused { get; set; }                                            // staff turned the channel off on this server
        }

        /// <summary>What the host server adds: its own bot test and a chat log. Both optional.</summary>
        public static Func<Player, bool> IsBotHook;
        public static Action<string, string, string> ChatLogHook;   // (server, name, text)
        /// <summary>Deliver one line to one player some other way (player, server, name, text, bot); true = done.</summary>
        public static Func<Player, string, string, string, bool, bool> DeliverHook;
        /// <summary>A player turned the channel on or off (the host may tell its client).</summary>
        public static Action<Player> StateHook;
        /// <summary>What players read: the channel's name in a sentence ("the Dereth channel", "cross-server chat"), the
        /// command they are told to type ("@dereth", "/crossserver"), and extra command names that do the same.</summary>
        public static string ChannelName = "the Dereth channel", PlayerHint = null;
        public static string[] Aliases = Array.Empty<string>();
        static string N => ChannelName;
        static string Cap => ChannelName.Length > 0 ? char.ToUpperInvariant(ChannelName[0]) + ChannelName.Substring(1) : ChannelName;
        static string Hint => PlayerHint ?? "@" + (s_Cfg?.command ?? "dereth");
        public static Action<string> LogInfo = s => Console.WriteLine(s), LogWarn = s => Console.WriteLine(s);

        static Config s_Cfg;
        static State s_State = new State();
        static string s_Folder;
        static ECDsa s_Key;
        static string s_Me = "";              // this server's display name, from the hub's welcome
        static volatile bool s_Connected;
        static string[] s_Servers = Array.Empty<string>();
        static readonly ConcurrentQueue<Frame> s_Out = new ConcurrentQueue<Frame>();
        static readonly ConcurrentDictionary<string, uint> s_Pending = new ConcurrentDictionary<string, uint>();   // say id -> sender guid
        static readonly RateLimiter s_PlayerRate = new RateLimiter(3, TimeSpan.FromSeconds(3));
        static CancellationTokenSource s_Stop;
        static ClientWebSocket s_Socket;
        static int s_Sent, s_Received;
        static ChatMessageType s_ChatType = ChatMessageType.Channel;
        static readonly object s_StateLock = new object();
        // connections whose client shows the channel itself (it said "@dereth client"); a new login is a new Session
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Session, object> s_TabClients = new System.Runtime.CompilerServices.ConditionalWeakTable<Session, object>();

        /// <summary>Did this connection's client say it shows the channel in a place of its own?</summary>
        public static bool ClientShowsChannel(Session s) => s != null && s_TabClients.TryGetValue(s, out _);

        public static bool On => s_Cfg != null;
        public static string Command => s_Cfg?.command ?? "dereth";
        /// <summary>Does this player hear the channel (not turned off by them, not paused by staff)?</summary>
        public static bool IsOn(Player p)
        {
            if (s_Cfg == null || p == null) return false;
            lock (s_StateLock) return !s_State.paused && !s_State.off.Contains(p.Guid.Full);
        }

        /// <summary>Called once from the host mod's Initialize. Reads the config beside the mod; without it, nothing happens.</summary>
        public static void Start(string folder)
        {
            try
            {
                s_Folder = folder;
                var path = Path.Combine(folder, CONFIG);
                if (!File.Exists(path)) { LogInfo($"[DerethChat] off (no {CONFIG})"); return; }
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (cfg == null || !cfg.enabled) { LogInfo($"[DerethChat] off (enabled is false in {CONFIG})"); return; }
                if (string.IsNullOrWhiteSpace(cfg.serverId) || string.IsNullOrWhiteSpace(cfg.hub)) { LogWarn($"[DerethChat] off ({CONFIG} needs serverId and hub)"); return; }
                if (!Enum.TryParse(cfg.chatType ?? "", true, out s_ChatType)) s_ChatType = ChatMessageType.Channel;
                s_Key = LoadOrMakeKey(Path.Combine(folder, KEY));
                LoadState();
                s_Cfg = cfg;
                string cmd = string.IsNullOrWhiteSpace(cfg.command) ? "dereth" : cfg.command.Trim().TrimStart('@', '/');
                AddCommand(cmd, PlayerCommand, AccessLevel.Player, "The Dereth channel, shared with other Asheron's Call servers.",
                    "<text> | on | off | who | ignore <Server or Name@Server> | unignore <...> | help");
                foreach (var a in Aliases ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(a) && !a.Equals(cmd, StringComparison.OrdinalIgnoreCase))
                        AddCommand(a.Trim().TrimStart('@', '/'), PlayerCommand, AccessLevel.Player, "Same as @" + cmd + ".", "<text> | on | off | who | help");
                AddCommand(cmd + "admin", AdminCommand, AccessLevel.Sentinel, "Dereth channel moderation on this server.",
                    "status | on | off | key | mute <Server or Name@Server> | unmute <...> | ban <character> | unban <character> | reconnect");
                s_Stop = new CancellationTokenSource();
                Task.Run(() => Run(s_Stop.Token));
                LogInfo($"[DerethChat] on: server id {cfg.serverId}, hub {cfg.hub}, @{cmd}; public key {Proto.PublicKey(s_Key)}");
            }
            catch (Exception e) { LogWarn($"[DerethChat] off ({e.GetType().Name}: {e.Message})"); s_Cfg = null; }
        }

        public static void Stop() { try { s_Stop?.Cancel(); } catch { } }

        static void AddCommand(string name, Action<Session, string[]> handler, AccessLevel access, string description, string usage)
        {
            // includeRaw: parameters[0] is the whole line after the command, so a chat line keeps its spacing and quotes
            var info = new CommandHandlerInfo
            {
                Attribute = new CommandHandlerAttribute(name, access, CommandHandlerFlag.RequiresWorld, true, -1, description, usage),
                Handler = (CommandHandler)Delegate.CreateDelegate(typeof(CommandHandler), handler.Method),
            };
            if (!CommandManager.TryAddCommand(info, true)) LogWarn($"[DerethChat] could not add @{name}");
        }

        static ECDsa LoadOrMakeKey(string path)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(path)) { key.ImportFromPem(File.ReadAllText(path)); return key; }
            File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            LogInfo($"[DerethChat] made this server's key ({KEY}); send the public key in the next line to the hub's admins to join");
            return key;
        }

        // ---- the per-server state: who turned it off, mutes, bans --------------------------------------------------

        static void LoadState()
        {
            try
            {
                var p = Path.Combine(s_Folder, STATE);
                if (!File.Exists(p)) return;
                var st = JsonSerializer.Deserialize<State>(File.ReadAllText(p));
                if (st == null) return;
                // the comparers do not survive JSON
                st.mutedServers = new HashSet<string>(st.mutedServers ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                st.mutedPlayers = new HashSet<string>(st.mutedPlayers ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                var ig = new Dictionary<uint, HashSet<string>>();
                foreach (var kv in st.ignore ?? new Dictionary<uint, HashSet<string>>()) ig[kv.Key] = new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
                st.ignore = ig;
                st.off ??= new HashSet<uint>(); st.banned ??= new HashSet<uint>(); st.told ??= new HashSet<uint>();
                s_State = st;
            }
            catch (Exception e) { LogWarn($"[DerethChat] {STATE} unreadable, starting empty ({e.Message})"); }
        }

        static void SaveState()
        {
            try
            {
                string json;
                lock (s_StateLock) json = JsonSerializer.Serialize(s_State);
                var p = Path.Combine(s_Folder, STATE);
                File.WriteAllText(p + ".new", json);
                File.Move(p + ".new", p, true);
            }
            catch (Exception e) { LogWarn($"[DerethChat] could not save {STATE}: {e.Message}"); }
        }

        // ---- players ------------------------------------------------------------------------------------------------

        static void Tell(Session s, string text) => s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void PlayerCommand(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null || s_Cfg == null) return;
            string raw = (parameters != null && parameters.Length > 0 ? parameters[0] : "").Trim();
            string first = raw.Split(' ', 2)[0].ToLowerInvariant();
            string rest = raw.Contains(' ') ? raw.Substring(raw.IndexOf(' ') + 1).Trim() : "";
            string cmd = Hint;
            switch (first)
            {
                case "":
                case "help":
                    Tell(session, $"{Cap} is shared with other Asheron's Call servers. {cmd} <text> to talk, {cmd} off to stop hearing it, "
                        + $"{cmd} on to hear it again, {cmd} who for the servers on it, {cmd} ignore <Server or Name@Server> to hide one. "
                        + "Other servers see your character name, this server's name and what you write, nothing else.");
                    return;
                case "off":
                    lock (s_StateLock) s_State.off.Add(p.Guid.Full);
                    SaveState(); Tell(session, $"{Cap} is off for you. {cmd} on to hear it again."); StateHook?.Invoke(p); return;
                case "on":
                    lock (s_StateLock) s_State.off.Remove(p.Guid.Full);
                    SaveState(); Tell(session, $"{Cap} is on."); StateHook?.Invoke(p); return;
                case "who":
                    Who(session); return;
                case "client":
                    // a client with its own place for the channel (Hollowtide's tab) says so once per login; no answer
                    s_TabClients.Remove(session); s_TabClients.Add(session, new object());
                    StateHook?.Invoke(p);
                    return;
                case "ignore":
                case "unignore":
                    if (rest.Length == 0) { Tell(session, $"{cmd} {first} <Server or Name@Server>"); return; }
                    lock (s_StateLock)
                    {
                        if (!s_State.ignore.TryGetValue(p.Guid.Full, out var set)) s_State.ignore[p.Guid.Full] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (first == "ignore") set.Add(rest); else set.Remove(rest);
                    }
                    SaveState(); Tell(session, first == "ignore" ? $"You will not see {rest} on {N}." : $"{rest} is no longer hidden."); return;
            }
            Send(p, raw);
        }

        static void Who(Session session)
        {
            if (!s_Connected) { Tell(session, $"{Cap} is not connected right now; it keeps trying."); return; }
            s_Out.Enqueue(new Frame { t = "who", id = "who:" + session.Player.Guid.Full });
        }

        /// <summary>A line from one of this server's players: checked, shown here, sent to the hub.</summary>
        static bool Send(Player p, string raw)
        {
            var s = p.Session;
            uint g = p.Guid.Full;
            bool off, banned, first;
            lock (s_StateLock) { off = s_State.off.Contains(g); banned = s_State.banned.Contains(g); first = s_State.told.Add(g); }
            if (first)
            {
                SaveState();
                Tell(s, $"{Cap} goes to other Asheron's Call servers. They see your character name, this server's name and your words. "
                    + $"Links are not allowed. {Hint} help for more.");
            }
            if (s_State.paused) { Tell(s, $"{Cap} is turned off on this server for now."); return false; }
            if (banned) { Tell(s, $"You cannot talk on {N} on this server."); return false; }
            if (off) { Tell(s, $"You have {N} turned off. {Hint} on first."); return false; }
            if (!s_Connected) { Tell(s, $"{Cap} is not connected right now; your line was not sent."); return false; }
            if (IsBot(p) && !s_Cfg.sendBots) return false;
            if ((p.Level ?? 1) < s_Cfg.minLevel) { Tell(s, $"{Cap} opens at level {s_Cfg.minLevel}."); return false; }
            string name = Proto.CleanName(p.Name);
            if (name == null) return false;
            string text = Proto.Filter(raw, out var why);
            if (text == null) { if (why == "link") Tell(s, $"Links are not allowed on {N}."); return false; }
            if (!s_PlayerRate.Take(g.ToString(), DateTime.UtcNow)) { Tell(s, $"Slow down a little on {N}."); return false; }
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            s_Pending[id] = g;
            s_Out.Enqueue(new Frame { t = "say", id = id, name = name, text = text, bot = IsBot(p) ? true : null });
            Show(s_Me.Length > 0 ? s_Me : s_Cfg.serverId, name, text, false, true);
            return true;
        }

        static bool IsBot(Player p)
        {
            try
            {
                if (IsBotHook != null && IsBotHook(p)) return true;
                string n = p.Name?.TrimStart('+'), a = p.Account?.AccountName;
                return (s_Cfg.botCharacters?.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)) ?? false)
                    || (a != null && (s_Cfg.botAccounts?.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)) ?? false));
            }
            catch { return false; }
        }

        /// <summary>Shows one line to everyone here who listens (on the world thread).</summary>
        static void Show(string server, string name, string text, bool bot, bool local)
        {
            if (bot && !local && string.Equals(s_Cfg.bots, "hide", StringComparison.OrdinalIgnoreCase)) return;
            bool tag = bot && !local && !string.Equals(s_Cfg.bots, "show", StringComparison.OrdinalIgnoreCase);
            if (!local)
                lock (s_StateLock)
                    if (s_State.paused || s_State.mutedServers.Contains(server) || s_State.mutedPlayers.Contains(name.TrimStart('+') + "@" + server)) return;
            try { ChatLogHook?.Invoke(server, name, text); } catch { }
            string key1 = server, key2 = name.TrimStart('+') + "@" + server;
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    var line = new GameMessageSystemChat(Proto.Line(server, name, text, tag), s_ChatType);
                    foreach (var p in PlayerManager.GetAllOnline())
                    {
                        uint g = p.Guid.Full;
                        lock (s_StateLock)
                        {
                            if (s_State.off.Contains(g)) continue;
                            if (s_State.ignore.TryGetValue(g, out var ig) && (ig.Contains(key1) || ig.Contains(key2))) continue;
                        }
                        bool done = false;
                        try { done = DeliverHook != null && DeliverHook(p, server, name, text, tag); } catch { }
                        if (!done) p.Session?.Network.EnqueueSend(line);
                    }
                }
                catch (Exception e) { LogWarn($"[DerethChat] show: {e.Message}"); }
            }));
        }

        // ---- staff ----------------------------------------------------------------------------------------------------

        static void AdminCommand(Session session, params string[] parameters)
        {
            if (s_Cfg == null) return;
            string raw = (parameters != null && parameters.Length > 0 ? parameters[0] : "").Trim();
            string first = raw.Split(' ', 2)[0].ToLowerInvariant();
            string rest = raw.Contains(' ') ? raw.Substring(raw.IndexOf(' ') + 1).Trim() : "";
            void Say(string t) { if (session != null) Tell(session, t); else LogInfo(t); }
            switch (first)
            {
                case "off":
                case "on":
                    lock (s_StateLock) s_State.paused = first == "off";
                    SaveState();
                    Say(first == "off" ? $"{Cap} turned off on this server: nothing is sent or shown until it is turned on again."
                                       : $"{Cap} back on for this server.");
                    return;
                case "key":
                    Say($"Server id {s_Cfg.serverId}, public key: {Proto.PublicKey(s_Key)}"); return;
                case "reconnect":
                    try { s_Socket?.Abort(); } catch { }
                    Say("Reconnecting to the hub."); return;
                case "mute":
                case "unmute":
                    if (rest.Length == 0) { Say("mute <Server> or mute <Name@Server>"); return; }
                    lock (s_StateLock)
                    {
                        var set = rest.Contains('@') ? s_State.mutedPlayers : s_State.mutedServers;
                        if (first == "mute") set.Add(rest); else set.Remove(rest);
                    }
                    SaveState(); Say($"{rest} {(first == "mute" ? "muted" : "unmuted")} on {N} on this server."); return;
                case "ban":
                case "unban":
                    {
                        if (rest.Length == 0) { Say("ban <character name>"); return; }
                        // a staff character's name may come with or without ACE's "+"
                        var pl = PlayerManager.FindByName(rest) ?? PlayerManager.FindByName("+" + rest.TrimStart('+')) ?? PlayerManager.FindByName(rest.TrimStart('+'));
                        if (pl == null) { Say($"No character named {rest}."); return; }
                        lock (s_StateLock) { if (first == "ban") s_State.banned.Add(pl.Guid.Full); else s_State.banned.Remove(pl.Guid.Full); }
                        SaveState(); Say($"{pl.Name} {(first == "ban" ? "may no longer talk" : "may talk again")} on {N}."); return;
                    }
                default:
                    Say(Status()); return;
            }
        }

        public static string Status()
        {
            if (s_Cfg == null) return Cap + ": off";
            int mutes, bans, offs;
            lock (s_StateLock) { mutes = s_State.mutedServers.Count + s_State.mutedPlayers.Count; bans = s_State.banned.Count; offs = s_State.off.Count; }
            return $"{Cap}: {(s_State.paused ? "TURNED OFF here, " : "")}{(s_Connected ? "connected as " + s_Me : "not connected")} to {s_Cfg.hub}; {s_Servers.Length} servers on it"
                + $"{(s_Servers.Length > 0 ? " (" + string.Join(", ", s_Servers) + ")" : "")}; {s_Sent} lines out, {s_Received} in; "
                + $"{mutes} mutes, {bans} local bans, {offs} characters with it off.";
        }

        // ---- the hub connection ---------------------------------------------------------------------------------------

        static async Task Run(CancellationToken stop)
        {
            int backoff = 2, fails = 0;
            while (!stop.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);   // a hub that vanished without closing is noticed (.NET 9+)
                s_Socket = ws;
                try
                {
                    await ws.ConnectAsync(new Uri(s_Cfg.hub), stop);
                    var challenge = await Receive(ws, stop);
                    if (challenge?.t != "challenge") throw new Exception("no challenge from the hub");
                    await SendFrame(ws, new Frame { t = "hello", v = Proto.VERSION, server = s_Cfg.serverId, nonce = challenge.nonce, sig = Proto.Sign(s_Key, s_Cfg.serverId, challenge.nonce) }, stop);
                    var welcome = await Receive(ws, stop);
                    if (welcome?.t != "welcome") throw new Exception("refused: " + (welcome?.why ?? "no answer"));
                    s_Me = welcome.server ?? s_Cfg.serverId;
                    s_Servers = welcome.servers ?? Array.Empty<string>();
                    s_Connected = true;
                    while (s_Out.TryDequeue(out _)) { }   // nothing said while away is sent late
                    LogInfo($"[DerethChat] connected to the hub as {s_Me}; {s_Servers.Length} servers on the channel" + (fails > 0 ? $" (after {fails} failed tries)" : ""));
                    backoff = 2; fails = 0;
                    var reader = ReadLoop(ws, stop);
                    while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open && !reader.IsCompleted)
                    {
                        while (s_Out.TryDequeue(out var f)) { await SendFrame(ws, f, stop); if (f.t == "say") s_Sent++; }
                        await Task.WhenAny(reader, Task.Delay(150, stop));
                    }
                    if (reader.IsFaulted) throw reader.Exception.GetBaseException();
                    throw new Exception("the hub closed the connection");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    bool wasUp = s_Connected;
                    s_Connected = false;
                    fails++;
                    if (wasUp || fails == 1 || fails % 20 == 0) LogWarn($"[DerethChat] hub: {e.Message}; retrying in {backoff} s");
                    if (wasUp) SayToAll($"{Cap} lost its connection; it will come back on its own.");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), stop); } catch { break; }
                backoff = Math.Min(backoff * 2, 120);
            }
        }

        static async Task ReadLoop(ClientWebSocket ws, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var f = await Receive(ws, stop);
                if (f == null) return;
                switch (f.t)
                {
                    case "say":
                        {
                            string server = Proto.CleanText(f.server, 24), name = Proto.CleanName(f.name);
                            string text = Proto.Filter(f.text, out _);   // the same filters again: never trust the wire
                            if (server.Length == 0 || name == null || text == null) break;
                            s_Received++;
                            Show(server, name, text, f.bot == true, false);
                            break;
                        }
                    case "ok":
                        if (f.id != null) s_Pending.TryRemove(f.id, out _);
                        break;
                    case "rejected":
                        if (f.id != null && s_Pending.TryRemove(f.id, out var g))
                        {
                            var p = PlayerManager.GetOnlinePlayer(g);
                            string why = f.why switch
                            {
                                "rate" => "Your last line was not passed on to the other servers: too many lines too fast.",
                                "banned" => $"You cannot talk on {N}.",
                                "link" => $"Links are not allowed on {N}.",
                                _ => "Your last line was not passed on to the other servers."
                            };
                            if (p != null) Tell(p.Session, why);
                        }
                        break;
                    case "servers":
                        s_Servers = f.servers ?? Array.Empty<string>();
                        if (f.id != null && f.id.StartsWith("who:") && uint.TryParse(f.id.Substring(4), out var wg))
                        {
                            var p = PlayerManager.GetOnlinePlayer(wg);
                            if (p != null) Tell(p.Session, s_Servers.Length == 0 ? $"No other servers are on {N}." : $"On {N}: " + string.Join(", ", s_Servers));
                        }
                        break;
                    case "notice":
                        if (!string.IsNullOrWhiteSpace(f.text)) SayToAll(Cap + ": " + Proto.CleanText(f.text, 300));
                        break;
                }
            }
        }

        static void SayToAll(string text)
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                foreach (var p in PlayerManager.GetAllOnline())
                {
                    lock (s_StateLock) if (s_State.off.Contains(p.Guid.Full)) continue;
                    Tell(p.Session, text);
                }
            }));
        }

        static Task SendFrame(ClientWebSocket ws, Frame f, CancellationToken stop)
            => ws.SendAsync(Encoding.UTF8.GetBytes(f.ToJson()), WebSocketMessageType.Text, true, stop);

        static async Task<Frame> Receive(ClientWebSocket ws, CancellationToken stop)
        {
            var buf = new byte[8192];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, stop);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 64 * 1024) throw new Exception("frame too large");
                if (r.EndOfMessage) break;
            }
            return Frame.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }
}
