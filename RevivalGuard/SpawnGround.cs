using System;
using System.Collections.Concurrent;
using System.Reflection;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// GENERATOR SPAWNS LAND (report 261023-0008, a Virindi Observer 9 m up over the Direlands). Retail generator data puts
    /// many spawns in the air on purpose: rifts summon at +9 m, Marae's brood at +20/+40 m, electrical storms at +20 m,
    /// Specific profiles and indoor scatter keep the generator's height. Retail's server ran gravity on every creature, so
    /// they fell and landed. ACE seats a spawn where the data says (enter_world placement never drops) and ticks an idle
    /// monster's physics only on its first frame, so it hangs there until a player wakes it and it moves (then it falls).
    /// Here a generator-spawned creature with Gravity that ACE placed touching nothing is swept straight down with ACE's
    /// own transition and seated on the walkable surface under it, before any client is told it exists; its Home moves
    /// with it. Players, AI-immobile creatures (tentacles, Rynthid stones), and anything without Gravity are untouched;
    /// no floor within 200 m, or one too steep to stand on, leaves ACE's position. docs/findings/floating-creatures-2026-10-23.md.
    ///   @modifybool rg_spawn_ground false   ACE's own heights again (from the next spawn)
    /// </summary>
    [HarmonyPatch]
    static class SpawnGround
    {
        internal const string P_ON = "rg_spawn_ground";
        static readonly ConcurrentDictionary<uint, bool> s_Logged = new();

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(WorldObject), nameof(WorldObject.AddPhysicsObj), Type.EmptyTypes);
            if (m == null) Mod.Log.Error("[RevivalGuard] SpawnGround: WorldObject.AddPhysicsObj() not found; spawns keep ACE's heights");
            return m;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            Mod.Log.Info("[RevivalGuard] SpawnGround: generator spawns placed in the air are dropped to the ground (rg_spawn_ground)");
            return true;
        }

        static void Postfix(WorldObject __instance, bool __result)
        {
            try
            {
                if (!__result || __instance is not Creature c || c is Player) return;
                var po = c.PhysicsObj;
                if (po == null || po.CurCell == null || c.Location == null) return;
                bool contact = (po.TransientState & TransientStateFlags.Contact) != 0;
                if (!SpawnGroundRule.ShouldTry(true, false, c.Generator != null, po.State.HasFlag(PhysicsState.Gravity),
                        po.State.HasFlag(PhysicsState.Static), c.GetProperty(PropertyBool.AiImmobile) ?? false, contact)) return;
                if (!PropertyManager.GetBool(P_ON, true).Item) return;

                float z0 = po.Position.Frame.Origin.Z;
                if (!PhysicsDrop.Drop(po, SpawnGroundRule.MAX_DROP, out float fell, out string why))
                {
                    if (s_Logged.TryAdd(c.WeenieClassId, true))
                        Mod.Log.Info($"[RevivalGuard] SpawnGround: {c.Name} ({c.WeenieClassId}) left at {c.Location.ToLOCString()}: {why}");
                    return;
                }
                c.SyncLocation();
                c.SetPosition(PositionType.Home, new Position(c.Location));
                if (s_Logged.TryAdd(c.WeenieClassId, true))
                    Mod.Log.Info($"[RevivalGuard] SpawnGround: {c.Name} ({c.WeenieClassId}) from {c.Generator?.Name} dropped {fell:0.0} m (z {z0:0.00} -> {c.Location.PositionZ:0.00}, 0x{c.Location.Cell:X8}); once per kind");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SpawnGround: {e.Message}"); }
        }
    }
}
