using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>The pure part of AllegianceOrphans (no ACE types; tools/nettest compiles it by path).</summary>
    public static class AllegianceOrphanRule
    {
        /// <summary>The players whose patron is set but is not a player any more, in the order given.</summary>
        public static List<uint> Orphans(IEnumerable<(uint guid, uint? patron)> players, Func<uint, bool> exists)
        {
            var list = new List<uint>();
            foreach (var (g, p) in players)
                if (p.HasValue && p.Value != 0 && !exists(p.Value)) list.Add(g);
            return list;
        }

        /// <summary>Everyone under root (vassals, their vassals, ...), not the root itself.</summary>
        public static List<uint> Descendants(uint root, IEnumerable<(uint guid, uint? patron)> players)
        {
            var children = new Dictionary<uint, List<uint>>();
            foreach (var (g, p) in players)
            {
                if (!p.HasValue || p.Value == 0 || p.Value == g) continue;
                if (!children.TryGetValue(p.Value, out var l)) children[p.Value] = l = new List<uint>();
                l.Add(g);
            }
            var seen = new HashSet<uint> { root };
            var order = new List<uint>();
            var queue = new Queue<uint>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                uint at = queue.Dequeue();
                if (!children.TryGetValue(at, out var kids)) continue;
                foreach (var k in kids)
                    if (seen.Add(k)) { order.Add(k); queue.Enqueue(k); }
            }
            return order;
        }
    }
}
