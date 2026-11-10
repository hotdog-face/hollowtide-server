using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// AT MOST ONE MANSION DISPLAY IS DRESSED PER LANDBLOCK TICK (MansionFurniture.RefreshPaced). On a
    /// mansion's load all its displays' 3 s retries came due together and dressed in one tick: 0x576B ticks
    /// of 0.8-2.4 s on live (2026-11-08..10), 170-370 ms a display. The first to dress takes the landblock's
    /// slot and queues End on the landblock, which runs at the start of its next tick; any other display
    /// that comes due before then goes back on the queue behind End. A slot never released (the landblock
    /// unloaded with End still queued) goes stale after <c>staleAfter</c> seconds.
    /// Pure (no ACE types), so tools/nettest compiles and checks it by path.
    /// </summary>
    public sealed class DisplayPaceRule
    {
        readonly Dictionary<uint, double> m_Busy = new Dictionary<uint, double>();
        readonly object m_Lock = new object();
        readonly double m_StaleAfter;

        public DisplayPaceRule(double staleAfter) { m_StaleAfter = staleAfter; }

        /// <summary>True: this display may dress now on landblock <paramref name="key"/> (the slot is taken).</summary>
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
