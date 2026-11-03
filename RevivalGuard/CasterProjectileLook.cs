using System;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CASTER MAY NAME ITS PROJECTILE'S LOOK (owner 2026-11-03: "make the bread a loaf shooter. have it shoot slices of
    /// bread like the egg shooter", then "yes build it"). A new projectile can't be a new spell: ACE builds every spell
    /// from the client's spell table (Spell.cs), and every spell there already has a server row. So a caster with
    /// PropertyDataId 9070 (a Setup) fires its spell's own projectile wearing that Setup: only the look changes; the
    /// projectile item, its flight, damage and the cast text stay the spell's. Used by the staff-only Loaf Shooter
    /// (tools/gpubox-ace/gen_costume_prototypes.py): retail's Egg spell, a Slice of Bread in flight.
    ///
    /// LaunchSpellProjectiles notes the casting item (its weapon argument, else the caster's wielded wand) for the
    /// projectiles it makes on this thread; SpellProjectile.Setup builds each projectile's physics from SetupTableId, so
    /// the swap goes in just before it. Items without the property: nothing changes.
    /// </summary>
    static class CasterProjectileLook
    {
        internal const PropertyDataId P_LOOK = (PropertyDataId)9070;
        [ThreadStatic] static uint t_Look;

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.LaunchSpellProjectiles))]
        static class OnLaunch
        {
            static void Prefix(WorldObject __instance, WorldObject weapon)
            {
                t_Look = 0;
                try
                {
                    var item = weapon ?? (__instance as Creature)?.GetEquippedWand();
                    t_Look = item?.GetProperty(P_LOOK) ?? 0;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] caster projectile look: {e.Message}"); }
            }

            static void Finalizer() => t_Look = 0;
        }

        [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.Setup))]
        static class OnSetup
        {
            static void Prefix(SpellProjectile __instance)
            {
                if (t_Look != 0) __instance.SetupTableId = t_Look;
            }
        }
    }
}
