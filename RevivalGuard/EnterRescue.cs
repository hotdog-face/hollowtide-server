using System;
using System.Collections.Generic;
using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(LandblockManager), nameof(LandblockManager.AddObject))]
    static class EnterRescue
    {
        [ThreadStatic] static bool t_Rescuing;

        static void Postfix(WorldObject worldObject, bool loadAdjacents, ref bool __result)
        {
            if (__result || t_Rescuing || !(worldObject is Player p) || p.Location == null) return;
            var tried = new List<Position> { new Position(p.Location) };
            // Nudges first: a login placement is stricter than a teleport's transition, and a creature standing on the saved spot is enough to refuse it
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
