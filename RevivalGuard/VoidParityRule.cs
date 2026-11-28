using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevivalGuard
{
    /// <summary>Void magic parity with war magic, the pure part.</summary>
    public static class VoidParityRule
    {
        /// <summary>Default per-level multipliers ("level:mult,..."; an unlisted level is 1.0).</summary>
        public const string DEFAULT_LEVELS = "5:1.30,6:1.50,7:1.50,8:1.30";

        public const double MAX_MULT = 3.1;

        /// <summary>Void's damage curses (the three nether DoT categories, ACE SpellCategory 636/637/638): spell id -> level.</summary>
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
                if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) || double.IsNaN(m) || double.IsInfinity(m)) continue;
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

        /// <summary>Does the multiplier apply?</summary>
        public static bool Applies(bool on, bool nether, bool targetIsPlayer, bool sourceIsPlayer) =>
            on && nether && !targetIsPlayer && sourceIsPlayer;

        // ---- the bench's spell lists ----

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

        /// <summary>What the bench casts next.</summary>
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
