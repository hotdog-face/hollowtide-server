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
    /// (UseDone YourSpellFizzled, OnCastDone clears IsBusy; no mana or components spent). Not while ACE is waiting on
    /// purpose for the player to let go of a held turn key (PendingTurnRelease). A legit cast, turn included, takes
    /// 1-4 s. Runs on PlayerManager.Tick (TeleportWatchdog explains why not Heartbeat); the fail itself is queued
    /// on the player's own action queue so it runs on the thread that owns the cast. Switch: rg_cast_watchdog.
    /// With the server body off walkable ground (ServerFooting) neither the turn nor the gesture can play, so a cast is
    /// failed once the body has been off it cast.unfooted_seconds (2 s), not after the full 10 s (report 261023-0014).
    /// On walkable ground a gesture with no motion-done past its own length plus cast.footed_grace_seconds (3 s) is finished
    /// the way that callback finishes it, so the spell lands (report 261024-0003, CastWatchdogRule.FinishNow).
    /// A cast in its turn with the body standing still on walkable ground and the heading fixed for 1.5 s will never
    /// complete the turn (261029-0010, often on a slope): it ends then, with no mana or components spent
    /// (CastTurnStallRule, switch rg_cast_turn_stall). Checked every 0.25 s so the 1.5 s is not rounded up to 3.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class CastWatchdog
    {
        const string P_ON = "rg_cast_watchdog";
        const string P_STALL = "rg_cast_turn_stall";
        static double CAST_STUCK => Tunables.D("cast.stuck_seconds", 10.0);
        static double UNFOOTED => Tunables.D("cast.unfooted_seconds", 2.0);
        static double FOOTED_GRACE => Tunables.D("cast.footed_grace_seconds", 3.0);
        static double s_Next;
        static readonly System.Collections.Generic.Dictionary<uint, double> s_Unfooted = new System.Collections.Generic.Dictionary<uint, double>();

        sealed class TurnSample { public int CastNum; public double At, Since = double.NaN; public float Heading; public ACE.Server.Physics.Common.Position Pos; }
        static readonly System.Collections.Generic.Dictionary<uint, TurnSample> s_Turn = new System.Collections.Generic.Dictionary<uint, TurnSample>();

        static void Postfix()
        {
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("CastWatchdog", e); }
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 0.25;
            if (!PropertyManager.GetBool(P_ON, true).Item) return;
            var utc = System.DateTime.UtcNow;
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                var ms = p?.MagicState;
                if (ms == null || !ms.IsCasting || p.Teleporting || p.IsDead) { if (p != null) { s_Unfooted.Remove(p.Guid.Full); s_Turn.Remove(p.Guid.Full); } continue; }
                double age = (utc - ms.StartTime).TotalSeconds;
                if (!s_Unfooted.TryGetValue(p.Guid.Full, out var since)) since = double.NaN;
                double unfooted = CastWatchdogRule.Unfooted(ServerFooting.Walkable(p), ref since, now);
                s_Unfooted[p.Guid.Full] = since;
                if (ms.CastGestureStartTime != System.DateTime.MinValue && !ms.CastMotionDone)
                {
                    double sinceGesture = (utc - ms.CastGestureStartTime).TotalSeconds;
                    float gesture = ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(p.MotionTableId, ACE.Entity.Enum.MotionStance.Magic,
                                                                                             ms.CastGesture, Player.CastSpeed);
                    if (CastWatchdogRule.FinishNow(sinceGesture, gesture, FOOTED_GRACE, ms.CastMotionDone, ms.IsTurning, ServerFooting.Walkable(p)))
                    {
                        Finish(p, ms.CastNum, sinceGesture, gesture);
                        continue;
                    }
                }
                if (TurnStalled(p, ms, now, age)) continue;
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
                        + $"{ServerFooting.Describe(p)}): failed, nothing spent");
                    BusyLedger.Note(p, $"cast watchdog: cast #{castNum} failed after {age:F0}s (off walkable {unfooted:F0}s)");
                    // FailCast(false) with the fizzle answer, as the turn-stall end: the line shows, nothing is spent
                    if (CastWatchdogRule.WatchdogFailSpends) p.FailCast();
                    else { p.SendUseDoneEvent(ACE.Entity.Enum.WeenieError.YourSpellFizzled); p.MagicState.OnCastDone(); }
                }));
            }
            if (s_Unfooted.Count > 200) s_Unfooted.Clear();
            if (s_Turn.Count > 200) s_Turn.Clear();
        }

        /// <summary>The turn-stall early end (CastTurnStallRule): true once it has queued the end of this cast.</summary>
        static bool TurnStalled(Player p, ACE.Server.Entity.MagicState ms, double now, double age)
        {
            var po = p.PhysicsObj;
            if (po == null || !PropertyManager.GetBool(P_STALL, true).Item) return false;
            uint id = p.Guid.Full;
            if (!s_Turn.TryGetValue(id, out var t) || t.CastNum != ms.CastNum) s_Turn[id] = t = new TurnSample { CastNum = ms.CastNum };
            float heading = po.get_heading();
            double dt = t.Pos == null ? 0 : now - t.At;
            double moved = t.Pos == null ? 0 : t.Pos.Distance(po.Position);
            double turned = t.Pos == null ? 0 : CastTurnStallRule.HeadingDelta(t.Heading, heading);
            double stalled = CastTurnStallRule.Stalled(ms.IsTurning, ms.PendingTurnRelease, ServerFooting.Walkable(p), moved, turned, dt, ref t.Since, now);
            t.At = now; t.Heading = heading; t.Pos = new ACE.Server.Physics.Common.Position(po.Position);
            if (!CastTurnStallRule.FailNow(stalled)) return false;
            s_Turn.Remove(id);
            int castNum = ms.CastNum;
            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                var m = p.MagicState;
                if (!m.IsCasting || m.CastNum != castNum || !m.IsTurning) return;
                Mod.Log.Warn($"[RevivalGuard] cast watchdog: {p.Name} (0x{p.Guid.Full:X8}) cast #{castNum} turn stalled {stalled:F1}s "
                    + $"({age:F1}s into the cast; heading {heading:F0}, velocity {po.Velocity.Length():F2}, {ServerFooting.Describe(p)}): ended, nothing spent");
                BusyLedger.Note(p, $"cast watchdog: cast #{castNum} turn stalled {stalled:F1}s, ended at {age:F1}s");
                // FailCast(tryFizzle: false) without its DoCastSpell_Inner: no mana, no components; the fizzle answer so the
                // client and the assistant see the cast end. OnCastDone first, so the cancelled turn's OnMoveComplete_Magic
                // finds no cast to carry on.
                p.SendUseDoneEvent(ACE.Entity.Enum.WeenieError.YourSpellFizzled);
                m.OnCastDone();
                try { po.cancel_moveto(); } catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] cast watchdog: cancel turn: {e.Message}"); }
            }));
            return true;
        }

        // Once per cast: a finish that does not end it (DoCastSpell refused, a turn started) is left to the 10 s fail.
        static readonly System.Collections.Generic.Dictionary<uint, int> s_Finished = new System.Collections.Generic.Dictionary<uint, int>();

        static void Finish(Player p, int castNum, double sinceGesture, float gesture)
        {
            if (s_Finished.TryGetValue(p.Guid.Full, out int done) && done == castNum) return;
            if (s_Finished.Count > 200) s_Finished.Clear();
            s_Finished[p.Guid.Full] = castNum;
            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                if (!p.MagicState.IsCasting || p.MagicState.CastNum != castNum || p.MagicState.CastMotionDone) return;
                Mod.Log.Warn($"[RevivalGuard] cast watchdog: {p.Name} (0x{p.Guid.Full:X8}) cast #{castNum} gesture {p.MagicState.CastGesture} "
                    + $"not done {sinceGesture:F1}s after it began (plays {gesture:F1}s; {ServerFooting.Describe(p)}): finished as ACE would");
                BusyLedger.Note(p, $"cast watchdog: cast #{castNum} finished {sinceGesture:F1}s into its gesture");
                // HandleMotionDone_Magic's own steps
                p.MagicState.CastMotionDone = true;
                p.MagicState.AlwaysTurn = true;
                p.DoCastSpell(p.MagicState);
            }));
        }
    }
}
