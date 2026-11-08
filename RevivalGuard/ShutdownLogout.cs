using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A SERVER SHUTDOWN LOGS EVERYONE OUT AT ONCE, WITH NO LOGOUT ANIMATION (ticket 261107-0013, Mark the Noble). ACE's
    /// shutdown calls Session.LogOffPlayer(true) for every player, and Player.LogOut_Final then plays the LogOut motion and
    /// leaves the body in the world for its length (about 5 s) before FinalizeLogout. The body can still be hit
    /// meanwhile and cannot act: at the nightly restart (2026-11-07 09:30:06 UTC) Mark's Assistant was healing in the
    /// Tusker armory; the logout began 09:30:09, an Armored Tusker killed him at 09:30:11, and he logged out at 09:30:12
    /// with a corpse in the dungeon. Overnight runs ride through that restart on purpose (docs/OVERNIGHT-LEVELING-PLAN.md),
    /// so every one with a monster on it risked the same death. Nobody chose this logout, so during a shutdown
    /// (ServerManager.ShutdownInProgress, set just before the log-off loop) LogOut_Final takes ACE's own skipAnimations
    /// path, the one a death logout uses: FinalizeLogout at once, saved as usual. Switch: rg_shutdown_quick_logout.
    /// </summary>
    [HarmonyPatch(typeof(Player), "LogOut_Final")]
    static class ShutdownLogout
    {
        const string P_ON = "rg_shutdown_quick_logout";

        static void Prefix(Player __instance, ref bool skipAnimations)
        {
            try
            {
                if (skipAnimations || !ServerManager.ShutdownInProgress) return;
                if (!PropertyManager.GetBool(P_ON, true).Item) return;
                skipAnimations = true;
                Mod.Log.Info($"[RevivalGuard] ShutdownLogout: {__instance?.Name} logged out at once (server shutting down)");
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] ShutdownLogout: {e.Message}"); }
        }
    }
}
