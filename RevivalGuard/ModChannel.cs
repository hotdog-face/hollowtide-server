using System.Runtime.CompilerServices;
using System.Text;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Managers;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>
    /// ONE STRUCTURED CHANNEL FROM THIS MOD TO OUR CLIENT (docs/MODERNIZATION-IDEAS-2026-09-25.md,
    /// "Data between the server mod and the client travels as tagged chat lines"). Ours.
    ///
    /// Until now each feature that needed data on the client (the journal's @questlog) answered in
    /// chat lines starting with a control character the client hid. Chat is throttled, logged, and
    /// capped in length; every new feature (the looking-for-group board next) would have added
    /// another tag. This is a game message of its own, opcode 0x52474D31 ("RGM1", far outside
    /// retail's range), carrying a channel name and a UTF-8 payload of any length (ACE fragments
    /// it like any other message):
    ///
    ///     u32 opcode | u16 channel length | channel (ASCII) | u32 payload length | payload (UTF-8)
    ///
    /// A retail client must never receive it, so it is sent only to a CONNECTION that asked for it:
    /// our client sends "@revivalclient 1" each time it enters the world, and the answer is a
    /// "hello" on the channel. The mark lives on the Session object (a new login is a new Session),
    /// so a character that later logs in with a retail client is not sent anything it cannot read.
    /// </summary>
    public static class ModChannel
    {
        public const uint OPCODE = 0x52474D31;
        public const int VERSION = 1;

        sealed class Mark { public int Version; public string Client = ""; public string Os = ""; public string Lang = ""; }
        static readonly ConditionalWeakTable<Session, Mark> s_Capable = new ConditionalWeakTable<Session, Mark>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Hello, "revivalclient", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client announces that it reads the mod channel.", "<channel version> [client version] [os] [system language]");
            // Features that exist only on this channel register with it. Both are off by default
            // (rg_expansion_motions, rg_nether_rift): docs/NEW-ANIMATIONS.md.
            ExpansionMotions.Register();
            NetherRift.Register();
            ShopsFor.Register();   // the supply run's "which shop sells it" (on, our client only)
            CommandManager.TryAddCommand(AskRecalls, "recalls", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client asks for its lifestone and portal ties again (answered on the mod channel).", "");
        }

        static void Hello(Session session, params string[] parameters)
        {
            if (session?.Player == null) return;
            int v = 1;
            if (parameters != null && parameters.Length > 0) int.TryParse(parameters[0], out v);
            // THE CLIENT'S OWN VERSION (from 0.1.68; owner 2026-10-19: "the client tells the server its version so very old
            // clients can be turned away later"): "@revivalclient 1 0.1.68 mac English" (the last word: the computer's language,
            // Unity's SystemLanguage, for the localization question). Older clients send the channel version
            // only, so an empty Client means older than 0.1.68. Recorded as a "client" event (Telemetry) once per hello.
            string client = parameters != null && parameters.Length > 1 ? Clean(parameters[1]) : "";
            string os = parameters != null && parameters.Length > 2 ? Clean(parameters[2]) : "";
            string lang = parameters != null && parameters.Length > 3 ? Clean(parameters[3]) : "";
            s_Capable.Remove(session);
            s_Capable.Add(session, new Mark { Version = v, Client = client, Os = os, Lang = lang });
            if (Gate(session, client)) return;
            try { Telemetry.Emit("client", session.Player, ",\"v\":" + Telemetry.J(client.Length > 0 ? client : "<0.1.68") + ",\"os\":" + Telemetry.J(os) + ",\"lang\":" + Telemetry.J(lang)); } catch { }
            Send(session, "hello", "{\"v\":" + VERSION + "}");
            ItemLock.Push(session);   // the client's lock marks come from the items themselves
            FriendsAtSelect.NoteCapable(session);   // this account may be sent the select-screen friends
            RagdollCorpse.Hello(session);           // this shard places ragdoll corpses for opted-in clients
            WorldBossSchedule.Hello(session);       // the world bosses' schedule and state, for the map
            SmallHitbox.Hello(session);             // weenies that collide smaller than their spheres (Azimar)
            AzimarTheme.Hello(session);             // Azimar's festival theme, for his hats and sparkles
            FestivalChannel.Hello(session);         // the running festivals (the Lightbringer's red water)
            TreeStatsUpload.Hello(session);         // the TreeStats switch's state (and that the feature is here)
            DerethChatHost.Hello(session);          // the Dereth channel (other servers): the Cross-Server tab and its on/off
            Drinking.Hello(session);                // a character that relogs while drunk keeps its sway
            SendRecalls(session);                   // the lifestone, for the tracker's recall routes
            WhatsNew.Hello(session);                // last logout, for the client's what's-new card
        }

        /// <summary>"recalls": the character's lifestone as map coordinates (F7 261023-0031: a character that never
        /// used /ls was routed 3 km on foot, its lifestone 10 m from the portal). ACE sends the client no recall
        /// destinations; a new character's Sanctuary is where it was made. Outdoor lifestones only (the route data's
        /// surface places). Also both portal ties, t1/t2 (F7 261120-0003: the client's guessed secondary tie went stale;
        /// ACE never sends LinkedPortalOne/TwoDID): null for none, else {"w":portal wcid,"d":[ns,ew,"lb hex if indoors"]},
        /// the portal's Destination as a tie recall lands. "@recalls" asks again (the client, after a new tie).</summary>
        internal static void SendRecalls(Session session)
        {
            var p = session?.Player;
            if (p == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{");
            var sp = p.Sanctuary;
            if (sp != null && (sp.Cell & 0xFFFF) < 0x100)
            {
                MapXY(sp.LandblockId.Raw, sp.PositionX, sp.PositionY, out float ns, out float ew);
                sb.Append(string.Format(inv, "\"ls\":[{0:0.0},{1:0.0}],", ns, ew));
            }
            sb.Append("\"t1\":").Append(Tie(p.LinkedPortalOneDID)).Append(",\"t2\":").Append(Tie(p.LinkedPortalTwoDID)).Append('}');
            Send(session, "recalls", sb.ToString());
        }

        static void MapXY(uint lb, float x, float y, out float ns, out float ew)
        {
            ew = (((lb >> 24) & 0xFF) * 192f + x) / 240f - 101.95f;
            ns = (((lb >> 16) & 0xFF) * 192f + y) / 240f - 101.95f;
        }

        /// <summary>One tie as "recalls" says it; "null" when none or its portal is gone.</summary>
        static string Tie(uint? wcid)
        {
            try
            {
                if (wcid == null || wcid.Value == 0) return "null";
                var w = ACE.Database.DatabaseManager.World.GetCachedWeenie(wcid.Value);
                var d = w == null ? null : ACE.Entity.Models.WeenieExtensions.GetPosition(w, ACE.Entity.Enum.Properties.PositionType.Destination);
                if (d == null) return "{\"w\":" + wcid.Value + "}";
                uint cell = d.Cell;
                MapXY(cell, d.PositionX, d.PositionY, out float ns, out float ew);
                string hub = (cell & 0xFFFF) >= 0x100 ? (cell >> 16).ToString("x4") : "";
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{{\"w\":{0},\"d\":[{1:0.0},{2:0.0},\"{3}\"]}}", wcid.Value, ns, ew, hub);
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] recalls tie {wcid}: {e.Message}"); return "null"; }
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, System.DateTime> s_RecallsAsked = new();

        /// <summary>"@recalls": our client asks for "recalls" again (a tie just changed). Our client only; one per 2 s.</summary>
        static void AskRecalls(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null || !Capable(session)) return;
            var now = System.DateTime.UtcNow;
            if (s_RecallsAsked.TryGetValue(p.Guid.Full, out var last) && now - last < System.TimeSpan.FromSeconds(2)) return;
            s_RecallsAsked[p.Guid.Full] = now;
            if (s_RecallsAsked.Count > 2000) s_RecallsAsked.Clear();
            SendRecalls(session);
        }

        /// <summary>The client version this connection announced ("" for a client older than 0.1.68, or one that has not
        /// said hello). For a later minimum-version gate.</summary>
        public static string ClientVersion(Session session) =>
            session != null && s_Capable.TryGetValue(session, out var m) ? m.Client : "";

        // THE MINIMUM CLIENT (owner 2026-10-19: "so very old clients can be turned away later"). OFF until set:
        //   rg_min_client          a version such as "0.1.68"; a client that reports a lower one is told to update and
        //                          disconnected. Empty (the default) = off.
        //   rg_min_client_strict   true: also turn away our clients too old to report a version (before 0.1.68).
        // Live ACE string/bool properties, so the owner's admin panel can change them without a restart. Staff are never
        // turned away. Retail clients never say hello and are not affected.
        internal const string P_MIN_CLIENT = "rg_min_client", P_MIN_STRICT = "rg_min_client_strict";
        const string UPDATE_WHERE = "lumpybutt33.itch.io/hollowtide (or play at hollowtide.org)";

        static bool Gate(Session session, string client)
        {
            try
            {
                string min = PkCommon.Str(P_MIN_CLIENT, "").Trim();
                if (min.Length == 0 || session.AccessLevel > AccessLevel.Player) return false;
                bool tooOld = client.Length > 0 ? CompareVersions(client, min) < 0 : PropertyManager.GetBool(P_MIN_STRICT, false).Item;
                if (!tooOld) return false;
                Mod.Log.Info($"[RevivalGuard] client {(client.Length > 0 ? client : "<0.1.68")} below {min}: {session.Player?.Name} told to update");
                session.Terminate(SessionTerminationReason.AccountBooted,
                    new GameMessageBootAccount($" because this Hollowtide client is out of date. Please update to {min} or newer from {UPDATE_WHERE}"));
                return true;
            }
            catch (System.Exception e) { Mod.Log.Error("[RevivalGuard] min client gate: " + e.Message); return false; }
        }

        /// <summary>"0.1.9" against "0.1.10": numeric parts left to right, a missing part is 0 (as the client's
        /// AcUpdateCheck.Compare).</summary>
        internal static int CompareVersions(string a, string b)
        {
            var pa = (a ?? "").Split('.'); var pb = (b ?? "").Split('.');
            for (int i = 0; i < System.Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length && int.TryParse(pa[i], out var u) ? u : 0;
                int y = i < pb.Length && int.TryParse(pb[i], out var w) ? w : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        /// <summary>A version or OS word from a player's command line: digits, letters, dots and dashes, at most 24.</summary>
        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "")
                if (sb.Length < 24 && (char.IsLetterOrDigit(ch) || ch == '.' || ch == '-')) sb.Append(ch);
            return sb.ToString();
        }

        /// <summary>Does this connection read the channel?</summary>
        public static bool Capable(Session session) => session != null && s_Capable.TryGetValue(session, out _);

        public static void Send(Session session, string channel, string payload)
        {
            if (session?.Network == null) return;
            session.Network.EnqueueSend(new Message(channel, payload));
        }

        sealed class Message : GameMessage
        {
            public Message(string channel, string payload)
                : base((GameMessageOpcode)OPCODE, GameMessageGroup.UIQueue)
            {
                var c = Encoding.ASCII.GetBytes(channel ?? "");
                Writer.Write((ushort)c.Length);
                Writer.Write(c);
                var b = Encoding.UTF8.GetBytes(payload ?? "");
                Writer.Write((uint)b.Length);
                Writer.Write(b);
            }
        }
    }
}
