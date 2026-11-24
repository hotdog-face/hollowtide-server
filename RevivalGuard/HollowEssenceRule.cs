using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// HOLLOWTIDE ESSENCES IN THE NORMAL ESSENCE LOOT, the pure part (HollowEssences.cs patches ACE; tools/nettest
    /// HollowEssenceCase compiles this file by path). Owner 2026-11-24: "add them to loot tables like similar items of
    /// that type drop ... knath dont only drop from knath kills"; "why only maronette? we have lots of other creatures".
    /// ACE's PetDeviceWcids.Roll picks a pet level for the tier and one of retail's 36 families; then RATE of those rolls
    /// whose level has Hollowtide essences become one of ours at that same level, picked evenly over every Hollowtide
    /// family of that level. Total essences and the levels per tier do not change; only the family does.
    /// RATE stays 0.10: every level has at least 5 of ours, so each of ours is at most 2.0% of that level's essences,
    /// under any one retail family (0.90 / 36 = 2.5%). 15% would need 7 at every level to stay under (0.85 / 36 = 2.36%).
    /// Ids: 2026-11-24-summon-1-marionette.sql (919100-919105), 2026-11-25-summon-2-families.sql (919106-919181,
    /// tools/gpubox-ace/gen_summon_families.py prints this table).
    /// </summary>
    public static class HollowEssenceRule
    {
        /// <summary>About 1 in 10 essence rolls at a covered level (docs/proposals/summoning-plan.md "How they're given").</summary>
        public const double RATE = 0.10;

        public const uint MARIONETTE_50 = 919100, MARIONETTE_150 = 919102, MARIONETTE_180 = 919104;

        /// <summary>Every Hollowtide essence: wcid -> (family, pet level).</summary>
        public static readonly IReadOnlyDictionary<uint, (string family, int level)> Essences = new Dictionary<uint, (string, int)>
        {
            { MARIONETTE_50, ("Marionette", 50) },
            { MARIONETTE_150, ("Marionette", 150) },
            { MARIONETTE_180, ("Marionette", 180) },
            { 919106, ("Drudge", 50) },
            { 919108, ("Drudge", 80) },
            { 919110, ("Drudge", 100) },
            { 919112, ("Mosswart", 50) },
            { 919114, ("Mosswart", 80) },
            { 919116, ("Mosswart", 100) },
            { 919118, ("Reedshark", 50) },
            { 919120, ("Reedshark", 80) },
            { 919122, ("Mu-miyah", 50) },
            { 919124, ("Mu-miyah", 80) },
            { 919126, ("Mu-miyah", 100) },
            { 919128, ("Banderling", 80) },
            { 919130, ("Banderling", 100) },
            { 919132, ("Banderling", 125) },
            { 919134, ("Crystal", 80) },
            { 919136, ("Crystal", 100) },
            { 919138, ("Tusker", 100) },
            { 919140, ("Tusker", 125) },
            { 919142, ("Tusker", 150) },
            { 919144, ("Fiun", 100) },
            { 919146, ("Fiun", 125) },
            { 919148, ("Ursuin", 125) },
            { 919150, ("Ursuin", 150) },
            { 919152, ("Revenant", 125) },
            { 919154, ("Revenant", 150) },
            { 919156, ("Revenant", 180) },
            { 919158, ("Revenant", 200) },
            { 919160, ("Olthoi", 150) },
            { 919162, ("Olthoi", 180) },
            { 919164, ("Olthoi", 200) },
            { 919166, ("Lugian", 150) },
            { 919168, ("Lugian", 180) },
            { 919170, ("Lugian", 200) },
            { 919172, ("Shadow", 180) },
            { 919174, ("Shadow", 200) },
            { 919176, ("Virindi", 150) },
            { 919178, ("Virindi", 180) },
            { 919180, ("Virindi", 200) },
        };

        /// <summary>Pet level -> our essences at that level (one picked evenly). Levels missing here stay all retail.</summary>
        public static readonly IReadOnlyDictionary<int, uint[]> ByLevel = Essences
            .GroupBy(kv => kv.Value.level)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).OrderBy(w => w).ToArray());

        /// <summary>Every Hollowtide essence wcid (for ACE's PetDeviceWcids set, so a mutated copy rolls ratings).</summary>
        public static IEnumerable<uint> All => Essences.Keys;

        public static bool IsOurs(uint wcid) => Essences.ContainsKey(wcid);

        public static int LevelOf(uint wcid) => Essences.TryGetValue(wcid, out var e) ? e.level : 0;

        public static string FamilyOf(uint wcid) => Essences.TryGetValue(wcid, out var e) ? e.family : null;

        /// <summary>
        /// What one ACE essence roll at <paramref name="petLevel"/> becomes: 0 keeps ACE's pick, otherwise one of our
        /// wcids of that same level. <paramref name="swap01"/> and <paramref name="pick01"/> are uniform in [0, 1).
        /// </summary>
        public static uint Pick(int petLevel, double swap01, double pick01, double rate = RATE)
        {
            if (!(swap01 < rate)) return 0;                      // NaN or above the rate: retail's essence stands
            if (!ByLevel.TryGetValue(petLevel, out var ours) || ours.Length == 0) return 0;
            if (double.IsNaN(pick01)) pick01 = 0;
            int i = (int)(pick01 * ours.Length);
            if (i < 0) i = 0;
            if (i >= ours.Length) i = ours.Length - 1;
            return ours[i];
        }
    }
}
