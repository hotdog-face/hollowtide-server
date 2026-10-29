using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A SERVER BODY WHOSE MOTION QUEUE NEVER DRAINS (261025-0012, Lillypad: "assistant isnt looting"; earlier Mark the
    /// Noble 261024-0003). For a PK (FastTick) every use waits on the server body: MoveToManager's first node
    /// (BeginTurnToHeading) does nothing while PhysicsObj.IsAnimating, and IsAnimating stays true while the
    /// MotionTableManager holds a pending motion. Live: corpse uses waited 15 s with "queue 9 (SlashMed,...)", "queue 108
    /// (ThrustMed,...)", "first cyclic True", update lag 0: the body is ticking, the head entry waits for links that will
    /// never finish. Offline (tools/ace-physprobe `jumpfight`, ACE's own physics): a jump in a sword and shield fight
    /// leaves the table at Falling on the floor and the swing (ACE plays it as the forward command) replayed by HitGround as
    /// SlashMed/5 with 4 links to come; every later swing queues behind it, 7 entries after 10 swings, for good.
    /// MotionQueueRule decides: a queue whose head and finished-link counter have not moved for motion.stale_seconds is
    /// finished link by link (MotionTableManager.AnimationDone, as each link ending would), and a body left in a state a
    /// standing body cannot be in (an action as the forward command, or Falling on the ground) is put back to Ready in its
    /// stance, as ApplyPhysicsMotion does. Offline, after that every later swing finishes. Switch: rg_motion_queue_watchdog.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class MotionQueueWatchdog
    {
        const string P_ON = "rg_motion_queue_watchdog";
        static double STALE => Tunables.D("motion.stale_seconds", 8.0);
        static double s_Next;

        sealed class Seen { public object Head; public uint Counter; public double Since; }
        static readonly Dictionary<uint, Seen> s_Seen = new Dictionary<uint, Seen>();

        static void Postfix()
        {
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("MotionQueueWatchdog", e); }
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 0.5;
            if (!PropertyManager.GetBool(P_ON, true).Item) { s_Seen.Clear(); return; }
            var live = new HashSet<uint>();
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                if (p == null || !p.FastTick || p.PhysicsObj == null || p.Teleporting || p.IsDead) continue;
                var mtm = p.PhysicsObj.PartArray?.MotionTableManager;
                var q = mtm?.PendingAnimations;
                if (q == null || q.Count == 0) continue;
                uint guid = p.Guid.Full;
                live.Add(guid);
                var head = (object)q.First;
                if (!s_Seen.TryGetValue(guid, out var s) || !ReferenceEquals(s.Head, head) || s.Counter != mtm.AnimationCounter)
                {
                    s_Seen[guid] = new Seen { Head = head, Counter = mtm.AnimationCounter, Since = now };
                    continue;
                }
                double age = now - s.Since;
                if (!MotionQueueRule.Stale(q.Count, true, true, age, STALE)) continue;
                s.Since = now;   // once per window
                p.EnqueueAction(new ActionEventDelegate(() => Drain(p, head, age)));
            }
            if (s_Seen.Count > live.Count)
                foreach (var g in s_Seen.Keys.Where(g => !live.Contains(g)).ToList()) s_Seen.Remove(g);
        }

        static void Drain(Player p, object head, double age)
        {
            var po = p.PhysicsObj;
            var mtm = po?.PartArray?.MotionTableManager;
            var q = mtm?.PendingAnimations;
            if (q == null || q.Count == 0 || !ReferenceEquals(q.First, head)) return;   // it moved meanwhile
            string before = $"queue {q.Count} ({string.Join(",", q.Select(a => $"{(MotionCommand)a.Motion}/{a.NumAnims}").Take(4))}), counter {mtm.AnimationCounter}";
            int guard = 0;
            while (q.Count > 0 && guard++ < 2000) mtm.AnimationDone(true);
            var mi = po.get_minterp();
            bool contact = (po.TransientState & TransientStateFlags.Contact) != 0;
            bool fwdAction = (mi.InterpretedState.ForwardCommand & (uint)CommandMask.Action) != 0;
            bool falling = mtm.State?.Substate == (uint)MotionCommand.Falling;
            string fixedState = "";
            if (MotionQueueRule.BackToReady(fwdAction, falling, contact))
            {
                fixedState = $"; was {(MotionCommand)mi.InterpretedState.ForwardCommand}, table {(MotionCommand)(mtm.State?.Substate ?? 0)}: back to Ready";
                var raw = mi.RawState;
                var allowJump = mi.motion_allows_jump(mi.InterpretedState.ForwardCommand) == WeenieError.None;
                raw.ForwardCommand = (uint)MotionCommand.Ready; raw.ForwardSpeed = 1f;
                if (!po.IsMovingOrAnimating) po.UpdateTime = ACE.Server.Physics.Common.PhysicsTimer.CurrentTime;
                mi.apply_raw_movement(true, allowJump);
            }
            Mod.Log.Warn($"[RevivalGuard] motion queue watchdog: {p.Name} (0x{p.Guid.Full:X8}) {before} had not moved for {age:F1}s; "
                + $"finished it{fixedState} ({ServerFooting.Describe(p)})");
            BusyLedger.Note(p, $"motion queue watchdog: drained a stuck queue after {age:F0}s");
        }
    }
}
