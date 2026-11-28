using System;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CreateEnchantment))]
    public static class EnchantGuard
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorldObject __instance, WorldObject target, WorldObject caster, Spell spell)
        {
            string why = target == null ? "no target" : caster == null ? "no caster" : spell == null ? "no spell"
                       : target.EnchantmentManager == null ? "the target has no enchantments"
                       : target is Player tp && tp.Session == null ? "the target has logged off" : null;
            if (why == null) return true;
            Mod.Log.Warn($"[RevivalGuard] enchantment skipped ({why}): {spell?.Name ?? "?"} by {caster?.Name ?? __instance?.Name} on {target?.Name}");
            return false;
        }

        static Exception Finalizer(WorldObject __instance, WorldObject target, WorldObject caster, Spell spell, Exception __exception)
        {
            if (__exception != null)
                Mod.Log.Error($"[RevivalGuard] enchantment {spell?.Name} by {caster?.Name ?? __instance?.Name} on {target?.Name} threw, kept the server up: {__exception}");
            return null;
        }
    }
}
