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
    /// OWNER-APPROVED DEPARTURE FROM RETAIL (owner, 2026-11-08, report 261109-0006): "i want dapper suit and other
    /// suits to be able to be reduced to single slot. this breaks retail parody i think. but i want players to use
    /// it as a tailor." Retail's Armor Reduction Tools work on "loot generated multi-slot armor" only, and full robes
    /// and guises cannot be tailored at all (RobeTailoring). Here a full-body OUTFIT can be reduced like a loot
    /// hauberk, so its look can then be taken with an Armor Tailoring Kit and put on armour.
    ///
    /// WHAT COUNTS AS AN OUTFIT. Not loot (no workmanship), covers all six body armour slots (Chest|Abdomen|UpperArm|
    /// LowerArm|UpperLeg|LowerLeg, the RobeTailoring test), and carries NO spells. In ace_world that is the Dapper
    /// Suit, Sleek Dress, Trench Coat, Bathrobes, Wedding Gown/Raiment/Cyclas, the plain gowns and dresses, the plain
    /// society robes (Faran/Dho/Suikan/Vestiri Robe), the Festival Robe and the no-spell guises. Robes with spells
    /// (every mage robe, Aerfalle's Pallium, Siraluun dresses, Empyrean robes...) stay as retail: reducing one to the
    /// chest would let its spells sit under a full armour set.
    ///
    /// THE TOOLS DO WHAT THEY DO ON A HAUBERK (ACE's TailorReduceArmor order): Main keeps the chest (ValidLocations
    /// 0x200, ClothingPriority OuterwearChest), Middle keeps the upper legs (0x2000, OuterwearUpperLegs), Lower is
    /// refused (ACE refuses it on anything covering the chest). The tool is used up, the piece is then chest- or
    /// leg-only, so RobeTailoring no longer sees a full robe and an Armor Tailoring Kit takes its look (the chest
    /// intermediate). ReducedTailoring treats it as reduced like any reduced loot piece.
    ///
    ///   @modifybool suit_reduction false   retail: outfits cannot be reduced
    /// </summary>
    static class SuitReduction
    {
        internal const string P_ON = "suit_reduction";
        static int s_Registered;

        const EquipMask FullBody = EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor
                                 | EquipMask.LowerArmArmor | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor;

        internal static bool On
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
                if (!b.ContainsKey(P_ON)) b[P_ON] = new ACE.Server.Managers.Property<bool>(true, "RevivalGuard SuitReduction: full-body outfits with no spells (Dapper Suit etc.) can be reduced to one slot (owner-approved, not retail; false: retail)");
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] SuitReduction: {P_ON} not registered with @modifybool ({e.Message}); it reads its default (on)"); }
        }

        static bool NoSpells(WorldObject wo)
        {
            var book = wo.Biota?.PropertiesSpellBook;
            return book == null || book.Count == 0;
        }

        /// <summary>A not-yet-reduced outfit: non-loot, full body, no spells.</summary>
        internal static bool IsOutfit(WorldObject wo)
            => wo != null && wo.ItemWorkmanship == null
               && ((wo.ValidLocations ?? EquipMask.None) & FullBody) == FullBody && NoSpells(wo);

        /// <summary>Its weenie is an outfit (for ReducedTailoring: a reduced outfit counts as reduced).</summary>
        internal static bool WeenieIsOutfit(WorldObject wo)
        {
            if (wo == null || wo.ItemWorkmanship != null || !NoSpells(wo)) return false;
            var weenie = DatabaseManager.World.GetCachedWeenie(wo.WeenieClassId);
            var orig = (EquipMask)(weenie?.GetProperty(PropertyInt.ValidLocations) ?? 0);
            return (orig & FullBody) == FullBody;
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorReduceArmor))]
        static class Reduce
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Player player, WorldObject source, WorldObject target)
            {
                if (player == null || source == null || target == null || !On) return true;
                if (!IsOutfit(target))
                {
                    if (!WeenieIsOutfit(target)) return true;   // everything else: ACE + TailoringParity
                    // an outfit already reduced: ACE would call it "not loot armor"; say what it is
                    TailoringWords.Say(player, $"Your {target.Name} covers a single slot already, so it cannot be reduced. Your tool was not used.");
                    player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                    return false;
                }

                EquipMask slot;
                CoverageMask cover;
                switch (source.WeenieClassId)
                {
                    case Tailoring.ArmorMainReductionTool:   slot = EquipMask.ChestArmor;    cover = CoverageMask.OuterwearChest;     break;
                    case Tailoring.ArmorMiddleReductionTool: slot = EquipMask.UpperLegArmor; cover = CoverageMask.OuterwearUpperLegs; break;
                    default:
                        TailoringWords.Say(player, $"The {source.Name} cannot reduce the {target.Name}. Use a Main Reduction Tool to keep the top, or a Middle Reduction Tool to keep the legs. Your tool was not used.");
                        player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                        return false;
                }

                player.UpdateProperty(target, PropertyInt.ValidLocations, (int)slot);
                player.UpdateProperty(target, PropertyInt.ClothingPriority, (int)cover);
                player.Session.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(
                    $"You reduce your {target.Name} to a single slot. You can now take its look with an Armor Tailoring Kit.", ChatMessageType.Broadcast));
                player.TryConsumeFromInventoryWithNetworking(source, 1);
                target.SaveBiotaToDatabase();
                player.SendUseDoneEvent();
                Mod.Log.Info($"[RevivalGuard] SuitReduction: {player.Name} reduced {target.Name} (0x{target.Guid.Full:X8}) to 0x{(uint)slot:X} with {source.Name}");
                return false;
            }
        }
    }
}
