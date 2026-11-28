using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Who is online, and who among them HAS stopped moving.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class PresenceWatch
    {
        const double SAMPLE_SECONDS = 10.0;
        const double DEFAULT_STUCK_MINUTES = 5.0;
        const float MOVED_METRES = 0.5f;

        sealed class Seen
        {
            public float X, Y, Z;
            public uint Cell;
            public double MovedAt;     // unix seconds of the last real move
            public double FirstSeen;
        }

        static readonly Dictionary<uint, Seen> s_Seen = new Dictionary<uint, Seen>();
        /// <summary>Unix seconds of each player's last hit dealt or taken (PresenceCombatHook); cleared with s_Seen.</summary>
        static readonly Dictionary<uint, double> s_Hit = new Dictionary<uint, double>();
        static readonly object s_Lock = new object();
        static double s_NextSample;

        static void Postfix()
        {
            try { Sample(); }
            catch (Exception e) { TickGuard.Report("PresenceWatch", e); }
        }

        static void Sample()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_NextSample) return;
            s_NextSample = now + SAMPLE_SECONDS;

            var online = PlayerManager.GetAllOnline();
            var live = new HashSet<uint>();
            lock (s_Lock)
            {
                foreach (var p in online)
                {
                    if (p?.Location == null) continue;
                    live.Add(p.Guid.Full);
                    if (!s_Seen.TryGetValue(p.Guid.Full, out var s))
                    {
                        s_Seen[p.Guid.Full] = new Seen
                        {
                            X = p.Location.PositionX, Y = p.Location.PositionY, Z = p.Location.PositionZ,
                            Cell = p.Location.Cell, MovedAt = now, FirstSeen = now,
                        };
                        continue;
                    }
                    float dx = p.Location.PositionX - s.X, dy = p.Location.PositionY - s.Y,
                          dz = p.Location.PositionZ - s.Z;
                    bool moved = s.Cell != p.Location.Cell
                              || (dx * dx + dy * dy + dz * dz) > MOVED_METRES * MOVED_METRES;
                    if (moved)
                    {
                        s.X = p.Location.PositionX; s.Y = p.Location.PositionY; s.Z = p.Location.PositionZ;
                        s.Cell = p.Location.Cell; s.MovedAt = now;
                    }
                }
                // Forget anyone who has logged off, so the table cannot grow without bound across a long uptime.
                foreach (var gone in s_Seen.Keys.Where(k => !live.Contains(k)).ToList())
                    s_Seen.Remove(gone);
                foreach (var gone in s_Hit.Keys.Where(k => !live.Contains(k)).ToList())
                    s_Hit.Remove(gone);
            }
        }

        internal static void Register()
        {
            CommandManager.TryAddCommand(Pop, "who", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "Who is online, where, and how long since they moved.", "[name]");
            CommandManager.TryAddCommand(Stuck, "stuck", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "Players who have stopped moving, or whose teleport never finished.", "[minutes]");
            CommandManager.TryAddCommand(WhoMap, "whomap", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "Every online player's place, for the admin map's player dots (machine-readable).", "");
        }

        /// <summary>`[WhoMap] begin N`, then lines of up to 8 `name|cell|x|y|kind` joined by `;`, then `[WhoMap] end`.</summary>
        static void WhoMap(Session session, params string[] parameters)
        {
            var online = PlayerManager.GetAllOnline().Where(p => p.Location != null && p.Name != null).ToList();
            Say(session, $"[WhoMap] begin {online.Count}");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var chunk in online.Select((p, i) => (p, i)).GroupBy(t => t.i / 8))
                Say(session, "[WhoMap] " + string.Join(";", chunk.Select(t =>
                {
                    var l = t.p.Location;
                    char kind = !l.Indoors ? 'o' : (t.p.CurrentLandblock?.IsDungeon ?? true) ? 'd' : 'b';
                    return $"{t.p.Name}|{l.Cell:X8}|{l.PositionX.ToString("0.#", inv)}|{l.PositionY.ToString("0.#", inv)}|{kind}";
                })));
            Say(session, "[WhoMap] end");
        }

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static string Still(double seconds) => PresenceStateRule.Span(seconds);

        /// <summary>A hit landed (DamageHistory.Add): both sides, when they are players, are in combat now.</summary>
        internal static void OnHit(WorldObject victim, WorldObject attacker)
        {
            var now = ACE.Common.Time.GetUnixTime();
            lock (s_Lock)
            {
                if (victim is Player v) s_Hit[v.Guid.Full] = now;
                if (attacker is Player a) s_Hit[a.Guid.Full] = now;
            }
        }

        /// <summary>The activity word (PresenceStateRule): portal space, combat, idle, active; Assistant or away first.</summary>
        static string State(Player p, double now)
        {
            double moved, hit;
            lock (s_Lock)
            {
                moved = s_Seen.TryGetValue(p.Guid.Full, out var s) ? s.MovedAt : now;   // not sampled yet: just logged in
                hit = s_Hit.TryGetValue(p.Guid.Full, out var h) ? h : 0;
            }
            bool assistant = p.GetProperty(AwayMessage.ON) == true && p.GetProperty(AwayMessage.AUTO) == true;
            bool away = p.GetProperty(AwayMessage.ON) == true || p.IsAfk;
            return PresenceStateRule.State(now, moved, hit, p.LastSuccessCast_Time, p.Teleporting, assistant, away);
        }

        /// <summary>Seconds since this player last moved, or -1 if we have not sampled them yet.</summary>
        static double StillFor(Player p, double now)
        {
            lock (s_Lock)
                return s_Seen.TryGetValue(p.Guid.Full, out var s) ? now - s.MovedAt : -1;
        }

        static string Line(Player p, double now)
        {
            var still = StillFor(p, now);
            var where = p.Location == null ? "nowhere"
                : $"0x{p.Location.Cell:X8} [{p.Location.PositionX:0.#} {p.Location.PositionY:0.#} {p.Location.PositionZ:0.#}]";
            // "(kind; state)" last: the admin panel (AcAdminWhoRow) reads it for its place and activity words.
            string kind = p.Location == null ? "outdoors" : PresenceStateRule.Kind(p.Location.Indoors, p.CurrentLandblock?.IsDungeon ?? true);
            return $"{p.Name} (lvl {p.Level}, {p.Account?.AccountName ?? "?"})  {where}  "
                 + $"still {(still < 0 ? "?" : Still(still))}{(p.Teleporting ? "  TELEPORTING" : "")}  ({kind}; {State(p, now)})";
        }

        static void Pop(Session session, params string[] parameters)
        {
            var now = ACE.Common.Time.GetUnixTime();
            var online = PlayerManager.GetAllOnline();
            if (parameters.Length > 0)
            {
                var one = online.FirstOrDefault(p =>
                    p.Name != null && p.Name.IndexOf(parameters[0], StringComparison.OrdinalIgnoreCase) >= 0);
                if (one == null) { Say(session, $"No one online matching '{parameters[0]}'."); return; }
                Say(session, Line(one, now));
                Say(session, $"  guid 0x{one.Guid.Full:X8}, access {one.Account?.AccessLevel}, "
                    + $"health {one.Health?.Current}/{one.Health?.MaxValue}"
                    + $"{(one.IsBusy ? ", BUSY" : "")}{(one.Teleporting ? ", TELEPORTING" : "")}");
                return;
            }
            if (online.Count == 0) { Say(session, "Nobody is online."); return; }
            Say(session, $"--- {online.Count} online ---");
            foreach (var p in online.OrderByDescending(p => StillFor(p, now)).Take(30))
                Say(session, Line(p, now));
            if (online.Count > 30) Say(session, $"...and {online.Count - 30} more.");
        }

        static void Stuck(Session session, params string[] parameters)
        {
            double mins = DEFAULT_STUCK_MINUTES;
            if (parameters.Length > 0 && double.TryParse(parameters[0], out var m) && m > 0) mins = m;
            var now = ACE.Common.Time.GetUnixTime();

            // Teleporting is listed however recently they moved
            var hits = PlayerManager.GetAllOnline()
                .Where(p => p.Teleporting || StillFor(p, now) >= mins * 60)
                .OrderByDescending(p => StillFor(p, now))
                .ToList();

            if (hits.Count == 0)
            {
                Say(session, $"Nobody has been still for {mins:0.#} minutes, and no teleport is hanging.");
                return;
            }
            Say(session, $"--- {hits.Count} not moving for {mins:0.#}m or more ---");
            foreach (var p in hits.Take(30)) Say(session, Line(p, now));
            Say(session, "Standing still is not the same as stuck -- ask before acting. "
                + "@who <name> for detail, @teleto <name> to go and look.");
        }
    }

    /// <summary>Every hit, melee, missile or spell, goes through DamageHistory.Add (as BaelShadowPullHook): marks combat.</summary>
    [HarmonyPatch(typeof(ACE.Server.Entity.DamageHistory), nameof(ACE.Server.Entity.DamageHistory.Add))]
    static class PresenceCombatHook
    {
        static void Postfix(ACE.Server.Entity.DamageHistory __instance, WorldObject attacker)
        {
            try { PresenceWatch.OnHit(__instance.Creature, attacker); }
            catch (Exception e) { TickGuard.Report("PresenceCombatHook", e); }
        }
    }
}
