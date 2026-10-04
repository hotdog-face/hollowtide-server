using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// THE LOOKING-FOR-GROUP BOARD (owner, 2026-09-25: "a looking-for-group board in the social UI
    /// panel is a great idea"; docs/MODERNIZATION-IDEAS-2026-09-25.md). Ours.
    ///
    /// One post per character: a line of text, stamped with the poster's level, where they are,
    /// and whether they lead a fellowship (then it reads as "looking for more"). Posts live in
    /// memory only, end when the poster logs off or after POST_MINUTES, and are replaced by the
    /// poster's next one.
    ///
    ///     @lfgboard                 the board
    ///     @lfgboard post &lt;text&gt;    post or replace yours
    ///     @lfgboard clear           take yours down
    ///     @lfgboard join &lt;guid&gt;     ask to join the fellowship a leader's post is recruiting for
    ///
    /// NOT "@lfg": retail's /lfg and @lfg already mean "say this on the LFG chat channel", and the
    /// bridge sends them there (Program.cs, case "lfg"), so a command by that name never arrives.
    ///
    /// Our client reads the board over the mod channel ("lfg", JSON) and shows it on the Fellowship
    /// page; every change is pushed to every connection that reads the channel, so an open board is
    /// live. A retail client gets the same board as chat lines from @lfgboard.
    /// </summary>
    public static class LfgBoard
    {
        const int POST_MINUTES = 60, MAX_TEXT = 120, MIN_GAP_SECONDS = 10;

        sealed class Post
        {
            public uint Guid; public string Name, Text, Where; public int Level, Fellows; public bool Leads;
            public long At;
        }

        static readonly object s_Lock = new object();
        static readonly Dictionary<uint, Post> s_Posts = new Dictionary<uint, Post>();
        static readonly Dictionary<uint, long> s_LastPost = new Dictionary<uint, long>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "lfgboard", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The looking-for-group board.", "[post <text> | clear]");
        }

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            string verb = parameters != null && parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            long now = (long)ACE.Common.Time.GetUnixTime();

            if (verb == "post")
            {
                string text = Clean(string.Join(" ", parameters.Skip(1)));
                if (text.Length == 0) { Say(session, "Usage: @lfgboard post <what you are looking for>"); return; }
                lock (s_Lock)
                {
                    if (s_LastPost.TryGetValue(p.Guid.Full, out var last) && now - last < MIN_GAP_SECONDS)
                    { Say(session, "One post every few seconds, please."); return; }
                    s_LastPost[p.Guid.Full] = now;
                    s_Posts[p.Guid.Full] = Describe(p, text, now);
                }
                if (!ModChannel.Capable(session)) Say(session, "Posted to the looking-for-group board. @lfgboard to read it, @lfgboard clear to take it down.");
                Push();
                return;
            }
            if (verb == "join")
            {
                // A REQUEST, AND THE LEADER DECIDES (owner 2026-09-25: "it should be a send request
                // then wait type thing. something players cant spam and they see that they sent the
                // request to join and its pending the other players decision"). The leader gets a
                // Yes/No (ACE's own confirmation, so a retail leader sees it too); Yes adds the asker
                // straight in (they asked, so no second invite). One request at a time per asker, and
                // a declined or unanswered one cannot be repeated to the same leader for a minute.
                uint g = 0;
                string a = parameters.Length > 1 ? parameters[1] : "";
                if (a.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uint.TryParse(a.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out g);
                else uint.TryParse(a, out g);
                RequestJoin(session, p, g, now);
                return;
            }
            if (verb == "clear")
            {
                bool had;
                lock (s_Lock) had = s_Posts.Remove(p.Guid.Full);
                if (!ModChannel.Capable(session)) Say(session, had ? "Your post is down." : "You had nothing posted.");
                if (had) Push();
                else if (ModChannel.Capable(session)) SendBoard(session, now);
                return;
            }
            // anything else: the board
            if (ModChannel.Capable(session)) { SendBoard(session, now); return; }
            var list = Snapshot(now);
            if (list.Count == 0) { Say(session, "The looking-for-group board is empty. @lfgboard post <text> to add to it."); return; }
            Say(session, $"--- Looking for group: {list.Count} ---");
            foreach (var x in list.Take(30))
                Say(session, $"{x.Name} ({(LiveFellows(x.Guid) > 0 ? $"fellowship of {LiveFellows(x.Guid)}, " : "")}level {x.Level}{(x.Where != null ? ", " + x.Where : "")}, "
                    + $"{Math.Max(0, (now - x.At) / 60)} min ago): {x.Text}");
        }

        // ---- join requests ------------------------------------------------------------------------

        const int JOIN_COOLDOWN = 60;
        const int JOIN_STALE = 90;   // an unanswered request stops blocking the asker after this
        static readonly Dictionary<uint, (uint leader, long at)> s_Asking = new Dictionary<uint, (uint, long)>();
        static readonly Dictionary<(uint, uint), long> s_NoRepeatUntil = new Dictionary<(uint, uint), long>();

        static void RequestJoin(Session session, Player asker, uint leaderGuid, long now)
        {
            Post post;
            lock (s_Lock) s_Posts.TryGetValue(leaderGuid, out post);
            var leader = leaderGuid != 0 ? PlayerManager.GetOnlinePlayer(leaderGuid) : null;
            if (post == null || leader == null) { Tell(session, leaderGuid, "gone", "That post is gone."); Push(); return; }
            if (asker.Fellowship != null) { Tell(session, leaderGuid, "none", "You're already in a fellowship. Leave it first."); return; }
            var f = leader.Fellowship;
            if (f == null || f.FellowshipLeaderGuid != leader.Guid.Full)
            { Tell(session, leaderGuid, "none", $"{leader.Name} isn't leading a fellowship. Send them a tell."); return; }
            lock (s_Lock)
            {
                if (s_Asking.TryGetValue(asker.Guid.Full, out var waiting) && now - waiting.at < JOIN_STALE)
                {
                    var w = PlayerManager.GetOnlinePlayer(waiting.leader);
                    Tell(session, waiting.leader, "pending", $"You're already waiting on {(w?.Name ?? "someone")}.");
                    return;
                }
                if (s_NoRepeatUntil.TryGetValue((asker.Guid.Full, leaderGuid), out var until) && now < until)
                { Tell(session, leaderGuid, "declined", $"You asked {leader.Name} a moment ago. Try again in {until - now} s."); return; }
            }
            var ask = new LfgJoinConfirmation(leader.Guid, asker.Guid);
            if (!leader.ConfirmationManager.EnqueueSend(ask, $"{asker.Name} (level {asker.Level ?? 1}) asks to join your fellowship. Accept?"))
            { Tell(session, leaderGuid, "none", $"{leader.Name} is busy with another question. Try again shortly."); return; }
            lock (s_Lock) s_Asking[asker.Guid.Full] = (leaderGuid, now);
            Tell(session, leaderGuid, "pending", $"Request sent to {leader.Name}. Waiting for their answer.");
        }

        /// <summary>The leader's answer (or a timeout), from the confirmation.</summary>
        internal static void Answered(Player leader, uint askerGuid, bool yes, bool timeout)
        {
            long now = (long)ACE.Common.Time.GetUnixTime();
            uint leaderGuid = leader?.Guid.Full ?? 0;
            lock (s_Lock)
            {
                s_Asking.Remove(askerGuid);
                if (!yes) s_NoRepeatUntil[(askerGuid, leaderGuid)] = now + JOIN_COOLDOWN;
            }
            var asker = PlayerManager.GetOnlinePlayer(askerGuid);
            if (asker?.Session == null || leader == null) return;
            var f = leader.Fellowship;
            if (yes && f != null && f.FellowshipLeaderGuid == leaderGuid && asker.Fellowship == null)
            {
                f.AddConfirmedMember(leader, asker, true);
                Tell(asker.Session, leaderGuid, "accepted", $"{leader.Name} accepted you into the fellowship.");
                Push();
                return;
            }
            Tell(asker.Session, leaderGuid, yes ? "none" : "declined",
                timeout ? $"{leader.Name} didn't answer your request." : yes ? "That fellowship is no longer open to you." : $"{leader.Name} declined your request.");
        }

        /// <summary>A join request's state to the asker: over the channel for our client (which shows
        /// it on the Join button), and as a chat line for everyone.</summary>
        static void Tell(Session s, uint leaderGuid, string state, string line)
        {
            if (s == null) return;
            Say(s, line);
            if (ModChannel.Capable(s)) ModChannel.Send(s, "lfgjoin", "{\"g\":" + leaderGuid + ",\"state\":\"" + state + "\"}");
        }

        static Post Describe(Player p, string text, long now)
        {
            var f = p.Fellowship;
            bool leads = f != null && f.FellowshipLeaderGuid == p.Guid.Full;
            return new Post
            {
                Guid = p.Guid.Full, Name = p.Name, Level = p.Level ?? 1, Text = text, At = now,
                Where = p.Location?.GetMapCoordStr(),
                Leads = leads, Fellows = leads ? f.FellowshipMembers.Count : 0,
            };
        }

        /// <summary>The live posts, newest first: expired ones and those whose poster left are dropped.</summary>
        static List<Post> Snapshot(long now)
        {
            lock (s_Lock)
            {
                foreach (var g in s_Posts.Where(kv => now - kv.Value.At > POST_MINUTES * 60 || PlayerManager.GetOnlinePlayer(kv.Key) == null)
                                         .Select(kv => kv.Key).ToList())
                    s_Posts.Remove(g);
                return s_Posts.Values.OrderByDescending(x => x.At).ToList();
            }
        }

        static string Json(long now)
        {
            var sb = new StringBuilder("{\"now\":").Append(now).Append(",\"posts\":[");
            int n = 0;
            foreach (var x in Snapshot(now))
            {
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"g\":").Append(x.Guid).Append(",\"n\":").Append(Q(x.Name)).Append(",\"l\":").Append(x.Level)
                  .Append(",\"t\":").Append(Q(x.Text)).Append(",\"w\":").Append(Q(x.Where ?? "")).Append(",\"at\":").Append(x.At)
                  .Append(",\"f\":").Append(LiveFellows(x.Guid)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>The size of the fellowship this poster leads right now, or 0 (so "LFM (N)" is live).</summary>
        static int LiveFellows(uint guid)
        {
            var f = PlayerManager.GetOnlinePlayer(guid)?.Fellowship;
            return f != null && f.FellowshipLeaderGuid == guid ? f.FellowshipMembers.Count : 0;
        }

        static void SendBoard(Session s, long now) => ModChannel.Send(s, "lfg", Json(now));

        /// <summary>The changed board to every connection that reads the channel.</summary>
        static void Push()
        {
            long now = (long)ACE.Common.Time.GetUnixTime();
            string json = Json(now);
            foreach (var o in PlayerManager.GetAllOnline())
                if (o?.Session != null && ModChannel.Capable(o.Session)) ModChannel.Send(o.Session, "lfg", json);
        }

        static string Clean(string t)
        {
            var sb = new StringBuilder();
            foreach (char c in t ?? "") if (c >= ' ' && c != 127) sb.Append(c);
            var s = sb.ToString().Trim();
            return s.Length > MAX_TEXT ? s.Substring(0, MAX_TEXT) : s;
        }

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

        static void Say(Session s, string line) =>
            s.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
    }

    /// <summary>The leader's Yes/No for an LFG join request (LfgBoard.RequestJoin).</summary>
    sealed class LfgJoinConfirmation : Confirmation
    {
        readonly uint m_Asker;
        public LfgJoinConfirmation(ACE.Entity.ObjectGuid leader, ACE.Entity.ObjectGuid asker)
            : base(leader, ConfirmationType.Yes_No) { m_Asker = asker.Full; }

        public override void ProcessConfirmation(bool response, bool timeout = false) =>
            LfgBoard.Answered(Player, m_Asker, response && !timeout, timeout);
    }
}
