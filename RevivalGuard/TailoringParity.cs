using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// TWO RETAIL TAILORING RULES ACE MISSES (sweep docs/findings/tailoring-reduction-2026-11-06.md).
    ///
    /// 1. SINGLE-SLOT ARMOUR CANNOT BE REDUCED. Retail: the Armor Reduction Tools work on "loot generated multi-slot
    ///    armor" (each tool's own description), single-slot armour is on the wiki's "cannot be reduced" list, and the
    ///    Master of Design notes (November 2011) let sleeves and leggings reduce "down to any of the slots it covers",
    ///    never to a slot they do not cover. ACE's Tailoring.TailorReduceArmor tests one coverage flag at a time, so:
    ///      - the Lower tool on single-slot pauldrons (upper arms) turned them into bracers (lower arms), and on single
    ///        tassets (upper legs) into greaves (lower legs): a slot swap retail never had. On a worn piece,
    ///        WornTailoring then re-slotted it onto whatever was already worn there;
    ///      - the Main tool on a breastplate, girth or pauldrons, the Middle tool on tassets, and any tool on a piece
    ///        already reduced, used the tool up and changed nothing.
    ///    Now a loot piece that covers one body armour slot or fewer (Chest|Abdomen|UpperArm|LowerArm|UpperLeg|LowerLeg,
    ///    0x7E00) is refused with ACE's usual unnamed 0x0437 and the tool is kept. Every multi-slot loot coverage in
    ///    ace_world (0x0600 cuirass, 0x1800 sleeves, 0x1A00 coat, 0x1E00 hauberk, 0x6000 and 0x6400 leggings) still
    ///    reduces exactly as before. Retail's words for this refusal are not on record.
    ///
    /// 2. THE MATERIAL STAYS WITH THE TARGET. Master of Design release notes, November 2011: "Material no longer
    ///    transferred through tailoring". ACE's Tailoring.UpdateCommonProps copies the look piece's MaterialType onto
    ///    the target (the 2010 behaviour), which changes the material shown on the item and what it salvages into.
    ///    Now the target keeps its own MaterialType; everything else the intermediate carries (setup, clothing and
    ///    palette ids, palette template, shades, icon, name) still moves, so the look is unchanged.
    ///
    ///   @modifybool tailoring_retail_rules false   ACE's behaviour for both
    /// </summary>
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
