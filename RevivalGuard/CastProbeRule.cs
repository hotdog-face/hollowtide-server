namespace RevivalGuard
{
    /// <summary>CastWatchdog's per-cast movement probe, pure (no ACE types), so tools/nettest compiles and checks it by path.
    /// Diagnostics only (cast-spin audit 2026-11-10, H1): does the caster keep moving during the turn before the windup,
    /// so each MoveToState cancels ACE's turn and TurnTo_Magic starts it again? Counts per cast, reset lazily the first time
    /// a new CastNum is seen; written only while the player is casting. One probe per player, touched only by that
    /// player's own threads (world thread for messages, its landblock group for turns; ACE runs those phases one after another).</summary>
    public sealed class CastProbe
    {
        public int CastNum = int.MinValue;
        public int TurnTos, MoveToStates, AutoPositions;
        public bool HaveLast;
        public uint Fwd, Side, Turn, Hold;

        /// <summary>Starts counting afresh when castNum is a different cast from the one counted.</summary>
        public void For(int castNum)
        {
            if (castNum == CastNum) return;
            CastNum = castNum; TurnTos = MoveToStates = AutoPositions = 0;
            HaveLast = false; Fwd = Side = Turn = Hold = 0;
        }

        public void TurnTo(int castNum) { For(castNum); TurnTos++; }
        public void AutoPos(int castNum) { For(castNum); AutoPositions++; }
        public void MoveToState(int castNum, uint fwd, uint side, uint turn, uint hold)
        {
            For(castNum); MoveToStates++;
            HaveLast = true; Fwd = fwd; Side = side; Turn = turn; Hold = hold;
        }

        /// <summary>The probe part of a cast watchdog line. probe null or another cast's: all counts 0. cmd/key name a
        /// MotionCommand / HoldKey (0 prints "none"). target 0: no target; dist/angle NaN print "?".</summary>
        public static string Describe(CastProbe p, int castNum, System.Func<uint, string> cmd, System.Func<uint, string> key,
                                      uint heldTurn, uint heldTurnKey, uint target, double dist, double angle)
        {
            bool mine = p != null && p.CastNum == castNum;
            string C(uint v) => v == 0 ? "none" : cmd(v);
            string K(uint v) => v == 0 ? "none" : key(v);
            string N(double v, string f) => double.IsNaN(v) || double.IsInfinity(v) ? "?" : v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
            string last = mine && p.HaveLast
                ? $"last MoveToState fwd {C(p.Fwd)} side {C(p.Side)} turn {C(p.Turn)} hold {K(p.Hold)}"
                : "no MoveToState";
            string held = heldTurn == 0 ? "held turn none" : $"held turn {C(heldTurn)} (key {K(heldTurnKey)})";
            string tgt = target == 0 ? "target none" : $"target 0x{target:X8} {N(dist, "F1")} m, {N(angle, "F0")} deg off";
            return $"cast probe: TurnTo_Magic {(mine ? p.TurnTos : 0)}, MoveToState {(mine ? p.MoveToStates : 0)}, "
                 + $"AutonomousPosition {(mine ? p.AutoPositions : 0)}; {last}; {held}; {tgt}";
        }
    }
}
