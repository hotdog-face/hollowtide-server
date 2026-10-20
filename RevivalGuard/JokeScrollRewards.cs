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
    /// <summary>
    /// JOKE SCROLLS AS FESTIVAL AND EVENT REWARDS (owner 2026-10-20 chose "Festival/event rewards" after removing Tumble;
    /// proposal docs/JOKE-SCROLLS-FESTIVALS.md). OFF until he approves: @modifybool rg_joke_scroll_rewards true.
    /// No NPC, no world object: the scrolls (gen_joke_spells.py, 901661-901673) arrive two retail-shaped ways.
    ///   1. A creature a player kills while a SeasonalCalendar festival runs may carry its festival's scroll in its
    ///      corpse (SeasonalDrops' shape: festival trophies off ordinary creatures). One roll per kill.
    ///   2. An EventDirector event WON by the players hands each real player near it one scroll (EventDirector.End).
    /// rg_joke_scroll_rate (default 1) multiplies every drop chance, for tests. Never the Egg Launcher (holiday only).
    /// </summary>
    [HarmonyPatch]
    static class JokeScrollRewards
    {
        internal const string P_ON = "rg_joke_scroll_rewards", P_RATE = "rg_joke_scroll_rate";
        const uint COW = 901661, PRESENT = 901662, TABLE = 901663, BOVINE = 901664, OLDSCHOOL = 901665;
        static readonly uint[] BOOM = { 901666, 901667, 901668, 901669, 901670, 901671, 901672, 901673 };   // 8 colours
        const uint MAD_COW = 23623;   // cowmad, the April Fools' Mad Cow (eventmadcowgen 23631)

        // festival key -> (1 in N kills, the pool one is picked from, only from this creature or 0 = any creature)
        static readonly (string fest, int oneIn, uint[] pool, uint only)[] s_Festival =
        {
            ("festivalseason", 1000, new[] { COW }, 0),                      // the harvest: a cow
            ("aprilfools", 12, new[] { BOVINE, COW }, MAD_COW),              // raining cows: the Mad Cows only
            ("frostfell", 1000, new[] { PRESENT }, 0),                       // gift season: a present
            ("wedding", 500, new[] { TABLE, OLDSCHOOL }, 0),                 // the reception: a table, WeddingBliss fireworks
            ("anniversary", 200, BOOM.Append(OLDSCHOOL).ToArray(), 0),       // the fireworks week
            ("newyearsday", 200, BOOM, 0),                                   // fireworks for the new year
        };

        internal static bool On => PropertyManager.GetBool(P_ON, false).Item;
        static double Rate => Math.Max(0.0, PropertyManager.GetDouble(P_RATE, 1.0).Item);
        static int s_Given;

        static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard JokeScrollRewards: joke spell scrolls drop during festivals and are handed out when players win a live event (docs/JOKE-SCROLLS-FESTIVALS.md)");
                var d = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                if (!d.ContainsKey(P_RATE)) d[P_RATE] = new Property<double>(1.0, "RevivalGuard JokeScrollRewards: multiplier on every festival scroll drop chance (tests)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: switches not registered ({e.Message}); still read at their defaults"); }
            foreach (var f in s_Festival)
                if (!SeasonalCalendar.IsFestivalKey(f.fest)) Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: no festival '{f.fest}'");
            Mod.Log.Info($"[RevivalGuard] JokeScrollRewards: {(On ? "ON" : "off")} ({P_ON}); {s_Festival.Length} festivals, event wins");
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
                foreach (var (fest, oneIn, pool, only) in s_Festival)
                {
                    if (only != 0 && __instance.WeenieClassId != only) continue;
                    if (!SeasonalCalendar.IsFestivalActive(fest)) continue;
                    if (ThreadSafeRandom.Next(0.0f, 1.0f) >= Rate / oneIn) return;   // one roll per kill
                    var wcid = pool[ThreadSafeRandom.Next(0, pool.Length - 1)];
                    var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                    if (wo == null) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: weenie {wcid} missing"); return; }
                    if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
                    s_Given++;
                    Mod.Log.Info($"[RevivalGuard] JokeScrollRewards: {fest}: {wo.Name} in the corpse of {__instance.Name}, killed by {player.Name} ({s_Given} this run)");
                    return;
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeScrollRewards: drop: {e.Message}"); }
        }

        /// <summary>From EventDirector.End when the players won: one scroll each for the real players near it.
        /// The Present Raids give a Present; every other win, a firework to celebrate it.</summary>
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
