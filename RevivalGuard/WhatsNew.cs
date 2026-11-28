using System;
using System.Globalization;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace RevivalGuard
{
    /// <summary>"what changed since you were last here".</summary>
    static class WhatsNew
    {
        internal const string P_ON = "rg_whats_new";

        internal static void Register() =>
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard WhatsNew: our client is told a character's last logout at login, for its 'what changed since your last visit' card (false: the client uses its own memory)");

        internal static void Hello(Session session)
        {
            var p = session?.Player;
            if (p == null || !PropertyManager.GetBool(P_ON, true).Item) return;
            try
            {
                double own = p.GetProperty(PropertyFloat.LogoffTimestamp) ?? 0, last = own;
                var acct = p.Account?.AccountId;
                if (acct != null)
                    foreach (var c in PlayerManager.GetAccountPlayers(acct.Value).Values)
                        if (c.Guid.Full != p.Guid.Full) last = Math.Max(last, c.GetProperty(PropertyFloat.LogoffTimestamp) ?? 0);
                double now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
                own = Math.Min(own, now); last = Math.Min(last, now);
                ModChannel.Send(session, "whatsnew", string.Format(CultureInfo.InvariantCulture,
                    "{{\"own\":{0:0},\"last\":{1:0},\"now\":{2:0}}}", own, last, now));
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] WhatsNew: " + e.Message); }
        }
    }
}
