using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A MONSTER NOTICES A PLAYER WHO IS STANDING STILL. ACE wakes sleeping monsters only from
    /// Player.CheckMonsters, which runs when the player MOVES (Player_Tick) or finishes a teleport, so a
    /// creature a generator respawns beside a motionless player ignores them until they step. Owner,
    /// playtest 2026-09-18 (P23): "enemies respawn next to me but don't aggro until i move". Retail's
    /// monsters found idle players on their own; that is why AFK characters died at spawn points.
    /// So each player's heartbeat (about every 5 s) runs the same CheckMonsters: the same range test
    /// (the monster's VisualAwarenessRange) and the same AlertMonster, just not only on movement.
    /// </summary>
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
