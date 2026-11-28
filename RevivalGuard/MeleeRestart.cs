using System;
using System.Collections.Concurrent;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionTargetedMeleeAttack))]
    static class MeleeRestart
    {
        internal const string P_ON = "rg_melee_restart";
        const double REISSUE_AFTER = 0.3;      // the old chain polls every 0.1 s: its false answer has run by then
        sealed class Accepted { public int Seq; public double At; }
        static readonly ConcurrentDictionary<uint, Accepted> s_Accepted = new();
        static readonly ConcurrentDictionary<uint, double> s_LastLog = new();
        [ThreadStatic] static bool t_Reissuing;
        internal static int Restarts;

        static bool Prefix(Player __instance, uint targetGuid, uint attackHeight, float powerLevel, out int __state)
        {
            __state = __instance?.AttackSequence ?? 0;
            try
            {
                var p = __instance;
                if (p == null || t_Reissuing || !PropertyManager.GetBool(P_ON, true).Item) return true;
                var t = p.MeleeTarget;
                if (t == null || p.Attacking) return true;
                bool pending = Pending(p);
                double now = ACE.Common.Time.GetUnixTime();
                double age = s_Accepted.TryGetValue(p.Guid.Full, out var a) && a.Seq == p.AttackSequence ? now - a.At : double.MaxValue;
                bool same = t.Guid.Full == targetGuid;
                if (!MeleeLatchRule.Restart(t.IsAlive, p.Attacking, pending, same, age)) return true;

                int seq = p.AttackSequence;
                string old = $"{t.Name} 0x{t.Guid.Full:X8}";
                // what a jump does: the chain answers false on its next tick, i.e. OnAttackDone a charge ends through the physics move-to
                if (p.FastTick) p.StopExistingMoveToChains2();
                else { if (p.IsPlayerMovingTo) p.StopExistingMoveToChains(); if (p.IsMoving) p.PhysicsObj?.cancel_moveto(); }
                var chain = new ActionChain();
                chain.AddDelaySeconds(REISSUE_AFTER);
                chain.AddAction(p, () =>
                {
                    try
                    {
                        // a chain that did not answer (a turn in progress, a PK move-to): finish it as MeleeLatchGuard does
                        if (p.AttackSequence == seq && !p.Attacking && p.MeleeTarget != null)
                        {
                            if (p.FastTick)
                            {
                                p.PhysicsObj?.cancel_moveto();
                                p.IsPlayerMovingTo2 = false;
                                if (p.MoveToParams != null) p.CheckMoveToParams();
                            }
                            p.IsMoving = false;
                            if (p.MeleeTarget != null) p.OnAttackDone();
                        }
                        t_Reissuing = true;
                        try { p.HandleActionTargetedMeleeAttack(targetGuid, attackHeight, powerLevel); }
                        finally { t_Reissuing = false; }
                    }
                    catch (Exception e) { TickGuard.Report("MeleeRestart", e); }
                });
                chain.EnqueueChain();
                Restarts++;
                BusyLedger.Note(p, $"melee restart: a new attack replaced one waiting {(age > 1e6 ? "?" : age.ToString("0.0"))}s on its walk to {t.Name}");
                if (!s_LastLog.TryGetValue(p.Guid.Full, out var last) || now - last >= 10)
                {
                    s_LastLog[p.Guid.Full] = now;
                    Mod.Log.Info($"[RevivalGuard] melee restart: {p.Name} (0x{p.Guid.Full:X8}) attacked 0x{targetGuid:X8} while the attack on {old} "
                        + $"(seq {seq}) waited {(age > 1e6 ? "?" : age.ToString("0.0"))}s on its {(p.FastTick ? "PK " : "")}move-to with no swing; "
                        + $"that walk stopped (as a jump does), this attack sent again in {REISSUE_AFTER}s ({ServerFooting.Describe(p)})");
                }
                return false;
            }
            catch (Exception e) { TickGuard.Report("MeleeRestart", e); return true; }
        }

        /// <summary>The attack waits on a walk or turn: a PK move-to, a non-PK move-to chain or a charge.</summary>
        internal static bool Pending(Player p) =>
            p.FastTick ? p.IsPlayerMovingTo2 || p.MoveToParams?.Callback != null : p.IsPlayerMovingTo || p.IsMoving;

        /// <summary>When the attack now in hand was accepted: the restart's clock.</summary>
        static void Postfix(Player __instance, int __state)
        {
            try
            {
                var p = __instance;
                if (p != null && p.AttackSequence != __state)
                    s_Accepted[p.Guid.Full] = new Accepted { Seq = p.AttackSequence, At = ACE.Common.Time.GetUnixTime() };
            }
            catch { }
        }
    }
}
