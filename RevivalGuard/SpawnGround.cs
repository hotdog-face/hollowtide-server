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
    /// <summary>Generator spawns land.</summary>
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
