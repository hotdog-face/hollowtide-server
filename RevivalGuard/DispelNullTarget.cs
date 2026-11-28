using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.VerifyDispelPKStatus))]
    static class DispelNullTarget
    {
        static void Prefix(WorldObject caster, ref WorldObject target)
        {
            if (target == null)
                target = caster;
        }
    }

    /// <summary>The same dispel then runs on with target null through the school handlers</summary>
    [HarmonyPatch(typeof(Player), "CreatePlayerSpell", new[] { typeof(WorldObject), typeof(ACE.Server.Entity.Spell), typeof(bool) })]
    static class DispelNullTargetCast
    {
        static void Prefix(Player __instance, ref WorldObject target, ACE.Server.Entity.Spell spell)
        {
            if (target == null && spell != null && spell.MetaSpellType == ACE.Entity.Enum.SpellType.Dispel)
                target = __instance;
        }
    }
}
