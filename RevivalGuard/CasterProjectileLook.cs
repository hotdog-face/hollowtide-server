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
    /// <summary>A caster may name its projectile's look.</summary>
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
