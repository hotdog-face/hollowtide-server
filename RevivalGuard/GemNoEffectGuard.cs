using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Gem), nameof(Gem.UseGem))]
    public static class GemNoEffectGuard
    {
        static bool Prefix(Gem __instance, Player player)
        {
            if (__instance == null || player == null) return true;
            if (__instance.WeenieClassId == PkTrophies.WRIT) return true;   // the Combat Arena Writ: PkTrophies handles it
            bool hasEffect = __instance.SpellDID.HasValue
                             || (__instance.UseCreateContractId ?? 0) > 0
                             || (__instance.UseCreateItem ?? 0) > 0;
            if (hasEffect) return HasTarget(__instance, player);
            player.SendTransientError(GemNoEffectRule.Message(__instance.Name,
                __instance.Use, __instance.LongDesc));
            return false;
        }

        static bool HasTarget(Gem gem, Player player)
        {
            if (!gem.SpellDID.HasValue) return true;
            var spell = new Spell(gem.SpellDID.Value);
            if (spell.NotFound) return true;
            if (spell.IsImpenBaneType)
            {
                if (player.EquippedObjects.Values.Any(i => (i.WeenieType == WeenieType.Clothing || i.IsShield) && i.IsEnchantable))
                    return true;
                player.SendTransientError(GemNoEffectRule.WieldFirst(gem.Name, GemNoEffectRule.Needs.Armor));
                return false;
            }
            if (!spell.IsItemRedirectableType) return true;
            var t = spell.NonComponentTargetType;
            var item = t switch
            {
                ItemType.Weapon => player.GetEquippedWeapon(),
                ItemType.Caster => player.GetEquippedWand(),
                ItemType.WeaponOrCaster => player.GetEquippedWeapon() ?? player.GetEquippedWand(),
                ItemType.MeleeWeapon => player.GetEquippedMeleeWeapon(),
                ItemType.MissileWeapon => player.GetEquippedMissileWeapon(),
                _ => null
            };
            if (item != null && item.IsEnchantable) return true;
            var needs = t == ItemType.Caster ? GemNoEffectRule.Needs.Wand
                : t == ItemType.WeaponOrCaster ? GemNoEffectRule.Needs.WeaponOrWand
                : GemNoEffectRule.Needs.Weapon;
            player.SendTransientError(GemNoEffectRule.WieldFirst(gem.Name, needs, item != null));
            return false;
        }
    }
}
