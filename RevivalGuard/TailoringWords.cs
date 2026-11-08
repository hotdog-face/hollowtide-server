using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// EVERY TAILORING LINE SAYS WHAT HAPPENED AND WHAT TO DO NEXT, NAMING THE ITEM (owner 2026-11-06: "review all
    /// tailoring messages and make them as clear to players as possible"). docs/findings/tailoring-reduction-2026-11-06.md,
    /// "Messages". Retail's own tailoring lines are not on record (no hits in the 2017 captures, nothing in the client),
    /// so every line here replaces ACE's or fills a refusal that said nothing but the generic "Either you or one of the
    /// items involved does not pass the requirements for this craft interaction." (retail's WeenieError 0x0437 text,
    /// which still follows each refusal, unchanged). Kept as they are: "You must use Sandstone Salvage to remove the
    /// retained property before tailoring." (the wiki's retail rule, ACE's words) and the pack-space lines.
    ///
    ///   SUCCESS LINES: ACE's four fixed sentences ("You tailor the appearance off an existing piece of armor.", "...off
    ///   the weapon.", "...onto a different piece of armor.", "...onto a different weapon.", "You modify your armor.")
    ///   are swapped in the IL for a line built from the items (a transpiler on each ldstr), so the rest of ACE's code
    ///   runs untouched.
    ///   REFUSAL LINES: one prefix per method, last in order, speaks only when ACE itself is about to refuse (it tests
    ///   ACE's own conditions) and only when no guard of ours already refused (__runOriginal false: RobeTailoring,
    ///   ExpansionTailoring, ReducedTailoring and TailoringParity say their own line).
    ///   @modifybool tailoring_retail_rules false turns these off with TailoringParity (ACE's lines).
    /// The client (AcCraftWhy) stays quiet when one of these lines came first (AcTailoring.ServerExplains).
    /// </summary>
    static class TailoringWords
    {
        static bool On => ACE.Server.Managers.PropertyManager.GetBool(TailoringParity.P_ON, true).Item;

        internal static void Say(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Craft));
        }

        static string N(WorldObject wo) => string.IsNullOrEmpty(wo?.Name) ? "item" : wo.Name;

        internal static string Slots(EquipMask m)
        {
            var parts = new List<string>();
            if (m.HasFlag(EquipMask.HeadWear)) parts.Add("head");
            if (m.HasFlag(EquipMask.ChestArmor) || m.HasFlag(EquipMask.ChestWear)) parts.Add("chest");
            if (m.HasFlag(EquipMask.AbdomenArmor) || m.HasFlag(EquipMask.AbdomenWear)) parts.Add("abdomen");
            if (m.HasFlag(EquipMask.UpperArmArmor) || m.HasFlag(EquipMask.UpperArmWear)) parts.Add("upper arms");
            if (m.HasFlag(EquipMask.LowerArmArmor) || m.HasFlag(EquipMask.LowerArmWear)) parts.Add("lower arms");
            if (m.HasFlag(EquipMask.HandWear)) parts.Add("hands");
            if (m.HasFlag(EquipMask.UpperLegArmor) || m.HasFlag(EquipMask.UpperLegWear)) parts.Add("upper legs");
            if (m.HasFlag(EquipMask.LowerLegArmor) || m.HasFlag(EquipMask.LowerLegWear)) parts.Add("lower legs");
            if (m.HasFlag(EquipMask.FootWear)) parts.Add("feet");
            if (parts.Count == 0) return "the same slots";
            if (parts.Count == 1) return "the " + parts[0];
            return "the " + string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[parts.Count - 1];
        }

        // ---- success lines (called from the IL in place of ACE's strings) --------------------------------------------

        public static string TookArmorLook(WorldObject source, WorldObject target) => !On ? "You tailor the appearance off an existing piece of armor."
            : $"You take the look of the {N(target)} into an applicator; the {N(target)} is used up. Use the applicator on a piece that covers {Slots(target.ValidLocations ?? EquipMask.None)}.";

        public static string TookWeaponLook(WorldObject source, WorldObject target) => !On ? "You tailor the appearance off the weapon."
            : $"You take the look of the {N(target)} into an applicator; the {N(target)} is used up. Use the applicator on a weapon of the same type and damage type.";

        public static string GaveArmorLook(WorldObject source, WorldObject target) => !On ? "You tailor the appearance onto a different piece of armor."
            : $"Your {N(target)} now looks like the {N(source)}. It keeps its own armor, spells and other properties.";

        public static string GaveWeaponLook(WorldObject source, WorldObject target) => !On ? "You tailor the appearance onto a different weapon."
            : $"Your {N(target)} now looks like the {N(source)}. It keeps its own damage, spells and other properties.";

        public static string Reduced(WorldObject source, WorldObject target) => !On ? "You modify your armor."
            : $"Your {N(target)} now covers only {Slots(target.ValidLocations ?? EquipMask.None)}. It still looks the same; if another piece shows over it, use an Armor Layering Tool (Top) on it.";

        static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> code, string text, string builder)
        {
            var m = AccessTools.Method(typeof(TailoringWords), builder);
            int n = 0;
            foreach (var ci in code)
            {
                if (ci.opcode == OpCodes.Ldstr && (ci.operand as string) == text)
                {
                    n++;
                    var a = new CodeInstruction(OpCodes.Ldarg_1); a.labels.AddRange(ci.labels); a.blocks.AddRange(ci.blocks);
                    yield return a;                                       // source
                    yield return new CodeInstruction(OpCodes.Ldarg_2);    // target
                    yield return new CodeInstruction(OpCodes.Call, m);
                    continue;
                }
                yield return ci;
            }
            if (n != 1) Mod.Log.Warn($"[RevivalGuard] TailoringWords: \"{text}\" found {n} times; ACE's line stays where it was not swapped");
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorArmor))]
        static class T1 { static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => Swap(c, "You tailor the appearance off an existing piece of armor.", nameof(TookArmorLook)); }
        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorWeapon))]
        static class T2 { static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => Swap(c, "You tailor the appearance off the weapon.", nameof(TookWeaponLook)); }
        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.ArmorApply))]
        static class T3 { static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => Swap(c, "You tailor the appearance onto a different piece of armor.", nameof(GaveArmorLook)); }
        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.WeaponApply))]
        static class T4 { static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => Swap(c, "You tailor the appearance onto a different weapon.", nameof(GaveWeaponLook)); }
        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorReduceArmor))]
        static class T5 { static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => Swap(c, "You modify your armor.", nameof(Reduced)); }

        // ---- refusal lines for ACE's own refusals --------------------------------------------------------------------

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.VerifyUseRequirements))]
        static class VerifyWhy
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix(Player player, WorldObject source, WorldObject target, WeenieError __result)
            {
                if (!On || __result == WeenieError.None || source == null || target == null || source == target || target.Retained) return;
                if (source.IsSocietyArmor || target.IsSocietyArmor) Say(player, "Society armor cannot be tailored.");
            }
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorArmor))]
        static class TakeArmorWhy
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(Player player, WorldObject target, bool __runOriginal)
            {
                if (!__runOriginal || !On || target == null) return;
                if (Tailoring.GetArmorWCID(target.ValidLocations ?? EquipMask.None) == null)
                    Say(player, $"The {N(target)} is not armor, clothing, a cloak or a shield, so an Armor Tailoring Kit cannot take its look.");
            }
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorWeapon))]
        static class TakeWeaponWhy
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(Player player, WorldObject target, bool __runOriginal)
            {
                if (!__runOriginal || !On || target == null) return;
                if (!(target is MeleeWeapon) && !(target is MissileLauncher) && !(target is Caster))
                    Say(player, $"The {N(target)} is not a weapon, so a Weapon Tailoring Kit cannot take its look. Use an Armor Tailoring Kit on armor.");
                else if (target is MeleeWeapon && target.W_WeaponType == WeaponType.Undef)
                    Say(player, $"The {N(target)} is a weapon that is difficult to master, and those cannot be tailored.");
            }
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.TailorReduceArmor))]
        static class ReduceWhy
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(Player player, WorldObject source, WorldObject target, bool __runOriginal)
            {
                if (!__runOriginal || !On || target == null || source == null) return;
                if (target.ItemWorkmanship == null)
                {
                    Say(player, $"The {N(target)} is not loot armor, so it cannot be reduced. Your tool was not used.");
                    return;
                }
                var v = target.ValidLocations ?? EquipMask.None;
                bool ok = source.WeenieClassId switch
                {
                    Tailoring.ArmorMainReductionTool => v.HasFlag(EquipMask.ChestArmor) || v.HasFlag(EquipMask.UpperArmArmor) || v.HasFlag(EquipMask.AbdomenArmor),
                    Tailoring.ArmorLowerReductionTool => !v.HasFlag(EquipMask.ChestArmor) && (v.HasFlag(EquipMask.UpperArmArmor) || v.HasFlag(EquipMask.UpperLegArmor) || v.HasFlag(EquipMask.LowerLegArmor | EquipMask.FootWear)),
                    Tailoring.ArmorMiddleReductionTool => v.HasFlag(EquipMask.UpperLegArmor),
                    _ => true,
                };
                if (!ok)
                    Say(player, $"The {N(source)} cannot reduce the {N(target)}. Main keeps the chest, upper arms or abdomen; Lower keeps the lower arms or lower legs; Middle keeps the upper legs. Your tool was not used.");
            }
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.ArmorApply))]
        static class ApplyArmorWhy
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(Player player, WorldObject source, WorldObject target, bool __runOriginal)
            {
                if (!__runOriginal || !On || source == null || target == null || source.ClothingPriority == target.ClothingPriority) return;
                int look = Bits((uint)(source.ClothingPriority ?? 0)), piece = Bits((uint)(target.ClothingPriority ?? 0));
                if (ReducedTailoring.IsReduced(target) && look > piece)
                    Say(player, $"This look covers more slots than your {N(target)}, which is reduced. Put the look on before reducing, or reduce the look piece first.");
                else
                    Say(player, $"This look covers different slots than your {N(target)}. Use it on a piece that covers the same slots.");
            }
        }

        [HarmonyPatch(typeof(Tailoring), nameof(Tailoring.WeaponApply))]
        static class ApplyWeaponWhy
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(Player player, WorldObject source, WorldObject target, bool __runOriginal)
            {
                if (!__runOriginal || !On || source == null || target == null) return;
                bool refuse;
                switch (source.TargetType)
                {
                    case ItemType.MeleeWeapon: refuse = source.W_WeaponType != target.W_WeaponType || source.W_DamageType != target.W_DamageType; break;
                    case ItemType.MissileWeapon: refuse = source.DefaultCombatStyle != target.DefaultCombatStyle || source.W_DamageType != DamageType.Undef && source.W_DamageType != target.W_DamageType; break;
                    case ItemType.Caster: refuse = source.W_DamageType != DamageType.Undef && source.W_DamageType != target.W_DamageType; break;
                    default: refuse = true; break;
                }
                if (refuse)
                    Say(player, $"This look goes only on a weapon of the same type and damage type as the {N(source)}, and your {N(target)} is not one.");
            }
        }

        static int Bits(uint v) { int n = 0; for (; v != 0; v &= v - 1) n++; return n; }
    }
}
