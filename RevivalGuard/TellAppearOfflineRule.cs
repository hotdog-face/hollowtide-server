using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>The pure part of TellAppearOffline.cs (no ACE types: tools/nettest compiles it by path, TellOfflineCase.cs).</summary>
    public sealed class TellAppearOfflineRule
    {
        public static readonly TimeSpan REPLY_WINDOW = TimeSpan.FromMinutes(30);
        const int MAX = 4096;

        readonly Dictionary<(uint from, uint to), DateTime> m_Told = new Dictionary<(uint, uint), DateTime>();

        /// <summary>A tell from from reached to.</summary>
        public void Told(uint from, uint to, DateTime now)
        {
            lock (m_Told)
            {
                if (m_Told.Count >= MAX) Prune(now);
                m_Told[(from, to)] = now;
            }
        }

        /// <summary>Whether a tell from sender to an online target gets the offline answer.</summary>
        public bool Hide(uint sender, uint target, bool targetAppearsOffline, bool senderIsStaff, DateTime now)
        {
            if (!targetAppearsOffline || senderIsStaff || sender == target) return false;
            lock (m_Told)
                return !(m_Told.TryGetValue((target, sender), out var at) && now - at <= REPLY_WINDOW);
        }

        void Prune(DateTime now)
        {
            var old = new List<(uint, uint)>();
            foreach (var kv in m_Told) if (now - kv.Value > REPLY_WINDOW) old.Add(kv.Key);
            foreach (var k in old) m_Told.Remove(k);
            if (m_Told.Count >= MAX) m_Told.Clear();
        }
    }
}
