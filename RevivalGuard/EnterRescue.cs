using System;
using System.Collections.Generic;
using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A PLAYER WHOSE PLACEMENT FAILS IS PLACED SOMEWHERE ELSE, NEVER NOWHERE.
    ///
    /// ACE's DoPlayerEnterWorld retries a failed placement at the Sanctuary (the lifestone bind), which
    /// is the saved Location itself for anyone who logged out beside their lifestone. Measured
    /// 2026-09-18: "+testing character" bound at the Academy lifestone, relogged, and both the
    /// placement and the retry failed at 0x86020134 [116.09 -160.00 -6.00] ("couldn't spawn" twice);
    /// the session carried on with no physics object, LoginComplete and every Use threw
    /// NullReferenceException, and the character could do nothing. A real player would call it a
    /// locked-out character.
    ///
    /// So when LandblockManager.AddObject fails for a player, this tries, in order, the Sanctuary, the
    /// LastPortal, the Instantiation (where the character was made) and Holtburg, skipping any within
    /// 2 m of a spot that already failed; before those, small nudges around the saved spot, and reports success on the first that places. ACE's own
    /// fallback then never runs.
    /// </summary>
    [HarmonyPatch(typeof(LandblockManager), nameof(LandblockManager.AddObject))]
    static class EnterRescue
    {
        [ThreadStatic] static bool t_Rescuing;

        static void Postfix(WorldObject worldObject, bool loadAdjacents, ref bool __result)
        {
            if (__result || t_Rescuing || !(worldObject is Player p) || p.Location == null) return;
            var tried = new List<Position> { new Position(p.Location) };
            // Nudges first: a login placement is stricter than a teleport's transition, and a creature
            // standing on the saved spot (the Academy lifestone room has an Olthoi generator 6 m away)
            // is enough to refuse it, while a teleport to the very same point succeeds.
            var nudges = new List<(string, Position)>();
            foreach (var (dx, dy, dz) in new[] { (0f, 0f, 0.5f), (1.5f, 0f, 0.2f), (-1.5f, 0f, 0.2f), (0f, 1.5f, 0.2f), (0f, -1.5f, 0.2f), (3f, 0f, 0.2f), (-3f, 0f, 0.2f) })
            {
                var n = new Position(p.Location);
                n.PositionX += dx; n.PositionY += dy; n.PositionZ += dz;
                nudges.Add(("spot nudged", n));
            }
            var candidates = new List<(string name, Position pos)>(nudges)
            {
                ("sanctuary", p.Sanctuary), ("last portal", p.LastPortal), ("instantiation", p.Instantiation),
                ("Holtburg", new Position(0xA9B40019, 84, 7.1f, 94, 0, 0, -0.0784591f, 0.996917f)),
            };
            bool isNudge = true;
            t_Rescuing = true;
            try
            {
                foreach (var (name, pos) in candidates)
                {
                    if (pos == null) continue;
                    isNudge = name == "spot nudged";
                    bool near = false;
                    if (!isNudge)
                        foreach (var t in tried)
                            if (t.Landblock == pos.Landblock && t.Distance2D(pos) < 2f) { near = true; break; }
                    if (near) continue;
                    tried.Add(new Position(pos));
                    p.Location = new Position(pos);
                    if (LandblockManager.AddObject(p, loadAdjacents))
                    {
                        Mod.Log.Warn($"[RevivalGuard] enter rescue: {p.Name} could not be placed at {tried[0].ToLOCString()}; placed at the {name} {pos.ToLOCString()}");
                        __result = true;
                        return;
                    }
                }
                p.Location = new Position(tried[0]);
                Mod.Log.Error($"[RevivalGuard] enter rescue: {p.Name} could not be placed anywhere ({tried.Count} spots)");
            }
            finally { t_Rescuing = false; }
        }
    }
}
