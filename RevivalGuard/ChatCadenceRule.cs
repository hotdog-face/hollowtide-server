using System;
using System.Collections.Generic;
using System.Text;

namespace RevivalGuard
{
    /// <summary>Automated chat, whatever the client.</summary>
    public sealed class ChatCadenceRule
    {
        public const int MIN_LEN = 6;
        public static readonly TimeSpan SAME_WINDOW = TimeSpan.FromMinutes(5);
        public const int SAME_MAX = 3;
        public static readonly TimeSpan CADENCE_WINDOW = TimeSpan.FromMinutes(45);
        public const int CADENCE_COUNT = 4;
        public const double CADENCE_JITTER = 0.10, CADENCE_SLACK_S = 2.0;
        public const double NEAR = 0.85;
        public static readonly TimeSpan STRIKE_MEMORY = TimeSpan.FromHours(1);
        public static readonly TimeSpan MUTE1 = TimeSpan.FromMinutes(5), MUTE2 = TimeSpan.FromMinutes(30), MUTE3 = TimeSpan.FromHours(2);
        const int KEEP = 32, MAX_PEOPLE = 4096;

        public enum Verdict { Ok, Warn, Mute, Muted }

        public struct Result
        {
            public Verdict Verdict;
            /// <summary>Why, for the log (Warn and Mute).</summary>
            public string Why;
            /// <summary>Mute and Muted: when it ends.</summary>
            public DateTime Until;
            public int Strike;
            public bool Drop => Verdict != Verdict.Ok;
        }

        sealed class Person
        {
            public readonly List<(DateTime at, string norm)> Lines = new List<(DateTime, string)>();
            public readonly List<DateTime> Strikes = new List<DateTime>();
            public DateTime MutedUntil, LastSeen;
        }

        readonly Dictionary<uint, Person> m_People = new Dictionary<uint, Person>();

        /// <summary>A line as compared: letters and digits only, lower case, a run of one letter cut to one, so spacing</summary>
        public static string Normalize(string text)
        {
            var sb = new StringBuilder();
            foreach (char c0 in text ?? "")
            {
                if (!char.IsLetterOrDigit(c0)) continue;
                char c = char.ToLowerInvariant(c0);
                if (sb.Length > 0 && sb[sb.Length - 1] == c) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The same line or nearly (one word changed, a number counted up): edit similarity NEAR or more.</summary>
        public static bool Near(string a, string b)
        {
            if (a == b) return true;
            if (a.Length < MIN_LEN || b.Length < MIN_LEN) return false;
            int la = Math.Min(a.Length, 160), lb = Math.Min(b.Length, 160);
            int longer = Math.Max(la, lb);
            if (Math.Abs(la - lb) > longer * (1 - NEAR)) return false;
            var prev = new int[lb + 1]; var cur = new int[lb + 1];
            for (int j = 0; j <= lb; j++) prev[j] = j;
            for (int i = 1; i <= la; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= lb; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return 1.0 - (double)prev[lb] / longer >= NEAR;
        }

        /// <summary>Times of times end in CADENCE_COUNT at one period</summary>
        public static bool Regular(IReadOnlyList<DateTime> times, out double period)
        {
            period = 0;
            if (times.Count < CADENCE_COUNT) return false;
            double min = double.MaxValue, max = 0, sum = 0;
            for (int i = times.Count - CADENCE_COUNT + 1; i < times.Count; i++)
            {
                double g = (times[i] - times[i - 1]).TotalSeconds;
                min = Math.Min(min, g); max = Math.Max(max, g); sum += g;
            }
            period = sum / (CADENCE_COUNT - 1);
            return period > 0 && max - min <= period * CADENCE_JITTER + CADENCE_SLACK_S;
        }

        /// <summary>Whether a muted character is muted now (any chat is dropped).</summary>
        public bool IsMuted(uint who, DateTime now, out DateTime until)
        {
            lock (m_People)
            {
                until = default;
                if (!m_People.TryGetValue(who, out var p) || p.MutedUntil <= now) return false;
                until = p.MutedUntil; return true;
            }
        }

        /// <summary>Judge one line who is sending at now.</summary>
        public Result Check(uint who, string text, DateTime now)
        {
            lock (m_People)
            {
                if (!m_People.TryGetValue(who, out var p))
                {
                    if (m_People.Count >= MAX_PEOPLE) Prune(now);
                    m_People[who] = p = new Person();
                }
                p.LastSeen = now;
                string t = (text ?? "").TrimStart();
                if (t.StartsWith("@") || t.StartsWith("/")) return new Result { Verdict = Verdict.Ok };
                if (p.MutedUntil > now) return new Result { Verdict = Verdict.Muted, Until = p.MutedUntil, Strike = p.Strikes.Count };
                string norm = Normalize(t);
                if (norm.Length < MIN_LEN) return new Result { Verdict = Verdict.Ok };

                p.Lines.RemoveAll(l => now - l.at > CADENCE_WINDOW);
                int same = 0; var times = new List<DateTime>();
                foreach (var l in p.Lines)
                {
                    if (!Near(l.norm, norm)) continue;
                    times.Add(l.at);
                    if (now - l.at <= SAME_WINDOW) same++;
                }
                times.Add(now);

                string why = null;
                if (same >= SAME_MAX) why = $"the same line {same + 1} times in {SAME_WINDOW.TotalMinutes:0} minutes";
                else if (Regular(times, out double period)) why = $"the same line every {period:0} s, {CADENCE_COUNT} times";
                if (why == null)
                {
                    p.Lines.Add((now, norm));
                    if (p.Lines.Count > KEEP) p.Lines.RemoveAt(0);
                    return new Result { Verdict = Verdict.Ok };
                }

                // a strike: that line's history is spent, so the next strike needs a new run of repeats
                p.Lines.RemoveAll(l => Near(l.norm, norm));
                p.Strikes.RemoveAll(s => now - s > STRIKE_MEMORY);
                p.Strikes.Add(now);
                int n = p.Strikes.Count;
                if (n == 1) return new Result { Verdict = Verdict.Warn, Why = why, Strike = 1 };
                p.MutedUntil = now + (n == 2 ? MUTE1 : n == 3 ? MUTE2 : MUTE3);
                return new Result { Verdict = Verdict.Mute, Why = why, Until = p.MutedUntil, Strike = n };
            }
        }

        /// <summary>What the speaker is told (sender only).</summary>
        public static string Notice(Result r, DateTime now)
        {
            switch (r.Verdict)
            {
                case Verdict.Warn: return $"Repeating the same line over and over looks like a macro. Do it again and you will be muted for {MUTE1.TotalMinutes:0} minutes.";
                case Verdict.Mute: case Verdict.Muted:
                    return $"You are muted for repeating chat. You can talk again in {Math.Max(1, (int)Math.Ceiling((r.Until - now).TotalMinutes))} minutes.";
                default: return null;
            }
        }

        void Prune(DateTime now)
        {
            var gone = new List<uint>();
            foreach (var kv in m_People)
                if (kv.Value.MutedUntil <= now && now - kv.Value.LastSeen > STRIKE_MEMORY) gone.Add(kv.Key);
            foreach (var k in gone) m_People.Remove(k);
        }
    }
}
