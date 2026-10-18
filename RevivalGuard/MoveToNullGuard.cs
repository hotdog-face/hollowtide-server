using System;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A FINISHED MOVE-TO WITH NO PARAMETERS CANNOT CRASH THE SERVER (live crash 2026-10-18 14:42 UTC). Player.OnMoveComplete
    /// calls OnMoveComplete_MoveTo2 whenever IsPlayerMovingTo2 is set, and that reads MoveToParams.Callback with no null
    /// check. CheckMoveToParams (a new use or a cancel) clears MoveToParams without clearing IsPlayerMovingTo2, so a server
    /// move that completed after it threw NullReferenceException on the physics thread and the process died.
    ///
    /// The patch is on OnMoveComplete, not OnMoveComplete_MoveTo2: the crash's stack has no frame for the inner method,
    /// so the JIT inlined it and a patch there could be skipped. With no parameters there is nothing to call back, so an
    /// empty MoveToParams (no callback) is put in its place: ACE's own "Callback == null" path then finishes the move,
    /// and the spell half of OnMoveComplete still runs. Logged once per player.
    /// </summary>
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
