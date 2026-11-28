using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    static class ExpansionTailoring
    {
        const uint WCID_LO = 900000, WCID_HI = 999999;

        internal static bool IsOurs(WorldObject wo)
        {
            if (wo == null || wo.WeenieClassId < WCID_LO || wo.WeenieClassId > WCID_HI) return false;
            if (CarriesOurLook(wo)) return true;
            if (wo.Biota?.PropertiesAnimPart != null && wo.Biota.PropertiesAnimPart.Count > 0) return true;
            var weenie = ACE.Database.DatabaseManager.World.GetCachedWeenie(wo.WeenieClassId);
            if (weenie?.PropertiesAnimPart != null && weenie.PropertiesAnimPart.Count > 0) return true;
            var icon = weenie?.GetProperty(ACE.Entity.Enum.Properties.PropertyDataId.Icon) ?? 0;
            return (icon >> 16) == 0x06FF;
        }

        static bool CarriesOurLook(WorldObject wo)
            => wo != null && ((wo.IconId >> 16) == 0x06FF);

        static bool Refuse(Player player, WorldObject source, WorldObject target, bool applying)
        {
            if (!IsOurs(target) && !(applying && CarriesOurLook(source))) return true;
            TailoringWords.Say(player, IsOurs(target)
                ? $"The {target.Name} is drawn from art a tailoring kit cannot carry, so it cannot be tailored."
                : $"This look comes from art a tailoring kit cannot carry, so it cannot go on the {target.Name}.");
            player?.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
            return false;
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorArmor))]
        static class TakeArmorLook
        {
            static bool Prefix(Player player, WorldObject source, WorldObject target) => Refuse(player, source, target, false);
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorWeapon))]
        static class TakeWeaponLook
        {
            static bool Prefix(Player player, WorldObject source, WorldObject target) => Refuse(player, source, target, false);
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.ArmorApply))]
        static class ApplyArmorLook
        {
            static bool Prefix(Player player, WorldObject source, WorldObject target) => Refuse(player, source, target, true);
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.WeaponApply))]
        static class ApplyWeaponLook
        {
            static bool Prefix(Player player, WorldObject source, WorldObject target) => Refuse(player, source, target, true);
        }
    }
}
