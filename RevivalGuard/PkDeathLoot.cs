using System.Reflection;
using System.Reflection.Emit;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// PK DEATH LOOT, RETAIL'S RULES. The shard is full PvP and the owner chose retail's harsher PK
    /// death over ACE's (2026-09-22, docs/PK-RULESET-2026-09-22.md). Turbine's April 2010 update says
    /// rare weapons, armor and jewelry are no longer guaranteed to drop; they stay in ACE's value sort.
    /// The remaining normal-bonded rare types are forced onto the corpse. The update does not say
    /// clearly whether its equipment rule covers every rare or only newly rebalanced rares, so this
    /// applies the named equipment categories to all rares. ACE's Player.CalculateDeathItems keeps the 35+ gate on
    /// wielded gear for a PK death (measured: a level 23 victim kept its worn Mace and Buckler twice)
    /// and has no rare step at all (the Eye of Muramm dropped only when it topped the value sort).
    ///
    /// ONE TRANSPILER on the method that declares the rule, with three anchors that each occur once
    /// in the live dll (verified by reading its IL, 2026-09-22):
    ///   1. "ldc.i4.s 35" in "canDropWielded = level >= 35" becomes WieldGate(this, corpse), which is
    ///      35 for a normal death and 0 for a PK death, so wielded gear is eligible at any level;
    ///   2. "new DeathItems(inventory)" takes SortInput(inventory, this, corpse), which removes only
    ///      non-equipment rares from the value sort on a PK death;
    ///   3. "GetSlipperyItems()" returns WithRares(list, this, corpse), which appends each normal-bonded
    ///      non-equipment rare still in the victim's possession, worn or packed. ACE's own loop removes them,
    ///      puts them on the corpse, lists them in the one retail-format "You've lost ..." line and in
    ///      the [CORPSE] log line, the same way the Slippery crystals already went.
    /// Equipment means melee, missile and caster weapons, armor, jewelry, and clothing whose ValidLocations
    /// overlap armor or extremity slots. This follows Turbine's April 2010 "Lay of the Land" update:
    /// https://asheron.fandom.com/wiki/Announcements_-_2010/03_-_Lay_of_the_Land . A rare is an item whose
    /// weenie carries PropertyInt.RareId (17): 339 weenies in ace_world, every
    /// wcid in ACE's LootGenerationFactory_Rare tier tables among them, and TryCreateRare builds the
    /// object from that weenie. Bonded (BondedStatus.Bonded) rares, the foolproof rare salvage, stay
    /// bonded and never drop; Destroy rares are destroyed by ACE before this step; Slippery ones are
    /// already in the list. A non-PK death is untouched: IsPKDeath is false, every helper returns its
    /// input. Any anchor that is missing is logged and that rule is left as ACE has it, never thrown.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.CalculateDeathItems))]
    static class PkDeathLoot
    {
        const int RETAIL_WIELD_LEVEL = 35;
        internal static bool WieldHooked, RareHooked;

        public static void Register()
        {
            Mod.Log.Info($"[RevivalGuard] PkDeathLoot: wielded-at-any-level {(WieldHooked ? "on" : "OFF (anchor missing)")}, rare split {(RareHooked ? "on" : "OFF (anchor missing)")}");
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            try { return Rewrite(code); }
            catch (Exception e)
            {
                WieldHooked = RareHooked = false;
                Mod.Log.Error($"[RevivalGuard] PkDeathLoot: {e.GetType().Name} rewriting {original?.Name}: {e.Message}; PK deaths keep ACE's rules");
                return code;
            }
        }

        static List<CodeInstruction> Rewrite(List<CodeInstruction> code)
        {
            int gate = -1, sort = -1, slip = -1, gateN = 0, sortN = 0, slipN = 0;
            for (int i = 0; i < code.Count; i++)
            {
                var c = code[i];
                if (c.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(c.operand) == RETAIL_WIELD_LEVEL) { gate = i; gateN++; }
                else if (c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo ci && ci.DeclaringType == typeof(DeathItems)) { sort = i; sortN++; }
                else if (c.opcode == OpCodes.Call && c.operand is MethodInfo mi && mi.DeclaringType == typeof(Player) && mi.Name == nameof(Player.GetSlipperyItems)) { slip = i; slipN++; }
            }
            WieldHooked = gateN == 1;
            RareHooked = sortN == 1 && slipN == 1;
            if (!WieldHooked)
                Mod.Log.Error($"[RevivalGuard] PkDeathLoot: {gateN} 'ldc.i4.s 35' in CalculateDeathItems, expected 1; wielded gear keeps ACE's 35+ gate on PK deaths");
            if (!RareHooked)
                Mod.Log.Error($"[RevivalGuard] PkDeathLoot: {sortN} DeathItems ctor and {slipN} GetSlipperyItems calls in CalculateDeathItems, expected 1 each; rare handling stays with ACE on PK deaths");

            var self = typeof(PkDeathLoot);
            var result = new List<CodeInstruction>(code.Count + 9);
            for (int i = 0; i < code.Count; i++)
            {
                var c = code[i];
                if (WieldHooked && i == gate)
                {
                    // level < 35  ->  level < WieldGate(this, corpse)
                    var first = new CodeInstruction(OpCodes.Ldarg_0);
                    c.MoveLabelsTo(first); c.MoveBlocksTo(first);
                    result.Add(first);
                    result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(self, nameof(WieldGate))));
                    continue;
                }
                if (RareHooked && i == sort)
                {
                    // new DeathItems(inventory)  ->  new DeathItems(SortInput(inventory, this, corpse))
                    var first = new CodeInstruction(OpCodes.Ldarg_0);
                    c.MoveLabelsTo(first); c.MoveBlocksTo(first);
                    result.Add(first);
                    result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(self, nameof(SortInput))));
                    result.Add(c);
                    continue;
                }
                result.Add(c);
                if (RareHooked && i == slip)
                {
                    // GetSlipperyItems()  ->  WithRares(GetSlipperyItems(), this, corpse)
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(self, nameof(WithRares))));
                }
            }
            return result;
        }

        static bool IsPkDeath(Player p, Corpse c)
        {
            try { return p != null && c != null && p.IsPKDeath(c.KillerId); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathLoot: {e.GetType().Name} reading the death of {p?.Name}: {e.Message}; treated as a normal death"); return false; }
        }

        internal static bool IsRare(WorldObject wo) => wo != null && (wo.GetProperty(PropertyInt.RareId) ?? 0) > 0;

        /// <summary>The level below which wielded items are protected: retail's 35 for a normal death, none for a PK death.</summary>
        public static int WieldGate(Player p, Corpse c) => IsPkDeath(p, c) ? 0 : RETAIL_WIELD_LEVEL;

        const ItemType SORTED_RARE_GEAR = ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster | ItemType.Armor | ItemType.Jewelry;
        const EquipMask ARMOR_WEAR = EquipMask.Armor | EquipMask.Extremity;

        static bool IsRareEquipment(WorldObject item)
        {
            if (item == null) return false;
            if ((item.ItemType & SORTED_RARE_GEAR) != 0) return true;
            return (item.ItemType & ItemType.Clothing) != 0 && ((item.ValidLocations ?? EquipMask.None) & ARMOR_WEAR) != 0;
        }

        /// <summary>On a PK death only non-equipment rares leave ACE's value sort for the forced-drop list.</summary>
        public static List<WorldObject> SortInput(List<WorldObject> inventory, Player p, Corpse c)
        {
            try
            {
                if (inventory == null || !IsPkDeath(p, c)) return inventory;
                return inventory.Where(i => !IsRare(i) || IsRareEquipment(i)).ToList();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathLoot: {e.GetType().Name} sorting {p?.Name}'s death items: {e.Message}; ACE's sort stands"); return inventory; }
        }

        /// <summary>ACE's always-drop list (BondedStatus.Slippery), plus normal-bonded non-equipment rares on a PK death.</summary>
        public static List<WorldObject> WithRares(List<WorldObject> slippery, Player p, Corpse c)
        {
            try
            {
                if (!IsPkDeath(p, c)) return slippery;
                // ACE's Slippery list stays whole: a Slippery item always drops, and the value sort never sees bonded
                // items, so taking Slippery rare gear off this list would make it never drop at all.
                var list = new List<WorldObject>(slippery ?? new List<WorldObject>());
                var seen = new HashSet<uint>(list.Select(i => i.Guid.Full));
                var rares = new List<WorldObject>();
                int sorted = 0;
                int forcedAlready = list.Where(IsRare).Select(i => i.Guid.Full).Distinct().Count();
                foreach (var item in p.GetAllPossessions())
                {
                    if (!IsRare(item)) continue;
                    if ((item.Bonded ?? BondedStatus.Normal) != BondedStatus.Normal) continue;   // Bonded never drops; Destroy is gone; Slippery is already listed
                    if (IsRareEquipment(item)) { sorted++; continue; }
                    if (!seen.Add(item.Guid.Full)) continue;
                    rares.Add(item);
                }
                list.AddRange(rares);
                var names = rares.Count == 0 ? "none" : string.Join(", ", rares.Select(r => $"{r.Name}{(r.CurrentWieldedLocation != null ? " (worn)" : "")}"));
                Mod.Log.Info($"[RevivalGuard] PkDeathLoot: {p.Name} (0x{p.Guid.Full:X8}, level {p.Level ?? 1}) killed by 0x{c.KillerId ?? 0:X8}: wielded gear eligible, {forcedAlready + rares.Count} rare(s) forced, {sorted} normal-bonded rare(s) in the value sort: {names}");
                return list;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathLoot: {e.GetType().Name} listing {p?.Name}'s rares: {e.Message}; ACE's list stands"); return slippery; }
        }
    }

    /// <summary>
    /// HOW MANY ITEMS A PK DEATH DROPS, RETAIL'S COUNT (owner 2026-10-17: "yes do retail", after the loot research in
    /// docs/LOOT-RESEARCH-2026-10-17.md). Turbine's Throne of Destiny FAQ: NPK deaths moved to floor(level/20) + 0..2, but PK
    /// deaths kept the old count, floor(effective level / 10) + 0..2 with the effective level capped at 126 (12 to 14 items
    /// at the cap). ACE's GetNumItemsDropped uses /20 for every death (its own comment quotes the /10 rule), so a level 100
    /// PK lost 5 to 7 items where retail took 10 to 12. A PK death now takes retail's count; a normal death is untouched.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.GetNumItemsDropped))]
    static class PkDeathItemCount
    {
        static void Postfix(Player __instance, Corpse corpse, ref int __result)
        {
            try
            {
                if (__instance == null || corpse == null || !__instance.IsPKDeath(corpse.KillerId)) return;
                int level = System.Math.Min(__instance.Level ?? 1, 126);
                __result = System.Math.Min(level / 10 + ACE.Common.ThreadSafeRandom.Next(0, 2), Player.MaxItemsDropped);
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathItemCount: {e.Message}; ACE's count stands"); }
        }
    }
}
