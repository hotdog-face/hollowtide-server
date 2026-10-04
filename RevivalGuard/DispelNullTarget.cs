using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AN UNTARGETED DISPEL MUST NOT TAKE THE WORLD DOWN (found on staging 2026-09-28 by the duelist
    /// work). A player casting a dispel with no target -- our client sends self spells untargeted, and
    /// ACE takes them through Player.CreatePlayerSpell(uint) with target null -- reaches
    /// WorldObject.VerifyDispelPKStatus(caster, null), which reads target.Wielder and throws a
    /// NullReferenceException that kills the server. An untargeted dispel is a dispel on the caster,
    /// so a null target is taken as the caster; every other check in the method is unchanged.
    /// </summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.VerifyDispelPKStatus))]
    static class DispelNullTarget
    {
        static void Prefix(WorldObject caster, ref WorldObject target)
        {
            if (target == null)
                target = caster;
        }
    }

    /// <summary>The same dispel then runs on with target null through the school handlers; hand it
    /// the caster there too, exactly as a dispel cast on oneself arrives.</summary>
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
