using System.Collections.Concurrent;
using System.Reflection;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// HOVER DROP (owner 2026-10-19 "Big and low", docs/findings/FINDINGS-buff-npc.md). NOT RETAIL: retail has no such rule.
    /// A Zefir's animation carries its body 1.09-2.09 m above its origin, times ObjScale; Azimar at 1.8 hovered with his
    /// lowest point 1.96 m up. ACE cannot spawn him lower (placement pushes his scaled spheres back onto the floor), so his
    /// PHYSICS stays on the floor, untouched, and only his reported Location (what CreateObject and UpdatePosition send to
    /// clients) sits DROP metres lower. Every client then draws, picks and collides with him there.
    /// Hooked where ACE copies physics into Location: WorldObject.SyncLocation (spawn, enter_cell) and
    /// Creature.UpdatePosition_SyncLocation (emote interrupts, moves). Listed weenies only; anything else returns at once.
    /// Not covered: UpdateObjectPhysics' own copy when the physics origin moves, which a Stuck NPC's does not.
    ///   @modifybool hover_drop false   ACE's own heights (takes effect at his next spawn or sync)
    /// </summary>
    [HarmonyPatch]
    static class HoverDrop
    {
        internal const string P_ON = "hover_drop";

        // weenie class id -> metres his Location sits below his physics origin
        static readonly Dictionary<uint, float> s_Drop = new()
        {
            { 901631, 0.9f },   // Azimar the Gleaming, scale 1.8: lowest point 1.96 m -> 1.06 m
            // his Hotel Swank review copies (AzimarThemeRule.Fixed), the same body and scale
            { 901633, 0.9f }, { 901634, 0.9f }, { 901635, 0.9f }, { 901636, 0.9f }, { 901637, 0.9f },
        };

        static readonly ConcurrentDictionary<uint, bool> s_Logged = new();

        static IEnumerable<MethodBase> TargetMethods()
        {
            var a = AccessTools.DeclaredMethod(typeof(WorldObject), nameof(WorldObject.SyncLocation), Type.EmptyTypes);
            var b = AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.UpdatePosition_SyncLocation), Type.EmptyTypes);
            if (a == null) Mod.Log.Error("[RevivalGuard] HoverDrop: WorldObject.SyncLocation() not found; skipped");
            if (b == null) Mod.Log.Error("[RevivalGuard] HoverDrop: Creature.UpdatePosition_SyncLocation() not found; skipped");
            if (a != null) yield return a;
            if (b != null) yield return b;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (!TargetMethods().Any()) return false;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard HoverDrop: listed hovering NPCs are reported lower than their physics (Azimar)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] HoverDrop: switch not registered with @modifybool ({e.Message}); still read at its default"); }
            Mod.Log.Info($"[RevivalGuard] HoverDrop: {s_Drop.Count} hovering NPC(s) drawn lower (hover_drop)");
            return true;
        }

        static void Postfix(WorldObject __instance)
        {
            try
            {
                if (__instance == null || !s_Drop.TryGetValue(__instance.WeenieClassId, out float drop)) return;
                if (!PropertyManager.GetBool(P_ON, true).Item) return;
                var po = __instance.PhysicsObj;
                var loc = __instance.Location;
                if (po == null || po.CurCell == null || loc == null) return;
                loc.PositionZ = po.Position.Frame.Origin.Z - drop;
                if (s_Logged.TryAdd(__instance.Guid.Full, true))
                    Mod.Log.Info($"[RevivalGuard] HoverDrop: {__instance.Name} 0x{__instance.Guid.Full:X8} reported {drop} m low (z {loc.PositionZ:F3}, physics {po.Position.Frame.Origin.Z:F3})");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] HoverDrop: {e.Message}"); }
        }
    }
}
