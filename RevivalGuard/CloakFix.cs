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
    /// <summary>A saved cloak is a real cloak.</summary>
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
