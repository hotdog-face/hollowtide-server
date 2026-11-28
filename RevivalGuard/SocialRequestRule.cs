using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>Requests that pop up on another player's screen</summary>
    public sealed class SocialRequestRule
    {
        public const uint SwearAllegiance = 0x001D, FellowshipRecruit = 0x00A5, OpenTrade = 0x01F6;
        public const int PAIR_MAX = 2;
        public static readonly TimeSpan PAIR_WINDOW = TimeSpan.FromMinutes(2);
        public const int ANY_MAX = 6;
        public static readonly TimeSpan ANY_WINDOW = TimeSpan.FromMinutes(1);
        const int MAX = 4096;

        /// <summary>The game actions this rule judges; their payload starts with the target's guid.</summary>
        public static bool Covers(uint opcode) => opcode == SwearAllegiance || opcode == FellowshipRecruit || opcode == OpenTrade;

        public static string What(uint opcode) =>
            opcode == OpenTrade ? "a trade" : opcode == FellowshipRecruit ? "a fellowship invite" : "an allegiance request";

        readonly Dictionary<uint, List<(DateTime at, uint op, uint to)>> m_Sent = new Dictionary<uint, List<(DateTime, uint, uint)>>();

        /// <summary>Null when from may send this request now (it is counted), else the sender's notice.</summary>
        public string Check(uint from, uint opcode, uint to, DateTime now)
        {
            lock (m_Sent)
            {
                if (!m_Sent.TryGetValue(from, out var list))
                {
                    if (m_Sent.Count >= MAX) Prune(now);
                    m_Sent[from] = list = new List<(DateTime, uint, uint)>();
                }
                var keep = PAIR_WINDOW > ANY_WINDOW ? PAIR_WINDOW : ANY_WINDOW;
                list.RemoveAll(s => now - s.at > keep);
                int pair = 0, any = 0;
                foreach (var s in list)
                {
                    if (now - s.at <= ANY_WINDOW) any++;
                    if (s.op == opcode && s.to == to && now - s.at <= PAIR_WINDOW) pair++;
                }
                if (pair >= PAIR_MAX) return $"You have already asked them for {What(opcode)}. Give them a moment to answer.";
                if (any >= ANY_MAX) return "You are sending requests too quickly. Please wait a moment.";
                list.Add((now, opcode, to));
                return null;
            }
        }

        void Prune(DateTime now)
        {
            var gone = new List<uint>();
            foreach (var kv in m_Sent)
                if (kv.Value.Count == 0 || now - kv.Value[kv.Value.Count - 1].at > PAIR_WINDOW) gone.Add(kv.Key);
            foreach (var k in gone) m_Sent.Remove(k);
        }
    }
}
