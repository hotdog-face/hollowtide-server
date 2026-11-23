namespace RevivalGuard
{
    /// <summary>CastWatchdog's turn-stall rule, pure (no ACE types), so tools/nettest compiles and checks it by path.
    /// A cast in its turn (MagicState.IsTurning) whose caster stands still on walkable ground with the heading not moving
    /// is a turn that will never complete (report 261029-0010: turning True on a slope, contact plane z 0.70, held the
    /// full 10 s). Owner-approved 2026-10-29: such a cast ends once it has stood so for <see cref="StallSeconds"/>.</summary>
    public static class CastTurnStallRule
    {
        public const double StallSeconds = 1.5;
        /// <summary>Metres a second: under this the body is standing (a walk is 3+, a run 10+).</summary>
        public const double StillSpeed = 0.2;
        /// <summary>Degrees a second: under this the heading is not changing (a turn is 90+).</summary>
        public const double StillTurnRate = 5.0;

        /// <summary>The smallest difference between two headings in degrees, 0..180.</summary>
        public static double HeadingDelta(double a, double b)
        {
            double d = System.Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        /// <summary>One sample: is the turn stalled right now (turning, footed, not moving, heading fixed)? Not while ACE
        /// waits on purpose for a held turn key (PendingTurnRelease).</summary>
        public static bool StalledNow(bool turning, bool pendingTurnRelease, bool walkable, double speed, double turnRate) =>
            turning && !pendingTurnRelease && walkable && speed < StillSpeed && turnRate < StillTurnRate;

        /// <summary>How long the turn has been stalled, from the first sample seen stalled (since = NaN: not stalled).
        /// dt is the time since the previous sample; with no previous sample (dt &lt;= 0) speed and rate are unknown, so the
        /// clock does not start.</summary>
        public static double Stalled(bool turning, bool pendingTurnRelease, bool walkable, double moved, double headingDelta,
                                     double dt, ref double since, double now)
        {
            bool stalled = dt > 0 && StalledNow(turning, pendingTurnRelease, walkable, moved / dt, headingDelta / dt);
            if (!stalled) { since = double.NaN; return 0; }
            if (double.IsNaN(since)) since = now - dt;   // it was already standing so at the previous sample
            return now - since;
        }

        /// <summary>End the cast now: stalled long enough.</summary>
        public static bool FailNow(double stalledSeconds, double limit = StallSeconds) => stalledSeconds >= limit;

        // ---- turn not converging (bug hunt 2026-11-10: 13 of 18 full 10 s fizzles still turning, 126 and 34 deg/s, 0-2 of
        // 40 samples stalled, so the stall rule never qualified). A turn to a target is at most 180 degrees.

        /// <summary>Seconds of turning before a spin can end the cast.</summary>
        public const double SpinSeconds = 3.0;
        /// <summary>Total degrees turned past which the turn is not converging (a legit turn-to-target is at most 180).</summary>
        public const double SpinDegrees = 360.0;

        /// <summary>Adds this sample's heading change to the turn's total. since = NaN and total = 0 while not turning (or
        /// while ACE waits for a held turn key); the first sample (dt &lt;= 0) has no delta, so the clock does not start.</summary>
        public static void Spin(bool turning, bool pendingTurnRelease, double headingDelta, double dt,
                                ref double since, ref double total, double now)
        {
            if (!turning || pendingTurnRelease) { since = double.NaN; total = 0; return; }
            if (dt <= 0) return;
            if (double.IsNaN(since)) since = now - dt;   // it was already turning at the previous sample
            total += headingDelta;
        }

        /// <summary>End the cast now: turning at least <paramref name="seconds"/> and more than <paramref name="degrees"/> turned.</summary>
        public static bool SpinNow(double since, double total, double now, double seconds = SpinSeconds, double degrees = SpinDegrees) =>
            !double.IsNaN(since) && now - since >= seconds && total > degrees;
    }
}
