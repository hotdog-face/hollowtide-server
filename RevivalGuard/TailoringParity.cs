using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Two retail tailoring rules ACE misses.</summary>
    static class TailoringParity
    {
        internal const string P_ON = "tailoring_retail_rules";
        static int s_Registered;

        const EquipMask BodyArmor = EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor
                                  | EquipMask.LowerArmArmor | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor;

        static bool On
        {
            get
            {
                Register();
                return ACE.Server.Managers.PropertyManager.GetBool(P_ON, true).Item;
            }
        }

        static void Register()
        {
            if (System.Threading.Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new ACE.Server.Managers.Property<bool>(true, "RevivalGuard TailoringParity: single-slot armour cannot be reduced, and tailoring keeps the target's material (retail; false: ACE)");
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] TailoringParity: {P_ON} not registered with @modifybool ({e.Message}); it reads its default (on)"); }
        }

        /// <summary>How many of the six body armour slots a coverage holds.</summary>
        internal static int BodySlots(EquipMask valid)
        {
            uint v = (uint)(valid & BodyArmor);
            int n = 0;
            for (; v != 0; v &= v - 1) n++;
            return n;
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorReduceArmor))]
        static class SingleSlotNotReducible
        {
            static bool Prefix(Player player, WorldObject source, WorldObject target)
            {
                if (target == null || target.ItemWorkmanship == null || !On) return true;   // ACE refuses non-loot itself
                var valid = target.ValidLocations ?? EquipMask.None;
                if (BodySlots(valid) > 1) return true;
                Mod.Log.Info($"[RevivalGuard] TailoringParity: refused {source?.Name} on single-slot {target.Name} "
                             + $"(0x{target.Guid.Full:X8}, covers 0x{(uint)valid:X}) for {player?.Name}; tool kept");
                TailoringWords.Say(player, $"Your {target.Name} covers a single slot already, so it cannot be reduced. Your tool was not used.");
                player?.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return false;
            }
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.UpdateCommonProps))]
        static class MaterialStays
        {
            static void Prefix(WorldObject target, out (bool run, int? material) __state)
                => __state = (target != null && On, (int?)target?.MaterialType);

            static void Postfix(Player player, WorldObject target, (bool run, int? material) __state)
            {
                if (!__state.run || target == null || (int?)target.MaterialType == __state.material) return;
                player.UpdateProperty(target, PropertyInt.MaterialType, __state.material);
            }
        }
    }
}
