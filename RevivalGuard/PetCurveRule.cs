using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>Summoning curve, the pure part (PetCurve.cs patches ACE; tools/nettest PetCurveCase compiles this file by path).</summary>
    public static class PetCurveRule
    {
        public const string P_ON = "rg_pet_curve";

        /// <summary>AC wiki "Summoned Essence" health: level 180 pets 1020, level 200 pets 1370.</summary>
        public const double RETAIL_HEALTH_180 = 1020, RETAIL_HEALTH_200 = 1370;

        /// <summary>ACE's level medians over the distinct retail mastery damage lines (nettest petcurve checks them).</summary>
        public static readonly IReadOnlyDictionary<int, double> AceMedian = new Dictionary<int, double>
        {
            { 50, 73.6 }, { 80, 133.2 }, { 100, 172.9 }, { 125, 244.2 }, { 150, 329.6 }, { 180, 396.8 }, { 200, 1350.3 },
        };

        /// <summary>Damage per second per pet level with the curve on.</summary>
        public static readonly IReadOnlyDictionary<int, double> Target = new Dictionary<int, double>
        {
            { 50, 73.6 }, { 80, 133.2 }, { 100, 172.9 }, { 125, 244.2 }, { 150, 329.6 }, { 180, 396.8 },
            { 200, Math.Round(396.8 * RETAIL_HEALTH_200 / RETAIL_HEALTH_180, 1) },
        };

        /// <summary>Golem motion table: ACE's golems (vendor, no mastery) are left as they are.</summary>
        public const uint GOLEM_MT = 0x09000081;

        public const double MIN_MULT = 0.25, MAX_MULT = 2.5;

        /// <summary>(motion table, combat table) -> mean animation seconds at speed 1, mean strikes per swing.</summary>
        public static readonly IReadOnlyDictionary<(uint mt, uint cmt), (double anim1, double strikes)> Swing =
            new Dictionary<(uint, uint), (double, double)>
        {
            // retail pets
            { (0x09000081, 0x30000000), (1.7111, 1) }, { (0x09000081, 0x30000008), (1.7019, 1) },
            { (0x09000001, 0x30000000), (1.1134, 1) }, { (0x0900008F, 0x30000000), (1.4167, 1) },
            { (0x09000032, 0x30000000), (2.4339, 1) }, { (0x09000031, 0x30000000), (1.4444, 1) },
            { (0x09000192, 0x30000000), (1.3222, 1) }, { (0x0900009A, 0x30000000), (1.6778, 1) },
            { (0x09000166, 0x30000000), (1.8788, 1) }, { (0x09000167, 0x30000000), (0.5000, 1) },
            { (0x090001CB, 0x30000000), (1.3389, 1) }, { (0x09000168, 0x30000000), (0.5000, 3) },
            // Hollowtide pets
            { (0x0900009B, 0x3000002A), (2.2778, 1) }, { (0x09000008, 0x30000004), (1.1333, 1) },
            { (0x09000009, 0x30000005), (1.1444, 1) }, { (0x0900001A, 0x3000000A), (0.8000, 1) },
            { (0x09000025, 0x30000000), (1.4167, 1) }, { (0x09000007, 0x30000002), (1.1444, 1) },
            { (0x09000098, 0x30000027), (2.6667, 1) }, { (0x090000A3, 0x30000027), (2.6667, 1) },
            { (0x0900000C, 0x3000000B), (1.6667, 1) }, { (0x0900017E, 0x30000042), (2.2222, 1) },
            { (0x0900009C, 0x30000029), (1.3531, 1) }, { (0x09000017, 0x30000000), (1.4167, 1) },
            { (0x09000002, 0x30000001), (1.5556, 1) }, { (0x09000006, 0x30000003), (1.7667, 1) },
            { (0x09000093, 0x30000028), (1.4167, 1) }, { (0x09000028, 0x3000000D), (1.9222, 1) },
        };

        /// <summary>CombatBodyPart.Breath: only special attacks use it (ACE GetAttackPart).</summary>
        public const int BREATH = 22;

        /// <summary>Mean strike before mods over the parts ACE picks from (DVal &gt; 0, not Breath).</summary>
        public static double HitMean(IEnumerable<(int key, int dval, double dvar)> parts)
        {
            var use = parts.Where(p => p.dval > 0 && p.key != BREATH).ToList();
            return use.Count == 0 ? 0 : use.Average(p => p.dval * (1 - p.dvar / 2));
        }

        /// <summary>ACE Creature.GetAnimSpeed with no weapon.</summary>
        public static double AnimSpeed(int quickness)
        {
            double div = 1.0 - quickness / 300.0;
            return div <= 0 ? 2.0 : Math.Clamp(1.0 / div, 0.5, 2.0);
        }

        /// <summary>ACE SkillFormula.GetAttributeMod (melee).</summary>
        public static double AttrMod(int strength) => Math.Max(1.0 + (strength - 55) * 0.011, 1.0);

        /// <summary>Damage per second before the target's mitigation; NaN when the swing is not in SWING.</summary>
        public static double Dps(double hitMean, int strength, int quickness, double? powerup, uint mt, uint cmt)
        {
            if (!Swing.TryGetValue((mt, cmt), out var s) || hitMean <= 0) return double.NaN;
            double cycle = s.anim1 / AnimSpeed(quickness) + (powerup ?? 1.0) / 2;
            return s.strikes * hitMean * AttrMod(strength) / cycle;
        }

        /// <summary>The factor on every hit of this pet with the curve on. 1 = ACE's own damage.</summary>
        public static double Multiplier(int level, double hitMean, int strength, int quickness, double? powerup, uint mt, uint cmt)
        {
            if (mt == GOLEM_MT || !Target.TryGetValue(level, out var target)) return 1.0;
            double dps = Dps(hitMean, strength, quickness, powerup, mt, cmt);
            double m = double.IsNaN(dps) || dps <= 0 ? target / AceMedian[level] : target / dps;   // unknown swing: the level's curve only
            if (double.IsNaN(m) || double.IsInfinity(m)) return 1.0;
            return Math.Clamp(m, MIN_MULT, MAX_MULT);
        }
    }
}
