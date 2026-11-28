using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>@BROADCAST: a world-wide message with a look before it goes, because a typo goes to everyone.</summary>
    public static class Broadcast
    {
        const double EXPIRE_SECONDS = 120;

        sealed class Staged { public string Text; public DateTime At; }

        static readonly ConditionalWeakTable<Session, Staged> s_Staged = new ConditionalWeakTable<Session, Staged>();
        static Staged s_Console;   // the server console has no Session
        static readonly object s_Lock = new object();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "broadcast", AccessLevel.Envoy, CommandHandlerFlag.None,
                "Stage a message for everyone online and see it as they will; then @broadcast send, or @broadcast cancel.",
                "<message> | send | cancel");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static string Line(Session s, string text) => $"Broadcast from {(s != null ? s.Player?.Name : "System")}> {text}";

        static Staged Get(Session s)
        {
            lock (s_Lock)
            {
                var st = s == null ? s_Console : (s_Staged.TryGetValue(s, out var v) ? v : null);
                if (st == null) return null;
                if ((DateTime.UtcNow - st.At).TotalSeconds > EXPIRE_SECONDS) { Clear(s); return null; }
                return st;
            }
        }

        static void Set(Session s, Staged st)
        {
            lock (s_Lock)
            {
                if (s == null) { s_Console = st; return; }
                s_Staged.Remove(s);
                s_Staged.Add(s, st);
            }
        }

        static void Clear(Session s)
        {
            lock (s_Lock)
            {
                if (s == null) s_Console = null;
                else s_Staged.Remove(s);
            }
        }

        static void Handle(Session session, params string[] parameters)
        {
            int online = PlayerManager.GetAllOnline().Count;

            if (parameters.Length == 0)
            {
                var st = Get(session);
                if (st == null)
                {
                    Say(session, "Nothing is staged. @broadcast <message> shows you the line everyone would see; @broadcast send sends it.");
                    return;
                }
                Say(session, $"Staged for {online} online:");
                session?.Network?.EnqueueSend(new GameMessageSystemChat(Line(session, st.Text), ChatMessageType.WorldBroadcast));
                if (session == null) Console.WriteLine(Line(null, st.Text));
                Say(session, "@broadcast send to send it, @broadcast cancel to drop it.");
                return;
            }

            var word = parameters[0].Trim().ToLowerInvariant();
            if (parameters.Length == 1 && (word == "send" || word == "yes"))
            {
                var st = Get(session);
                if (st == null)
                {
                    Say(session, "Nothing is staged (a staged line is dropped after two minutes). @broadcast <message> first.");
                    return;
                }
                Clear(session);
                // ACE's own world broadcast: the retail line, ChatMessageType.WorldBroadcast, and the AllBroadcast chat log.
                AdminCommands.HandleGamecast(session, new[] { st.Text });
                Say(session, $"Sent to {online} online.");
                Mod.Log.Info($"[RevivalGuard] @broadcast by {session?.Player?.Name ?? "console"} to {online}: {st.Text}");
                return;
            }
            if (parameters.Length == 1 && (word == "cancel" || word == "no"))
            {
                bool had = Get(session) != null;
                Clear(session);
                Say(session, had ? "Dropped. Nothing was sent." : "Nothing was staged.");
                return;
            }

            var text = string.Join(" ", parameters).Trim();
            if (text.Length == 0)
            {
                Say(session, "Usage: @broadcast <message>, then @broadcast send or @broadcast cancel.");
                return;
            }
            Set(session, new Staged { Text = text, At = DateTime.UtcNow });

            // The preview goes to the sender on the World_Broadcast type, so it reads exactly as it will for everyone: same prefix, same channel colour.
            Say(session, $"Not sent yet. Everyone online ({online}) would see:");
            if (session?.Network != null)
                session.Network.EnqueueSend(new GameMessageSystemChat(Line(session, text), ChatMessageType.WorldBroadcast));
            else
                Console.WriteLine(Line(null, text));
            Say(session, "@broadcast send to send it to everyone, or @broadcast cancel. It is dropped after two minutes.");
        }
    }
}
