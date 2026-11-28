using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Joke scrolls as festival and event rewards.</summary>
    [HarmonyPatch]
    static class JokeScrollRewards
    {
        internal const string P_ON = "rg_joke_scroll_rewards", P_RATE = "rg_joke_scroll_rate";
        const uint PRESENT = SeasonalSourcesRule.PRESENT;
        static readonly uint[] BOOM = SeasonalSourcesRule.BOOM;
        // the festival table (rates, pools, the Mad Cow rule) is SeasonalSourcesRule.JokeDrops, checked by tools/nettest

        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;
        static double Rate => Math.Max(0.0, PropertyManager.GetDouble(P_RATE, 1.0).Item);
        static int s_Given;

        static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard JokeScrollRewards: joke spell scrolls drop during festivals and are handed out when players win a live event (docs/JOKE-SCROLLS-FESTIVALS.md)");
                var d = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                if (!d.ContainsKey(P_RATE)) d[P_RATE] = new Property<double>(1.0, "RevivalGuard JokeScrollRewards: multiplier on every festival scroll drop chance (tests)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: switches not registered ({e.Message}); still read at their defaults"); }
            foreach (var f in SeasonalSourcesRule.JokeDrops)
                if (!SeasonalCalendar.IsFestivalKey(f.Festival)) Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: no festival '{f.Festival}'");
            Mod.Log.Info($"[RevivalGuard] JokeScrollRewards: {(On ? "ON" : "off")} ({P_ON}); {SeasonalSourcesRule.JokeDrops.Length} festivals, event wins");
        }

        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            if (m == null) Mod.Log.Error("[RevivalGuard] JokeScrollRewards: Creature.GenerateTreasure not found; no festival scrolls");
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
                var d = SeasonalSourcesRule.JokeDropFor(__instance.WeenieClassId, SeasonalCalendar.IsFestivalActive);
                if (d == null || ThreadSafeRandom.Next(0.0f, 1.0f) >= SeasonalSourcesRule.JokeChance(d, Rate)) return;   // one roll per kill
                var wcid = d.Pool[ThreadSafeRandom.Next(0, d.Pool.Length - 1)];
                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: weenie {wcid} missing"); return; }
                if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
                s_Given++;
                Mod.Log.Info($"[RevivalGuard] JokeScrollRewards: {d.Festival}: {wo.Name} in the corpse of {__instance.Name}, killed by {player.Name} ({s_Given} this run)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: drop: {e.Message}"); }
        }

        /// <summary>From EventDirector.End when the players won: one scroll each for the real players near it.</summary>
        internal static void EventWon(string key, IEnumerable<Player> near)
        {
            if (!On) return;
            try
            {
                bool presents = key.StartsWith("presents-", StringComparison.OrdinalIgnoreCase);
                foreach (var p in near)
                {
                    if (p == null || Mercenaries.IsMerc(p)) continue;
                    var wcid = presents ? PRESENT : BOOM[ThreadSafeRandom.Next(0, BOOM.Length - 1)];
                    var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                    if (wo == null) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: weenie {wcid} missing"); return; }
                    if (!p.TryCreateInInventoryWithNetworking(wo))
                    {
                        wo.Destroy();
                        p.Session?.Network.EnqueueSend(new GameMessageSystemChat("You were to receive a scroll for your part in the fight, but your pack is full.", ChatMessageType.Broadcast));
                        continue;
                    }
                    p.Session?.Network.EnqueueSend(new GameMessageSystemChat($"For your part in the fight, you receive a {wo.Name}.", ChatMessageType.Broadcast));
                    s_Given++;
                    Mod.Log.Info($"[RevivalGuard] JokeScrollRewards: {key} won: {wo.Name} to {p.Name}");
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: event reward: {e.Message}"); }
        }
    }
}
