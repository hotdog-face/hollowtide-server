using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Costume casters as festival-goal prizes.</summary>
    static class FestivalCostumePrizes
    {
        internal const string P_ON = "rg_festival_costume_prizes";
        const string OWED = "FestivalCostumeOwed";
        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;

        internal static void Register()
        {
            ExpansionMotions.RegisterProperty(P_ON, true, "RevivalGuard FestivalCostumePrizes: a costume caster comes with each festival goal's trophy (SeasonalSourcesRule)");
            Mod.Log.Info($"[RevivalGuard] FestivalCostumePrizes: {(On ? "ON" : "off")} ({P_ON}); {SeasonalSourcesRule.CostumePrizes.Count} goals");
        }

        static bool Had(Player p, uint wcid) => p.QuestManager.HasQuest(SeasonalSourcesRule.PrizeFlag(wcid));

        /// <summary>The trophy flag was just stamped: pick and give this year's prize.</summary>
        internal static void TrophyStamped(Player p, string flag)
        {
            if (!On || p?.QuestManager == null) return;
            var wcid = SeasonalSourcesRule.CostumePrize(flag, w => Had(p, w), ThreadSafeRandomDouble());
            if (wcid == 0) return;
            Give(p, wcid, flag);
        }

        static double ThreadSafeRandomDouble() => ACE.Common.ThreadSafeRandom.Next(0.0f, 1.0f);

        static void Give(Player p, uint wcid, string why)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] FestivalCostumePrizes: weenie {wcid} missing (costume items datafix not applied?)"); return; }
            if (!p.CanAddToInventory(wo) || !p.TryCreateInInventoryWithNetworking(wo))
            {
                wo.Destroy();
                p.QuestManager.SetQuestCompletions(OWED + wcid, 1);
                Say(p, "There is a costume prize for you too, but your pack is full. Make room: it comes to you the next time you enter the world.");
                Mod.Log.Info($"[RevivalGuard] FestivalCostumePrizes: {p.Name}: {wcid} owed ({why}), pack full");
                return;
            }
            p.QuestManager.Stamp(SeasonalSourcesRule.PrizeFlag(wcid));
            p.QuestManager.Erase(OWED + wcid);
            Say(p, SeasonalSourcesRule.PrizeLine(wo.Name));
            Mod.Log.Info($"[RevivalGuard] FestivalCostumePrizes: {p.Name} got {wo.Name} ({wcid}) for {why}");
        }

        /// <summary>At login: prizes a full pack held back.</summary>
        internal static void PayOwed(Player p)
        {
            if (!On || p?.QuestManager == null) return;
            foreach (var w in SeasonalSourcesRule.CostumePrizes.Values.SelectMany(x => x).Distinct())
                if (p.QuestManager.HasQuest(OWED + w)) Give(p, w, "owed");
        }

        static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    /// <summary>A trophy flag's stamp that took (its solves went up) pays the prize.</summary>
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Stamp))]
    static class FestivalCostumePrizesStamp
    {
        static bool s_Done;
        static bool Prepare() { if (!s_Done) { s_Done = true; try { FestivalCostumePrizes.Register(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalCostumePrizes: {e.Message}"); } } return true; }

        static void Prefix(QuestManager __instance, string questFormat, out int __state)
        {
            __state = -1;
            try
            {
                if (questFormat == null || !SeasonalSourcesRule.CostumePrizes.ContainsKey(questFormat) || !(__instance.Creature is Player)) return;
                __state = __instance.GetCurrentSolves(questFormat);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalCostumePrizes stamp: {e.Message}"); }
        }

        static void Postfix(QuestManager __instance, string questFormat, int __state)
        {
            if (__state < 0) return;
            try
            {
                if (__instance.GetCurrentSolves(questFormat) > __state && __instance.Creature is Player p)
                    FestivalCostumePrizes.TrophyStamped(p, questFormat);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalCostumePrizes stamp after: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class FestivalCostumePrizesLogin
    {
        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance;
                if (p?.QuestManager == null || !FestivalCostumePrizes.On) return;
                var chain = new ACE.Server.Entity.Actions.ActionChain();
                chain.AddDelaySeconds(10);   // after the login burst, like OwnerDelivery
                chain.AddAction(p, () => { if (p.Session != null && !p.IsDestroyed) FestivalCostumePrizes.PayOwed(p); });
                chain.EnqueueChain();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalCostumePrizes login: {e.Message}"); }
        }
    }
}
