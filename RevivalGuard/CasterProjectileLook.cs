using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CASTER MAY NAME ITS PROJECTILE'S LOOK (owner 2026-11-03: "make the bread a loaf shooter. have it shoot slices of
    /// bread like the egg shooter", then "yes build it"). A new projectile can't be a new spell: ACE builds every spell
    /// from the client's spell table (Spell.cs), and every spell there already has a server row. So a caster with
    /// PropertyDataId 9070 (a Setup) fires its spell's own projectile wearing that Setup: only the look changes; the
    /// projectile item, its flight and damage stay the spell's. Used by the staff-only Loaf Shooter
    /// (tools/gpubox-ace/gen_costume_prototypes.py): retail's Egg spell, a Slice of Bread in flight.
    ///
    /// LaunchSpellProjectiles notes the casting item (its weapon argument, else the caster's wielded wand) for the
    /// projectiles it makes on this thread; SpellProjectile.Setup builds each projectile's physics from SetupTableId, so
    /// the swap goes in just before it. Items without the property: nothing changes.
    ///
    /// The look also tumbles (owner 261103-0033: "the toast should spin mid air as it flys"): Setup gives a projectile
    /// with RotationSpeed a spin and drops AlignPath, as it does for retail's swirling missiles, so the borrowed prop
    /// turns over like a thrown item instead of flying nose-first like the spell's own missile.
    ///
    /// And it may carry its own name (same ticket: "says it shoots an egg but it needs to say toast"): PropertyString 9070
    /// on the caster replaces the spell's name in the hit lines DamageTarget sends ("You bash X for 2 points with Toast.").
    /// The examine panel reads the client's spell table; our client renames it there (AppraisalHud.ItemSpellNames).
    /// </summary>
    static class CasterProjectileLook
    {
        internal const PropertyDataId P_LOOK = (PropertyDataId)9070;
        internal const PropertyString P_NAME = (PropertyString)9070;
        [ThreadStatic] static uint t_Look;
        [ThreadStatic] static string t_Name;
        [ThreadStatic] static SpellProjectile t_Hitting;
        static readonly ConditionalWeakTable<SpellProjectile, string> s_Names = new ConditionalWeakTable<SpellProjectile, string>();
        const double SPIN = 1.5;   // turns a second (Setup's omega is 2 pi x RotationSpeed, about the x axis)

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.LaunchSpellProjectiles))]
        static class OnLaunch
        {
            static void Prefix(WorldObject __instance, WorldObject weapon)
            {
                t_Look = 0; t_Name = null;
                try
                {
                    var item = weapon ?? (__instance as Creature)?.GetEquippedWand();
                    t_Look = item?.GetProperty(P_LOOK) ?? 0;
                    if (t_Look != 0) t_Name = item.GetProperty(P_NAME);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] caster projectile look: {e.Message}"); }
            }

            static void Finalizer() { t_Look = 0; t_Name = null; }
        }

        [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.Setup))]
        static class OnSetup
        {
            static void Prefix(SpellProjectile __instance)
            {
                if (t_Look == 0) return;
                __instance.SetupTableId = t_Look;
                __instance.RotationSpeed = SPIN;
                if (!string.IsNullOrEmpty(t_Name)) s_Names.AddOrUpdate(__instance, t_Name);
            }
        }

        [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.DamageTarget))]
        static class OnHit
        {
            static void Prefix(SpellProjectile __instance) => t_Hitting = __instance;
            static void Finalizer() => t_Hitting = null;

            /// <summary>Every Spell.Name read in DamageTarget goes through NameOf (the hit and drain lines).</summary>
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                var getName = AccessTools.PropertyGetter(typeof(Spell), nameof(Spell.Name));
                var nameOf = AccessTools.Method(typeof(CasterProjectileLook), nameof(NameOf));
                var code = instructions.ToList();
                int n = 0;
                foreach (var c in code)
                    if ((c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && Equals(c.operand, getName))
                    { c.opcode = OpCodes.Call; c.operand = nameOf; n++; }
                if (n == 0) Mod.Log.Error($"[RevivalGuard] CasterProjectileLook: no Spell.Name in {original?.DeclaringType?.Name}.{original?.Name}; hits keep the spell's name");
                return code;
            }
        }

        public static string NameOf(Spell spell)
        {
            var p = t_Hitting;
            return p != null && s_Names.TryGetValue(p, out var name) ? name : spell.Name;
        }
    }
}
