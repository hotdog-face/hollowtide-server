using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A monster notices a player who is standing still.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Heartbeat))]
    static class IdleAwareness
    {
        static void Postfix(Player __instance)
        {
            try { if (__instance?.PhysicsObj != null && __instance.Location != null) __instance.CheckMonsters(); }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] idle awareness: {e.Message}"); }
        }
    }
}
