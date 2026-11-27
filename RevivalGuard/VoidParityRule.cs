using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevivalGuard
{
    /// <summary>
    /// VOID MAGIC PARITY WITH WAR MAGIC, the pure part (VoidParity.cs patches ACE; tools/nettest VoidParityCase compiles
    /// this file by path). Owner 2026-11-27: "id like to make void as good or formidable when compared to war magic";
    /// parity with war is the ceiling (docs/findings/void-parity-2026-11-27.md has the numbers behind every value here).
    ///
    /// The gap it closes: against monsters a war mage lands one Life Magic vulnerability (Fire Vulnerability Other VII,
    /// Inferno's Gift, x2.85; the Incantation x3.1; ace_world.spell stat_Mod_Val) and every war bolt after it is multiplied;
    /// retail has no nether vulnerability at all (no spell with stat_Mod_Key 166 ResistNether above 1), and no salvage
    /// gives nether rending. Void's own answer, its curses, only lowers damage resist rating by tick/8 (ACE
    /// GetNetherDotDamageRating, about x1.3 with all three up at level VII). Formula model and box bench: war does about
    /// 1.6x void's damage per second from spell level VI up, while void is ahead at I-III (vulns are weak there).
    ///
    /// The fix keeps void's identity: while one of void's own damage curses (Corrosion, Corruption, Destructive Curse) is on
    /// a monster, nether damage from players to that monster is multiplied by the curse level's factor below. It needs
    /// the curse cast and kept up, as war needs its vulnerability, and it never touches players (PvP is unchanged:
    /// void_pvp_modifier and the DoT rules stay ACE's).
    /// </summary>
    public static class VoidParityRule
    {
        /// <summary>Default per-level multipliers ("level:mult,..."; an unlisted level is 1.0). Set from the formula model
        /// (war DPS / void DPS with three curses up, docs/findings/void-parity-2026-11-27.md) and rounded DOWN so void
        /// stays at or under war: V 1.34 -> 1.30, VI 1.60 -> 1.50, VII 1.57 -> 1.50, VIII 1.40 -> 1.30. The box bench (VII
        /// and VIII) put 1.55/1.35 at 94-111% of war, so VI-VIII came down a step more.</summary>
        public const string DEFAULT_LEVELS = "5:1.30,6:1.50,7:1.50,8:1.30";

        /// <summary>The highest multiplier any table may give (a typo must not make void stronger than war's own best
        /// vulnerability, the Incantation's x3.1).</summary>
        public const double MAX_MULT = 3.1;

        /// <summary>Void's damage curses (the three nether DoT categories, ACE SpellCategory 636/637/638): spell id -> level.
        /// Ids from ace_world.spell / the DAT SpellTable, read 2026-11-27.</summary>
        public static readonly IReadOnlyDictionary<uint, int> CurseLevel = Build();

        static Dictionary<uint, int> Build()
        {
            var d = new Dictionary<uint, int>();
            for (int i = 0; i < 7; i++) { d[5387u + (uint)i] = i + 1; d[5395u + (uint)i] = i + 1; }   // Corrosion I-VII, Corruption I-VII
            d[5394] = 8; d[5402] = 8;                                                                 // Incantations of Corrosion, Corruption
            for (int i = 0; i < 6; i++) d[5339u + (uint)i] = i + 1;                                   // Destructive Curse I-VI
            d[5337] = 7; d[5338] = 8;                                                                 // Destructive Curse VII, Incantation
            return d;
        }

        /// <summary>"5:1.30,6:1.55" -> level -> multiplier (1..MAX_MULT); bad parts are skipped.</summary>
        public static Dictionary<int, double> ParseLevels(string table)
        {
            var d = new Dictionary<int, double>();
            foreach (var part in (table ?? DEFAULT_LEVELS).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                if (!int.TryParse(kv[0].Trim(), out var lvl) || lvl < 1 || lvl > 8) continue;
                if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) || double.IsNaN(m)) continue;
                d[lvl] = Math.Clamp(m, 1.0, MAX_MULT);
            }
            return d;
        }

        /// <summary>The multiplier for the strongest curse level on the target (0 = none: 1.0).</summary>
        public static double Mult(IReadOnlyDictionary<int, double> table, int curseLevel)
        {
            if (curseLevel <= 0 || table == null) return 1.0;
            return table.TryGetValue(Math.Min(curseLevel, 8), out var m) ? m : 1.0;
        }

        /// <summary>The strongest of the given enchantment spell ids that is one of void's damage curses, or 0.</summary>
        public static int StrongestCurse(IEnumerable<int> spellIds)
        {
            int best = 0;
            if (spellIds == null) return 0;
            foreach (var id in spellIds)
                if (id > 0 && CurseLevel.TryGetValue((uint)id, out var l) && l > best) best = l;
            return best;
        }

        /// <summary>Does the multiplier apply? Nether damage, a monster (not a player) taking it, a player dealing it
        /// (directly, or by the bolt or curse they cast).</summary>
        public static bool Applies(bool on, bool nether, bool targetIsPlayer, bool sourceIsPlayer) =>
            on && nether && !targetIsPlayer && sourceIsPlayer;

        // ---- the bench's spell lists (ace_world.spell names, read 2026-11-27) ----

        /// <summary>Level I..VIII (index 0..7).</summary>
        public static readonly uint[] FlameBolt = { 27, 81, 82, 83, 84, 85, 2128, 4439 };            // ..., Ilservian's Flame, Incantation
        public static readonly uint[] FireVulnOther = { 21, 1104, 1105, 1106, 1107, 1108, 2170, 4481 }; // ..., Inferno's Gift, Incantation
        public static readonly uint[] ShockWave = { 64, 65, 66, 67, 68, 69, 2144, 4455 };            // ..., Crushing Shame, Incantation
        public static readonly uint[] BludgeonVulnOther = { 1048, 1049, 1050, 1051, 1052, 1053, 2166, 4477 }; // ..., Tusker's Gift, Incantation
        public static readonly uint[] NetherBolt = { 5349, 5350, 5351, 5352, 5353, 5354, 5355, 5356 };
        public static readonly uint[] Corrosion = { 5387, 5388, 5389, 5390, 5391, 5392, 5393, 5394 };
        public static readonly uint[] Corruption = { 5395, 5396, 5397, 5398, 5399, 5400, 5401, 5402 };
        public static readonly uint[] DestructiveCurse = { 5339, 5340, 5341, 5342, 5343, 5344, 5337, 5338 };

        public static uint At(uint[] list, int level) => list[Math.Clamp(level, 1, 8) - 1];

        /// <summary>What the bench casts next. War: its element's vulnerability until it lands, then bolts. Void: the curses
        /// in order (Corrosion, Corruption, Destructive Curse) whenever one is missing, else Nether Bolt. "bolts" rotations
        /// skip the vulnerability / curses (the plain bolt baseline).</summary>
        public static uint NextSpell(string school, string element, int level, bool fullRotation, bool hasVuln,
            bool hasCorrosion, bool hasCorruption, bool hasCurse)
        {
            bool bludg = string.Equals(element, "bludgeon", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(school, "war", StringComparison.OrdinalIgnoreCase))
            {
                if (fullRotation && !hasVuln) return At(bludg ? BludgeonVulnOther : FireVulnOther, level);
                return At(bludg ? ShockWave : FlameBolt, level);
            }
            if (fullRotation)
            {
                if (!hasCorrosion) return At(Corrosion, level);
                if (!hasCorruption) return At(Corruption, level);
                if (!hasCurse) return At(DestructiveCurse, level);
            }
            return At(NetherBolt, level);
        }
    }
}
