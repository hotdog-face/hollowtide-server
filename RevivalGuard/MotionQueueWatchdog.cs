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
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class MotionQueueWatchdog
    {
        const string P_ON = "rg_motion_queue_watchdog";
        internal const string P_BACKLOG = "rg_motion_backlog";
        static double STALE => Tunables.D("motion.stale_seconds", 8.0);
        static double FALLING_S => Tunables.D("motion.backlog_falling_seconds", 1.0);
        static int DEEP => (int)Tunables.D("motion.backlog_swings", 3);
        static double DEEP_S => Tunables.D("motion.backlog_seconds", 2.0);
        static double s_Next;

        sealed class Seen { public object Head; public uint Counter; public double Since; public double FallingSince = -1, DeepSince = -1; }
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
            bool backlogOn = PropertyManager.GetBool(P_BACKLOG, true).Item;
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
                if (!s_Seen.TryGetValue(guid, out var s))
                    s_Seen[guid] = s = new Seen { Head = head, Counter = mtm.AnimationCounter, Since = now };
                if (backlogOn && Backlog(p, mtm, q.Count(a => a.NumAnims > 0), q.Count, s, now)) continue;
                if (!ReferenceEquals(s.Head, head) || s.Counter != mtm.AnimationCounter)
                {
                    s.Head = head; s.Counter = mtm.AnimationCounter; s.Since = now;
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

        /// <summary>The backlog arm: track how long the body has stood with its table at Falling, or held a deep queue</summary>
        static bool Backlog(Player p, ACE.Server.Physics.Animation.MotionTableManager mtm, int linked, int count, Seen s, double now)
        {
            var po = p.PhysicsObj;
            bool contact = (po.TransientState & TransientStateFlags.Contact) != 0;
            bool falling = mtm.State?.Substate == (uint)MotionCommand.Falling;
            s.FallingSince = contact && falling ? (s.FallingSince < 0 ? now : s.FallingSince) : -1;
            s.DeepSince = contact && linked >= DEEP ? (s.DeepSince < 0 ? now : s.DeepSince) : -1;
            double fallingFor = s.FallingSince < 0 ? 0 : now - s.FallingSince, deepFor = s.DeepSince < 0 ? 0 : now - s.DeepSince;
            if (!MotionQueueRule.Backlog(linked, contact, falling, fallingFor, deepFor, FALLING_S, DEEP, DEEP_S)) return false;
            string why = falling && fallingFor >= FALLING_S ? $"table at Falling on the ground for {fallingFor:F1}s" : $"{linked} swings waiting ({count} entries) for {deepFor:F1}s";
            s.FallingSince = s.DeepSince = -1; s.Since = now;
            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                var q = p.PhysicsObj?.PartArray?.MotionTableManager?.PendingAnimations;
                if (q == null || q.Count == 0) return;
                string what = Finish(p);
                Mod.Log.Warn($"[RevivalGuard] motion queue backlog: {p.Name} (0x{p.Guid.Full:X8}) {what}; {why} ({ServerFooting.Describe(p)})");
                BusyLedger.Note(p, $"motion queue backlog: drained ({why})");
            }));
            return true;
        }

        static void Drain(Player p, object head, double age)
        {
            var q = p.PhysicsObj?.PartArray?.MotionTableManager?.PendingAnimations;
            if (q == null || q.Count == 0 || !ReferenceEquals(q.First, head)) return;   // it moved meanwhile
            string what = Finish(p);
            Mod.Log.Warn($"[RevivalGuard] motion queue watchdog: {p.Name} (0x{p.Guid.Full:X8}) {what}; it had not moved for {age:F1}s "
                + $"({ServerFooting.Describe(p)})");
            BusyLedger.Note(p, $"motion queue watchdog: drained a stuck queue after {age:F0}s");
        }

        /// <summary>The head of the body's motion queue, its counter of finished links and its length, to tell later whether it moved.</summary>
        internal static (object head, uint counter, int count) Snapshot(Player p)
        {
            var mtm = p?.PhysicsObj?.PartArray?.MotionTableManager;
            var q = mtm?.PendingAnimations;
            return q == null ? (null, 0u, 0) : (q.First, mtm.AnimationCounter, q.Count);
        }

        /// <summary>Finish every entry of the body's motion queue link by link</summary>
        internal static string Finish(Player p)
        {
            var po = p.PhysicsObj;
            var mtm = po?.PartArray?.MotionTableManager;
            var q = mtm?.PendingAnimations;
            if (q == null || q.Count == 0) return "queue 0";
            string what = $"queue {q.Count} ({string.Join(",", q.Select(a => $"{(MotionCommand)a.Motion}/{a.NumAnims}").Take(4))}), counter {mtm.AnimationCounter}: finished";
            int guard = 0;
            while (q.Count > 0 && guard++ < 2000) mtm.AnimationDone(true);
            var mi = po.get_minterp();
            bool contact = (po.TransientState & TransientStateFlags.Contact) != 0;
            bool fwdAction = (mi.InterpretedState.ForwardCommand & (uint)CommandMask.Action) != 0;
            bool falling = mtm.State?.Substate == (uint)MotionCommand.Falling;
            if (MotionQueueRule.BackToReady(fwdAction, falling, contact))
            {
                what += $"; was {(MotionCommand)mi.InterpretedState.ForwardCommand}, table {(MotionCommand)(mtm.State?.Substate ?? 0)}: back to Ready";
                var raw = mi.RawState;
                var allowJump = mi.motion_allows_jump(mi.InterpretedState.ForwardCommand) == WeenieError.None;
                raw.ForwardCommand = (uint)MotionCommand.Ready; raw.ForwardSpeed = 1f;
                if (!po.IsMovingOrAnimating) po.UpdateTime = ACE.Server.Physics.Common.PhysicsTimer.CurrentTime;
                mi.apply_raw_movement(true, allowJump);
            }
            return what;
        }
    }
}
