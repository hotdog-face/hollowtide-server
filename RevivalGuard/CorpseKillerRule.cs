using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// CORPSE CAPSULES: WHO COULD HAVE KILLED IT HERE (the pure half; tools/nettest compiles this file by path). Owner
    /// bug 261117-0025 at the Facility Hub: "these two corpses say they were killed by monsters that couldnt have killed
    /// them here" (an Olthoi Grub and a Veteran Reedshark: gen.py gave hub corpses any low-level dungeon creature).
    /// A corpse's "Killed by" must fit where it lies:
    ///   a creature  only one that spawns in that landblock. The site table is the proof: a 2017, dungeon, outdoor or
    ///               daily site's killer is the creature that spawns (or died in the 2017 captures) right there, so a
    ///               landblock's creatures are its sites' killers. The Facility Hub has none (only its Wardens, who
    ///               cannot be attacked), so no creature there.
    ///   a player    (the PK line or a real Darktide PK) only where PK is possible: PK spots, lawns, Darktide places, the
    ///               Town Network, the Facility Hub, Ayan Baqur and the open world outdoors; dungeons get their own
    ///               creatures. Never a victim under the newcomer shield (newcomer.shield_level), who is not yet a PK.
    ///   misadventure anywhere.
    /// </summary>
    public static class CorpseKillerRule
    {
        public const string PK_LINE = "a player killer", MISADVENTURE = "misadventure";
        public const int KIND_2017 = 0, KIND_DUNGEON = 1, KIND_OUTDOOR = 2, KIND_PK = 3, KIND_LAWN = 4, KIND_DAILY = 5,
                         KIND_DT = 6, KIND_TN = 7, KIND_HUB = 8, KIND_AYAN = 9;

        /// <summary>A site kind whose killer is a creature spawning (or dying in 2017) at that very spot.</summary>
        public static bool MonsterSiteKind(int kind) => kind == KIND_2017 || kind == KIND_DUNGEON || kind == KIND_OUTDOOR || kind == KIND_DAILY;

        /// <summary>A place players fought each other (killer: a player, or the local creatures).</summary>
        public static bool PkKind(int kind) => kind == KIND_PK || kind == KIND_LAWN || kind == KIND_DT || kind == KIND_TN || kind == KIND_HUB || kind == KIND_AYAN;

        public static bool Indoor(uint cell) => (cell & 0xFFFF) >= 0x100;

        /// <summary>Could a player have killed this corpse where it lies? kind -1: no site (the 11 real ones, staff spawns).</summary>
        public static bool PkPossible(int kind, uint cell, int level, int shieldLevel) =>
            level >= shieldLevel && (PkKind(kind) || !Indoor(cell));

        /// <summary>A Facility Hub victim is at least the shield level: the hub has no creatures, so only a PK killed there.</summary>
        public static int SiteLevel(int kind, int level, int shieldLevel) => kind == KIND_HUB ? Math.Max(level, shieldLevel) : level;

        /// <summary>Each landblock's own creatures, from the site table (see the summary).</summary>
        public static Dictionary<ushort, string[]> LocalMonsters(IEnumerable<(uint cell, int kind, string killer)> sites)
        {
            var sets = new Dictionary<ushort, HashSet<string>>();
            foreach (var (cell, kind, killer) in sites)
            {
                if (!MonsterSiteKind(kind) || string.IsNullOrEmpty(killer) || killer == PK_LINE || killer == MISADVENTURE) continue;
                var lb = (ushort)(cell >> 16);
                if (!sets.TryGetValue(lb, out var s)) sets[lb] = s = new HashSet<string>(StringComparer.Ordinal);
                s.Add(killer);
            }
            var d = new Dictionary<ushort, string[]>();
            foreach (var kv in sets) { var a = new string[kv.Value.Count]; kv.Value.CopyTo(a); Array.Sort(a, StringComparer.Ordinal); d[kv.Key] = a; }
            return d;
        }

        /// <summary>Does this killer fit where the corpse lies? monsters: every creature name the data knows; local: the
        /// landblock's own (LocalMonsters). Anything else is a player.</summary>
        public static bool Plausible(string killer, int kind, uint cell, int level, int shieldLevel,
                                     ICollection<string> local, ICollection<string> monsters)
        {
            if (string.IsNullOrEmpty(killer) || killer == MISADVENTURE) return true;
            if (killer != PK_LINE && monsters != null && monsters.Contains(killer)) return local != null && local.Contains(killer);
            return PkPossible(kind, cell, level, shieldLevel);
        }

        /// <summary>Owner 2026-11-17: "it needs real darktide names at all cost." The victim: a free Darktide person first
        /// (random tries, then a scan from a random start, so the pool is used up before anything else), another real
        /// person only when no Darktide one is free (fellBack). -1: nobody free.</summary>
        public static int PickVictim(IList<int> darktide, IList<int> others, Func<int, bool> free, Random rng, out bool fellBack)
        {
            fellBack = false;
            int i = PickFree(darktide, free, rng);
            if (i >= 0) return i;
            fellBack = true;
            return PickFree(others, free, rng);
        }

        static int PickFree(IList<int> pool, Func<int, bool> free, Random rng)
        {
            if (pool == null || pool.Count == 0) return -1;
            for (int t = 0; t < 40; t++) { int i = pool[rng.Next(pool.Count)]; if (free(i)) return i; }
            int start = rng.Next(pool.Count);
            for (int k = 0; k < pool.Count; k++) { int i = pool[(start + k) % pool.Count]; if (free(i)) return i; }
            return -1;
        }

        /// <summary>A killer that fits: the preferred one if local, else a local creature, else (where PK is possible) a
        /// player (pkName, or the PK line), else misadventure.</summary>
        public static string Replacement(string preferred, IList<string> local, bool pkPossible, Func<string> pkName, Random rng)
        {
            if (local != null && local.Count > 0)
                return preferred != null && local.Contains(preferred) ? preferred : local[rng.Next(local.Count)];
            if (pkPossible) return pkName?.Invoke() ?? PK_LINE;
            return MISADVENTURE;
        }
    }
}
