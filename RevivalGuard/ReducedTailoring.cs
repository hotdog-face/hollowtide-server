using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A reduced item cannot be tailored onto another reduced item, as in retail (docs/CRAFTING-TAILORING.md).</summary>
    static class ReducedTailoring
    {
        internal const PropertyBool FROM_REDUCED = (PropertyBool)29102;

        internal static bool IsReduced(WorldObject wo)
        {
            if (wo == null) return false;
            if (wo.ItemWorkmanship == null && !SuitReduction.WeenieIsOutfit(wo)) return false;   // loot armour, and outfits
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
                TailoringWords.Say(player, $"This look came from a reduced piece, and your {target.Name} is reduced too. Put the look on a plain single-slot piece first, then take it from that piece with a new kit.");
                player?.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return false;
            }
        }
    }
}
