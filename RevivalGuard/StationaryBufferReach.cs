using System;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A stationary buffer buffs who is standing at it.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.GetMaxRange))]
    public static class StationaryBufferReach
    {
        const float BUFF_REACH = 5f;

        static readonly Func<Creature, Spell> s_CurrentSpell =
            AccessTools.MethodDelegate<Func<Creature, Spell>>(AccessTools.PropertyGetter(typeof(Creature), "CurrentSpell"));

        static void Postfix(Creature __instance, ref float __result)
        {
            try
            {
                if (__instance == null || __result <= BUFF_REACH) return;
                if (__instance.CurrentAttack != CombatType.Magic || !(__instance.AttackTarget is Player)) return;
                if (!(__instance.GetProperty(PropertyBool.Stuck) ?? false)) return;
                var spell = s_CurrentSpell(__instance);
                if (spell == null || !spell.IsBeneficial) return;
                __result = BUFF_REACH;
            }
            catch (Exception e)
            {
                Mod.Log.Error("[RevivalGuard] StationaryBufferReach threw", e);
            }
        }
    }
}
