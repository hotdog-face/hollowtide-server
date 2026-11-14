using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of TickContainment (no ACE types; tools/nettest compiles it by path): what to say about one
    /// contained tick exception, and when an object has failed often enough to be taken out of the tick.
    ///
    /// Per object: the first failure of each kind (site + exception type) is logged in full with its stack; later
    /// failures of that kind give one summary line per <see cref="TickFaultLimits.RepeatEveryS"/> with how many were
    /// held back. <see cref="TickFaultLimits.QuarantineAfter"/> failures inside <see cref="TickFaultLimits.WindowS"/>
    /// quarantine the object (once), and that line is always said. Across all objects, the other lines are capped per
    /// minute so a fault in a common code path (every monster at once) cannot flood the log; what the cap holds back is
    /// counted (<see cref="TickFaultBudget.HeldBack"/>) and said on the next line it allows.
    /// </summary>
    public sealed class TickFaultLimits
    {
        public int QuarantineAfter = 5;          // failures inside the window that take an object out of the tick
        public double WindowS = 120;             // a failure older than this no longer counts toward quarantine
        public double RepeatEveryS = 60;         // a repeated kind of failure: one summary line per object this often
        public int LinesPerMinute = 30;          // non-quarantine lines, all objects together, per minute
    }

    public enum TickFaultLog { Full, Summary, Silent }

    public struct TickFaultVerdict
    {
        public TickFaultLog Log;
        public int HeldBack;          // failures of this kind on this object not logged since the last line
        public int Strikes;           // failures inside the window, this one included
        public bool Quarantine;       // true exactly once: this failure takes the object out of the tick
    }

    /// <summary>Lines allowed per minute across every object. Not thread safe: the caller locks.</summary>
    public sealed class TickFaultBudget
    {
        double m_Start = double.MinValue;
        int m_Used;
        public int HeldBack;          // lines the cap turned into silence since the caller last reset it

        public bool TryTake(double now, int perMinute)
        {
            if (now - m_Start >= 60) { m_Start = now; m_Used = 0; }
            if (m_Used >= perMinute) { HeldBack++; return false; }
            m_Used++;
            return true;
        }
    }

    /// <summary>One object's tick failures. Not thread safe: the caller locks.</summary>
    public sealed class TickFaultRecord
    {
        readonly HashSet<string> m_Seen = new HashSet<string>();
        readonly Dictionary<string, (double last, int held)> m_Repeat = new Dictionary<string, (double, int)>();
        readonly Queue<double> m_Recent = new Queue<double>();
        public bool Quarantined { get; private set; }
        public int Total { get; private set; }

        public TickFaultVerdict Record(string kind, double now, TickFaultLimits l, TickFaultBudget budget)
        {
            Total++;
            var v = new TickFaultVerdict();
            m_Recent.Enqueue(now);
            while (m_Recent.Count > 0 && now - m_Recent.Peek() > l.WindowS) m_Recent.Dequeue();
            v.Strikes = m_Recent.Count;

            if (!Quarantined && v.Strikes >= l.QuarantineAfter)
            {
                Quarantined = true;
                v.Quarantine = true;
            }

            m_Repeat.TryGetValue(kind, out var r);
            if (!m_Seen.Contains(kind))
            {
                // a first sighting is said in full unless the minute's lines are spent; then the next one tries again
                if (v.Quarantine || budget.TryTake(now, l.LinesPerMinute))
                {
                    m_Seen.Add(kind);
                    v.Log = TickFaultLog.Full;
                    v.HeldBack = r.held;
                    m_Repeat[kind] = (now, 0);
                }
                else
                {
                    v.Log = TickFaultLog.Silent;
                    m_Repeat[kind] = (r.last, r.held + 1);
                }
                return v;
            }

            if (v.Quarantine || (now - r.last >= l.RepeatEveryS && budget.TryTake(now, l.LinesPerMinute)))
            {
                v.Log = TickFaultLog.Summary;
                v.HeldBack = r.held;
                m_Repeat[kind] = (now, 0);
            }
            else
            {
                v.Log = TickFaultLog.Silent;
                m_Repeat[kind] = (r.last, r.held + 1);
            }
            return v;
        }

        /// <summary>Back into the tick with a clean slate (an admin release, or a new login of the same character).</summary>
        public void Clear() { Quarantined = false; m_Recent.Clear(); m_Seen.Clear(); m_Repeat.Clear(); }
    }
}
