using System;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A BOLT CAST AT A MONSTER IN YOUR FACE IS NOT A WALL (live ticket 261103-0029, Blood: "war spells that dont have any
    /// reaction to the enemy"; 2,529 of 2,755 SpellMissWatch "at launch" events 2026-11-01..03, Lunatic High, Blood, Tomoki).
    /// When the target is within 0.6 m surface to surface (an Armored Tusker 2.7 m off, centre to centre),
    /// CalculateProjectileOrigins spawns the bolt only the caster's radius plus its own away (0.58 m flat, 1.3 m up for a
    /// human), inside the caster's sphere. LaunchSpellProjectiles then asks IsProjectileVisible whether the caster can see
    /// that spawn, a sweep from the spawn back to the caster that starts already inside it; when it reports no contact the
    /// bolt goes straight to OnCollideEnvironment. No damage, no message; a vuln (no projectile) lands.
    /// On the M5 shard (2026-11-03) the same 0.58 m spawn at Blood's spot passed 41 of 41 times with one or two tuskers, so
    /// what tips the live sweep (crowding monsters?) is not pinned down; what is certain is the spawn's place.
    ///
    /// The rule: a spawn inside the caster's own sphere is in the caster's sight (the caster is not standing in a wall), so
    /// the bolt flies, and its flight meets whatever is really there, a wall or a monster in the way, as any bolt does.
    /// A spawn clear of the caster keeps ACE's own answer; so does everything that is not a creature's own spell bolt.
    /// </summary>
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
