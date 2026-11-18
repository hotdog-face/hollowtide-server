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
    ///               A NAMED site (kind 10+, one place made a dense battlefield): a player from the other side.
    ///   misadventure anywhere.
    /// </summary>
    public static class CorpseKillerRule
    {
        public const string PK_LINE = "a player killer", MISADVENTURE = "misadventure";
        public const int KIND_2017 = 0, KIND_DUNGEON = 1, KIND_OUTDOOR = 2, KIND_PK = 3, KIND_LAWN = 4, KIND_DAILY = 5,
                         KIND_DT = 6, KIND_TN = 7, KIND_HUB = 8, KIND_AYAN = 9,
                         KIND_NAMED = 10;   // and up: a NAMED site (gen.py NAMED)
        // generated corpses standing in one landblock at most (anti-farm): outdoors, indoors (dungeons are big), the Town
        // Network and the Facility Hub (one busy space each: littered, not cluttered); a named site has its own cap
        public const int LB_CAP_OUT = 16, LB_CAP_IN = 30, CAP_TN = 25, CAP_HUB = 15;

        /// <summary>A site kind whose killer is a creature spawning (or dying in 2017) at that very spot.</summary>
        public static bool MonsterSiteKind(int kind) => kind == KIND_2017 || kind == KIND_DUNGEON || kind == KIND_OUTDOOR || kind == KIND_DAILY;

        /// <summary>A place players fought each other (killer: a player, or the local creatures).</summary>
        public static bool PkKind(int kind) => kind == KIND_PK || kind == KIND_LAWN || kind == KIND_DT || kind == KIND_TN || kind == KIND_HUB || kind == KIND_AYAN || kind >= KIND_NAMED;

        /// <summary>Generated corpses one landblock may hold: a named site's own cap (namedCap &gt; 0), else by kind.</summary>
        public static int LandblockCap(int kind, bool indoor, int namedCap) =>
            namedCap > 0 ? namedCap : kind == KIND_TN ? CAP_TN : kind == KIND_HUB ? CAP_HUB : indoor ? LB_CAP_IN : LB_CAP_OUT;

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

        // ---------------------------------------------------------------- named sites: two sides that fought there
        // Owner 2026-11-18: the lawn of his old allegiance's mansion, its people and the PKs they fought (his retail
        // screenshots). Each corpse there is one side's, killed by someone from the OTHER side. The sides are real names,
        // so they live only in the server-only names file ("sites": {id: {"sides": [[...], [...]]}}).

        /// <summary>The side a name is on (0, 1, ...), or -1.</summary>
        public static int SideOf(string name, IList<IList<string>> sides)
        {
            if (string.IsNullOrEmpty(name) || sides == null) return -1;
            for (int i = 0; i < sides.Count; i++)
                if (sides[i] != null) foreach (var n in sides[i]) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Did someone from the other side kill this victim? (both on a side, different sides)</summary>
        public static bool OtherSide(string victim, string killer, IList<IList<string>> sides)
        {
            int v = SideOf(victim, sides), k = SideOf(killer, sides);
            return v >= 0 && k >= 0 && v != k;
        }

        /// <summary>A killer from the other side (ok: may that name be used, e.g. no character here has it); a victim on no
        /// side may be killed by either. Never the victim. null: nobody fits.</summary>
        public static string OtherSideKiller(string victim, IList<IList<string>> sides, Func<string, bool> ok, Random rng)
        {
            if (sides == null) return null;
            int v = SideOf(victim, sides);
            var pool = new List<string>();
            for (int i = 0; i < sides.Count; i++)
            {
                if (i == v || sides[i] == null) continue;
                foreach (var n in sides[i])
                    if (!string.IsNullOrEmpty(n) && !string.Equals(n, victim, StringComparison.OrdinalIgnoreCase) && (ok == null || ok(n))) pool.Add(n);
            }
            return pool.Count == 0 ? null : pool[rng.Next(pool.Count)];
        }

        /// <summary>The next victim at a named site: from the side with fewer lying there now (standing[i]), so both sides'
        /// dead lie on the field; the other side when that one has nobody free. null: nobody free on any side.</summary>
        public static string PickSideVictim(IList<IList<string>> sides, IList<int> standing, Func<string, bool> free, Random rng)
        {
            if (sides == null || sides.Count == 0) return null;
            var keys = new double[sides.Count];
            var order = new List<int>();
            for (int i = 0; i < sides.Count; i++) { order.Add(i); keys[i] = (standing != null && i < standing.Count ? standing[i] : 0) + rng.NextDouble() * 0.5; }
            order.Sort((a, b) => keys[a].CompareTo(keys[b]));        // fewer standing first, ties in random order
            foreach (var s in order)
            {
                var side = sides[s];
                if (side == null || side.Count == 0) continue;
                for (int t = 0; t < 20; t++) { var n = side[rng.Next(side.Count)]; if (free(n)) return n; }
                int start = rng.Next(side.Count);
                for (int k = 0; k < side.Count; k++) { var n = side[(start + k) % side.Count]; if (free(n)) return n; }
            }
            return null;
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
