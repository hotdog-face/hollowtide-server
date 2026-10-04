using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// NEW HUMAN ANIMATIONS THE SERVER CAN TRIGGER (docs/NEW-ANIMATIONS.md). Ours, not retail.
    /// ON BY DEFAULT since 2026-09-28 (owner approved Flex permanently): server property rg_expansion_motions.
    /// Off, both commands do nothing at all and nothing is ever sent.
    ///
    /// A new clip cannot go in the DAT, and a new MotionCommand on the wire would reach retail clients
    /// that cannot read it. So the clip lives in our client (expansion/animations/human) and the
    /// trigger travels on the mod channel (ModChannel, opcode RGM1), and only to a session that asked:
    ///
    ///   @rgexpansion 1      our client, after the mod channel's hello, when its own switch is on.
    ///                       Marks the SESSION (a new login is a new session, as ModChannel does), so a
    ///                       character that logs in later with a retail client is sent nothing.
    ///   @rgemote &lt;trigger&gt;  an emote the DAT does not have ("flex"). Everyone in range gets the emote
    ///                       TEXT, which every client reads ("Aldric flexes his muscles."); opted-in
    ///                       clients also get "xanim" {g, m} and play the clip on that body. The sender
    ///                       animated and printed its own line already, so it is sent neither.
    ///
    /// Also the channel NetherRift uses for its cast gesture ("xover") and effect ("xfx").
    /// </summary>
    static class ExpansionMotions
    {
        internal const string P_ON = "rg_expansion_motions";
        const float EMOTE_RANGE = 30f;         // WorldObject.LocalBroadcastRange, where ACE's own emote text reaches
        const double EMOTE_COOLDOWN = 1.5;

        /// <summary>The emotes, as the DAT's ChatEmoteHash words them ("%s" is his/her).</summary>
        static readonly Dictionary<string, (string motion, string others)> s_Emotes = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["flex"] = ("Flex", "flexes %s muscles."),
            ["muscles"] = ("Flex", "flexes %s muscles."),
            ["strongman"] = ("Flex", "flexes %s muscles."),
        };

        sealed class Mark { public DateTime LastEmote; }
        static readonly ConditionalWeakTable<Session, Mark> s_OptedIn = new ConditionalWeakTable<Session, Mark>();

        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;   // owner-approved permanent 2026-09-28

        /// <summary>This connection asked for expansion messages (and the switch is on).</summary>
        internal static bool OptedIn(Session s) => s != null && On && s_OptedIn.TryGetValue(s, out _) && ModChannel.Capable(s);

        internal static void Register()
        {
            RegisterProperty(P_ON, true, "RevivalGuard ExpansionMotions: new human clips and emotes for opted-in clients on the mod channel (docs/NEW-ANIMATIONS.md)");
            CommandManager.TryAddCommand(OptIn, "rgexpansion", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client asks for expansion animations on the mod channel.", "1|0");
            CommandManager.TryAddCommand(Emote, "rgemote", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "An expansion emote (our client).", "<trigger>");
        }

        static void OptIn(Session session, params string[] parameters)
        {
            if (session?.Player == null || !On) return;
            bool yes = parameters == null || parameters.Length == 0 || parameters[0] != "0";
            s_OptedIn.Remove(session);
            if (!yes) return;
            s_OptedIn.Add(session, new Mark());
            NetherRift.OnOptIn(session);
        }

        static void Emote(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null || !OptedIn(session) || parameters == null || parameters.Length == 0) return;
            if (!s_Emotes.TryGetValue(parameters[0].Trim('*'), out var e)) return;
            if (!s_OptedIn.TryGetValue(session, out var mark)) return;
            var now = DateTime.UtcNow;
            if ((now - mark.LastEmote).TotalSeconds < EMOTE_COOLDOWN) return;
            mark.LastEmote = now;
            string pronoun = p.Gender == (int)ACE.Entity.Enum.Gender.Female ? "her" : "his";
            string text = e.others.Replace("%s", pronoun);
            string anim = "{\"g\":" + p.Guid.Full + ",\"m\":\"" + e.motion + "\"}";
            foreach (var o in PlayerManager.GetAllOnline())
            {
                if (o == null || o == p || o.Session == null || o.Location == null || p.Location == null) continue;
                if (o.Location.DistanceTo(p.Location) > EMOTE_RANGE) continue;
                o.Session.Network.EnqueueSend(new GameMessageEmoteText(p.Guid.Full, p.Name, text));
                if (OptedIn(o.Session)) ModChannel.Send(o.Session, "xanim", anim);
            }
        }

        /// <summary>Send one mod-channel message to every opted-in player within range of a spot.</summary>
        internal static void ToOptedInNear(ACE.Entity.Position at, float range, string channel, string payload)
        {
            if (at == null) return;
            foreach (var o in PlayerManager.GetAllOnline())
            {
                if (o?.Session == null || o.Location == null || !OptedIn(o.Session)) continue;
                if (o.Location.DistanceTo(at) > range) continue;
                ModChannel.Send(o.Session, channel, payload);
            }
        }

        internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Makes a switch an ordinary ACE property so @modifybool accepts it (MonsterAi's
        /// route: the Default*Properties maps are ReadOnlyDictionaries over private dictionaries). If
        /// this fails the switch is still read, at its default, off.</summary>
        internal static void RegisterProperty(string key, bool def, string desc)
        {
            try
            {
                var ro = DefaultPropertyManager.DefaultBooleanProperties;
                var inner = typeof(ReadOnlyDictionary<string, Property<bool>>)
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<bool>>>().FirstOrDefault();
                if (inner != null && !inner.ContainsKey(key)) inner[key] = new Property<bool>(def, desc);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {key}: not registered with @modifybool ({e.Message}); still read, default {def}"); }
        }
    }
}
