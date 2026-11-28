using System.Linq;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class CastWatchdog
    {
        const string P_ON = "rg_cast_watchdog";
        const string P_STALL = "rg_cast_turn_stall";
        const string P_SPIN = "rg_cast_turn_spin";
        static double SPIN_SECONDS => Tunables.D("cast.spin_seconds", CastTurnStallRule.SpinSeconds);
        static double SPIN_DEGREES => Tunables.D("cast.spin_degrees", CastTurnStallRule.SpinDegrees);
        static double CAST_STUCK => Tunables.D("cast.stuck_seconds", 10.0);
        static double UNFOOTED => Tunables.D("cast.unfooted_seconds", 2.0);
        // 1 s, was 3: Mark the Noble's self heals stalled this way 171 times in one night
        static double FOOTED_GRACE => Tunables.D("cast.footed_grace_seconds", 1.0);
        static double s_Next;
        static readonly System.Collections.Generic.Dictionary<uint, double> s_Unfooted = new System.Collections.Generic.Dictionary<uint, double>();

        sealed class TurnSample
        {
            public int CastNum; public double At, Since = double.NaN; public float Heading; public ACE.Server.Physics.Common.Position Pos;
            public double SpinSince = double.NaN, Turned;   // CastTurnStallRule.Spin: turning since, degrees turned in all
            // for the 10 s line: why the turn-stall rule did not end this cast
            public double Rate, Speed, MaxRate, MaxSpeed; public int Samples, StalledSamples;
            public string Describe() => Samples == 0 ? "turn watch: no samples"
                : $"turn watch: {StalledSamples}/{Samples} samples stalled, heading rate {Rate:F0} (max {MaxRate:F0}) deg/s, speed {Speed:F2} (max {MaxSpeed:F2}) m/s";
        }
        static readonly System.Collections.Generic.Dictionary<uint, TurnSample> s_Turn = new System.Collections.Generic.Dictionary<uint, TurnSample>();

        // Diagnostics only: per cast, how often ACE restarted its turn and what the client sent meanwhile.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, CastProbe> s_Probe =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Player, CastProbe>();

        /// <summary>Player.TurnTo_Magic was called (CastProbeTurnTo).</summary>
        internal static void ProbeTurnTo(Player p)
        {
            try { var ms = p?.MagicState; if (ms != null && ms.IsCasting) s_Probe.GetOrCreateValue(p).TurnTo(ms.CastNum); }
            catch (System.Exception e) { TickGuard.Report("CastWatchdog probe", e); }
        }

        /// <summary>A MoveToState was handled (FallTraceMoveToState's postfix, world thread).</summary>
        internal static void ProbeMoveToState(Player p)
        {
            try
            {
                var ms = p?.MagicState;
                if (ms == null || !ms.IsCasting) return;
                var rs = p.CurrentMoveToState?.RawMotionState;
                s_Probe.GetOrCreateValue(p).MoveToState(ms.CastNum, (uint)(rs?.ForwardCommand ?? 0), (uint)(rs?.SidestepCommand ?? 0),
                                                        (uint)(rs?.TurnCommand ?? 0), (uint)(rs?.CurrentHoldKey ?? 0));
            }
            catch (System.Exception e) { TickGuard.Report("CastWatchdog probe", e); }
        }

        /// <summary>An AutonomousPosition was handled (FallTraceAutoPos's postfix, world thread).</summary>
        internal static void ProbeAutoPos(Player p)
        {
            try { var ms = p?.MagicState; if (ms != null && ms.IsCasting) s_Probe.GetOrCreateValue(p).AutoPos(ms.CastNum); }
            catch (System.Exception e) { TickGuard.Report("CastWatchdog probe", e); }
        }

        /// <summary>The probe part of the 10 s and stall lines; built only when a line is written.</summary>
        static string ProbeLine(Player p, int castNum)
        {
            try
            {
                s_Probe.TryGetValue(p, out var probe);
                var held = p.LastMoveToState?.RawMotionState;
                var target = p.MagicState.CastSpellParams?.Target ?? p.TurnTarget;
                double dist = double.NaN, angle = double.NaN;
                if (target != null && target.Location != null && p.Location != null) { dist = p.GetDistance(target); angle = p.GetAngle(target); }
                return CastProbe.Describe(probe, castNum, v => ((ACE.Entity.Enum.MotionCommand)v).ToString(), v => ((ACE.Entity.Enum.HoldKey)v).ToString(),
                                          (uint)(held?.TurnCommand ?? 0), (uint)(held?.TurnHoldKey ?? 0), target?.Guid.Full ?? 0, dist, angle);
            }
            catch (System.Exception e) { return "cast probe: " + e.GetType().Name; }
        }

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
                string turnWatch = s_Turn.TryGetValue(p.Guid.Full, out var tw) && tw.CastNum == castNum ? tw.Describe() : "turn watch: none";
                p.EnqueueAction(new ActionEventDelegate(() =>
                {
                    // the same cast, still running: anything else finished or replaced it meanwhile
                    if (!p.MagicState.IsCasting || p.MagicState.CastNum != castNum) return;
                    Mod.Log.Warn($"[RevivalGuard] cast watchdog: {p.Name} (0x{p.Guid.Full:X8}) cast #{castNum} still running "
                        + $"after {age:F0}s (gesture {p.MagicState.CastGesture}, motion done {p.MagicState.CastMotionDone}, "
                        + $"turning {p.MagicState.IsTurning}, turn release {p.MagicState.PendingTurnRelease}; off walkable {unfooted:F0}s, "
                        + $"{turnWatch}; {ProbeLine(p, castNum)}; {ServerFooting.Describe(p)}): failed, nothing spent");
                    BusyLedger.Note(p, $"cast watchdog: cast #{castNum} failed after {age:F0}s (off walkable {unfooted:F0}s)");
                    // FailCast(false) with the fizzle answer, as the turn-stall end: the line shows, nothing is spent
                    if (CastWatchdogRule.WatchdogFailSpends) p.FailCast();
                    else { p.SendUseDoneEvent(ACE.Entity.Enum.WeenieError.YourSpellFizzled); p.MagicState.OnCastDone(); }
                }));
            }
            if (s_Unfooted.Count > 200) s_Unfooted.Clear();
            if (s_Turn.Count > 200) s_Turn.Clear();
        }

        /// <summary>The turn-stall and turn-spin early ends (CastTurnStallRule): true once it has queued the end of this cast.</summary>
        static bool TurnStalled(Player p, ACE.Server.Entity.MagicState ms, double now, double age)
        {
            var po = p.PhysicsObj;
            bool stallOn = PropertyManager.GetBool(P_STALL, true).Item, spinOn = PropertyManager.GetBool(P_SPIN, true).Item;
            if (po == null || (!stallOn && !spinOn)) return false;
            uint id = p.Guid.Full;
            if (!s_Turn.TryGetValue(id, out var t) || t.CastNum != ms.CastNum) s_Turn[id] = t = new TurnSample { CastNum = ms.CastNum };
            float heading = po.get_heading();
            double dt = t.Pos == null ? 0 : now - t.At;
            double moved = t.Pos == null ? 0 : t.Pos.Distance(po.Position);
            double turned = t.Pos == null ? 0 : CastTurnStallRule.HeadingDelta(t.Heading, heading);
            double stalled = CastTurnStallRule.Stalled(ms.IsTurning, ms.PendingTurnRelease, ServerFooting.Walkable(p), moved, turned, dt, ref t.Since, now);
            CastTurnStallRule.Spin(ms.IsTurning, ms.PendingTurnRelease, turned, dt, ref t.SpinSince, ref t.Turned, now);
            if (dt > 0)
            {
                t.Rate = turned / dt; t.Speed = moved / dt; t.Samples++;
                if (t.Rate > t.MaxRate) t.MaxRate = t.Rate;
                if (t.Speed > t.MaxSpeed) t.MaxSpeed = t.Speed;
                if (!double.IsNaN(t.Since)) t.StalledSamples++;
            }
            t.At = now; t.Heading = heading; t.Pos = new ACE.Server.Physics.Common.Position(po.Position);
            string why;
            if (stallOn && CastTurnStallRule.FailNow(stalled)) why = $"turn stalled {stalled:F1}s";
            else if (spinOn && CastTurnStallRule.SpinNow(t.SpinSince, t.Turned, now, SPIN_SECONDS, SPIN_DEGREES))
                why = $"turn not converging ({t.Turned:F0} deg turned in {now - t.SpinSince:F1}s, rate {t.Rate:F0} deg/s)";
            else return false;
            s_Turn.Remove(id);
            int castNum = ms.CastNum;
            p.EnqueueAction(new ActionEventDelegate(() =>
            {
                var m = p.MagicState;
                if (!m.IsCasting || m.CastNum != castNum || !m.IsTurning) return;
                Mod.Log.Warn($"[RevivalGuard] cast watchdog: {p.Name} (0x{p.Guid.Full:X8}) cast #{castNum} {why} "
                    + $"({age:F1}s into the cast; heading {heading:F0}, velocity {po.Velocity.Length():F2}, {ProbeLine(p, castNum)}, {ServerFooting.Describe(p)}): ended, nothing spent");
                BusyLedger.Note(p, $"cast watchdog: cast #{castNum} {why}, ended at {age:F1}s");
                // FailCast without its DoCastSpell_Inner: no mana, no components; the fizzle answer so the client and the assistant see the cast end.
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

    /// <summary>Counts ACE's cast turn (re)starts for CastWatchdog's probe.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.TurnTo_Magic))]
    static class CastProbeTurnTo
    {
        static void Prefix(Player __instance) => CastWatchdog.ProbeTurnTo(__instance);
    }
}
