using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// The target dummy's build words, pure (no ACE types; tools/nettest compiles it by path, TargetDummyCase.cs). Owner
    /// 2026-11-27, balance review "measure damage": every dummy run goes to the event log by build (dummy_dps), so
    /// tools/reports/kill_speed_weekly.py can table damage per second per build and balance calls have numbers.
    ///
    ///   A hit's build is the skill that made it (ACE's Skill names): melee (heavy, light, finesse and the retired weapon
    ///   skills), 2h, missile, war, void, life, summon (a pet's hit, counted for its owner), other. The run's build is the
    ///   one that did the most damage; the template is the player's specialized combat skills (trained when none).
    /// </summary>
    public static class TargetDummyRule
    {
        public const string MELEE = "melee", TWO_HANDED = "2h", MISSILE = "missile", WAR = "war", VOID = "void", LIFE = "life",
            SUMMON = "summon", OTHER = "other";

        static readonly Dictionary<string, string> s_Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HeavyWeapons"] = MELEE, ["LightWeapons"] = MELEE, ["FinesseWeapons"] = MELEE, ["DualWield"] = MELEE,
            ["Axe"] = MELEE, ["Dagger"] = MELEE, ["Mace"] = MELEE, ["Spear"] = MELEE, ["Staff"] = MELEE, ["Sword"] = MELEE,
            ["UnarmedCombat"] = MELEE,
            ["TwoHandedCombat"] = TWO_HANDED,
            ["MissileWeapons"] = MISSILE, ["Bow"] = MISSILE, ["Crossbow"] = MISSILE, ["ThrownWeapon"] = MISSILE,
            ["WarMagic"] = WAR, ["VoidMagic"] = VOID, ["LifeMagic"] = LIFE, ["Summoning"] = SUMMON,
        };

        /// <summary>The build a hit counts for, from the ACE skill name that made it (a pet's hit: summon).</summary>
        public static string KindOf(string skill, bool pet = false) =>
            pet ? SUMMON : (skill != null && s_Kinds.TryGetValue(skill, out var k) ? k : OTHER);

        /// <summary>Is this ACE skill one of the combat skills a template is made of?</summary>
        public static bool IsCombat(string skill) => skill != null && s_Kinds.ContainsKey(skill);

        /// <summary>The run's build: the kind with the most damage (ties: the order of the constants above), OTHER if none.</summary>
        public static string Dominant(IDictionary<string, long> bySource)
        {
            if (bySource == null || bySource.Count == 0) return OTHER;
            var order = new[] { MELEE, TWO_HANDED, MISSILE, WAR, VOID, LIFE, SUMMON, OTHER };
            return bySource.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
                .ThenBy(kv => Array.IndexOf(order, kv.Key) < 0 ? 99 : Array.IndexOf(order, kv.Key)).Select(kv => kv.Key).FirstOrDefault() ?? OTHER;
        }

        /// <summary>The template: the distinct kinds of the specialized combat skills, or of the trained ones when none is
        /// specialized, in a fixed order joined with "+" ("2h+war"); "none" with neither.</summary>
        public static string Template(IEnumerable<string> specialized, IEnumerable<string> trained)
        {
            var order = new[] { MELEE, TWO_HANDED, MISSILE, WAR, VOID, LIFE, SUMMON };
            string Of(IEnumerable<string> skills)
            {
                var kinds = (skills ?? Enumerable.Empty<string>()).Where(IsCombat).Select(s => KindOf(s)).Distinct().ToList();
                return kinds.Count == 0 ? null : string.Join("+", order.Where(kinds.Contains));
            }
            return Of(specialized) ?? Of(trained) ?? "none";
        }

        /// <summary>Damage per second over the run, 0 under a second (a single blow has no rate).</summary>
        public static double Dps(long total, double seconds) => seconds >= 1 ? total / seconds : 0;
    }
}
