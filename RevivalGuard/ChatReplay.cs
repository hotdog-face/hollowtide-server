using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// CHAT REPLAY: the retail servers' own chat, played back as if the world were full (docs/CHAT-REPLAY.md).
    ///
    /// The corpus is ~3,200 public lines from the January-2017 retail captures (General, Trade,
    /// Allegiance, local /say and emotes; tells, fellowship and spell words dropped), built by
    /// tools/chatreplay/extract.py. It is real people's words, so it is never in git: it lives at
    /// ~/ace/chatreplay/corpus.jsonl on the shard host (RG_CHAT_REPLAY_CORPUS overrides).
    ///
    /// OFF FOR EVERYONE BY DEFAULT, AND ADMIN-ONLY WHEN ON. A session hears replayed chat only after
    /// it opts in with @chatreplay (the admin panel's "chatter" button), and only while its account is
    /// Admin: the access check is repeated on every send, not just at opt-in. A new login is a new
    /// session and starts opted out. A normal player is never sent a replayed line.
    ///
    /// ON THE WIRE IT IS ORDINARY CHAT. Channel lines go out as ACE's own TurbineChat event
    /// (GameMessageTurbineChat, the room ids ACE gives every client in SetTurbineChatChannels), local
    /// lines as HearSpeech / EmoteText / SoulEmote, so our client and the retail client both print
    /// them exactly as they print a real player. The recipient's own "hear General/Trade/..." options
    /// are honoured, as ACE's TurbineChatHandler honours them.
    ///
    /// NAMES. Speakers keep their retail names unless a name belongs to a character on this shard
    /// (online or not), in which case it is swapped for a generated one, so a /tell never reaches a
    /// real player and a reply to a replayed name gets ACE's own "not available" answer. `@chatreplay
    /// names generated` swaps every name (and mentions of those names in the text) for generated
    /// AC-style names: the mode for any future public use.
    ///
    /// PACING. Channel lines play in their captured order and spacing from a random start, each gap
    /// clamped to 1.5..120 s and divided by the pace (0.5x, 1x, 2x, 4x). The stream pauses while nobody
    /// is opted in. Local lines keep the landblock they were heard in and play, on their own cursor, to
    /// an opted-in admin standing within one landblock of it (most are Arwic's).
    ///
    ///   @chatreplay                  toggle for this session, then status (the panel button)
    ///   @chatreplay on | off | status | skip | pace [0.5|1|2|4] | names real|generated
    ///               | allegiance on|off | reload
    ///
    /// Registers itself on the first world tick rather than from Mod.Initialize.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class ChatReplay
    {
        sealed class Line { public double T; public string Ch, Who, Text, Src; public uint Landblock; }
        sealed class Town { public uint Landblock; public List<Line> Lines = new List<Line>(); public int Cursor; public double Due; }
        sealed class Mark { }

        static readonly object s_Lock = new object();
        static readonly ConditionalWeakTable<Session, Mark> s_OptedIn = new ConditionalWeakTable<Session, Mark>();
        static readonly Random s_Rng = new Random();

        static bool s_Registered;
        static List<Line> s_Channel = new List<Line>();
        static List<Town> s_Towns = new List<Town>();
        static int s_Cursor = -1;
        static double s_Due, s_NextTick;
        static double s_Pace = 1.0;
        static bool s_Generated, s_Allegiance;
        static string s_Loaded = "not loaded";
        static HashSet<string> s_Shard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> s_Fake = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static Regex s_Mentions;

        const double MIN_GAP = 1.5, MAX_GAP = 120, LOCAL_MIN = 4, LOCAL_MAX = 90;
        const int MAX_TEXT = 250;       // GameMessageTurbineChat's length prefix is wrong from 256 chars

        static string CorpusPath => Environment.GetEnvironmentVariable("RG_CHAT_REPLAY_CORPUS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "chatreplay", "corpus.jsonl");

        static double Now => ACE.Common.Time.GetUnixTime();

        static void Postfix()
        {
            if (!s_Registered) Register();
            var now = Now;
            if (now < s_NextTick) return;
            s_NextTick = now + 0.5;
            try { lock (s_Lock) Tick(now); }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] ChatReplay tick threw; retrying in a minute", e); s_NextTick = now + 60; }
        }

        static void Register()
        {
            s_Registered = true;
            CommandManager.TryAddCommand(Command, "chatreplay", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Replay retail's 2017 chat to this admin session (RevivalGuard ChatReplay, docs/CHAT-REPLAY.md).",
                "[on | off | status | skip | pace [x] | names real|generated | allegiance on|off | reload]");
            Mod.Log.Info("[RevivalGuard] ChatReplay: registered (@chatreplay; off for every session until an admin opts in)");
        }

        // ---------------------------------------------------------------- recipients

        /// <summary>The one gate every send goes through: opted in, still online, and Admin.</summary>
        static List<Player> Listeners()
        {
            var list = new List<Player>();
            foreach (var p in PlayerManager.GetAllOnline())
            {
                var s = p?.Session;
                if (s == null || s.AccessLevel < AccessLevel.Admin || !s_OptedIn.TryGetValue(s, out _)) continue;
                list.Add(p);
            }
            return list;
        }

        static bool Hears(Player p, string ch)
        {
            switch (ch)
            {
                case "General": return p.GetCharacterOption(CharacterOption.ListenToGeneralChat);
                case "Trade": return p.GetCharacterOption(CharacterOption.ListenToTradeChat);
                case "LFG": return p.GetCharacterOption(CharacterOption.ListenToLFGChat);
                case "Roleplay": return p.GetCharacterOption(CharacterOption.ListenToRoleplayChat);
                case "Society": return p.GetCharacterOption(CharacterOption.ListenToSocietyChat);
                case "Allegiance": return s_Allegiance && p.GetCharacterOption(CharacterOption.ListenToAllegianceChat);
                case "Olthoi": return true;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- the clock

        static void Tick(double now)
        {
            var who = Listeners();
            if (who.Count == 0 || s_Channel.Count == 0 && s_Towns.Count == 0) { s_Due = 0; return; }

            if (s_Channel.Count > 0)
            {
                if (s_Cursor < 0) s_Cursor = s_Rng.Next(s_Channel.Count);
                if (s_Due == 0) s_Due = now + 2;
                if (now >= s_Due)
                {
                    var line = s_Channel[s_Cursor];
                    foreach (var p in who) if (Hears(p, line.Ch)) SendChannel(p, line);
                    int next = (s_Cursor + 1) % s_Channel.Count;
                    s_Due = now + Gap(line, s_Channel[next], MIN_GAP, MAX_GAP);
                    s_Cursor = next;
                }
            }

            foreach (var town in s_Towns)
            {
                List<Player> near = null;
                foreach (var p in who)
                    if (p.Location != null && Near(p.Location.Landblock, town.Landblock)) (near ??= new List<Player>()).Add(p);
                if (near == null) continue;
                if (town.Due == 0) { town.Due = now + 3; town.Cursor = s_Rng.Next(town.Lines.Count); }
                if (now < town.Due) continue;
                var line = town.Lines[town.Cursor];
                foreach (var p in near) SendLocal(p, line);
                int next = (town.Cursor + 1) % town.Lines.Count;
                town.Due = now + Gap(line, town.Lines[next], LOCAL_MIN, LOCAL_MAX);
                town.Cursor = next;
            }
        }

        /// <summary>The captured gap to the next line, clamped, at the chosen pace. A jump to the next
        /// capture (or a wrap) counts as a short pause, not the days between two evenings.</summary>
        static double Gap(Line a, Line b, double lo, double hi)
        {
            double g = b.Src == a.Src && b.T >= a.T ? b.T - a.T : 10;
            return Math.Clamp(g, lo, hi) / s_Pace;
        }

        /// <summary>Within one landblock in x and y: a town is bigger than the block its centre is in.</summary>
        static bool Near(uint a, uint b)
        {
            int ax = (int)(a >> 8 & 0xFF), ay = (int)(a & 0xFF), bx = (int)(b >> 8 & 0xFF), by = (int)(b & 0xFF);
            return Math.Abs(ax - bx) <= 1 && Math.Abs(ay - by) <= 1;
        }

        // ---------------------------------------------------------------- the wire

        static uint RoomOf(string ch) => ch switch
        {
            "Allegiance" => TurbineChatChannel.Allegiance,
            "Trade" => TurbineChatChannel.Trade,
            "LFG" => TurbineChatChannel.LFG,
            "Roleplay" => TurbineChatChannel.Roleplay,
            "Society" => TurbineChatChannel.Society,
            "Olthoi" => TurbineChatChannel.Olthoi,
            _ => TurbineChatChannel.General,
        };

        static ChatType TypeOf(string ch) => ch switch
        {
            "Allegiance" => ChatType.Allegiance,
            "Trade" => ChatType.Trade,
            "LFG" => ChatType.LFG,
            "Roleplay" => ChatType.Roleplay,
            "Society" => ChatType.Society,
            "Olthoi" => ChatType.Olthoi,
            _ => ChatType.General,
        };

        /// <summary>A guid no character here has: ACE allocates players upward from 0x50000001, and
        /// the client only uses a TurbineChat speaker id to recognise its own echo.</summary>
        static uint FakeGuid(string name) => 0x5FFE0000u | (uint)(Hash(name) & 0xFFFF);

        static void SendChannel(Player p, Line l)
        {
            string name = Name(l.Who), text = Text(l.Text);
            p.Session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_EVENT_BINARY,
                ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, RoomOf(l.Ch), name, text, FakeGuid(name), TypeOf(l.Ch)));
        }

        static void SendLocal(Player p, Line l)
        {
            string name = Name(l.Who), text = Text(l.Text);
            uint g = FakeGuid(name);
            switch (l.Ch)
            {
                case "Emote": p.Session.Network.EnqueueSend(new GameMessageEmoteText(g, name, text)); break;
                case "SoulEmote": p.Session.Network.EnqueueSend(new GameMessageSoulEmote(g, name, text)); break;
                default: p.Session.Network.EnqueueSend(new GameMessageHearSpeech(text, name, g, ChatMessageType.Speech)); break;
            }
        }

        // ---------------------------------------------------------------- names

        /// <summary>The name a replayed speaker goes by: its own, unless the generated mode is on or
        /// a character on this shard already has it (then the generated one, so a /tell can never
        /// reach a real player).</summary>
        static string Name(string real)
        {
            if (!s_Generated && !s_Shard.Contains(real.TrimStart('+')) && PlayerManager.GetOnlinePlayer(real) == null) return real;
            if (!s_Fake.TryGetValue(real, out var fake))
            {
                fake = Generate(real);
                for (int salt = 1; s_Shard.Contains(fake) || s_Fake.ContainsValue(fake); salt++) fake = Generate(real + "#" + salt);
                s_Fake[real] = fake;
            }
            return fake;
        }

        static string Text(string t) => !s_Generated || s_Mentions == null ? t
            : s_Mentions.Replace(t, m => s_Fake.TryGetValue(m.Value, out var f) ? f : Name(m.Value));

        static readonly string[] A = { "Al", "Bre", "Cor", "Dar", "El", "Fen", "Gar", "Hal", "Is", "Jor", "Kel", "Lor", "Mar", "Nor",
            "Or", "Per", "Quin", "Ras", "Sel", "Tor", "Ul", "Val", "Wy", "Ya", "Zan", "Ash", "Bael", "Cyr", "Dru", "Eth" };
        static readonly string[] B = { "an", "ric", "wyn", "eth", "ion", "ar", "ald", "is", "on", "ius", "ra", "en", "ard", "ec", "yn", "ath", "or", "el" };
        static readonly string[] S = { "", "", "", " of Holtburg", " the Bold", " Ironhand", " al-Rashid", " Tsukino", " Stormhelm", " the Wise",
            " ibn Sayid", " Kaishin", " Amberleaf", " the Quiet", " Blackmoor" };

        static string Generate(string seed)
        {
            uint h = Hash(seed);
            string first = A[h % (uint)A.Length] + B[(h / 31) % (uint)B.Length];
            return first + S[(h / 997) % (uint)S.Length];
        }

        static uint Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return h;
        }

        // ---------------------------------------------------------------- the corpus

        static void Load()
        {
            var path = CorpusPath;
            s_Channel = new List<Line>();
            s_Towns = new List<Town>();
            s_Cursor = -1; s_Due = 0;
            s_Fake.Clear();
            if (!File.Exists(path)) { s_Loaded = $"no corpus at {path} (tools/chatreplay/extract.py builds it)"; return; }
            var towns = new Dictionary<uint, Town>();
            var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int bad = 0;
            foreach (var raw in File.ReadLines(path))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var r = doc.RootElement;
                    var l = new Line
                    {
                        T = r.GetProperty("t").GetDouble(), Ch = r.GetProperty("ch").GetString(),
                        Who = r.GetProperty("who").GetString(), Text = r.GetProperty("text").GetString(),
                        Src = r.TryGetProperty("src", out var src) ? src.GetString() : "",
                    };
                    if (string.IsNullOrWhiteSpace(l.Who) || string.IsNullOrWhiteSpace(l.Text) || l.Text.Length > MAX_TEXT) { bad++; continue; }
                    speakers.Add(l.Who);
                    if (l.Ch == "Say" || l.Ch == "Emote" || l.Ch == "SoulEmote" || l.Ch == "Shout")
                    {
                        if (!r.TryGetProperty("cell", out var cell)) { bad++; continue; }
                        l.Landblock = Convert.ToUInt32(cell.GetString(), 16) >> 16;
                        if (!towns.TryGetValue(l.Landblock, out var t)) towns[l.Landblock] = t = new Town { Landblock = l.Landblock };
                        t.Lines.Add(l);
                    }
                    else s_Channel.Add(l);
                }
                catch { bad++; }
            }
            // Captures in the order they were taken, lines in the order they were said.
            var order = s_Channel.GroupBy(l => l.Src).OrderBy(g => g.Min(l => l.T)).SelectMany(g => g.OrderBy(l => l.T)).ToList();
            s_Channel = order;
            s_Towns = towns.Values.Where(t => t.Lines.Count >= 3).ToList();
            foreach (var t in s_Towns) t.Lines.Sort((a, b) => a.Src == b.Src ? a.T.CompareTo(b.T) : string.CompareOrdinal(a.Src, b.Src));

            s_Shard = new HashSet<string>(PlayerManager.GetAllPlayers().Where(p => p?.Name != null).Select(p => p.Name.TrimStart('+')),
                StringComparer.OrdinalIgnoreCase);
            var names = speakers.Where(n => n.Length >= 4).OrderByDescending(n => n.Length).Select(Regex.Escape).ToList();
            s_Mentions = names.Count == 0 ? null
                : new Regex(@"(?<![\w'])(" + string.Join("|", names) + @")(?![\w'])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            int clash = speakers.Count(n => s_Shard.Contains(n.TrimStart('+')));
            s_Loaded = $"{s_Channel.Count} channel lines, {s_Towns.Sum(t => t.Lines.Count)} local lines in {s_Towns.Count} towns, "
                + $"{speakers.Count} speakers ({clash} renamed: a character here has the name), {bad} skipped";
            Mod.Log.Info($"[RevivalGuard] ChatReplay: loaded {path}: {s_Loaded}");
        }

        // ---------------------------------------------------------------- @chatreplay

        static void Command(Session session, params string[] args)
        {
            if (session?.Player == null) return;
            string verb = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "toggle";
            string arg = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
            lock (s_Lock)
            {
                bool mine = s_OptedIn.TryGetValue(session, out _);
                switch (verb)
                {
                    case "toggle": SetOptIn(session, !mine); break;
                    case "on": SetOptIn(session, true); break;
                    case "off": SetOptIn(session, false); break;
                    case "skip":
                        if (s_Channel.Count > 0) { s_Cursor = s_Rng.Next(s_Channel.Count); s_Due = 0; }
                        foreach (var t in s_Towns) t.Due = 0;
                        break;
                    case "pace":
                        if (double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && x > 0)
                            s_Pace = Math.Clamp(x, 0.25, 8);
                        else s_Pace = s_Pace < 1 ? 1 : s_Pace < 2 ? 2 : s_Pace < 4 ? 4 : 0.5;
                        s_Due = Math.Min(s_Due, Now + MIN_GAP);
                        break;
                    case "names":
                        if (arg == "generated" || arg == "real") { s_Generated = arg == "generated"; s_Fake.Clear(); }
                        break;
                    case "allegiance":
                        if (arg == "on" || arg == "off") s_Allegiance = arg == "on";
                        break;
                    case "reload": Load(); break;
                    case "status": break;
                    default:
                        Tell(session, "Usage: @chatreplay [on | off | status | skip | pace [0.5|1|2|4] | names real|generated | allegiance on|off | reload]");
                        return;
                }
                Tell(session, Status(session));
            }
        }

        static void SetOptIn(Session session, bool on)
        {
            s_OptedIn.Remove(session);
            if (!on) return;
            if (s_Channel.Count == 0 && s_Towns.Count == 0) Load();
            s_OptedIn.Add(session, new Mark());
        }

        static string Status(Session session)
        {
            bool mine = s_OptedIn.TryGetValue(session, out _);
            var p = session.Player;
            var muted = new[] { "General", "Trade", "Allegiance" }.Where(ch => (ch != "Allegiance" || s_Allegiance) && !Hears(p, ch)).ToList();
            string when = s_Cursor >= 0 && s_Cursor < s_Channel.Count
                ? DateTimeOffset.FromUnixTimeSeconds((long)s_Channel[s_Cursor].T).UtcDateTime.ToString("MMM d yyyy HH:mm") + " UTC" : "a random point";
            var sb = new StringBuilder();
            sb.Append($"Chat replay is {(mine ? "ON" : "OFF")} for you ({Listeners().Count} admin(s) listening). ");
            sb.Append($"Pace {s_Pace:0.##}x, names {(s_Generated ? "generated" : "as on retail")}, allegiance lines {(s_Allegiance ? "on" : "off")}. ");
            sb.Append($"Playing from {when}. Corpus: {s_Loaded}.");
            if (mine && muted.Count > 0) sb.Append($" Your options mute: {string.Join(", ", muted)}.");
            return sb.ToString();
        }

        static void Tell(Session s, string text) =>
            s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }
}
