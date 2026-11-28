using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>At most one mansion display is dressed per landblock tick (MansionFurniture.RefreshPaced).</summary>
    public sealed class DisplayPaceRule
    {
        readonly Dictionary<uint, double> m_Busy = new Dictionary<uint, double>();
        readonly object m_Lock = new object();
        readonly double m_StaleAfter;

        public DisplayPaceRule(double staleAfter) { m_StaleAfter = staleAfter; }

        /// <summary>True: this display may dress now on landblock key (the slot is taken).</summary>
        public bool TryBegin(uint key, double now)
        {
            lock (m_Lock)
            {
                if (m_Busy.TryGetValue(key, out var since) && now - since < m_StaleAfter) return false;
                m_Busy[key] = now;
                return true;
            }
        }

        /// <summary>The landblock's next tick has begun: its slot is free again.</summary>
        public void End(uint key)
        {
            lock (m_Lock) m_Busy.Remove(key);
        }
    }
}
