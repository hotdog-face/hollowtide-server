using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// FallGuard's rule, pure (no ACE types), so tools/nettest compiles and checks it by path. ACE hurts a PK by its server
    /// body's own downward speed at each touch of the ground (Player.HandleFallingDamage), so a body that bounces down a
    /// cliff face is charged at every bounce, and a body that falls where the client did not is charged for a fall nobody
    /// made. This caps a descent's damage at what the client's own reports show: a fall from the highest report since its
    /// last ground report to its latest one (or to the landing report that ended it), plus the lift of a recent jump.
    /// A hit the reports do not show yet is held (10 s) for the landing report; if none shows it, it is dropped.
    /// </summary>
    public sealed class FallGuardState
    {
        public const float G = 9.8f;                       // PhysicsGlobals.Gravity
        public const float JUMP_V = 11.25434f, LEEWAY = 4.5f, UNIT = 87.29381f;   // ACE Player_Move.HandleFallingDamage
        public const float SLACK_M = 1.5f;                 // report z noise, step heights
        public const double WINDOW_S = 6;                  // one descent's hits share a budget
        public const double LANDING_S = 3;                 // a landing report's drop counts this long
        public const double PENDING_S = 10;                // a hit waits this long for the landing report (a client hitch: box 70 s freeze)
        public const double JUMP_S = 6;                    // a jump's lift counts this long

        float _topZ = float.NaN, _lastZ = float.NaN, _landingDrop;
        double _landingT = double.NegativeInfinity, _jumpT = double.NegativeInfinity;
        float _jumpVz;
        readonly List<(double t, float amount)> _charged = new List<(double, float)>();
        public float Pending { get; private set; }
        double _pendingT;

        /// <summary>ACE's damage amount (before armour) for a landing at this downward speed.</summary>
        public static float AceAmount(float downSpeed) => Math.Max(0f, (downSpeed - JUMP_V - LEEWAY) / JUMP_V * UNIT);

        /// <summary>The landing speed a fall of <paramref name="dropM"/> gives, starting with a jump's upward speed.</summary>
        public static float Speed(float dropM, float jumpVz) =>
            (float)Math.Sqrt(Math.Max(0f, jumpVz) * Math.Max(0f, jumpVz) + 2f * G * Math.Max(0f, dropM + SLACK_M));

        /// <summary>A client report: a ground report (AutonomousPosition with contact) ends a descent and records its drop.</summary>
        public void Report(double t, float z, bool ground)
        {
            if (ground)
            {
                if (!float.IsNaN(_topZ)) { _landingDrop = _topZ - z; _landingT = t; }
                _topZ = z;
            }
            else _topZ = float.IsNaN(_topZ) ? z : Math.Max(_topZ, z);
            _lastZ = z;
        }

        public void Jump(double t, float vz) { _jumpT = t; _jumpVz = vz; }

        /// <summary>A teleport: the next report is from somewhere else, not the end of a fall.</summary>
        public void Reset()
        {
            _topZ = _lastZ = float.NaN; _landingT = _jumpT = double.NegativeInfinity; _charged.Clear(); Pending = 0f;
        }

        /// <summary>How far the client's own reports say it has fallen, now.</summary>
        public float Drop(double t)
        {
            float d = float.IsNaN(_topZ) ? 0f : _topZ - _lastZ;
            if (t - _landingT <= LANDING_S) d = Math.Max(d, _landingDrop);
            return Math.Max(0f, d);
        }

        public float Allowed(double t) => AceAmount(Speed(Drop(t), t - _jumpT <= JUMP_S ? _jumpVz : 0f));

        float Charged(double t)
        {
            _charged.RemoveAll(c => t - c.t > WINDOW_S);
            float s = 0f; foreach (var c in _charged) s += c.amount; return s;
        }

        /// <summary>ACE wants to hurt by <paramref name="aceAmount"/>: what to apply now. The rest waits for a report.</summary>
        public float Hit(double t, float aceAmount)
        {
            float pay = Math.Max(0f, Math.Min(aceAmount, Allowed(t) - Charged(t)));
            if (pay > 0f) _charged.Add((t, pay));
            float rest = aceAmount - pay;
            if (rest > 0.5f) { Pending = Math.Max(Pending, rest); _pendingT = t; }
            return pay;
        }

        /// <summary>After a report: the held damage its drop now shows (0 if none). <paramref name="dropped"/> is the held
        /// amount given up because no report showed the fall in time.</summary>
        public float Settle(double t, out float dropped)
        {
            dropped = 0f;
            if (Pending <= 0f) return 0f;
            if (t - _pendingT > PENDING_S) { dropped = Pending; Pending = 0f; return 0f; }
            float pay = Math.Max(0f, Math.Min(Pending, Allowed(t) - Charged(t)));
            if (pay <= 0.5f) return 0f;
            _charged.Add((t, pay)); Pending -= pay;
            if (Pending <= 0.5f) Pending = 0f;
            return pay;
        }
    }
}
