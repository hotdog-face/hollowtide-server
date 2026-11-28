using System.Collections.Generic;
using System.Linq;
using ACE.DatLoader;

namespace RevivalGuard
{
    /// <summary>The one list of spells this shard adds or newly makes obtainable.</summary>
    static class ShardSpells
    {
        internal const uint OURS_FROM = 60000;

        internal static readonly (uint lo, uint hi, string what)[] UNLOCKED =
        {
            (2366, 2366, "Bovine Intervention"),
            (4267, 4267, "Cow"), (4269, 4269, "Present"), (4270, 4270, "Table"),   // the Jester's gag bolts (JokeSpells)
            (3902, 3902, "Ring around the Rabbit"),
            (2815, 2926, "fireworks, OUT and UP"),
            (4375, 4390, "Incantation fireworks"),
            (2927, 2927, "Old School Fireworks"),   // a joke scroll (gen_joke_spells.py)
        };

        /// <summary>Every id above that the loaded spell table holds, ours first.</summary>
        internal static List<uint> All()
        {
            var table = DatManager.PortalDat.SpellTable.Spells;
            var ids = table.Keys.Where(k => k >= OURS_FROM).OrderBy(k => k).ToList();
            foreach (var (lo, hi, _) in UNLOCKED)
                for (var id = lo; id <= hi; id++)
                    if (table.ContainsKey(id) && !ids.Contains(id)) ids.Add(id);
            return ids;
        }
    }
}
