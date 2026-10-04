using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A REDUCED ITEM CANNOT BE TAILORED ONTO ANOTHER REDUCED ITEM, as in retail
    /// (docs/CRAFTING-TAILORING.md). The AC wiki's Tailoring page, "Tailoring After Reducing": reduced
    /// items "behave the same as non-reduced items with one exception: a reduced item cannot be tailored
    /// onto another reduced item", and players worked around it through a junk single-slot piece.
    /// ACE's Tailoring.ArmorApply compares ClothingPriority only, so it allowed it.
    ///
    /// WHAT "REDUCED" MEANS HERE. ACE records nothing when an item is reduced: TailorReduceArmor only
    /// narrows the biota's ValidLocations (and ClothingPriority). So an item is reduced when its
    /// ValidLocations covers LESS than its weenie's (a Main-reduced hauberk: weenie 0x1A00, biota
    /// 0x0200). That also finds every item reduced before this patch.
    ///
    /// THE LOOK KEEPS THE FACT. Taking the look off a reduced item (TailorArmor) builds the
    /// intermediate in SetArmorProperties(source = the item, target = the intermediate); the postfix
    /// marks the intermediate with PropertyBool 29102 (ours, like ItemLock's 29101: persisted, never
    /// sent to a client). ArmorApply then refuses a marked intermediate on a reduced item with ACE's
    /// own tailoring refusal, WeenieError 0x0437; retail's words for this case are not on record.
    /// Both targets are declared on ACE.Server.Entity.Tailoring (static), which is what is patched.
    /// </summary>
    static class ReducedTailoring
    {
        internal const PropertyBool FROM_REDUCED = (PropertyBool)29102;

        internal static bool IsReduced(WorldObject wo)
        {
            if (wo == null || wo.ItemWorkmanship == null) return false;   // only loot armour can be reduced
            var now = (uint)(wo.ValidLocations ?? EquipMask.None);
            if (now == 0) return false;
            var weenie = DatabaseManager.World.GetCachedWeenie(wo.WeenieClassId);
            var orig = (uint)(weenie?.GetProperty(PropertyInt.ValidLocations) ?? 0);
            return orig != 0 && orig != now && (orig & now) == now;
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.SetArmorProperties))]
        static class MarkLook
        {
            static void Postfix(WorldObject source, WorldObject target)
            {
                if (target != null && IsReduced(source)) target.SetProperty(FROM_REDUCED, true);
            }
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.ArmorApply))]
        static class ApplyLook
        {
            static bool Prefix(Player player, WorldObject source, WorldObject target)
            {
                if (source == null || !(source.GetProperty(FROM_REDUCED) ?? false) || !IsReduced(target)) return true;
                Mod.Log.Info($"[RevivalGuard] ReducedTailoring: refused {source.Name} (0x{source.Guid.Full:X8}, from a reduced item) "
                             + $"onto reduced {target.Name} (0x{target.Guid.Full:X8}) for {player?.Name}");
                player?.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return false;
            }
        }
    }
}
