using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE HOLLOW COMPANY IS NEVER COUNTED AS PEOPLE (owner, 2026-10-07: "a feature, not some deceptive plan
    /// to fake population"). The mercenaries (tools/mercs, docs/MERCENARIES.md) are real game sessions on
    /// accounts named hcmerc*, so ACE counts them as players everywhere. This leaves them out of:
    ///   * the login screen's "N online" (PopulationQuery),
    ///   * the world-event director's minimum-players check (EventDirector),
    ///   * the gameplay telemetry (Telemetry marks them "merc":1, and the reports skip them),
    /// and /pop, which names everyone online but them.
    /// </summary>
    public static class Mercenaries
    {
        const string PREFIX = "hcmerc";

        public static bool IsMerc(Player p) =>
            p?.Account?.AccountName != null && p.Account.AccountName.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase);

        public static int RealOnline()
        {
            try { return PlayerManager.GetAllOnline().Count(p => !IsMerc(p)); }
            catch { return PlayerManager.GetOnlineCount(); }
        }

        public static int MercsOnline()
        {
            try { return PlayerManager.GetAllOnline().Count(IsMerc); }
            catch { return 0; }
        }

        // ---- /mercfollow: a sellsword rejoins its leader ----------------------------------------------------
        //
        // Owner, 2026-10-07: Sefa "followed me in [to a dungeon] but then disappeared". A merc follows by
        // walking straight at its leader (tools/mercs/merc.py), and a dungeon corner stops it; past the corner
        // the server stops showing it, and it is lost. Real pathfinding is a lot of work for a feature the owner
        // may retire, so the merc's brain asks for this instead when it has lost its leader: the server puts it
        // a step behind them. MERC ACCOUNTS ONLY (IsMerc), the leader of its OWN fellowship only, and only when
        // the leader is out of reach (another landblock, or more than CATCHUP_MIN metres away).
        const float CATCHUP_MIN = 15f;

        public static void Register()
        {
            CommandManager.TryAddCommand(HandleFollow, "mercfollow", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Hollow Company only: rejoin your fellowship leader.", "");
        }

        static void HandleFollow(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null || !IsMerc(p)) return;
            var f = p.Fellowship;
            if (f == null || f.FellowshipLeaderGuid == p.Guid.Full) return;
            var leader = PlayerManager.GetOnlinePlayer(f.FellowshipLeaderGuid);
            if (leader?.Location == null || p.Location == null || leader.Teleporting) return;
            bool far = leader.Location.LandblockId.Landblock != p.Location.LandblockId.Landblock
                    || leader.Location.DistanceTo(p.Location) > CATCHUP_MIN;
            if (!far) return;
            var to = new ACE.Entity.Position(leader.Location);
            p.Teleport(to);
            Mod.Log.Info($"[RevivalGuard] {p.Name} rejoined {leader.Name} (mercfollow)");
        }
    }

    /// <summary>/pop: WHO is online, not only how many (owner, 2026-10-13: "make /pop list all players online for all
    /// players. dont include the mercenaries in that"). Every player's name, sorted, a dozen to a line; the Hollow
    /// Company is neither counted nor named.</summary>
    [HarmonyPatch(typeof(PlayerCommands), nameof(PlayerCommands.HandlePop))]
    public static class MercenaryPop
    {
        const int PER_LINE = 12;

        static bool Prefix(Session session)
        {
            List<string> names;
            try { names = PlayerManager.GetAllOnline().Where(p => !Mercenaries.IsMerc(p)).Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { names = new List<string>(); }
            void Say(string line)
            {
                if (session == null) Console.WriteLine(line);
                else session.Network?.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
            }
            Say($"Current world population: {names.Count:N0}");
            for (int i = 0; i < names.Count; i += PER_LINE)
                Say((i == 0 ? "Online: " : "  ") + string.Join(", ", names.Skip(i).Take(PER_LINE)));
            return false;
        }
    }
}
