using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A drink, food or gem use that never finishes leaves the player "too busy" for good, the same shape as a stuck cast
    /// (CastWatchdog). Every character here is FastTick (pk_server), so with allow_fast_chug ACE runs
    /// ApplyConsumableWithAnimationCallbacks: IsBusy = true and no timed UseDone; only the server body's motion-done for
    /// the Drink/Eat motion and then for Ready (Player_Use HandleMotionDone_UseConsumable) finishes it. Lost callback:
    /// nothing times it out. Seen as auto tickets "the server never finished use" right after a run of Stamina Elixir or
    /// Tincture uses in a fight (261018-0032, 261018-0052, 261020-0069, 261022-0004, 261022-0047) and the player report
    /// "Macro keeps crashing trying to stam elixirs while fighting" (261022-0046).
    ///
    /// The rescue does what ACE's own callbacks would have done, in their order: the drink's motion-done (the boost and
    /// the item used, as the player asked), then Ready's (FinishChugging, back to the stance the drink left, UseDone,
    /// IsBusy cleared). A phase is timed from when this watchdog first saw it (ChugWatchdogRule). Runs on
    /// PlayerManager.Tick (TeleportWatchdog says why not Heartbeat); the rescue is queued on the player's own action
    /// queue. Switch: rg_chug_watchdog; chug.stuck_seconds.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class ChugWatchdog
    {
        const string P_ON = "rg_chug_watchdog";
        static double STUCK => Tunables.D("chug.stuck_seconds", 10.0);
        static double s_Next;

        sealed class Seen { public object Callback; public uint Motion; public double Since; }
        static readonly Dictionary<uint, Seen> s_Seen = new Dictionary<uint, Seen>();

        static void Postfix()
        {
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("ChugWatchdog", e); }
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 1.0;
            if (!PropertyManager.GetBool(P_ON, true).Item) { s_Seen.Clear(); return; }
            var online = new HashSet<uint>();
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                var fs = p?.FoodState;
                if (fs == null) continue;
                uint guid = p.Guid.Full;
                online.Add(guid);
                if (!s_Seen.TryGetValue(guid, out var s)) s_Seen[guid] = s = new Seen { Since = now };
                object cb = s.Callback; uint mo = s.Motion; double since = s.Since;
                double age = ChugWatchdogRule.Age(fs.IsChugging && !p.Teleporting && !p.IsDead, fs.Callback, (uint)fs.UseMotion,
                                                  ref cb, ref mo, ref since, now);
                s.Callback = cb; s.Motion = mo; s.Since = since;
                if (!ChugWatchdogRule.Stuck(age, STUCK)) continue;
                s.Since = now;   // once per phase per STUCK window, whatever the queue does
                var callback = fs.Callback; var motion = fs.UseMotion;
                p.EnqueueAction(new ActionEventDelegate(() =>
                {
                    var f = p.FoodState;
                    // the same phase, still waiting: a callback that came meanwhile moved it on
                    if (f == null || !f.IsChugging || !ReferenceEquals(f.Callback, callback) || f.UseMotion != motion) return;
                    Mod.Log.Warn($"[RevivalGuard] chug watchdog: {p.Name} (0x{p.Guid.Full:X8}) {f.UseMotion} still waiting after "
                        + $"{age:F0}s (anim {f.UseAnimTime:F1}s, back to {f.PrevStance}, busy {p.IsBusy}): finished as ACE would");
                    if (f.UseMotion != MotionCommand.Ready) p.HandleMotionDone_UseConsumable((uint)f.UseMotion, true);
                    if (p.FoodState.IsChugging) p.HandleMotionDone_UseConsumable((uint)MotionCommand.Ready, true);
                }));
            }
            if (s_Seen.Count > online.Count)
                foreach (var g in s_Seen.Keys.Where(g => !online.Contains(g)).ToList()) s_Seen.Remove(g);
        }
    }
}
