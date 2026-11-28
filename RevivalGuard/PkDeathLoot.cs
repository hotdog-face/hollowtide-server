using System.Reflection;
using System.Reflection.Emit;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>PK death loot, retail's rules.</summary>
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
                    // level < 35 -> level < WieldGate(this, corpse)
                    var first = new CodeInstruction(OpCodes.Ldarg_0);
                    c.MoveLabelsTo(first); c.MoveBlocksTo(first);
                    result.Add(first);
                    result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(self, nameof(WieldGate))));
                    continue;
                }
                if (RareHooked && i == sort)
                {
                    // new DeathItems(inventory) -> new DeathItems(SortInput(inventory, this, corpse))
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
                    // GetSlipperyItems() -> WithRares(GetSlipperyItems(), this, corpse)
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
                var list = new List<WorldObject>(slippery ?? new List<WorldObject>());
                var seen = new HashSet<uint>(list.Select(i => i.Guid.Full));
                var rares = new List<WorldObject>();
                int sorted = 0;
                int forcedAlready = list.Where(IsRare).Select(i => i.Guid.Full).Distinct().Count();
                foreach (var item in p.GetAllPossessions())
                {
                    if (!IsRare(item)) continue;
                    if ((item.Bonded ?? BondedStatus.Normal) != BondedStatus.Normal) continue;
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

    /// <summary>How many items a PK death drops, retail's count.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.GetNumItemsDropped))]
    static class PkDeathItemCount
    {
        const string P_NPK = "retail_npk_death_count";
        const int RETAIL_NPK_MAX = 15;
        static int s_Registered;

        static bool Prepare()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 0)
            {
                try
                {
                    var b = MonsterAi.Inner(ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties);
                    if (!b.ContainsKey(P_NPK)) b[P_NPK] = new ACE.Server.Managers.Property<bool>(true, "RevivalGuard: NPK death item count as retail (11-20: 1 item; 21+: level/20 + 0..2, up to 15); off = ACE (0-1, cap 14)");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathItemCount: {P_NPK} not registered with @modifybool ({e.Message}); it reads its default (on)"); }
            }
            return true;
        }

        /// <summary>Retail's NPK count before Clutch of the Miser.</summary>
        internal static int RetailNpkCount(int level, int roll)
        {
            if (level <= 10) return 0;
            if (level <= 20) return 1;
            return Math.Min(level / 20 + roll, RETAIL_NPK_MAX);
        }

        static void Postfix(Player __instance, Corpse corpse, ref int __result)
        {
            try
            {
                if (__instance == null || corpse == null) return;
                if (__instance.IsPKDeath(corpse.KillerId))
                {
                    int level = Math.Min(__instance.Level ?? 1, 126);
                    __result = Math.Min(level / 10 + ACE.Common.ThreadSafeRandom.Next(0, 2), Player.MaxItemsDropped);
                    return;
                }
                if (!ACE.Server.Managers.PropertyManager.GetBool(P_NPK, true).Item) return;
                int n = RetailNpkCount(__instance.Level ?? 1, ACE.Common.ThreadSafeRandom.Next(0, 2));
                int miser = __instance.AugmentationLessDeathItemLoss;
                if (miser > 0) n = Math.Max(0, n - miser * 5);
                __result = n;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathItemCount: {e.Message}; ACE's count stands"); }
        }
    }
}
