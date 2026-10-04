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
    /// <summary>
    /// WHO IS ONLINE, AND WHO AMONG THEM HAS STOPPED MOVING.
    ///
    /// From docs/ADMIN-AND-VENUE-IDEAS.md §2, written at the owner's request for "a tool I'd likely
    /// need as an admin and a tester before and while the game has lots of players using it":
    ///
    ///   1. A live population view. Who is online, where, level, account, how long since they moved.
    ///   2. A "who is stuck" list. Players whose position has not changed and whose client is still
    ///      talking. This is the single most common live-game support call and it can be DETECTED
    ///      rather than reported.
    ///
    /// The second one is the point. ACE ships `@listplayers`, which gives names and access levels
    /// and nothing else -- not where anyone is, and not whether they have moved since they logged
    /// in. A stuck player is invisible to it, and a stuck player who does not know how to file a
    /// ticket is invisible full stop: they log off and do not come back, and nothing anywhere
    /// records that it happened.
    ///
    /// This project has now produced the wedge twice on its own bots (see
    /// <see cref="TeleportWatchdog"/>), which is the argument for detecting it rather than waiting
    /// to be told. The watchdog fixes the case it understands; this finds the ones it does not.
    ///
    ///   @who            everyone online: name, level, account, where, and how long since they moved
    ///   @who <name>     just that one, with more detail
    ///   @stuck          only those who have not moved in a while, or whose teleport never finished
    ///   @stuck <mins>   a different threshold than the default five minutes
    ///
    /// HOW "HAS NOT MOVED" IS DECIDED. A sample of every online player's position, taken on the same
    /// `PlayerManager.Tick` the teleport watchdog uses -- the world loop, which keeps running for a
    /// player whose landblock has stopped ticking, and that is exactly the player we are looking for.
    /// Sampling is throttled to once every ten seconds, because the question is "minutes since they
    /// moved" and a finer answer costs work on every tick for no extra information.
    ///
    /// A MOVE IS 0.5 m. Small enough to catch someone shuffling on the spot, large enough that
    /// floating-point noise in a saved position does not read as movement. Standing still is not the
    /// same as being stuck -- someone can be reading, or away -- so this is a list to LOOK at, never
    /// an alarm and never something that acts on a player by itself. It says who to ask.
    /// </summary>
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
        static readonly object s_Lock = new object();
        static double s_NextSample;

        static void Postfix()
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
                // Forget anyone who has logged off, so the table cannot grow without bound across a
                // long uptime. Their next login starts a fresh sample, which is correct -- a login
                // IS a move.
                foreach (var gone in s_Seen.Keys.Where(k => !live.Contains(k)).ToList())
                    s_Seen.Remove(gone);
            }
        }

        internal static void Register()
        {
            // `@who`, NOT `@pop`. ACE already ships a `pop` command (PlayerCommands.cs:26, and it is
            // AccessLevel.Player, so every player has it) which answers "Current world population: 1"
            // -- one number, no positions, no idle times. Registering ours under the same name on
            // 2026-09-21 did NOT override it: the deployed shard answered ACE's line, and the richer
            // listing this class exists for was simply unreachable. Checked before renaming: ACE has
            // no handler for who, online, players, roster, stuck, tickets, ticket or festival.
            CommandManager.TryAddCommand(Pop, "who", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "Who is online, where, and how long since they moved.", "[name]");
            CommandManager.TryAddCommand(Stuck, "stuck", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "Players who have stopped moving, or whose teleport never finished.", "[minutes]");
        }

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static string Still(double seconds)
        {
            if (seconds < 60) return $"{(int)seconds}s";
            if (seconds < 3600) return $"{(int)(seconds / 60)}m";
            return $"{seconds / 3600:0.#}h";
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
            return $"{p.Name} (lvl {p.Level}, {p.Account?.AccountName ?? "?"})  {where}  "
                 + $"still {(still < 0 ? "?" : Still(still))}{(p.Teleporting ? "  TELEPORTING" : "")}";
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

            // Teleporting is listed however recently they moved: an unfinished teleport is the one
            // failure we KNOW locks a player out of acting, and it does not look like standing still.
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
}
