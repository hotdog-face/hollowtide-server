using System;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.Actions;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A SAVED CLOAK IS A REAL CLOAK. Owner, 2026-09-29: "the cloaked command isnt working in admin panel.
    /// i want to travel through the locked door."
    ///
    /// ACE saves CloakStatus (On = 2) with the character but not the physics that make it work (Cloaked,
    /// Ethereal, NoDraw, no collision reports), and nothing re-applies them at login. So an admin who logged
    /// out cloaked comes back half-cloaked: solid, blocked by doors. And `@cloak on` then does nothing,
    /// because both SentinelCommands.HandleCloak and Player.HandleCloak return early when the saved status
    /// already says On. Two patches:
    ///   PlayerEnterWorld postfix   a saved On is applied for real on entering the world.
    ///   HandleCloak prefix         "@cloak on" while the status already reads On re-applies it (the
    ///                              status is cleared first so ACE's own path runs to the end).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class CloakFixOnLogin
    {
        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance;
                if (p == null || p.CloakStatus != CloakStatus.On) return;
                var chain = new ActionChain();
                chain.AddDelaySeconds(1.0);   // after the login's own physics state has gone out
                chain.AddAction(p, () =>
                {
                    p.Cloaked = true;
                    p.Ethereal = true;
                    p.NoDraw = true;
                    p.ReportCollisions = false;
                    p.EnqueueBroadcastPhysicsState();
                });
                chain.EnqueueChain();
            }
            catch (Exception e) { Mod.Log.Warn($"[CloakFix] login: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(SentinelCommands), nameof(SentinelCommands.HandleCloak))]
    static class CloakFixCommand
    {
        static void Prefix(Session session, string[] parameters)
        {
            try
            {
                var p = session?.Player;
                if (p == null || parameters == null || parameters.Length == 0) return;
                if (parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase) && p.CloakStatus == CloakStatus.On)
                    p.SetProperty(PropertyInt.CloakStatus, (int)CloakStatus.Off);
            }
            catch (Exception e) { Mod.Log.Warn($"[CloakFix] command: {e.Message}"); }
        }
    }
}
