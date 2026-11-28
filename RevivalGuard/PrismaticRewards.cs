using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Prismatic lightbringer rewards.</summary>
    static class PrismaticRewards
    {
        internal static bool On => PkCommon.On(PrismaticRewardsRule.P_ON);
        static int s_Registered;
        static readonly HashSet<uint> s_Dead = new HashSet<uint>();

        internal static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            PkCommon.RegisterBool(PrismaticRewardsRule.P_ON, false, "RevivalGuard PrismaticRewards: the hardest quests give the Prismatic Lightbringer pieces, once per character (off)");
            Mod.Log.Info($"[RevivalGuard] PrismaticRewards: {(On ? "ON" : "off")} ({PrismaticRewardsRule.P_ON}); {PrismaticRewardsRule.Deeds.Length} deeds");
        }

        /// <summary>Before a watched flag changes: its solves now (-1 = not watched, off, or not a player).</summary>
        internal static int Before(QuestManager qm, string quest)
        {
            if (!On || !(qm?.Creature is Player) || !PrismaticRewardsRule.WatchesQuest(quest)) return -1;
            return qm.GetCurrentSolves(quest);
        }

        internal static void QuestChanged(Player p, string quest, int before)
        {
            if (before < 0 || !On || p?.QuestManager == null) return;
            int after = p.QuestManager.GetCurrentSolves(quest);
            foreach (var d in PrismaticRewardsRule.ForChange(quest, before, after)) Pay(p, d);
        }

        internal static void BossDied(Creature boss, DamageHistoryInfo lastDamager)
        {
            if (!On || boss == null) return;
            var d = PrismaticRewardsRule.ForBoss(boss.WeenieClassId);
            if (d == null) return;
            lock (s_Dead) { if (!s_Dead.Add(boss.Guid.Full)) return; }   // ACE may enter OnDeath twice
            var who = new HashSet<Player>();
            if (boss.DamageHistory != null)
                foreach (var info in boss.DamageHistory.TotalDamage.Values.ToList())
                {
                    if (info == null || info.TotalDamage <= 0) continue;
                    var pl = info.IsPlayer ? PlayerManager.GetOnlinePlayer(info.Guid.Full) : info.TryGetPetOwner() as Player;
                    if (pl != null) who.Add(pl);
                }
            var killer = (lastDamager ?? boss.DamageHistory?.LastDamager)?.TryGetPetOwnerOrAttacker() as Player;
            if (killer != null) who.Add(killer);   // healing scales early damage down to 0 (BossTrophies' note)
            var lb = boss.Location?.Landblock;
            if (killer?.Fellowship != null && lb != null)
                foreach (var f in killer.Fellowship.GetFellowshipMembers().Values)
                    if (f?.Location != null && f.Location.Landblock == lb) who.Add(f);
            foreach (var p in who) Pay(p, d);
            Mod.Log.Info($"[RevivalGuard] PrismaticRewards: {boss.Name} ({boss.WeenieClassId}) died, deed {d.Key}, {who.Count} player(s)");
        }

        static void Pay(Player p, PrismaticRewardsRule.Deed d)
        {
            foreach (var w in d.Items)
                if (!p.QuestManager.HasQuest(PrismaticRewardsRule.Given(w))) Give(p, w, d);
        }

        static void Give(Player p, uint wcid, PrismaticRewardsRule.Deed d)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards: weenie {wcid} missing (prismatic-set datafix not applied?)"); return; }
            p.QuestManager.Stamp(PrismaticRewardsRule.Given(wcid));
            if (!p.CanAddToInventory(wo) || !p.TryCreateInInventoryWithNetworking(wo))
            {
                wo.Destroy();
                p.QuestManager.SetQuestCompletions(PrismaticRewardsRule.Owed(wcid), 1);
                Say(p, $"For {d.What} there is a reward waiting, but your pack is full. Make room: it comes to you the next time you enter the world.");
                Mod.Log.Info($"[RevivalGuard] PrismaticRewards: {p.Name}: {wcid} owed ({d.Key}), pack full");
                return;
            }
            p.QuestManager.Erase(PrismaticRewardsRule.Owed(wcid));
            Say(p, PrismaticRewardsRule.Line(wo.Name, d));
            Mod.Log.Info($"[RevivalGuard] PrismaticRewards: {p.Name} got {wo.Name} ({wcid}) for {d.Key}");
        }

        internal static void PayOwed(Player p)
        {
            if (!On || p?.QuestManager == null) return;
            foreach (var d in PrismaticRewardsRule.Deeds)
                foreach (var w in d.Items)
                    if (p.QuestManager.HasQuest(PrismaticRewardsRule.Owed(w)))
                    {
                        p.QuestManager.Erase(PrismaticRewardsRule.Given(w));   // Give stamps it again
                        Give(p, w, d);
                    }
        }

        static void Say(Player p, string text) => p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Update))]
    static class PrismaticRewardsUpdate
    {
        static bool Prepare() { try { PrismaticRewards.Register(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards: {e.Message}"); } return true; }
        static void Prefix(QuestManager __instance, string questFormat, out int __state)
        {
            __state = -1;
            try { __state = PrismaticRewards.Before(__instance, questFormat); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards update: {e.Message}"); }
        }
        static void Postfix(QuestManager __instance, string questFormat, int __state)
        {
            try { if (__state >= 0 && __instance?.Creature is Player p) PrismaticRewards.QuestChanged(p, questFormat, __state); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards update: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.SetQuestCompletions))]
    static class PrismaticRewardsSet
    {
        static void Prefix(QuestManager __instance, string questFormat, out int __state)
        {
            __state = -1;
            try { __state = PrismaticRewards.Before(__instance, questFormat); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards set: {e.Message}"); }
        }
        static void Postfix(QuestManager __instance, string questFormat, int __state)
        {
            try { if (__state >= 0 && __instance?.Creature is Player p) PrismaticRewards.QuestChanged(p, questFormat, __state); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards set: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class PrismaticRewardsBoss
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] PrismaticRewards: Creature.OnDeath(DamageHistoryInfo, DamageType, bool) not found; no boss rewards");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
        {
            try { PrismaticRewards.BossDied(__instance, lastDamager); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards boss: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class PrismaticRewardsLogin
    {
        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance;
                if (p?.QuestManager == null || !PrismaticRewards.On) return;
                var chain = new ACE.Server.Entity.Actions.ActionChain();
                chain.AddDelaySeconds(12);   // after the login burst (FestivalCostumePrizes waits 10)
                chain.AddAction(p, () => { if (p.Session != null && !p.IsDestroyed) PrismaticRewards.PayOwed(p); });
                chain.EnqueueChain();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PrismaticRewards login: {e.Message}"); }
        }
    }
}
