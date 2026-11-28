using System;
using System.Collections.Generic;
using System.Text;

namespace RevivalGuard
{
    /// <summary>The pure part of AwayMessage.cs (no ACE types: tools/nettest compiles it by path, AwayMessageCase.cs).</summary>
    public sealed class AwayMessageRule
    {
        public static readonly TimeSpan COOLDOWN = TimeSpan.FromMinutes(5);
        /// <summary>Retail's own cap for an AFK message (acclient.exe help, "limited to 192 characters").</summary>
        public const int MAX_LENGTH = 192;
        public const string PREFIX = "[Away]";
        public const string DEFAULT_MESSAGE = "I am away from the keyboard right now and will answer when I am back.";
        const int MAX = 4096;

        readonly Dictionary<(uint away, uint sender), DateTime> m_Answered = new Dictionary<(uint, uint), DateTime>();
        readonly Dictionary<uint, HashSet<uint>> m_Missed = new Dictionary<uint, HashSet<uint>>();

        /// <summary>Whether a tell from sender that reached away (who is away) gets the automatic answer now.</summary>
        public bool Answer(uint sender, uint away, string told, DateTime now)
        {
            if (sender == away || IsAnswer(told)) return false;
            lock (m_Answered)
            {
                if (m_Missed.Count >= MAX) m_Missed.Clear();
                if (!m_Missed.TryGetValue(away, out var who)) m_Missed[away] = who = new HashSet<uint>();
                who.Add(sender);
                if (m_Answered.TryGetValue((away, sender), out var at) && now - at < COOLDOWN) return false;
                if (m_Answered.Count >= MAX) Prune(now);
                m_Answered[(away, sender)] = now;
                return true;
            }
        }

        /// <summary>Back from away: how many different people told away meanwhile (and forget them).</summary>
        public int Back(uint away)
        {
            lock (m_Answered)
            {
                int n = m_Missed.TryGetValue(away, out var who) ? who.Count : 0;
                m_Missed.Remove(away);
                var mine = new List<(uint, uint)>();
                foreach (var k in m_Answered.Keys) if (k.away == away) mine.Add(k);
                foreach (var k in mine) m_Answered.Remove(k);
                return n;
            }
        }

        /// <summary>A line (or a tell's text) that is an automatic answer.</summary>
        public static bool IsAnswer(string text) =>
            text != null && text.TrimStart().StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase);

        /// <summary>The answer the sender sees.</summary>
        public static string Line(string awayName, string message) =>
            $"{PREFIX} {(awayName ?? "").TrimStart('+')}: {(string.IsNullOrEmpty(message) ? DEFAULT_MESSAGE : message)}";

        /// <summary>A message as typed, made safe to keep and send</summary>
        public static string Clean(string typed)
        {
            if (string.IsNullOrWhiteSpace(typed)) return null;
            var sb = new StringBuilder(typed.Length);
            bool space = false;
            foreach (char c in typed)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            var s = sb.ToString();
            while (IsAnswer(s)) s = s.TrimStart().Substring(PREFIX.Length).TrimStart(' ', ':');
            if (s.Length > MAX_LENGTH) s = s.Substring(0, MAX_LENGTH).TrimEnd();
            return s.Length == 0 ? null : s;
        }

        void Prune(DateTime now)
        {
            var old = new List<(uint, uint)>();
            foreach (var kv in m_Answered) if (now - kv.Value >= COOLDOWN) old.Add(kv.Key);
            foreach (var k in old) m_Answered.Remove(k);
            if (m_Answered.Count >= MAX) m_Answered.Clear();
        }
    }
}
