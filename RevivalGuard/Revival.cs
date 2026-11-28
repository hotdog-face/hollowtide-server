using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The revived systems' rules.</summary>
    static class Revival
    {
        internal const int TRIAL_KILLS = 150;
        internal const int TRIAL_MIN_LEVEL = 100;
        internal const string SHADOW_KILL = "LightbringerShadowKill";

        // WeaponType -> the trial's name (quest TattooTrial<name>, TattooGot<name>); TwoHanded and Magic have no tattoo
        internal static readonly Dictionary<WeaponType, string> Trials = new Dictionary<WeaponType, string>
        {
            [WeaponType.Thrown] = "Atlatl", [WeaponType.Axe] = "Axe", [WeaponType.Bow] = "Bow", [WeaponType.Crossbow] = "Crossbow",
            [WeaponType.Dagger] = "Dagger", [WeaponType.Mace] = "Mace", [WeaponType.Spear] = "Spear", [WeaponType.Staff] = "Staff",
            [WeaponType.Sword] = "Sword", [WeaponType.Unarmed] = "Unarmed",
        };

        static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static IEnumerable<Player> Damagers(Creature c)
        {
            var seen = new HashSet<uint>();
            if (c?.DamageHistory == null) yield break;
            foreach (var info in c.DamageHistory.TotalDamage.Values.ToList())
            {
                if (info == null || info.TotalDamage <= 0) continue;
                var p = info.TryGetAttacker() as Player ?? (info.PetOwner != null ? info.TryGetPetOwner() : null);
                if (p != null && seen.Add(p.Guid.Full)) yield return p;
            }
        }

        internal static void CountTrialKill(Creature dead)
        {
            if (dead == null || dead is Player || (dead.Level ?? 0) < TRIAL_MIN_LEVEL) return;
            foreach (var p in Damagers(dead))
            {
                var qm = p.QuestManager;
                if (qm == null) continue;
                var weapon = p.GetEquippedWeapon();
                var type = weapon == null ? WeaponType.Unarmed : weapon.W_WeaponType;
                if (!Trials.TryGetValue(type, out var w)) continue;
                string trial = "TattooTrial" + w;
                if (!qm.HasQuest(trial) || qm.HasQuest("TattooGot" + w)) continue;
                int had = qm.GetCurrentSolves(trial);
                if (had > TRIAL_KILLS) continue;
                qm.Stamp(trial);
                int kills = qm.GetCurrentSolves(trial) - 1;
                if (kills >= TRIAL_KILLS)
                    Say(p, $"Your {w} trial is complete: {TRIAL_KILLS} of {TRIAL_KILLS}. Return to Isembard the Inkwright in Cragstone.");
                else if (kills > 0 && kills % 25 == 0)
                    Say(p, $"Your {w} trial: {kills} of {TRIAL_KILLS}.");
            }
        }

        internal static void StartShadowCredit(Creature dead)
        {
            if (!SeasonalCalendar.IsFestivalActive("lightbringer")) return;
            var who = new HashSet<Player>();
            foreach (var p in Damagers(dead))
            {
                who.Add(p);
                if (p.Fellowship != null) foreach (var f in p.Fellowship.WithinRange(p)) who.Add(f);
            }
            foreach (var p in who)
                // at 0, not stamped: the kill makes it 1 of 1 and ACE says "You have killed 1 ... Your task is complete!"
                if (p.QuestManager != null && !p.QuestManager.HasQuest(SHADOW_KILL)) p.QuestManager.SetQuestCompletions(SHADOW_KILL, 0);
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class RevivalTrialKill
    {
        // A prefix: OnDeath resets the damage history on its way out (ForgeWardenDeath's note).
        static void Prefix(Creature __instance)
        {
            try { Revival.CountTrialKill(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Revival trial kill: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath_HandleKillTask))]
    static class RevivalShadowCredit
    {
        static void Prefix(Creature __instance, string killQuest)
        {
            if (killQuest != Revival.SHADOW_KILL) return;
            try { Revival.StartShadowCredit(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Revival shadow credit: {e.Message}"); }
        }
    }
}
