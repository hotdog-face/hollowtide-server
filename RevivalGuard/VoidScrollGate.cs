using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Corrosion VII and corruption VII from riftwatcher ysanne.</summary>
    static class VoidScrollGate
    {
        internal static bool VoidTrained(Player p)
        {
            var s = p?.GetCreatureSkill(Skill.VoidMagic);
            return s != null && s.AdvancementClass >= SkillAdvancementClass.Trained;
        }
    }

    [HarmonyPatch(typeof(Vendor), nameof(Vendor.BuyItems_ValidateTransaction))]
    static class VoidScrollGateBuy
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Vendor __instance, List<ItemProfile> itemProfiles, Player player, ref bool __result)
        {
            try
            {
                if (__instance == null || __instance.WeenieClassId != SeasonalSourcesRule.YSANNE || player == null || itemProfiles == null) return true;
                var wcids = new List<uint>();
                foreach (var ip in itemProfiles)
                {
                    var g = new ObjectGuid(ip.ObjectGuid);
                    if (__instance.DefaultItemsForSale.TryGetValue(g, out var d)) wcids.Add(d.WeenieClassId);
                    else if (__instance.UniqueItemsForSale.TryGetValue(g, out var u)) wcids.Add(u.WeenieClassId);
                }
                if (!SeasonalSourcesRule.VoidSaleRefused(__instance.WeenieClassId, wcids, VoidScrollGate.VoidTrained(player))) return true;
                player.Session?.Network.EnqueueSend(new GameEventTell(__instance, SeasonalSourcesRule.VoidRefusal, player, ChatMessageType.Tell));
                player.SendTransientError("You must have Void Magic trained to buy that.");
                Mod.Log.Info($"[RevivalGuard] VoidScrollGate: refused {player.Name} (Void Magic not trained) at {__instance.Name}");
                __result = false;
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] VoidScrollGate: {e.Message}"); return true; }
        }
    }

    /// <summary>Ysanne as a vendor: her Nether Rift lesson runs as before, beside the shop.</summary>
    [HarmonyPatch(typeof(Vendor), nameof(Vendor.ActOnUse), new Type[] { typeof(WorldObject) })]
    static class VoidScrollGateTrainer
    {
        static void Postfix(Vendor __instance, WorldObject wo)
        {
            try
            {
                if (__instance?.WeenieClassId != NetherRift.TRAINER || !(wo is Player p) || p.IsBusy) return;
                NetherRift.TrainerUse(__instance, wo);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] VoidScrollGate trainer: {e.Message}"); }
        }
    }
}
