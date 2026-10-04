using System;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A STATIONARY BUFFER BUFFS WHO IS STANDING AT IT. Owner, 2026-09-23 (PUMPKIN-02): the Pumpkin
    /// Buffer "keeps buffing after I walk away; walking away should cancel the buffing".
    ///
    /// The Pumpkin Buffer (wcid 32205) is a Stuck creature whose spellbook is sixty Incantation ...
    /// Other buffs, cast through the ordinary monster AI. ACE's Creature.GetMaxRange returns
    /// GetSpellMaxRange for a magic attack, and an Incantation's range reaches tens of metres, so the
    /// pumpkin kept casting at a player long after they left it.
    ///
    /// So for a Stuck creature choosing a BENEFICIAL spell at a player, the attack range is capped at
    /// BUFF_REACH. A Stuck creature cannot close the gap, so walking out of reach ends the buffing,
    /// and walking back in resumes it. Harmful spells and mobile creatures are untouched.
    /// Ours, not retail's: recorded in docs/DIVERGENCES.md.
    /// </summary>
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
