using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>The pure part of MoveGuard (no ACE types; tools/nettest compiles it by path, MoveGuardCase).</summary>
    public sealed class MoveGuardParams
    {
        public double Tol = 1.25;      // x the run speed: slopes, rounding, a run rate a hair behind the client's
        public double Lateness = 2.0;  // seconds an ordinary report may arrive late relative to another
        public double Margin = 6.0;    // metres always allowed (position precision, collision push-outs)
        public double Window = 30.0;   // seconds of reports kept as anchors
        public double Flush = 3.0;     // seconds after a silence its reports may still arrive (retransmits catching up)
        public double Thin = 0.5;      // seconds between kept anchors (dropping anchors only ever lets more through)
    }

    public struct MoveVerdict
    {
        public bool Flag;        // some anchor proves the move impossible
        public double Dist;      // metres from the deciding anchor (the worst ratio)
        public double Allowed;   // metres that anchor allowed
        public double Span;      // seconds since that anchor arrived
        public double Ratio;     // Dist / Allowed at the worst anchor (1.0 = at the limit); 0 with no anchor
    }

    public sealed class MoveTrack
    {
        internal struct Sample { public double T, E, X, Y; }
        internal readonly List<Sample> Kept = new List<Sample>();
        internal Sample Last; internal bool HasLast;
        internal double Carry, CarryAt;
        internal readonly List<(double t, double m)> Credits = new List<(double, double)>();
        public bool Empty => !HasLast;
        public int Anchors => Kept.Count + (HasLast ? 1 : 0);
    }

    public static class MoveGuardRule
    {
        /// <summary>m/s at run rate 1: retail's RunForward cycle (MovementGuard's BASE_RUN).</summary>
        public const double BASE_RUN = 4.0;

        /// <summary>Mode switch values (rg_moveguard_mode).</summary>
        public const long OFF = 0, LOG = 1, ENFORCE = 2;
        public static long Mode(long raw) => raw <= OFF ? OFF : raw >= ENFORCE ? ENFORCE : LOG;

        /// <summary>A new baseline: the server's position, as of since.</summary>
        public static void Reset(MoveTrack tr, double since, double x, double y, MoveGuardParams p)
        {
            tr.Kept.Clear(); tr.Credits.Clear();
            tr.Carry = 0; tr.CarryAt = since;
            tr.Last = new MoveTrack.Sample { T = since, E = since - p.Lateness, X = x, Y = y };
            tr.HasLast = true;
            tr.Kept.Add(tr.Last);
        }

        /// <summary>The server threw the player (a knockback): that many metres are allowed on top, against every anchor older than now.</summary>
        public static void Credit(MoveTrack tr, double now, double metres)
        {
            if (metres > 0) tr.Credits.Add((now, metres));
        }

        /// <summary>Judges a report arriving at now at for a player whose top speed over the window is vmax m/s. Does not record it.</summary>
        public static MoveVerdict Judge(MoveTrack tr, double now, double x, double y, double vmax, MoveGuardParams p)
        {
            var v = new MoveVerdict();
            if (!tr.HasLast) return v;
            double speed = Math.Max(0.0, vmax) * p.Tol;
            void Check(in MoveTrack.Sample a)
            {
                double dx = x - a.X, dy = y - a.Y, d = Math.Sqrt(dx * dx + dy * dy);
                double credit = 0;
                foreach (var c in tr.Credits) if (c.t >= a.T) credit += c.m;
                double allowed = speed * Math.Max(0.0, now - a.E) + p.Margin + credit;
                double ratio = d / allowed;
                if (ratio > v.Ratio) { v.Ratio = ratio; v.Dist = d; v.Allowed = allowed; v.Span = now - a.T; }
                if (d > allowed) v.Flag = true;
            }
            foreach (var a in tr.Kept) Check(a);
            Check(tr.Last);
            return v;
        }

        /// <summary>Records an accepted report: its send-time bound, and an anchor every Thin seconds.</summary>
        public static void Accept(MoveTrack tr, double now, double x, double y, MoveGuardParams p)
        {
            if (!tr.HasLast) { Reset(tr, now, x, y, p); return; }
            double gap = Math.Max(0.0, now - tr.Last.T);
            double carry = now - tr.CarryAt <= p.Flush ? tr.Carry : 0.0;
            if (gap >= carry) { tr.Carry = gap; tr.CarryAt = now; carry = gap; }
            double e = Math.Max(tr.Last.E, now - p.Lateness - carry);
            tr.Last = new MoveTrack.Sample { T = now, E = Math.Min(e, now), X = x, Y = y };
            var newest = tr.Kept.Count > 0 ? tr.Kept[tr.Kept.Count - 1] : default;
            if (tr.Kept.Count == 0 || now - newest.T >= p.Thin) tr.Kept.Add(tr.Last);
            // keep one anchor older than the window, so a long quiet stretch is still measured from somewhere
            while (tr.Kept.Count > 1 && now - tr.Kept[1].T > p.Window) tr.Kept.RemoveAt(0);
            tr.Credits.RemoveAll(c => now - c.t > p.Window * 2);
        }
    }
}
