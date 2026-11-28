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
    /// <summary>Where the expression wear (weenies 922100-922154) comes from once it is off the shops: creature drops by theme
    /// and level band, and two quest rewards. Both behind rg_expression_wear_sources (off).</summary>
    static class ExpressionWear
    {
        internal const string P_ON = "rg_expression_wear_sources", P_RATE = "rg_expression_wear_rate";
        internal static bool On => PkCommon.On(P_ON);
        static double Rate => Math.Max(0.0, PkCommon.Dbl(P_RATE, 1.0));

        internal sealed class Band
        {
            public string Theme; public CreatureType[] Types; public int MinLevel, MaxLevel, OneIn; public uint[] Items;
        }

        /// <summary>One roll per kill; a hit gives one piece of the band, each equally likely.</summary>
        internal static readonly Band[] Bands =
        {
            // the road: bandits and banderlings carry a traveller's leathers and staff
            new Band { Theme = "Wayfarer's Leathers", Types = new[] { CreatureType.Human, CreatureType.Banderling }, MinLevel = 15, MaxLevel = 90, OneIn = 120,
                       Items = new uint[] { 922120, 922121, 922123, 922152 } },
            // festival wear: drudges and mosswarts, the thieves of every fair
            new Band { Theme = "Reveler's Motley", Types = new[] { CreatureType.Drudge, CreatureType.Mosswart }, MinLevel = 10, MaxLevel = 80, OneIn = 120,
                       Items = new uint[] { 922100, 922101, 922102, 922103, 922151 } },
            // a dead court: the smallsword and the herald's pennant
            new Band { Theme = "court arms", Types = new[] { CreatureType.Undead, CreatureType.Skeleton }, MinLevel = 40, MaxLevel = 130, OneIn = 150,
                       Items = new uint[] { 922150, 922154 } },
        };

        /// <summary>Quest rewards, once per character: (quest, solves needed, item, line).</summary>
        internal static readonly (string Quest, int Solves, uint Item, string Line)[] Quests =
        {
            ("FreedJester", 1, 922102, "For freeing the Jester you are left {0}."),
            ("ExplorationMarkersFound", 10, 922152, "Ten exploration markers found: the Exploration Society sends you {0}."),
        };

        static int s_Registered, s_Dropped;
        static string Given(uint wcid) => "ExpressionWearGiven" + wcid;
        static string Owed(uint wcid) => "ExpressionWearOwed" + wcid;

        internal static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard ExpressionWear: Reveler's, Wayfarer's and court pieces drop from themed creatures and come from two quests (off)");
            PkCommon.RegisterDouble(P_RATE, 1.0, "RevivalGuard ExpressionWear: multiplier on the drop chance (tests)");
            Mod.Log.Info($"[RevivalGuard] ExpressionWear: {(On ? "ON" : "off")} ({P_ON}); {Bands.Length} drop bands, {Quests.Length} quest rewards");
        }

        internal static Band BandFor(CreatureType type, int level)
        {
            foreach (var b in Bands)
                if (level >= b.MinLevel && level <= b.MaxLevel && Array.IndexOf(b.Types, type) >= 0) return b;
            return null;
        }

        [HarmonyPatch]
        static class Drop
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
                if (m == null) Mod.Log.Error("[RevivalGuard] ExpressionWear: Creature.GenerateTreasure not found; no drops");
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
                    if (!On || corpse == null || __instance == null || __instance is Player || killer == null) return;
                    var band = BandFor(__instance.CreatureType ?? CreatureType.Invalid, __instance.Level ?? 0);
                    if (band == null) return;
                    if (!(killer.TryGetPetOwnerOrAttacker() is Player player) || Mercenaries.IsMerc(player)) return;
                    if (ThreadSafeRandom.Next(0.0f, 1.0f) >= Rate / band.OneIn) return;
                    uint wcid = band.Items[ThreadSafeRandom.Next(0, band.Items.Length - 1)];
                    var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                    if (wo == null) { Mod.Log.Warn($"[RevivalGuard] ExpressionWear: weenie {wcid} missing"); return; }
                    if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
                    s_Dropped++;
                    Mod.Log.Info($"[RevivalGuard] ExpressionWear: {wo.Name} ({wcid}) in the corpse of {__instance.Name} (level {__instance.Level}), killed by {player.Name} ({s_Dropped} this run)");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExpressionWear: drop: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Update), new[] { typeof(string) })]
        static class QuestReward
        {
            static void Postfix(QuestManager __instance, string questFormat)
            {
                try
                {
                    if (!On || questFormat == null || !(__instance.Creature is Player p)) return;
                    string name = QuestManager.GetQuestName(questFormat);
                    foreach (var q in Quests)
                        if (string.Equals(q.Quest, name, StringComparison.OrdinalIgnoreCase) && __instance.GetCurrentSolves(q.Quest) >= q.Solves)
                            Once(p, q.Item, q.Line);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExpressionWear: quest reward: {e.Message}"); }
            }
        }

        static void Once(Player p, uint wcid, string line)
        {
            if (p?.QuestManager == null || p.QuestManager.HasQuest(Given(wcid))) return;
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] ExpressionWear: weenie {wcid} missing"); return; }
            p.QuestManager.Stamp(Given(wcid));
            if (!p.CanAddToInventory(wo) || !p.TryCreateInInventoryWithNetworking(wo))
            {
                string name = wo.Name;
                wo.Destroy();
                p.QuestManager.SetQuestCompletions(Owed(wcid), 1);
                Say(p, $"Your pack is too full for {name}. Make room: it comes to you the next time you enter the world.");
                return;
            }
            p.QuestManager.Erase(Owed(wcid));
            Say(p, string.Format(line, wo.Name));
            Mod.Log.Info($"[RevivalGuard] ExpressionWear: {p.Name} got {wo.Name} ({wcid})");
        }

        /// <summary>Owed rewards at login, whatever the switch says now.</summary>
        internal static void PayOwed(Player p)
        {
            if (p?.QuestManager == null) return;
            foreach (var q in Quests)
                if (p.QuestManager.HasQuest(Owed(q.Item)))
                {
                    p.QuestManager.Erase(Given(q.Item));   // Once stamps it again
                    Once(p, q.Item, "{0}, which you were owed, is in your pack.");
                }
        }

        static void Say(Player p, string text) => p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class ExpressionWearLogin
    {
        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance;
                if (p?.QuestManager == null) return;
                var chain = new ACE.Server.Entity.Actions.ActionChain();
                chain.AddDelaySeconds(14);   // after the login burst and LookItems (13)
                chain.AddAction(p, () => { if (p.Session != null && !p.IsDestroyed) ExpressionWear.PayOwed(p); });
                chain.EnqueueChain();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExpressionWear login: {e.Message}"); }
        }
    }
}
