using System;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// its own datafix (2026-11-28-item-*-on.sql); the weenies come from 2026-11-27-item-candidates.sql.
    /// rg_item_grasp_drop a raid Raid Commander's corpse carries the Grasp of T'thuun 1 time in 10, as it carries the
    static class LookItems
    {
        internal const string P_GRASP = "rg_item_grasp_drop", P_SHROUD = "rg_item_shroud_task", P_AEGIS = "rg_item_aegis_reward";
        internal const uint GRASP = 918102, SHROUD = 918103, AEGIS = 918107;
        internal const double GRASP_CHANCE = 0.1;   // the Kareb Mask's (TthuunRaidsRule.KAREB_CHANCE)
        static readonly uint[] REWARDS = { SHROUD, AEGIS };
        static int s_Registered;

        static string Given(uint wcid) => "LookItemGiven" + wcid;
        static string Owed(uint wcid) => "LookItemOwed" + wcid;

        internal static void Register()
        {
            if (System.Threading.Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            PkCommon.RegisterBool(P_GRASP, false, "RevivalGuard LookItems: a raid Raid Commander's corpse carries the Grasp of T'thuun 1 time in 10 (off)");
            PkCommon.RegisterBool(P_SHROUD, false, "RevivalGuard LookItems: the Royal Scouts' kill task gives Marauder's Shroud, once per character (off)");
            PkCommon.RegisterBool(P_AEGIS, false, "RevivalGuard LookItems: winning the Lightbringer's fight gives the Aegis of the Last Light, once per character (off)");
            Mod.Log.Info($"[RevivalGuard] LookItems: grasp drop {(PkCommon.On(P_GRASP) ? "ON" : "off")}, shroud task {(PkCommon.On(P_SHROUD) ? "ON" : "off")}, aegis reward {(PkCommon.On(P_AEGIS) ? "ON" : "off")}");
        }

        /// <summary>TthuunRaids.OnTreasure, a raid site's Raid Commander: roll in [0,1).</summary>
        internal static void CommanderCorpse(Corpse corpse, string who, double roll)
        {
            if (!PkCommon.On(P_GRASP) || corpse == null || roll >= GRASP_CHANCE) return;
            var wo = WorldObjectFactory.CreateNewWorldObject(GRASP);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] LookItems: weenie {GRASP} missing (item-candidates datafix not applied?)"); return; }
            if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
            Mod.Log.Info($"[RevivalGuard] LookItems: {who} carried a {wo.Name}");
        }

        /// <summary>TthuunRaids.OnScout, all three kill tasks paid.</summary>
        internal static void ScoutTaskDone(Player p)
        {
            if (PkCommon.On(P_SHROUD)) Once(p, SHROUD, "The Royal Scout gives you {0}, taken from a fallen raid commander.");
        }

        /// <summary>LightbringerScene.Reward, each who fought in a won fight.</summary>
        internal static void LightbringerWon(Player p)
        {
            if (PkCommon.On(P_AEGIS)) Once(p, AEGIS, "Asheron leaves you {0}, in memory of the shield he raised here.");
        }

        static void Once(Player p, uint wcid, string line)
        {
            if (p?.QuestManager == null || p.QuestManager.HasQuest(Given(wcid))) return;
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] LookItems: weenie {wcid} missing (item-candidates datafix not applied?)"); return; }
            p.QuestManager.Stamp(Given(wcid));
            if (!p.CanAddToInventory(wo) || !p.TryCreateInInventoryWithNetworking(wo))
            {
                string name = wo.Name;
                wo.Destroy();
                p.QuestManager.SetQuestCompletions(Owed(wcid), 1);
                Say(p, $"Your pack is too full for {name}. Make room: it comes to you the next time you enter the world.");
                Mod.Log.Info($"[RevivalGuard] LookItems: {p.Name}: {wcid} owed, pack full");
                return;
            }
            p.QuestManager.Erase(Owed(wcid));
            Say(p, string.Format(line, wo.Name));
            Mod.Log.Info($"[RevivalGuard] LookItems: {p.Name} got {wo.Name} ({wcid})");
        }

        /// <summary>Owed rewards at login, whatever the switches say now: they were earned while one was on.</summary>
        internal static void PayOwed(Player p)
        {
            if (p?.QuestManager == null) return;
            foreach (var w in REWARDS)
                if (p.QuestManager.HasQuest(Owed(w)))
                {
                    p.QuestManager.Erase(Given(w));   // Once stamps it again
                    Once(p, w, "{0}, which you were owed, is in your pack.");
                }
        }

        static void Say(Player p, string text) => p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class LookItemsLogin
    {
        static bool Prepare() { try { LookItems.Register(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LookItems: {e.Message}"); } return true; }
        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance;
                if (p?.QuestManager == null) return;
                var chain = new ACE.Server.Entity.Actions.ActionChain();
                chain.AddDelaySeconds(13);   // after the login burst and PrismaticRewards (12)
                chain.AddAction(p, () => { if (p.Session != null && !p.IsDestroyed) LookItems.PayOwed(p); });
                chain.EnqueueChain();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LookItems login: {e.Message}"); }
        }
    }
}
