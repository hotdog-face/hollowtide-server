using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// WHERE THE UNOBTAINABLE THINGS COME FROM (owner 2026-11-14). Pure, so tools/nettest checks it by path; the patches
    /// are in JokeScrollRewards.cs, FestivalCostumePrizes.cs and VoidScrollGate.cs. No em dashes (player text).
    ///   1. Joke spell scrolls drop at festivals (docs/JOKE-SCROLLS-FESTIVALS.md): the festival table and its rates.
    ///   2. The approved costume casters are festival-goal prizes, one with each year's goal trophy (masks are sold by
    ///      the seasonal Costumers: world data, tools/gpubox-ace/costume-items-in-season.sql).
    ///   2b. The same casters also drop, rarely, from level 20+ creatures all year (CostumeCasterDrops.cs, own switch).
    ///   3. Corrosion VII and Corruption VII scrolls are sold by Riftwatcher Ysanne only to a Void Magic trainee.
    /// </summary>
    public static class SeasonalSourcesRule
    {
        // ---- 1. joke scrolls -------------------------------------------------------------------------------------
        public const uint COW = 901661, PRESENT = 901662, TABLE = 901663, BOVINE = 901664, OLDSCHOOL = 901665;
        public static readonly uint[] BOOM = { 901666, 901667, 901668, 901669, 901670, 901671, 901672, 901673 };   // 8 colours
        public const uint MAD_COW = 23623;   // cowmad, the April Fools' Mad Cow (eventmadcowgen 23631)

        public sealed class JokeDrop
        {
            public string Festival;   // SeasonalCalendar key
            public int OneIn;         // 1 kill in N
            public uint[] Pool;       // one is picked
            public uint Only;         // only this creature, 0 = any
        }

        /// <summary>The design doc's table, in order: the first running festival a kill qualifies for rolls, once.</summary>
        public static readonly JokeDrop[] JokeDrops =
        {
            new JokeDrop { Festival = "festivalseason", OneIn = 1000, Pool = new[] { COW } },                 // the harvest: a cow
            new JokeDrop { Festival = "aprilfools", OneIn = 12, Pool = new[] { BOVINE, COW }, Only = MAD_COW }, // raining cows
            new JokeDrop { Festival = "frostfell", OneIn = 1000, Pool = new[] { PRESENT } },                  // gift season
            new JokeDrop { Festival = "wedding", OneIn = 500, Pool = new[] { TABLE, OLDSCHOOL } },            // the reception
            new JokeDrop { Festival = "anniversary", OneIn = 200, Pool = BOOM.Append(OLDSCHOOL).ToArray() },  // fireworks week
            new JokeDrop { Festival = "newyearsday", OneIn = 200, Pool = BOOM },                              // the new year
        };

        /// <summary>The drop a kill of this creature rolls for, or null: the first running festival it qualifies for.</summary>
        public static JokeDrop JokeDropFor(uint creatureWcid, Func<string, bool> festivalRunning)
        {
            foreach (var d in JokeDrops)
            {
                if (d.Only != 0 && creatureWcid != d.Only) continue;
                if (festivalRunning(d.Festival)) return d;
            }
            return null;
        }

        /// <summary>The chance of one kill, rg_joke_scroll_rate times 1 in N, never above 1.</summary>
        public static double JokeChance(JokeDrop d, double rate) => d == null ? 0 : Math.Min(1.0, Math.Max(0.0, rate) / d.OneIn);

        // ---- 2. costume casters as festival-goal prizes ----------------------------------------------------------
        // The 24 approved costume items (gen_costume_prototypes.py APPROVED + APPROVED_2): 11 masks go on the Costumers'
        // shelves (SQL); the 13 casters and joke weapons are prizes here, beside each goal's year trophy.
        public const uint CABBAGE_ORB = 908022, DRUMSTICK_WAND = 908024, CARROT_WAND = 908027, CASTING_PIE = 908028,
            CASTING_CAKE = 908029, CASTING_APPLE = 908030, CASTING_COOKIE = 908031, CASTING_STEIN = 908035,
            CARROT_DAGGER = 908036, ROYAL_LADLE = 908037, HOLIDAY_PRESENT = 908038, ENERGY_CRYSTAL = 908039, LOAF_SHOOTER = 908040;

        /// <summary>Trophy flag (FestivalGoals: "&lt;counter&gt;Trophy", stamped once a festival) -> its prize pool, themed.</summary>
        public static readonly IReadOnlyDictionary<string, uint[]> CostumePrizes = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "HarvestHuntTrophy", new[] { CASTING_PIE, CASTING_APPLE, CARROT_WAND } },              // Festival Season: the harvest
            { "PresentHuntTrophy", new[] { HOLIDAY_PRESENT, CASTING_COOKIE } },                      // Frostfell: gifts and cookies
            { "SpringCleaningTrophy", new[] { CABBAGE_ORB, CARROT_DAGGER } },                        // Spring: the garden
            { "CandleRaceWinsTrophy", new[] { CASTING_CAKE, ROYAL_LADLE } },                         // the Royal Wedding: cake and the royal ladle
            { "NightClubNightsTrophy", new[] { LOAF_SHOOTER, CASTING_STEIN, ENERGY_CRYSTAL } },      // the Night Club
            { "NewYearPeasTrophy", new[] { DRUMSTICK_WAND } },                                       // the New Year feast
        };

        /// <summary>One flag per prize ever given, so a regular collects the set over the years instead of repeats.</summary>
        public static string PrizeFlag(uint wcid) => "FestivalCostumePrize" + wcid;

        /// <summary>This year's prize for a trophy flag: the first of its pool not had yet; once all are had, one picked
        /// by roll (0 to 1). 0 = not a prize flag.</summary>
        public static uint CostumePrize(string trophyFlag, Func<uint, bool> had, double roll)
        {
            if (trophyFlag == null || !CostumePrizes.TryGetValue(trophyFlag, out var pool) || pool.Length == 0) return 0;
            foreach (var w in pool) if (!had(w)) return w;
            int i = (int)Math.Floor(Math.Max(0, Math.Min(0.999999, roll)) * pool.Length);
            return pool[i];
        }

        public static string PrizeLine(string name) => $"With it comes a {name}, from the costume chest.";

        // ---- 2b. costume casters as a rare year-round world drop (owner 2026-11-24: "Yes, add a source") -------------
        // The festival goals stay the sure way (one a year each); this is the second, chance way, any day of the year.
        // Same shape as the joke drops: one roll per player kill, into the corpse, killer's pet counts, mercs never.

        /// <summary>All 13 approved casters (the prize pools' union, never the prototypes); each is equally likely.</summary>
        public static readonly uint[] CostumeCasters = CostumePrizes.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToArray();

        /// <summary>1 kill in N of a level 20+ creature. Why 1,500: rarer than the festival Cow (1 in 1,000, only while
        /// its festival runs) because this runs all year on most of the world, so a caster stays a lucky find, not a farm (any one
        /// caster is about 1 in 19,500 kills); the festival goal stays the dependable route. Retune only here.</summary>
        public const int CASTER_DROP_ONE_IN = 1500;
        public const int CASTER_DROP_MIN_LEVEL = 20;   // no farming them off starter-area creatures

        /// <summary>The chance one kill drops a caster: 0 when off or under level 20, else rate times 1 in N, never above 1.</summary>
        public static double CasterDropChance(bool on, int creatureLevel, double rate) =>
            !on || creatureLevel < CASTER_DROP_MIN_LEVEL ? 0 : Math.Min(1.0, Math.Max(0.0, rate) / CASTER_DROP_ONE_IN);

        /// <summary>Which caster, by roll (0 to 1), evenly over the 13.</summary>
        public static uint CasterDropPick(double roll) =>
            CostumeCasters[(int)Math.Floor(Math.Max(0, Math.Min(0.999999, roll)) * CostumeCasters.Length)];

        // ---- 3. Corrosion VII and Corruption VII ----------------------------------------------------------------
        public const uint YSANNE = 901420;                                    // Riftwatcher Ysanne (nether-rift-trainer.sql)
        public const uint CORROSION_VII = 43284, CORRUPTION_VII = 43292;     // Scroll of Corrosion VII / Corruption VII
        // Scroll of Nether Blast VII (void parity 2026-11-27): retail's scroll, but no vendor or loot table has it (ACE's
        // ScrollSpells void list has no Nether Blast); 2026-11-28-void-parity-2-nether-blast-vii.sql adds it to her shop.
        public const uint NETHER_BLAST_VII = 44625;
        public static bool IsVoidScroll(uint wcid) => wcid == CORROSION_VII || wcid == CORRUPTION_VII || wcid == NETHER_BLAST_VII;

        /// <summary>Is this purchase refused? Only at Ysanne, only for her void scrolls, only without Void Magic trained.</summary>
        public static bool VoidSaleRefused(uint vendorWcid, IEnumerable<uint> itemWcids, bool voidTrained) =>
            vendorWcid == YSANNE && !voidTrained && itemWcids != null && itemWcids.Any(IsVoidScroll);

        public const string VoidRefusal = "These are the Void's deeper workings. I sell them only to one who has trained Void Magic.";
    }
}
