using System;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Player), nameof(Player.OnTeleportComplete))]
    public static class TeleportCompleteRescue
    {
        static Exception Finalizer(Player __instance, Exception __exception)
        {
            if (__exception == null) return null;
            var p = __instance;
            var who = p == null ? "a player" : $"{p.Name} (0x{p.Guid.Full:X8})";
            var whereTo = p?.Location?.ToString() ?? "an unknown position";

            Mod.Log.Error($"[RevivalGuard] OnTeleportComplete threw for {who} on the way to "
                + $"{whereTo}. The destination is almost certainly a point inside NO cell, which "
                + $"destroys the player's PhysicsObj and makes CheckMonsters dereference null. "
                + $"Everything after that point never ran -- including the broadcast that "
                + $"materialises the player -- so they would have looked alive and been unable to "
                + $"act. Forcing the relog, which is the only remedy proven to clear this. "
                + $"THE DESTINATION IS THE THING TO FIX: {whereTo}", __exception);

            try
            {
                if (p?.Session != null) p.Session.LogOffPlayer(true);
                else p?.LogOut();
            }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] and the forced logoff for {who} failed too", e);
            }
            return null;   // swallowed: the line above says everything the throw would have
        }
    }
}
