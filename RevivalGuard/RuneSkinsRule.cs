namespace RevivalGuard
{
    /// <summary>The pure half of RuneSkins (no ACE types; tools/nettest `runeskins`).</summary>
    internal static class RuneSkinsRule
    {
        internal const int SETS = 28, SLOTS = 9, FIRST_SETS = 7;
        internal const uint PIECE0 = 915600, MORE0 = 916000;   // the first 7 sets, then the rest (gen_rune_suits.py piece_ids)
        internal const int SET0 = 9150;
        internal const int CRAGSTONE = 16;
        /// <summary>Sets no creature drops: only Cragstone.</summary>
        internal static readonly int[] NoBoss = { CRAGSTONE };

        internal static uint Piece(int set, int slot) =>
            set < FIRST_SETS ? PIECE0 + (uint)(10 * set + slot) : MORE0 + (uint)(10 * (set - FIRST_SETS) + slot);
        internal static bool IsSet(int equipmentSetId) => equipmentSetId >= SET0 && equipmentSetId < SET0 + SETS;

        /// <summary>A boss: the sets whose look it wears (one picked per drop) and its chance per kill.</summary>
        internal readonly record struct Boss(string Name, int[] Sets, double Chance);

        // Levels and places from the world DB; kills measured in the proposal.
        const double HI = 0.25, MID = 0.15, LO = 0.05;
        /// <summary>The Bronze Statues Attack's living statues.</summary>
        internal const double STATUE = 0.02;
        static readonly int[] Bronze = { 8, 9, 10 };
        static readonly int[] SpectralHost = { 6, 20, 21 }, Bushi = { 6, 22, 23, 24, 25 };
        internal static readonly Dictionary<uint, Boss> Bosses = new()
        {
            [44104] = new("Hatshepsut", new[] { 0, 18 }, LO),                // Neftet Temple; gold rune is Neftet's statues'
            [44101] = new("Nephthys", new[] { 1, 18 }, LO),                  // Neftet
            [44138] = new("Nephthys", new[] { 1, 18 }, LO),
            [53365] = new("Wind Fury", new[] { 2 }, MID),                    // Viridian Rise, A'nekshen Storm body
            [72000] = new("NightBrier", new[] { 2 }, MID),                   // Viridian Rise, A'nekshen Thorn body
            [72179] = new("Vvermurgor", new[] { 3, 4 }, HI),                 // Viridian Root, the corrupt (red) A'nekshen
            [44089] = new("Bak'tshay Lady", new[] { 5, 18 }, LO),            // Neftet Temple
            [46439] = new("Samurai Titan", Bushi, HI),                       // Hoshino, Venerable Mausoleum
            [72530] = new("Spectral Samurai Daimyo", SpectralHost, MID),     // Mnemosyne
            [72531] = new("Spectral Samurai Daimyo", SpectralHost, MID),
            [72532] = new("Spectral Samurai Daimyo", SpectralHost, MID),
            [72668] = new("Spectral Nanjou Master", new[] { 6, 20, 21, 27 }, MID),   // Nanjou Shou-jen Academy
            [38029] = new("Elite Zharalim Agent", new[] { 7 }, LO),          // Oxidized Zharalim: the Zharalim's agents
            [24001] = new("Arikas, Warden of Knorr", new[] { 11 }, HI),      // Knorr Academy; Diamond Guardian body
            [23998] = new("Diamond Guardian", new[] { 11 }, HI),
            [23999] = new("Diamond Guardian", new[] { 11 }, HI),
            [24000] = new("Diamond Guardian", new[] { 11 }, HI),
            [34441] = new("Master Arbitrator", new[] { 12 }, LO),            // the Colosseum
            [71813] = new("Master Arbitrator", new[] { 12 }, LO),
            [87681] = new("Master of Decay", new[] { 13 }, LO),              // Acolyte of Decay's skin
            [87685] = new("Black Water Gatekeeper", new[] { 13 }, LO),
            [34614] = new("Gladiator Diemos", new[] { 14 }, LO),             // the Colosseum
            [34617] = new("The Master", new[] { 15 }, MID),                  // the Colosseum
            [37289] = new("Jester", new[] { 17 }, HI),                       // the Jester's prison
            [38017] = new("Jester", new[] { 17 }, HI),
            [72262] = new("Jester", new[] { 17 }, HI),
            [72263] = new("Jester", new[] { 17 }, HI),
            [72276] = new("Jester", new[] { 17 }, HI),
            [46428] = new("Ronin Shimakawa", new[] { 19 }, LO),              // Venerable Mausoleum; Blue Spectral body
            [46430] = new("Ronin Ginmura", new[] { 19 }, MID),
            [46426] = new("Ronin Hirachi", new[] { 19 }, MID),
            [70715] = new("General Tarkaris", new[] { 26 }, LO),             // his own skin
            [903850] = new("Bronze Statue", Bronze, STATUE), [903852] = new("Bronze Statue", Bronze, STATUE),
            [903854] = new("Bronze Statue", Bronze, STATUE), [903856] = new("Bronze Statue", Bronze, STATUE),
            [903858] = new("Bronze Statue", Bronze, STATUE), [903860] = new("Bronze Statue", Bronze, STATUE),
            [903862] = new("Bronze Statue", Bronze, STATUE), [903864] = new("Bronze Statue", Bronze, STATUE),
            [903866] = new("Bronze Statue", Bronze, STATUE), [903868] = new("Bronze Statue", Bronze, STATUE),
            [903870] = new("Bronze Statue", Bronze, STATUE), [903872] = new("Bronze Statue", Bronze, STATUE),
            [903874] = new("Bronze Statue", Bronze, STATUE), [903876] = new("Bronze Statue", Bronze, STATUE),
            [903878] = new("Bronze Statue", Bronze, STATUE), [903880] = new("Bronze Statue", Bronze, STATUE),
            [903882] = new("Bronze Statue", Bronze, STATUE),
        };

        /// <summary>Keeper credit only: sets 0, 1, 3 and 4's listed bosses are friendly quest NPCs in the world DB</summary>
        internal static readonly Dictionary<uint, int[]> SlainCredit = new()
        {
            [44098] = new[] { 0 },   // Mu-miyah Slave Master (300), the Temple under Neftet: holds Hatshepsut
            [44097] = new[] { 1 },   // Mu-miyah Grand Vizier (240), the Temple under Neftet
            [72189] = new[] { 3 },   // A'nekshen Storm Corruptor (300), Viridian Root
            [72195] = new[] { 4 },   // A'nekshen Thorn Corruptor (300), Viridian Root
        };

        /// <summary>The keeper sets (0-6) this kill stamps RuneSkinSlain for: the boss's own sets plus SlainCredit.</summary>
        internal static IEnumerable<int> SlainSets(uint wcid)
        {
            var sets = new List<int>();
            if (Bosses.TryGetValue(wcid, out var b)) sets.AddRange(b.Sets);
            if (SlainCredit.TryGetValue(wcid, out var c)) sets.AddRange(c);
            return sets.Where(s => s < FIRST_SETS).Distinct();
        }

        /// <summary>The chance for this kill: off is 0; a force of 1-100 (rune_skin_drop_force, tests) replaces the table.</summary>
        internal static double Chance(bool on, uint wcid, long forcePercent)
        {
            if (!on || !Bosses.TryGetValue(wcid, out var b)) return 0;
            return forcePercent > 0 ? Math.Min(100, forcePercent) / 100.0 : b.Chance;
        }

        /// <summary>One roll per kill: null on a miss, else the piece. roll, pickSet and pickSlot are uniform in [0, 1).</summary>
        internal static uint? Drop(uint wcid, double chance, double roll, double pickSet, double pickSlot)
        {
            if (chance <= 0 || roll >= chance || !Bosses.TryGetValue(wcid, out var b)) return null;
            int set = b.Sets[Math.Clamp((int)(pickSet * b.Sets.Length), 0, b.Sets.Length - 1)];
            return Piece(set, Math.Clamp((int)(pickSlot * SLOTS), 0, SLOTS - 1));
        }

        // ------------------------------------------------------------------ the quests (gen_rune_quests.py)
        internal const double TOKEN_CHANCE = 0.10;   // proposed: one token in ten kills of the theme's creatures
        internal const uint TOKEN_NEFTET = 916300, TOKEN_AMBER = 916301, TOKEN_CORRUPT = 916302, TOKEN_SPECTRAL = 916303;

        /// <summary>The quest token a creature of this name carries (by its family, as the sets' looks are), or 0.</summary>
        internal static uint TokenFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            if (name.StartsWith("Bak'tshay", StringComparison.Ordinal) || name.StartsWith("A'nekshay", StringComparison.Ordinal)) return TOKEN_NEFTET;
            if (name.StartsWith("A'nekshen", StringComparison.Ordinal))
                return name.Contains("Corrupt", StringComparison.Ordinal) ? TOKEN_CORRUPT : TOKEN_AMBER;
            if (name.StartsWith("Spectral ", StringComparison.Ordinal)) return TOKEN_SPECTRAL;
            return 0;
        }

        /// <summary>The quest flag a set's boss kill stamps (the giver's first gate).</summary>
        internal static string SlainQuest(int set) => "RuneSkinSlain" + set;

        /// <summary>PK Night: one Cragstone Guise piece, slot at random.</summary>
        internal static uint PkPrize(double pickSlot) => Piece(CRAGSTONE, Math.Clamp((int)(pickSlot * SLOTS), 0, SLOTS - 1));

        /// <summary>PK Night's one prize winner</summary>
        internal static uint PkWinner(IEnumerable<uint> present, IReadOnlyDictionary<uint, int> ticks)
        {
            uint best = 0; int most = -1;
            foreach (var g in present.OrderBy(x => x))
            {
                int t = ticks != null && ticks.TryGetValue(g, out var n) ? n : 0;
                if (t > most) { best = g; most = t; }
            }
            return best;
        }

        // the set bonus
        static readonly Dictionary<int, uint> Resist0 = new()
        {
            [13] = 4784, [14] = 4780, [15] = 4764, [16] = 4772, [17] = 4768, [18] = 4760, [19] = 4776,   // slash pierce bludg cold fire acid elec
        };
        /// <summary>Each set's two themed damage types, gen_rune_suits.py THEMES.</summary>
        internal static readonly (int a, int b)[] Themes = { (19, 17), (18, 17), (19, 14), (18, 19), (14, 18), (13, 15), (16, 13),
            (18, 13), (17, 15), (15, 19), (15, 16), (14, 16), (19, 16), (18, 16), (13, 15), (17, 19), (15, 14), (17, 18), (13, 18),
            (16, 19), (16, 17), (16, 18), (16, 14), (17, 13), (19, 13), (16, 15), (18, 15), (14, 19) };

        const uint HERO1 = 4734, HERO2 = 4735, HERO3 = 4736, SURVIVOR3 = 4755, GUARD1 = 4858, GUARD2 = 4859, GUARD3 = 4860, WAY2 = 4863, WAY3 = 4864;

        /// <summary>The spells a set grants with this many pieces worn: Wardenforged's ladder with the set's resistances.</summary>
        internal static uint[] Tier(int set, int pieces)
        {
            if (set < 0 || set >= SETS) return Array.Empty<uint>();
            uint a = Resist0[Themes[set].a], b = Resist0[Themes[set].b];
            return Math.Clamp(pieces, 0, SLOTS) switch
            {
                9 => new[] { a + 3, b + 3, HERO3, SURVIVOR3, GUARD3, WAY3 },
                8 => new[] { a + 2, b + 2, HERO3, GUARD2, WAY2 },
                6 or 7 => new[] { a + 2, b + 2, HERO2, GUARD1 },
                4 or 5 => new[] { a + 1, b + 1, HERO1 },
                2 or 3 => new[] { a, b },
                _ => Array.Empty<uint>(),
            };
        }

        internal static IEnumerable<uint> AllSpells(int set) =>
            Enumerable.Range(0, SLOTS + 1).SelectMany(n => Tier(set, n)).Distinct();
    }
}
