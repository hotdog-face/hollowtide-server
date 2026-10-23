using System.Collections.Generic;
using System.Linq;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A USE WHOSE SERVER WALK CAN NEVER END. For a PK (FastTick) every use of a world object, corpse or use-with target is
    /// CreateMoveToChain2: the use runs from Player.MoveToParams.Callback when the server body's MoveToManager arrives. Two
    /// ways it never does, both leaving no UseDone ("the server never finished use ... Corpse of Olthoi Gardener", John
    /// Yaya 261023-0007): the body is off walkable ground (ServerFooting: RunForward is refused, no progress, and ACE
    /// never reads FailProgressCount), or the move was cancelled and ACE waits for the body to stop, checking only on a
    /// physics tick that was moving. UseWalkRule decides; Settle calls ACE's own HandleMoveToCallback, Fail cancels the
    /// move and fails the use the way a newer use does (CheckMoveToParams: callback(false), UseDone). Runs on
    /// PlayerManager.Tick; the act is queued on the player's own action queue. Switch: rg_use_walk_watchdog;
    /// use.settle_seconds, use.unfooted_seconds, use.walk_limit_seconds.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class UseWalkWatchdog
    {
        const string P_ON = "rg_use_walk_watchdog";
        static double SETTLE => Tunables.D("use.settle_seconds", 1.0);
        static double UNFOOTED => Tunables.D("use.unfooted_seconds", 2.0);
        static double LIMIT => Tunables.D("use.walk_limit_seconds", 15.0);
        static double s_Next;

        sealed class Seen { public object Params; public double Since; }
        static readonly Dictionary<uint, Seen> s_Seen = new Dictionary<uint, Seen>();

        static void Postfix()
        {
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("UseWalkWatchdog", e); }
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 0.5;
            if (!PropertyManager.GetBool(P_ON, true).Item) { s_Seen.Clear(); return; }
            var waiting = new HashSet<uint>();
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                if (p == null || !p.FastTick || p.PhysicsObj == null || p.Teleporting || p.IsDead) continue;
                var mtp = p.MoveToParams;
                if (mtp?.Callback == null) continue;
                uint guid = p.Guid.Full;
                waiting.Add(guid);
                if (!s_Seen.TryGetValue(guid, out var s) || !ReferenceEquals(s.Params, mtp))
                {
                    s_Seen[guid] = new Seen { Params = mtp, Since = now };
                    continue;
                }
                double age = now - s.Since;
                bool movingTo = p.IsPlayerMovingTo2, bodyMoving = p.PhysicsObj.IsMovingOrAnimating, walkable = ServerFooting.Walkable(p);
                var act = UseWalkRule.Decide(age, movingTo, bodyMoving, walkable, SETTLE, UNFOOTED, LIMIT);
                if (act == UseWalkRule.Act.None) continue;
                s.Since = now;   // once per window, whatever the queue does
                string target = mtp.Target?.Name ?? "?";
                p.EnqueueAction(new ActionEventDelegate(() =>
                {
                    if (!ReferenceEquals(p.MoveToParams, mtp) || mtp.Callback == null) return;   // it ended meanwhile
                    Mod.Log.Warn($"[RevivalGuard] use walk watchdog: {p.Name} (0x{p.Guid.Full:X8}) use of {target} waited {age:F1}s "
                        + $"(moving to {p.IsPlayerMovingTo2}, body moving {p.PhysicsObj.IsMovingOrAnimating}; {ServerFooting.Describe(p)}): "
                        + (act == UseWalkRule.Act.Settle ? "answered now, as ACE does once the body stops" : "failed as a newer use would"));
                    if (act == UseWalkRule.Act.Settle) { p.HandleMoveToCallback(); return; }
                    if (p.IsPlayerMovingTo2) p.PhysicsObj.cancel_moveto();   // OnMoveComplete: IsPlayerMovingTo2 off, callback kept
                    if (ReferenceEquals(p.MoveToParams, mtp)) p.CheckMoveToParams();   // callback(false): UseDone
                }));
            }
            if (s_Seen.Count > waiting.Count)
                foreach (var g in s_Seen.Keys.Where(g => !waiting.Contains(g)).ToList()) s_Seen.Remove(g);
        }
    }
}
