using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// BONDED QUEST TROPHIES ARE PERSONAL LOOT (owner, 2026-10-01: "could one person steal them all from the
    /// fellowship that kills her?"). Ours, the world boss's rule (docs/WORLD-BOSS-SYSTEM.md). A boss in
    /// <see cref="Specs"/> no longer drops its bonded trophy into the corpse (gen_sunken_lyceum.py); when it
    /// dies, every player in its damage history (a pet's damage counts for its owner), the killer, and every
    /// fellow of the killer's fellowship on the boss's landblock and inside fellowship XP-share range
    /// (Fellowship.GetDistanceScalar) gets ONE trophy in the pack through WorldBossSchedule.Grant (a full or
    /// overloaded pack keeps it owed for the next login or @worldboss claim). Each grant stamps the trophy's
    /// own pickup timer, the retail item Quest a stray trophy on a corpse or generator is also gated by.
    /// </summary>
    static class BossTrophies
    {
        internal sealed class Spec
        {
            public uint Boss, Trophy;
            public string BossName, TrophyName, Taken, Slain, Window, Line, Why;
        }

        internal static readonly Dictionary<uint, Spec> Specs = new Dictionary<uint, Spec>
        {
            [900803] = new Spec
            {
                Boss = 900803, Trophy = 900813, BossName = "the Unbound Queen", TrophyName = "a crown of the Unbound Queen",
                Taken = "UnboundQueenCrownTaken", Slain = "UnboundQueenSlain", Window = "20 hours",
                Line = "The Unbound Queen is dead, and one of her crowns is yours. Take it to Aldra Venn in the Scholars' Breach.",
                Why = "for your part in the Unbound Queen's fall",
            },
            [900802] = new Spec
            {
                Boss = 900802, Trophy = 900812, BossName = "the First Branded", TrophyName = "a First Brand",
                Taken = "FirstBrandTaken", Slain = null, Window = "30 minutes",
                Line = "The First Branded is dead, and a First Brand cut from its carapace is yours. Take it to Aldra Venn in the Scholars' Breach.",
                Why = "for your part in the First Branded's fall",
            },
        };

        static readonly HashSet<uint> s_Done = new HashSet<uint>();

        internal static void OnDeath(Creature boss, DamageHistoryInfo lastDamager)
        {
            if (boss == null || !Specs.TryGetValue(boss.WeenieClassId, out var s)) return;
            lock (s_Done) { if (!s_Done.Add(boss.Guid.Full)) return; }   // ACE may enter OnDeath twice
            var why = new Dictionary<uint, string>();
            if (boss.DamageHistory != null)
                foreach (var info in boss.DamageHistory.TotalDamage.Values.ToList())
                {
                    if (info == null || info.TotalDamage <= 0) continue;
                    uint g = info.IsPlayer ? info.Guid.Full : (info.PetOwner != null && info.TryGetPetOwner() is Player o ? o.Guid.Full : 0);
                    if (g != 0) why.TryAdd(g, "damage");
                }
            var killer = (lastDamager ?? boss.DamageHistory?.LastDamager)?.TryGetPetOwnerOrAttacker() as Player;
            // The killer always: ACE scales every damager's total down as the boss heals (DamageHistory.OnHeal),
            // so a tiny early share can read 0 by the end (seen live: an @smite after 1-damage hits).
            if (killer != null) why.TryAdd(killer.Guid.Full, "killer");
            var lb = boss.Location?.Landblock;
            if (killer?.Fellowship != null && lb != null)
                foreach (var f in killer.Fellowship.GetFellowshipMembers().Values)
                {
                    if (f?.Location == null || f.Location.Landblock != lb) continue;                  // in the dungeon
                    if (f != killer && killer.Fellowship.GetDistanceScalar(killer, f, XpType.Kill) <= 0) continue;
                    why.TryAdd(f.Guid.Full, "fellow");
                }

            int given = 0, had = 0;
            foreach (var g in why.Keys)
            {
                var p = PlayerManager.GetOnlinePlayer(g);
                if (p != null)
                {
                    if (!p.QuestManager.CanSolve(s.Taken))
                    {
                        had++;
                        p.SendMessage($"You have already taken {s.TrophyName} in the last {s.Window}, so none is yours from this one.", ChatMessageType.Broadcast);
                        continue;
                    }
                    p.QuestManager.Stamp(s.Taken);
                    if (s.Slain != null && why[g] != "killer") p.QuestManager.Stamp(s.Slain);   // the death emote stamps the killer only
                    p.SendMessage(s.Line, ChatMessageType.Broadcast);
                }
                WorldBossSchedule.Grant(g, s.Trophy, s.Why);
                given++;
            }
            Mod.Log.Info($"[RevivalGuard] BossTrophies: {s.BossName} 0x{boss.Guid.Full:X8} died; {given} trophy(ies) {s.Trophy} granted "
                + $"({why.Values.Count(v => v == "damage")} damager(s), killer {killer?.Name ?? "none"}, {why.Values.Count(v => v == "fellow")} more fellow(s)), {had} on the {s.Window} timer");
        }
    }

    [HarmonyPatch]
    static class BossTrophiesOnDeath
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] BossTrophies: Creature.OnDeath(DamageHistoryInfo, DamageType, bool) not found; no personal trophies");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        // A PREFIX, as ForgeWarden's: the damage history is whole before ACE's death sequence runs.
        static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
        {
            if (__instance == null || !BossTrophies.Specs.ContainsKey(__instance.WeenieClassId)) return;
            try { BossTrophies.OnDeath(__instance, lastDamager); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BossTrophies: OnDeath: {e.Message}"); }
        }
    }
}
