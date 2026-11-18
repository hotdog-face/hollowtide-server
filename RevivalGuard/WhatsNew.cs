using System;
using System.Globalization;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace RevivalGuard
{
    /// <summary>
    /// "WHAT CHANGED SINCE YOU WERE LAST HERE" (owner 2026-11-17, "Yes"; docs/findings/whats-new-card-2026-11-17.md). Our
    /// client shows a returning player a short card of the release-note lines newer than their last visit (AcWhatsNew).
    /// The last visit is the server's, so it is right on a new computer or browser too: on the mod channel's hello the
    /// client is sent "whatsnew" with
    ///   own   this character's last logout (ACE's PropertyFloat.LogoffTimestamp, set only at logout, so at login it is
    ///         still the previous one); 0 = this character has never logged out (a new character: no card)
    ///   last  the latest logout of any character on the account (an alt's yesterday counts, so a main played yesterday
    ///         does not get three weeks of news on an alt)
    ///   now   the server's clock, so the client measures the gap without trusting its own
    /// Switch: rg_whats_new (default on); off sends nothing and the client falls back to what it remembers itself.
    /// </summary>
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
                // a delayed PK logout sets it ahead of time; never later than now
                own = Math.Min(own, now); last = Math.Min(last, now);
                ModChannel.Send(session, "whatsnew", string.Format(CultureInfo.InvariantCulture,
                    "{{\"own\":{0:0},\"last\":{1:0},\"now\":{2:0}}}", own, last, now));
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] WhatsNew: " + e.Message); }
        }
    }
}
