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
    /// death over ACE's (2026-09-22, docs/PK-RULESET-2026-09-22.md). The wiki's Death Penalty page,
    /// "Player Killer Deaths": "Wielded items are not excluded from dropping, and you will always lose
    /// 50% of your pyreals and all your Rares." ACE's Player.CalculateDeathItems keeps the 35+ gate on
    /// wielded gear for a PK death (measured: a level 23 victim kept its worn Mace and Buckler twice)
    /// and has no rare step at all (the Eye of Muramm dropped only when it topped the value sort).
    ///
    /// ONE TRANSPILER on the method that declares the rule, with three anchors that each occur once
    /// in the live dll (verified by reading its IL, 2026-09-22):
    ///   1. "ldc.i4.s 35" in "canDropWielded = level >= 35" becomes WieldGate(this, corpse), which is
    ///      35 for a normal death and 0 for a PK death, so wielded gear is eligible at any level;
    ///   2. "new DeathItems(inventory)" takes SortInput(inventory, this, corpse), which leaves rares
    ///      out of the value sort on a PK death so they never consume one of the level/20 + 0..2 slots;
    ///   3. "GetSlipperyItems()" returns WithRares(list, this, corpse), which on a PK death appends every
    ///      rare still in the victim's possession, worn or packed. ACE's own loop then removes them,
    ///      puts them on the corpse, lists them in the one retail-format "You've lost ..." line and in
    ///      the [CORPSE] log line, the same way the Slippery crystals already went.
    /// A rare is an item whose weenie carries PropertyInt.RareId (17): 339 weenies in ace_world, every
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
            Mod.Log.Info($"[RevivalGuard] PkDeathLoot: wielded-at-any-level {(WieldHooked ? "on" : "OFF (anchor missing)")}, every-rare-drops {(RareHooked ? "on" : "OFF (anchor missing)")}");
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
                Mod.Log.Error($"[RevivalGuard] PkDeathLoot: {sortN} DeathItems ctor and {slipN} GetSlipperyItems calls in CalculateDeathItems, expected 1 each; rares keep ACE's value sort on PK deaths");

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

        /// <summary>The value-sort input. On a PK death the rares are taken out, so the level/20 + 0..2 slots go to other items and every rare drops on top through WithRares.</summary>
        public static List<WorldObject> SortInput(List<WorldObject> inventory, Player p, Corpse c)
        {
            try
            {
                if (inventory == null || !IsPkDeath(p, c)) return inventory;
                return inventory.Where(i => !IsRare(i)).ToList();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathLoot: {e.GetType().Name} sorting {p?.Name}'s death items: {e.Message}; ACE's sort stands"); return inventory; }
        }

        /// <summary>ACE's always-drop list (BondedStatus.Slippery), plus every unbonded rare on a PK death.</summary>
        public static List<WorldObject> WithRares(List<WorldObject> slippery, Player p, Corpse c)
        {
            try
            {
                if (!IsPkDeath(p, c)) return slippery;
                var list = new List<WorldObject>(slippery ?? new List<WorldObject>());
                var seen = new HashSet<uint>(list.Select(i => i.Guid.Full));
                var rares = new List<WorldObject>();
                foreach (var item in p.GetAllPossessions())
                {
                    if (!IsRare(item)) continue;
                    if ((item.Bonded ?? BondedStatus.Normal) != BondedStatus.Normal) continue;   // Bonded never drops; Destroy is gone; Slippery is already listed
                    if (!seen.Add(item.Guid.Full)) continue;
                    rares.Add(item);
                }
                list.AddRange(rares);
                var names = rares.Count == 0 ? "none" : string.Join(", ", rares.Select(r => $"{r.Name}{(r.CurrentWieldedLocation != null ? " (worn)" : "")}"));
                Mod.Log.Info($"[RevivalGuard] PkDeathLoot: {p.Name} (0x{p.Guid.Full:X8}, level {p.Level ?? 1}) killed by 0x{c.KillerId ?? 0:X8}: wielded gear eligible, {rares.Count} rare(s) forced beyond the item count: {names}");
                return list;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathLoot: {e.GetType().Name} listing {p?.Name}'s rares: {e.Message}; ACE's list stands"); return slippery; }
        }
    }
}
