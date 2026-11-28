using System;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch]
    static class PkDeathFeed
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Player), "OnDeath", new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] PkDeathFeed: Player.OnDeath(DamageHistoryInfo, DamageType, bool) not found; #pk-deaths gets nothing");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Prefix(Player __instance, DamageHistoryInfo lastDamager, DamageType damageType)
        {
            try
            {
                var victim = __instance;
                if (victim == null || lastDamager == null || !DiscordBridge.PkFeedOn) return;
                if (!lastDamager.IsPlayer || !victim.IsPKDeath(lastDamager)) return;
                var killer = PlayerManager.FindByGuid(lastDamager.Guid.Full);
                string killerName = lastDamager.Name ?? killer?.Name;
                bool killerStaff = killer?.Account != null && killer.Account.AccessLevel > (uint)AccessLevel.Player;
                bool victimStaff = victim.Account != null && victim.Account.AccessLevel > (uint)AccessLevel.Player;
                bool killerBot = killer != null && Mercenaries.IsMercAccount(killer.Account?.AccountName);
                bool victimBot = Mercenaries.IsMerc(victim);
                if (!PkDeathFeedRule.ShouldPost(killerName, victim.Name, killerStaff, victimStaff, killerBot, victimBot)) return;

                string line = PkDeathFeedRule.Line(killerName, victim.Name, DeathLog.WhereOf(victim),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), (uint)damageType, PkNight.NearHill(victim));
                DiscordBridge.PkDeath(line);
                Mod.Log.Info($"[RevivalGuard] PkDeathFeed: {line}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkDeathFeed: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
