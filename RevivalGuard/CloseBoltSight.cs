using System;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsProjectileVisible))]
    static class CloseBoltSight
    {
        const float SLACK = 0.05f;

        static void Postfix(WorldObject __instance, WorldObject proj, ref bool __result)
        {
            if (__result) return;
            try
            {
                if (!(proj is SpellProjectile sp) || !(__instance is Creature caster) || sp.ProjectileSource != caster) return;
                var cp = caster.PhysicsObj; var pp = sp.PhysicsObj;
                if (cp == null || pp == null) return;
                var d = pp.Position.GetOffset(cp.Position);
                float flat = (float)Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (flat > cp.GetPhysicsRadius() + pp.GetPhysicsRadius() + SLACK) return;   // spawned clear of the caster
                __result = true;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CloseBoltSight: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
