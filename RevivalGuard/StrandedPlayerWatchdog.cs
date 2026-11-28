using System.Collections.Generic;
using System.Linq;
using ACE.Server.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A player in no landblock is relogged.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class StrandedPlayerWatchdog
    {
        internal const string P_ON = "rg_stranded_watchdog";
        static double LIMIT => Tunables.D("stranded.seconds", 15.0);
        static double s_Next;

        sealed class Seen { public bool EverPlaced; public double Since; }
        static readonly Dictionary<uint, Seen> s_Seen = new Dictionary<uint, Seen>();

        static void Postfix()
        {
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("StrandedPlayerWatchdog", e); }
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 1.0;
            if (!PropertyManager.GetBool(P_ON, true).Item || ServerManager.ShutdownInProgress) { s_Seen.Clear(); return; }

            var online = new HashSet<uint>();
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                if (p == null) continue;
                uint guid = p.Guid.Full;
                online.Add(guid);
                if (!s_Seen.TryGetValue(guid, out var s)) s_Seen[guid] = s = new Seen();
                bool placed = p.CurrentLandblock != null;
                if (placed) s.EverPlaced = true;
                double age = LandblockUnloadRule.StrandedAge(placed, p.Teleporting, p.IsLoggingOut || p.PKLogout, ref s.Since, now);
                if (!LandblockUnloadRule.Stranded(s.EverPlaced, age, LIMIT)) continue;

                Mod.Log.Warn($"[RevivalGuard] stranded watchdog: {p.Name} (0x{guid:X8}) has been in no landblock for {age:F0}s "
                    + $"(not teleporting or logging out) at {p.Location}; nothing ticks them, so forcing the relog. "
                    + BusyLedger.Describe(p));
                s.Since = 0;
                s.EverPlaced = false;
                if (p.Session != null) p.Session.LogOffPlayer(true); else p.LogOut();
            }
            foreach (var g in s_Seen.Keys.Where(k => !online.Contains(k)).ToList()) s_Seen.Remove(g);
        }
    }
}
