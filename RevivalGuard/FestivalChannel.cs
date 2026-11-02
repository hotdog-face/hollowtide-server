using System;
using System.Linq;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace RevivalGuard
{
    /// <summary>
    /// The running festivals' keys for the client, on mod channel "festivals" ("lightbringer,frostfell"; empty when
    /// none). Sent after the hello and to everyone when the set changes (SeasonalCalendar's minute tick), so a GM's
    /// @festival on/off reaches the client too; its own calendar cannot see that. The client's first use: the
    /// Lightbringer's red water (AcBloodWater, docs/findings/lightbringer-red-water.md).
    /// </summary>
    static class FestivalChannel
    {
        const string CHANNEL = "festivals";
        static string s_Sent;

        internal static string Current() =>
            string.Join(",", SeasonalCalendar.Calendar.Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase)
                                             .Where(SeasonalCalendar.IsFestivalActive));

        internal static void Hello(Session session)
        {
            try { ModChannel.Send(session, CHANNEL, Current()); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalChannel hello: {e.Message}"); }
        }

        internal static void Tick()
        {
            var now = Current();
            if (s_Sent == null) { s_Sent = now; return; }   // the first tick: every hello already had it
            if (now == s_Sent) return;
            s_Sent = now;
            foreach (var p in PlayerManager.GetAllOnline())
                if (p?.Session != null && ModChannel.Capable(p.Session)) ModChannel.Send(p.Session, CHANNEL, now);
            Mod.Log.Info($"[RevivalGuard] FestivalChannel: running now {(now.Length > 0 ? now : "none")}");
        }
    }
}
