using System.Collections.Concurrent;
using System.Linq;
using ACE.Server.Network;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class TeleportWatchdog
    {
        /// <summary>Retail's own teleports settle in a second or two and ACE re-checks every 0.1 s</summary>
        const double STUCK_SECONDS = 20.0;

        /// <summary>When the gentle arms have not worked, ACE's own remedy.</summary>
        const double FORCE_LOGOFF_SECONDS = 90.0;

        static double CLIENT_PORTAL_SECONDS => TeleportArrival.ClientWaitSeconds(STUCK_SECONDS);

        /// <summary>When each player's client last sent LoginComplete (unix time), from LoginCompleteSeen.</summary>
        internal static readonly ConcurrentDictionary<uint, double> s_ClientArrived = new ConcurrentDictionary<uint, double>();

        /// <summary>The thresholds are 20 s and more; the world loop calls Tick about every millisecond.</summary>
        const double EVERY_SECONDS = 0.5;
        static double s_Next;

        static void Postfix()
        {
            var t = ACE.Common.Time.GetUnixTime();
            if (t < s_Next) return;
            s_Next = t + EVERY_SECONDS;
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("TeleportWatchdog", e); }
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            // ToList: the logoff arm can take a player out of the online list underneath us.
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                if (p == null || !p.Teleporting) continue;
                // Already on the way out: it stays Teleporting and online until FinalizeLogout, so without this the 90 s arm re-fired and warned every tick.
                if (p.IsLoggingOut || p.PKLogout) continue;
                var started = p.LastTeleportStartTimestamp;
                if (started == null || started <= 0) continue;
                var stuck = now - started.Value;
                // The client's own LoginComplete for this teleport: then only the retry chain can be holding it
                bool clientDone = s_ClientArrived.TryGetValue(p.Guid.Full, out var at) && at >= started.Value;
                // ...or our own completion (TeleportArrival's movement arm) already asked for it
                if (!clientDone && TeleportArrival.Requested(p, started.Value)) clientDone = true;
                double grace = clientDone ? 0.0 : CLIENT_PORTAL_SECONDS - STUCK_SECONDS;
                if (stuck < STUCK_SECONDS + grace) continue;

                var who = $"{p.Name} (0x{p.Guid.Full:X8}) was Teleporting for {stuck:F0}s";

                if (!clientDone) who += " (its client never said portal space ended)";
                if (stuck >= FORCE_LOGOFF_SECONDS + grace)
                {
                    Mod.Log.Warn($"[RevivalGuard] teleport watchdog: {who} and did not recover -- "
                        + $"forcing the relog ACE's own five-minute rule intends but never reaches. "
                        + $"The destination it was sent to is the thing to look at: {p.Location}");
                    TeleportArrival.t_ForceWhy = "TeleportWatchdog forced the relog";
                    try { if (p.Session != null) p.Session.LogOffPlayer(true); else p.LogOut(); }
                    finally { TeleportArrival.t_ForceWhy = null; }
                    continue;
                }

                var lb = p.CurrentLandblock;
                if (lb != null && lb.CreateWorldObjectsCompleted)
                {
                    TeleportArrival.Complete(p, "the teleport watchdog", clientDone
                        ? "its landblock HAS finished loading, so the re-check chain died with the landblock tick"
                        : $"its client never said portal space ended within rg_tele_arrive_seconds ({CLIENT_PORTAL_SECONDS:F0}s); landblock loaded");
                    continue;
                }

                // Case 2: give the player their client back and let them move under their own power.
                p.Teleporting = false;
                Mod.Log.Warn($"[RevivalGuard] teleport watchdog: {who} and is now released. Its "
                    + $"landblock {(lb == null ? "is null" : "never reported CreateWorldObjectsCompleted")} "
                    + $"-- the destination it was sent to is the thing to look at: {p.Location}");
            }
        }
    }

    /// <summary>Notes each client's LoginComplete (the end of its portal space) for TeleportWatchdog.</summary>
    [HarmonyPatch(typeof(GameActionLoginComplete), nameof(GameActionLoginComplete.Handle))]
    public static class LoginCompleteSeen
    {
        static void Prefix(Session session)
        {
            try
            {
                var p = session?.Player;
                if (p != null) TeleportWatchdog.s_ClientArrived[p.Guid.Full] = ACE.Common.Time.GetUnixTime();
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] LoginCompleteSeen: {e.Message}"); }
        }
    }
}
