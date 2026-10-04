using System;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE TELEPORT THAT THREW HALFWAY THROUGH FINISHING, AND LEFT A PLAYER WHO IS NOT REALLY THERE.
    ///
    /// This is the SECOND of the two ways a teleport can strand a player, and it is the one that
    /// actually happened in Hotel Swank. <see cref="TeleportWatchdog"/> handles the other one.
    /// Both were called "the teleport wedge" for a day and they are not the same failure:
    ///
    ///   * Watchdog's case:  `Teleporting` is left TRUE and everything gated on it stays shut.
    ///   * This case:        `Teleporting` is already FALSE. Nothing is gated. The player is simply
    ///                       never finished being put into the world.
    ///
    /// WHAT HAPPENS. Teleport a player to a point that is inside **no cell** — not a wrong cell, a
    /// point no cell contains — and the player's own `AddWorldObjectInternal` fails exactly the way
    /// a creature's does, and its `PhysicsObj` is destroyed. `OnTeleportComplete`
    /// (Player_Location.cs:762) then walks into `CheckMonsters`, which dereferences
    /// `PhysicsObj.ObjMaint` (Player_Monster.cs:21), and throws:
    ///
    ///     System.NullReferenceException: Object reference not set to an instance of an object.
    ///        at ACE.Server.WorldObjects.Player.CheckMonsters() in Player_Monster.cs:line 21
    ///        at ACE.Server.WorldObjects.Player.OnTeleportComplete() in Player_Location.cs:line 762
    ///
    /// `Teleporting = false` is the line *before* that call, so the flag is not what is left wrong.
    /// What is left wrong is everything AFTER the throw, which never runs — `CheckHouse()` and,
    /// critically, `EnqueueBroadcastPhysicsState()`, the broadcast that takes a player from the pink
    /// bubble to materialised. Nothing on the server ever retries it.
    ///
    /// The symptom is milder than it sounds and that is what makes it nasty: the client keeps
    /// answering. Position queries work, objects can be listed at the right distances, the session
    /// is alive. But further teleports are accepted by the client and do nothing on the server, and
    /// no second failure appears in the log to say why. A player reports "I'm stuck and nothing
    /// works", and from the outside the session looks perfectly healthy.
    ///
    /// A RELOG IS THE ONLY THING PROVEN TO CLEAR IT — measured twice on 2026-09-21, once by hand and
    /// once by an agent reproducing it deliberately. That is why this ends in a forced logoff rather
    /// than something gentler: the player has **no physics object**, so there is nothing left on the
    /// server to broadcast, nudge or re-place. Re-entering the world is what rebuilds it, and the
    /// position they come back at is the one the server already holds. An abrupt relog is a bad
    /// experience; a session that looks alive and silently does nothing for the rest of the evening
    /// is a worse one, and it is the one that makes a player stop playing.
    ///
    /// A Harmony **Finalizer** rather than a Postfix, because a Postfix does not run when the
    /// original threw — and the throw IS the event. Returning null swallows the exception, which is
    /// correct here only because the log line below carries everything the original would have, plus
    /// the destination that caused it. The underlying bad placement still has to be fixed; this line
    /// is the trail to it.
    /// </summary>
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
