using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// FULL-COVERAGE ROBES AND GUISES ARE NOT TAILORABLE, as in retail (docs/CRAFTING-TAILORING.md).
    ///
    /// The AC wiki's Tailoring page (asheron.fandom.com, rev. 2015-02-04) lists what "cannot be
    /// tailored": Society Armor, and "Full coverage robes and guises". ACE's Tailoring.VerifyUseRequirements
    /// refuses society armour and Retained items only, and Tailoring.GetArmorWCID sends a robe
    /// (ValidLocations 0x7F00: chest, abdomen, both arm and both leg slots, feet) to the Dusky Winged
    /// Coat intermediate because it HasFlag(ChestArmor). So on this shard an Armor Tailoring Kit ate a
    /// Faran Robe and made an intermediate (2026-09-27, ++Buffprobe), which retail never allowed.
    ///
    /// THE TEST IS COVERAGE, NOT NAME. A full robe or guise covers all six body armour slots
    /// (Chest|Abdomen|UpperArm|LowerArm|UpperLeg|LowerLeg = 0x7E00); every robe and guise in ace_world
    /// with that coverage (Armsman's/Faran/Suikan/Mattekar robes 0x7F00, Doppelganger 0x7F01, Armored
    /// Skeleton Guise 0x7F21, Ursuin Guise 0x7F20) matches, and no loot or quest armour piece covers all
    /// six (the widest loot pieces are hauberks, 0x1E00, and three-slot leggings, 0x6400). A partial
    /// over-robe (Balor's Over-robe, chest only) is not full coverage and stays tailorable.
    ///
    /// Both ends are guarded: taking the look OFF a robe (TailorArmor) and putting an intermediate ON
    /// one (ArmorApply), so an intermediate made before this patch cannot reach a robe either. The
    /// refusal is ACE's own for every other tailoring rule, WeenieError 0x0437, because retail's words
    /// for this case are not on record; the client explains it (AcTailoring.WhyRefused).
    /// </summary>
    static class RobeTailoring
    {
        const EquipMask FullBody = EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor
                                 | EquipMask.LowerArmArmor | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor;

        internal static bool IsFullRobe(WorldObject wo)
            => wo != null && ((wo.ValidLocations ?? EquipMask.None) & FullBody) == FullBody;

        static bool Refuse(Player player, WorldObject item)
        {
            if (!IsFullRobe(item)) return true;
            player?.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
            return false;
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorArmor))]
        static class TakeLook
        {
            static bool Prefix(Player player, WorldObject target) => Refuse(player, target);
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.ArmorApply))]
        static class ApplyLook
        {
            static bool Prefix(Player player, WorldObject target) => Refuse(player, target);
        }
    }
}
