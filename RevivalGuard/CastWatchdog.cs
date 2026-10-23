using System.Linq;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A cast that never finishes leaves the caster "too busy" for good (player report, Owls 2026-10-22: "Sometimes
    /// casting will glitch out and you'll become too busy to do anything instead of fizzle").
    ///
    /// Every character here is a PK type (pk_server), so ACE runs the FastTick cast: event driven, finished only by
    /// the server body's motion-done callback for the cast gesture (Player_Magic HandleMotionDone_Magic) or by a turn
    /// completing (OnMoveComplete_Magic). MagicState.OnCastStart sets IsBusy and IsCasting and nothing times them
    /// out (Entity/MagicState.cs has no timeout), so if that callback is lost (the motion queue cleared under the
    /// cast, an interrupted turn), every later cast, use and attack answers "You're too busy!" until relog. The fast
    /// chug has the same shape (FINDINGS-overnight-reports.md 0043).
    ///
    /// The rescue: a cast still running CAST_STUCK seconds after it started is failed the way ACE fails one
    /// (FailCast: the fizzle, UseDone YourSpellFizzled, OnCastDone clears IsBusy). Not while ACE is waiting on
    /// purpose for the player to let go of a held turn key (PendingTurnRelease). A legit cast, turn included, takes
    /// 1-4 s. Runs on PlayerManager.Tick (TeleportWatchdog explains why not Heartbeat); the fail itself is queued
    /// on the player's own action queue so it runs on the thread that owns the cast. Switch: rg_cast_watchdog.
    /// With the server body off walkable ground (ServerFooting) neither the turn nor the gesture can play, so a cast is
    /// failed once the body has been off it cast.unfooted_seconds (2 s), not after the full 10 s (report 261023-0014).
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class CastWatchdog
    {
        const string P_ON = "rg_cast_watchdog";
        static double CAST_STUCK => Tunables.D("cast.stuck_seconds", 10.0);
        static double UNFOOTED => Tunables.D("cast.unfooted_seconds", 2.0);
        static double s_Next;
        static readonly System.Collections.Generic.Dictionary<uint, double> s_Unfooted = new System.Collections.Generic.Dictionary<uint, double>();

        static void Postfix()
        {
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("CastWatchdog", e); }
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 1.0;
            if (!PropertyManager.GetBool(P_ON, true).Item) return;
            var utc = System.DateTime.UtcNow;
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                var ms = p?.MagicState;
                if (ms == null || !ms.IsCasting || p.Teleporting || p.IsDead) { if (p != null) s_Unfooted.Remove(p.Guid.Full); continue; }
                double age = (utc - ms.StartTime).TotalSeconds;
                if (!s_Unfooted.TryGetValue(p.Guid.Full, out var since)) since = double.NaN;
                double unfooted = CastWatchdogRule.Unfooted(ServerFooting.Walkable(p), ref since, now);
                s_Unfooted[p.Guid.Full] = since;
                if (!CastWatchdogRule.Stuck(age, ms.PendingTurnRelease, (uint)(p.LastMoveToState?.RawMotionState.TurnCommand ?? 0), CAST_STUCK,
                                            unfooted, UNFOOTED)) continue;
                s_Unfooted.Remove(p.Guid.Full);
                int castNum = ms.CastNum;
                p.EnqueueAction(new ActionEventDelegate(() =>
                {
                    // the same cast, still running: anything else finished or replaced it meanwhile
                    if (!p.MagicState.IsCasting || p.MagicState.CastNum != castNum) return;
                    Mod.Log.Warn($"[RevivalGuard] cast watchdog: {p.Name} (0x{p.Guid.Full:X8}) cast #{castNum} still running "
                        + $"after {age:F0}s (gesture {p.MagicState.CastGesture}, motion done {p.MagicState.CastMotionDone}, "
                        + $"turning {p.MagicState.IsTurning}, turn release {p.MagicState.PendingTurnRelease}; off walkable {unfooted:F0}s, "
                        + $"{ServerFooting.Describe(p)}): failed as a fizzle");
                    BusyLedger.Note(p, $"cast watchdog: cast #{castNum} failed after {age:F0}s (off walkable {unfooted:F0}s)");
                    p.FailCast();
                }));
            }
            if (s_Unfooted.Count > 200) s_Unfooted.Clear();
        }
    }
}
