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
    /// <summary>One structured channel from this mod to our client.</summary>
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
            // Features that exist only on this channel register with it.
            ExpansionMotions.Register();
            NetherRift.Register();
            ExperimentalSpells.Register();   // drafts, off by default (rg_experimental_spells)
            ShopsFor.Register();   // the supply run's "which shop sells it" (on, our client only)
            CommandManager.TryAddCommand(AskRecalls, "recalls", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client asks for its lifestone and portal ties again (answered on the mod channel).", "");
        }

        static void Hello(Session session, params string[] parameters)
        {
            if (session?.Player == null) return;
            int v = 1;
            if (parameters != null && parameters.Length > 0) int.TryParse(parameters[0], out v);
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
            ChessLobby.Hello(session);
            WhatsNew.Hello(session);                // last logout, for the client's what's-new card
            AwayMessage.Push(session);              // the away message's state, for both Assistants' boxes (Feature Requests #33)
        }

        /// <summary>"recalls": the character's lifestone as map coordinates.</summary>
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

        /// <summary>"@recalls": our client asks for "recalls" again (a tie just changed).</summary>
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

        public static string ClientVersion(Session session) =>
            session != null && s_Capable.TryGetValue(session, out var m) ? m.Client : "";

        // The minimum client.
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
