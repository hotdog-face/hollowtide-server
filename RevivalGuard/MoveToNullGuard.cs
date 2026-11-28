using System;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A finished move-to with no parameters cannot crash the server.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnMoveComplete))]
    static class MoveToNullGuard
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> s_Said = new System.Collections.Concurrent.ConcurrentDictionary<uint, bool>();

        static void Prefix(Player __instance, WeenieError status)
        {
            try
            {
                if (__instance == null || !__instance.IsPlayerMovingTo2 || __instance.MoveToParams != null) return;
                __instance.MoveToParams = new MoveToParams(null, null);
                if (s_Said.TryAdd(__instance.Guid.Full, true))
                    Mod.Log.Warn($"[RevivalGuard] MoveToNullGuard: {__instance.Name} finished a move ({status}) with no move-to parameters; finished as ACE finishes one with no callback instead of crashing");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MoveToNullGuard: {e.Message}"); }
        }
    }
}
