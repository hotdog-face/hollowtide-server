using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE RUNE SKIN SETS AS END-GAME ARMOUR (owner 2026-11-25: "definitely end game style items, hard to get";
    /// docs/proposals/rune-skins-endgame.md, every number PROPOSED). One switch, rg_rune_skins, default OFF, covers:
    ///   - the boss drop: a themed boss (RuneSkinsRule.Bosses) a player kills rolls once; a hit puts one piece of its set,
    ///     slot at random, into the corpse. rune_skin_drop_force (0-100, default 0) replaces the rate, for tests.
    ///   - the PK Night prize: one Cragstone Guise piece (slot at random) per recorded night, to the winner who stood on
    ///     the hill longest (PkNight.GiveRunePrize; offline, at login through PkNight's owed list). Cragstone never drops.
    ///   - the set bonus: EquipmentSetId 9150-9177 (not in the DAT's SpellTable) answered with retail item-set spells,
    ///     RuneSkinsRule.Tier, as WardenforgedPlate does for 901.
    ///   - the quests: the theme's creatures carry tokens (RuneSkinsRule.TokenFor, 10%), a set's boss kill stamps
    ///     RuneSkinSlain<set>; the givers' emotes are data (gen_rune_quests.py, not placed).
    /// The end-game stats themselves are data (2026-11-25-rune-skins-endgame.sql).
    /// </summary>
    static class RuneSkins
    {
        internal const string P_ON = "rg_rune_skins", P_FORCE = "rune_skin_drop_force";
        internal static bool On => PropertyManager.GetBool(P_ON, false).Item;
        static long Force => PropertyManager.GetLong(P_FORCE, 0).Item;
        static int s_Registered, s_Dropped;

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard RuneSkins: rune skin sets drop from their themed bosses, are PK Night prizes, and their set bonus (proposal, off)");
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_FORCE)) l[P_FORCE] = new Property<long>(0, "RevivalGuard RuneSkins: 1-100 forces the boss drop percent (tests); 0 uses the table");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RuneSkins: switches not registered ({e.Message}); read at their defaults (off)"); }
            Mod.Log.Info($"[RevivalGuard] RuneSkins: {(On ? "ON" : "off")} ({P_ON}); {RuneSkinsRule.Bosses.Count} boss weenies, sets {RuneSkinsRule.SET0}-{RuneSkinsRule.SET0 + RuneSkinsRule.SETS - 1}");
        }

        static double R() => ThreadSafeRandom.Next(0.0f, 1.0f);

        /// <summary>PK Night: the piece for a winner (PkNight.Reward), or 0 when off.</summary>
        internal static uint PkNightPrize() => On ? RuneSkinsRule.PkPrize(R()) : 0;

        // ------------------------------------------------------------------ the boss drop
        [HarmonyPatch]
        static class BossDrop
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
                if (m == null) Mod.Log.Error("[RevivalGuard] RuneSkins: Creature.GenerateTreasure not found; no rune skin drops");
                return m;
            }

            static bool Prepare(MethodBase original)
            {
                if (original == null) Register();
                return original != null || TargetMethod() != null;
            }

            static void Postfix(Creature __instance, DamageHistoryInfo killer, Corpse corpse)
            {
                try
                {
                    if (corpse == null || __instance == null || __instance is Player || killer == null || !On) return;
                    if (!(killer.TryGetPetOwnerOrAttacker() is Player player) || Mercenaries.IsMerc(player)) return;
                    Token(__instance, corpse, player);
                    if (RuneSkinsRule.Bosses.TryGetValue(__instance.WeenieClassId, out var boss))
                        foreach (var set in boss.Sets) player.QuestManager.Stamp(RuneSkinsRule.SlainQuest(set));   // the quest's gate
                    double chance = RuneSkinsRule.Chance(On, __instance.WeenieClassId, Force);
                    if (chance <= 0) return;
                    var wcid = RuneSkinsRule.Drop(__instance.WeenieClassId, chance, R(), R(), R());
                    if (wcid == null) return;
                    var wo = WorldObjectFactory.CreateNewWorldObject(wcid.Value);
                    if (wo == null) { Mod.Log.Warn($"[RevivalGuard] RuneSkins: weenie {wcid} missing (2026-11-24-rune-suits.sql not applied?)"); return; }
                    if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
                    s_Dropped++;
                    Mod.Log.Info($"[RevivalGuard] RuneSkins: {wo.Name} ({wcid}) in the corpse of {__instance.Name} ({__instance.WeenieClassId}), killed by {player.Name}, chance {chance:P0} ({s_Dropped} this run)");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RuneSkins: drop: {e.Message}"); }
            }
        }

        /// <summary>The quest token of the creature's family (RuneSkinsRule.TokenFor), one roll per kill.</summary>
        static void Token(Creature c, Corpse corpse, Player player)
        {
            uint wcid = RuneSkinsRule.TokenFor(c.Name);
            double chance = Force > 0 ? Math.Min(100, Force) / 100.0 : RuneSkinsRule.TOKEN_CHANCE;
            if (wcid == 0 || R() >= chance) return;
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) return;   // 2026-11-25-rune-quests.sql not applied
            if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
            Mod.Log.Info($"[RevivalGuard] RuneSkins: {wo.Name} in the corpse of {c.Name}, killed by {player.Name}");
        }

        // ------------------------------------------------------------------ the set bonus (WardenforgedPlate's shape)
        static int SetOf(WorldObject wo) => (int?)wo?.EquipmentSetId is int id && RuneSkinsRule.IsSet(id) ? id - RuneSkinsRule.SET0 : -1;

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetSpellSet))]
        static class SetSpells
        {
            static void Postfix(List<WorldObject> setItems, int levelDiff, ref List<Spell> __result)
            {
                try
                {
                    int set = SetOf(setItems?.FirstOrDefault());
                    if (set < 0 || !On) return;
                    __result = RuneSkinsRule.Tier(set, setItems.Count).Select(id => new Spell(id, false)).ToList();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RuneSkins.SetSpells: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetSpellSetAll))]
        static class SetSpellsAll
        {
            static void Postfix(EquipmentSet equipmentSet, ref List<Spell> __result)
            {
                if (!RuneSkinsRule.IsSet((int)equipmentSet) || !On) return;
                __result = RuneSkinsRule.AllSpells((int)equipmentSet - RuneSkinsRule.SET0).Select(id => new Spell(id, false)).ToList();
            }
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ItemSetContains))]
        static class SetContains
        {
            static void Postfix(WorldObject __instance, uint spellID, ref bool __result)
            {
                int set = SetOf(__instance);
                if (__result || set < 0 || !On) return;
                __result = RuneSkinsRule.AllSpells(set).Contains(spellID);
            }
        }
    }
}
