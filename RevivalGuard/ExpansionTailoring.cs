using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// OUR OWN ITEMS ARE NOT TAILORABLE: ACE'S TAILORING CANNOT CARRY THEIR LOOK (launch sweep #8,
    /// docs/SWEEP-BOTHALVES-2026-09-30.md).
    ///
    /// Tailoring copies the look as SETUP, ICON, NAME and clothing/palette ids
    /// (Tailoring.SetCommonProperties / UpdateCommonProps). Our 900000+ weapons and armour do not
    /// get their look from those. A weapon's model is keyed by its WEENIE on the client
    /// (ServerLink.ModelKeyFor, "w{wcid}"), and worn armour's by weenie_properties_anim_part rows
    /// (ApplyOwnAnimParts); both stay with the original wcid. Seen on the fleet 2026-09-30: a Weapon
    /// Tailoring Kit on the Deru Leafblade, then the intermediate on a Long Sword, gave a sword named
    /// "Deru Leafblade" with the Leafblade's icon and the retail setup 0x02000065's plain blade, and
    /// the Leafblade and the kit were gone. The other way round, a retail look put on one of ours
    /// would keep our model under a retail name and icon.
    ///
    /// So both ends refuse, with ACE's own unnamed 0x0437 like every other tailoring rule (the
    /// client says why, AcTailoring.WhyRefused): taking a look OFF one of ours (TailorArmor,
    /// TailorWeapon), putting any look ON one of ours (ArmorApply, WeaponApply), and putting a look
    /// that came off one of ours before this guard (an intermediate carrying an expansion icon,
    /// 0x06FF....) on anything. Reduction and layering tools do not move a look and are untouched.
    /// </summary>
    static class ExpansionTailoring
    {
        const uint WCID_LO = 900000, WCID_HI = 999999;

        /// <summary>One of ours WHOSE LOOK TAILORING CANNOT CARRY. 2026-11-06 (owner, live: "my koji's beast mask not
        /// being able to be tailored with a kit feels broken and wrong"): the test used to be the wcid range alone, so it
        /// also refused our COPIES of retail items (the Staff Base mask wall 903300-903352, the owner's suit pieces such
        /// as Koji's Beast 907718, showcase and keepsake copies), whose setup, clothing table, palette and icon are the
        /// retail weenie's own and move through a kit like any retail piece. Retail let every mask be tailored like head
        /// armour (AC wiki, Tailoring). Now an item of ours is refused only when its look really lives on our weenie:
        /// it carries anim-part rows (worn armour drawn by ApplyOwnAnimParts) or an expansion icon (0x06FF....); every one
        /// of the 44 items with a client-side w(wcid).glb model has one or both (checked against ace_world 2026-11-06).</summary>
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
