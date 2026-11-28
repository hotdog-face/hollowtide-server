using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevivalGuard
{
    /// <summary>Boss kill news, PURE (no ACE types): BossBroadcast.cs runs it, tools/nettest BossBroadcastCase checks it.</summary>
    static class BossBroadcastRule
    {
        public const int COOLDOWN_S = 600, PER_MINUTE = 3;

        public sealed class Entry
        {
            public string Name { get; set; }          // as said: "the Thunder Turkey"; empty = the creature's own name
            public string From { get; set; }          // where it came from: "wiki", "ours: Thanksgiving", ...
        }

        public sealed class Data
        {
            public int CooldownSeconds { get; set; } = COOLDOWN_S;
            public int PerMinute { get; set; } = PER_MINUTE;
            public Dictionary<uint, Entry> Bosses { get; set; } = new Dictionary<uint, Entry>();
            public Dictionary<uint, string> Own { get; set; } = new Dictionary<uint, string>();   // has its own death line: why
            public List<uint> Deny { get; set; } = new List<uint>();                              // listed by the wiki, too common
        }

        public sealed class Overrides
        {
            public Dictionary<uint, string> Allow { get; set; } = new Dictionary<uint, string>();   // wcid -> name to say ("" = its own)
            public List<uint> Deny { get; set; } = new List<uint>();
        }

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            WriteIndented = true, NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        public static Data ParseData(string text) => JsonSerializer.Deserialize<Data>(text, Json) ?? throw new FormatException("empty");
        public static Overrides ParseOverrides(string text) =>
            string.IsNullOrWhiteSpace(text) ? new Overrides() : JsonSerializer.Deserialize<Overrides>(text, Json) ?? new Overrides();
        public static string Write(Overrides o) => JsonSerializer.Serialize(o, Json);

        public enum Why { Say, NotListed, Denied, OwnLine, SameInstance, Cooldown, GlobalCap }

        /// <summary>Is this wcid a listed boss after the overrides, and the name to say (null = the creature's own).</summary>
        public static Why Listed(Data d, Overrides o, uint wcid, out string name)
        {
            name = null;
            if (o != null && o.Deny.Contains(wcid)) return Why.Denied;
            if (o != null && o.Allow.TryGetValue(wcid, out var n)) { name = string.IsNullOrWhiteSpace(n) ? null : n.Trim(); return Why.Say; }
            if (d.Own.ContainsKey(wcid)) return Why.OwnLine;
            if (d.Deny.Contains(wcid)) return Why.Denied;
            if (!d.Bosses.TryGetValue(wcid, out var e)) return Why.NotListed;
            name = string.IsNullOrWhiteSpace(e.Name) ? null : e.Name.Trim();
            return Why.Say;
        }

        /// <summary>Every wcid that would be announced, with the name said (null = its own).</summary>
        public static SortedDictionary<uint, string> Effective(Data d, Overrides o)
        {
            var all = new SortedDictionary<uint, string>();
            foreach (var w in d.Bosses.Keys.Concat(o?.Allow.Keys ?? Enumerable.Empty<uint>()))
                if (Listed(d, o, w, out var n) == Why.Say) all[w] = n;
            return all;
        }

        /// <summary>The rate limits.</summary>
        public sealed class Limiter
        {
            readonly Dictionary<uint, double> m_LastByWcid = new Dictionary<uint, double>();
            readonly HashSet<uint> m_Instances = new HashSet<uint>();
            readonly Queue<uint> m_InstanceOrder = new Queue<uint>();
            readonly Queue<double> m_Recent = new Queue<double>();
            public int Said, Cooled, Capped;

            /// <summary>Should this death be said now?</summary>
            public Why Decide(double now, uint wcid, uint instance, int cooldownS, int perMinute)
            {
                if (m_Instances.Contains(instance)) return Why.SameInstance;
                Remember(instance);
                if (m_LastByWcid.TryGetValue(wcid, out var last) && now - last < cooldownS) { Cooled++; return Why.Cooldown; }
                while (m_Recent.Count > 0 && now - m_Recent.Peek() >= 60) m_Recent.Dequeue();
                if (m_Recent.Count >= perMinute) { Capped++; return Why.GlobalCap; }
                m_LastByWcid[wcid] = now;
                m_Recent.Enqueue(now);
                Said++;
                return Why.Say;
            }

            public double SecondsLeft(double now, uint wcid, int cooldownS) =>
                m_LastByWcid.TryGetValue(wcid, out var last) ? Math.Max(0, cooldownS - (now - last)) : 0;

            void Remember(uint instance)
            {
                m_Instances.Add(instance);
                m_InstanceOrder.Enqueue(instance);
                while (m_InstanceOrder.Count > 2000) m_Instances.Remove(m_InstanceOrder.Dequeue());
            }
        }

        /// <summary>"The Thunder Turkey has been slain by an admin character!" / "... and the fellowship Turkey Hunters!"</summary>
        public static string Line(string boss, string killer, string fellowship)
        {
            boss = (boss ?? "").Trim();
            if (boss.Length > 0) boss = char.ToUpperInvariant(boss[0]) + boss.Substring(1);
            return string.IsNullOrWhiteSpace(fellowship)
                ? $"{boss} has been slain by {killer}!"
                : $"{boss} has been slain by {killer} and the fellowship {fellowship.Trim()}!";
        }
    }
}
