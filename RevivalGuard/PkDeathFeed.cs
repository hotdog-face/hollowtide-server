using System;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// #pk-deaths: EVERY PK KILL, ONE LINE IN DISCORD (player idea, Rekah in #ideas: "so we know who killed us if we are
    /// not at keys"; owner 2026-11-26 "i like this idea if its possible. a new channel that shows this info."). Ours.
    ///
    /// A prefix on Player.OnDeath(lastDamager, damageType, criticalHit), declared on Player: the killing blow's damager
    /// and damage type are both in hand there. A Darktide PK death only (ACE IsPKDeath: the victim is PK and the last
    /// damager another player). Staff on either side and a Borg/merc killing another are left out; who, where (the
    /// /deaths place words), the time and the damage type go to DiscordBridge.PkDeath, which batches 10 s windows
    /// (PkDeathFeedRule). Never the dropped items. Kills on PK Night's hill are tagged "[PK Night]".
    ///
    /// CONFIG: "pkDeathsChannelId" in discord-bridge.json (the bridge's bot posts there). No field, no feed.
    /// </summary>
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
