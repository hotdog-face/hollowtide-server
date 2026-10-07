using System;
using System.Linq;
using ACE.Entity.Enum;
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
