namespace RevivalGuard
{
    /// <summary>When the cragstone scene RUNS, and one bael'zharon at a time.</summary>
    public static class LightbringerSceneRule
    {
        public const double EVERY = 4 * 3600, LEAD = 30 * 60, LENGTH = 9 * 60, GAP = 3600, STEP = 15 * 60, SHIFT = 120 * 60;

        public const double ROCK_X0 = 156, ROCK_X1 = 196, ROCK_NORTH = 99;
        public static readonly (double x, double y)[] SAFE = { (180, 103), (172, 108), (188, 108), (180, 112) };
        public const double STUCK_S = 3, STUCK_MOVE = 0.75, STUCK_REACH = 6;

        /// <summary>On (or inside) the rock, or south of its north face: no place for the landed Bael.</summary>
        public static bool InRock(double x, double y) => x > ROCK_X0 && x < ROCK_X1 && y < ROCK_NORTH;

        /// <summary>The known-good spot nearest (x, y).</summary>
        public static (double x, double y) NearestSafe(double x, double y)
        {
            var best = SAFE[0]; double bd = double.MaxValue;
            foreach (var p in SAFE) { double d = (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y); if (d < bd) { bd = d; best = p; } }
            return best;
        }

        /// <summary>The scene's watchdog on the landed Bael</summary>
        public sealed class StuckWatch
        {
            double ax, ay, at = double.NaN;
            public bool Update(double now, double x, double y, double targetDistance)
            {
                if (InRock(x, y)) return true;
                bool far = targetDistance > STUCK_REACH && !double.IsInfinity(targetDistance);
                if (double.IsNaN(at) || !far || Math.Abs(x - ax) + Math.Abs(y - ay) > STUCK_MOVE) { ax = x; ay = y; at = now; return false; }
                return now - at >= STUCK_S;
            }
            public void Reset() => at = double.NaN;
        }

        /// <summary>Is a scene starting at `start` at least GAP clear of every window [from, to)?</summary>
        public static bool Clear(double start, IEnumerable<(double from, double to)> windows)
        {
            double a = start - LEAD - GAP, b = start + LENGTH + GAP;
            foreach (var (from, to) in windows)
                if (from < b && to > a) return false;
            return true;
        }

        /// <summary>The slot at or after `t` (slots every EVERY from `anchor`).</summary>
        public static double SlotAtOrAfter(double anchor, double t) => anchor + Math.Ceiling((t - anchor) / EVERY) * EVERY;

        /// <summary>Where this slot's scene runs, or NaN when no start within SHIFT is clear.</summary>
        public static double Place(double slot, Func<double, double, IEnumerable<(double from, double to)>> windows, double earliest = double.MinValue)
        {
            var busy = windows(slot - SHIFT - LEAD - GAP, slot + SHIFT + LENGTH + GAP).ToList();
            for (int k = 0; k * STEP <= SHIFT; k++)
                foreach (var s in k == 0 ? new[] { slot } : new[] { slot + k * STEP, slot - k * STEP })
                    if (s >= earliest && Clear(s, busy)) return s;
            return double.NaN;
        }
    }
}
