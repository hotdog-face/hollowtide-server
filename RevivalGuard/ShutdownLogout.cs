using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A server shutdown LOGS everyone out at once, with no logout animation.</summary>
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
