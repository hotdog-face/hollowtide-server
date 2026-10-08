using System;
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
    /// and makes /pop say how many sellswords are about, separately.
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

    [HarmonyPatch(typeof(PlayerCommands), nameof(PlayerCommands.HandlePop))]
    public static class MercenaryPop
    {
        static bool Prefix(Session session)
        {
            int real = Mercenaries.RealOnline(), mercs = Mercenaries.MercsOnline();
            string line = $"Current world population: {real:N0}"
                        + (mercs > 0 ? $" (and {mercs} Hollow Company sellsword{(mercs == 1 ? "" : "s")}, who are not players)" : "");
            session?.Network?.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
            return false;
        }
    }
}
