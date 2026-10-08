using System;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CREATURE'S DEATH MUST NEVER TAKE THE WORLD DOWN WITH IT.
    ///
    /// 2026-10-05, 22:47 to 23:30 UTC, the live server aborted three times, each time the same way:
    /// a NullReferenceException in Creature.Die(lastDamager, topDamager), reached through
    /// WorldObject.Destroy, ProcessGeneratorDestructionDirective, GeneratorProfile.KillAll and Smite,
    /// on the landblock's multi-threaded tick. An unhandled exception there is the whole process
    /// (systemd: "code=dumped, status=6/ABRT"), so every player went with it.
    ///
    /// What Die dereferences without a check, after the dieEntered latch, is PhysicsObj
    /// (StopCompletely) and EmoteManager. A generator torn down while its landblock unloads kills
    /// spawns that the unload may already have taken out of the physics world, and those have no
    /// PhysicsObj. Our own two Die prefixes (RagdollCorpse.NoteDeath, Telemetry.Kill) catch everything
    /// they throw, so the throw was in the original body.
    ///
    /// Two guards:
    ///   * Prefix: a creature with no PhysicsObj is already leaving the world; its death is skipped
    ///     (no corpse for a body that is not there). The Destroy that is underway finishes the job.
    ///   * Finalizer: anything else Die throws is logged with the creature named and swallowed, so a
    ///     bad death costs one creature, not the server.
    /// </summary>
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
