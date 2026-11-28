using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    static class RobeTailoring
    {
        const EquipMask FullBody = EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor
                                 | EquipMask.LowerArmArmor | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor;

        internal static bool IsFullRobe(WorldObject wo)
            => wo != null && ((wo.ValidLocations ?? EquipMask.None) & FullBody) == FullBody;

        static bool Refuse(Player player, WorldObject item)
        {
            if (!IsFullRobe(item)) return true;
            TailoringWords.Say(player, $"Robes and guises cannot be tailored, so the {item.Name} cannot take or give a look.");
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
