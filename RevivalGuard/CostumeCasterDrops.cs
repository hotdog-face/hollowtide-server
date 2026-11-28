using System.Reflection;
using ACE.Common;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Costume casters as a rare world drop.</summary>
    [HarmonyPatch]
    static class CostumeCasterDrops
    {
        internal const string P_ON = "rg_costume_caster_drops", P_RATE = "rg_costume_caster_rate";
        internal static bool On => PropertyManager.GetBool(P_ON, false).Item;
        static double Rate => Math.Max(0.0, PropertyManager.GetDouble(P_RATE, 1.0).Item);
        static int s_Given;

        static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard CostumeCasterDrops: level 20+ creatures rarely drop one of the 13 costume casters (SeasonalSourcesRule 2b)");
                var d = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                if (!d.ContainsKey(P_RATE)) d[P_RATE] = new Property<double>(1.0, "RevivalGuard CostumeCasterDrops: multiplier on the costume caster drop chance (tests)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CostumeCasterDrops: switches not registered ({e.Message}); still read at their defaults"); }
            Mod.Log.Info($"[RevivalGuard] CostumeCasterDrops: {(On ? "ON" : "off")} ({P_ON}); {SeasonalSourcesRule.CostumeCasters.Length} casters, 1 in {SeasonalSourcesRule.CASTER_DROP_ONE_IN}, level {SeasonalSourcesRule.CASTER_DROP_MIN_LEVEL}+");
        }

        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            if (m == null) Mod.Log.Error("[RevivalGuard] CostumeCasterDrops: Creature.GenerateTreasure not found; no caster drops");
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
                if (corpse == null || __instance == null || __instance is Player || killer == null) return;
                double chance = SeasonalSourcesRule.CasterDropChance(On, __instance.Level ?? 0, Rate);
                if (chance <= 0) return;
                if (!(killer.TryGetPetOwnerOrAttacker() is Player player) || Mercenaries.IsMerc(player)) return;
                if (ThreadSafeRandom.Next(0.0f, 1.0f) >= chance) return;   // one roll per kill
                var wcid = SeasonalSourcesRule.CasterDropPick(ThreadSafeRandom.Next(0.0f, 1.0f));
                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null) { Mod.Log.Warn($"[RevivalGuard] CostumeCasterDrops: weenie {wcid} missing"); return; }
                if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
                s_Given++;
                Mod.Log.Info($"[RevivalGuard] CostumeCasterDrops: {wo.Name} ({wcid}) in the corpse of {__instance.Name} (level {__instance.Level}), killed by {player.Name} ({s_Given} this run)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CostumeCasterDrops: drop: {e.Message}"); }
        }
    }
}
