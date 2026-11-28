using System;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Creature), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
    public static class DeathGuard
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Creature __instance)
        {
            if (__instance != null && __instance.PhysicsObj == null)
            {
                Mod.Log.Warn($"[RevivalGuard] death skipped: {__instance.Name} 0x{__instance.Guid.Full:X8} has no physics object (leaving the world)");
                return false;
            }
            return true;
        }

        static Exception Finalizer(Creature __instance, Exception __exception)
        {
            if (__exception != null)
                Mod.Log.Error($"[RevivalGuard] death of {__instance?.Name} 0x{__instance?.Guid.Full:X8} threw, kept the server up: {__exception}");
            return null;
        }
    }
}
