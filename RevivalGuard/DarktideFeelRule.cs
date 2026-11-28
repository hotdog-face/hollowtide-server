using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevivalGuard
{
    /// <summary>"feels like retail darktide": the pure half of SignedLoot, the capsule house signs and OnThisDay.</summary>
    public static class SignedLootRule
    {
        public enum Kind { Other, Armor, Melee, Missile, Caster }

        // ACE's own tinkering mutations (ACE.Server/Entity/Mutations/Recipes): the same script a player's tinker runs.
        public const uint STEEL = 0x38000011, IRON = 0x3800001A, MAHOGANY = 0x3800001B, GRANITE = 0x3800001C, BRASS = 0x38000020,
            GREEN_GARNET = 0x3800004B, SUNSTONE = 0x38000025, BLACK_OPAL = 0x38000023, FIRE_OPAL = 0x38000024;
        public const int MAX_TINKERS = 10;   // retail: an item takes ten tinkers at most

        /// <summary>What the item already is.</summary>
        public sealed class Facts
        {
            public Kind Kind; public int Workmanship; public int Tinkered; public bool Imbued; public bool Salvage;
            public bool Inscribable; public bool Inscribed; public bool Crafted; public bool HasCraftsman; public bool Elemental;
            public bool Signed;   // a tinker, imbuer or craftsman name already on it
        }

        public sealed class Plan
        {
            public uint Imbue;                                     // 0 = none; applied first
            public List<uint> Tinkers = new List<uint>();          // after the imbue, in order
            public string Tinker, Imbuer, Craftsman, Scribe, Inscription;
            public int Count => Tinkers.Count + (Imbue != 0 ? 1 : 0);   // NumTimesTinkered afterwards (from 0)
            public bool Any => Count > 0 || Craftsman != null || Inscription != null;
        }

        /// <summary>Who signs: tinker- and imbuer-named retail Darktide characters first, then any Darktide person, then any retail person.</summary>
        public sealed class Pools
        {
            public IReadOnlyList<string> Tinkers = Array.Empty<string>(), Imbuers = Array.Empty<string>(),
                Darktide = Array.Empty<string>(), Other = Array.Empty<string>();
            public Func<string, bool> InUse;
            public bool Empty => Tinkers.Count + Imbuers.Count + Darktide.Count + Other.Count == 0;
        }

        /// <summary>The odds, kept here so the nettest and the staff command read the same numbers.</summary>
        public static double TinkeredChance(int workmanship) => workmanship <= 0 ? 0 : Math.Clamp(0.12 + 0.05 * workmanship, 0.15, 0.55);
        public const double IMBUE_CHANCE = 0.30, SELF_TINKER = 0.15, CRAFTED_SIGNED = 0.85, INSCRIBE_CHANCE = 0.04;

        /// <summary>Retail spread of a looted item's tinkers: mostly one to three, a few fully tinkered.</summary>
        static int TinkerCount(Random rng)
        {
            double x = rng.NextDouble();
            return x < 0.35 ? 1 : x < 0.60 ? 2 : x < 0.75 ? 3 : x < 0.90 ? 4 + rng.Next(3) : 7 + rng.Next(4);
        }

        static readonly int[] s_Crafted =
        {
            8904, 9032, 9033, 9034, 9035, 9036, 9037, 9038, 9039, 9040, 9082, 9083, 9084, 9085, 9086, 9087, 9088, 9089,
            9090, 11256, 11257, 11258, 11259, 11260, 11261, 11262, 11263, 11264, 11265, 11296, 11297, 11298, 11299, 11300,
            11301, 11302, 11303, 11304, 11305, 11306, 11307, 11308, 11309, 11310, 11311, 11312, 11313, 11314, 11315,
            12139, 12154, 12158, 12227, 12233, 12234, 19551, 19552, 19553, 19554, 19555, 19556, 19557, 19558, 19559,
            19560, 19561, 19562, 19563, 19564, 19565, 19566, 19567, 19568, 19569, 19570, 19571, 19572, 19573, 19574,
            19575, 19576, 19577, 19578, 19579, 19580, 19581, 19582, 19583, 19584, 19585, 19586, 19587, 19588, 19589,
            19590, 19591, 19592, 19593, 19594, 19595, 19596, 19597, 19598, 19599, 19600, 19601, 19602, 19603, 19604,
            19605, 19606, 19607, 19608, 19609, 19610, 19611, 19612, 19613, 19614, 19615, 19616, 19617, 19618, 19619,
            19620, 19621, 19622, 19623, 19624, 19625, 19626, 19627, 19628, 19629, 19630, 19631, 19632, 19633, 19634,
            19635, 19636, 19637, 19638, 19639, 19640, 19641, 19642, 19643, 19644, 19645, 19646, 19647, 19648, 19649,
            19650, 19651, 19652, 19653, 19654, 19655, 19656, 19657, 19658, 19659, 19660, 19661, 19662, 19663, 19664,
            19665, 19666, 19667, 19668, 19669, 19670, 19671, 19672, 19673, 19674, 19675, 19676, 19677, 19678, 19679,
            19680, 19681, 19682, 19683, 19684, 19685, 19686, 19687, 19688, 19689, 19690, 19691, 19692, 19693, 19694,
            19695, 19696, 19697, 19698, 19699, 19700, 19701, 19702, 19703, 19704, 19705, 19706, 19707, 19708, 19709,
            19710, 19711, 19712, 23356, 23357, 23358, 23359, 23360, 23930, 23931, 23932, 23933, 23934, 23935, 23936,
            23937, 23938, 23939, 23940, 23941, 23942, 23943, 23944, 23946, 23947, 23948, 23949, 23950, 23951, 23952,
            23953, 23954, 23963, 23964, 23966, 23967, 23968, 23969, 23970, 23971, 23978, 23979, 23981, 23982, 23983,
            23984, 23985, 23986, 24599, 24600, 24601, 24602, 24603, 24604, 24605, 24606, 24607, 24608, 24609, 24610,
            24611, 24612, 24613, 28987, 28988, 28989, 28990, 28991, 28992, 28993, 28994, 28995, 28996, 28997, 29923,
            29924, 29925, 29926, 29927, 29928, 29929, 29930, 29931, 29932, 29933, 33990, 33991, 33993, 33996, 33997,
            33998, 33999, 40088, 40089, 40517, 40518, 40519, 87748, 87749, 87750, 87751
        };
        static HashSet<int> s_CraftedSet;
        /// <summary>The results of every ace_world recipe that writes CraftsmanName</summary>
        public static bool Crafted(uint wcid) => (s_CraftedSet ??= new HashSet<int>(s_Crafted)).Contains((int)wcid);

        public static Plan Decide(Facts f, string owner, Pools pools, Random rng, bool inscriptions)
        {
            var p = new Plan();
            if (f == null || pools == null || pools.Empty || f.Signed) return p;
            // TINKERS: only what a player could have tinkered, in the materials retail used on that kind of item
            bool tinkerable = f.Workmanship > 0 && !f.Salvage && f.Tinkered == 0 && f.Kind != Kind.Other;
            if (tinkerable && rng.NextDouble() < TinkeredChance(f.Workmanship))
            {
                int n = Math.Min(MAX_TINKERS, TinkerCount(rng));
                bool weapon = f.Kind == Kind.Melee || f.Kind == Kind.Missile;
                if (weapon && !f.Imbued && rng.NextDouble() < IMBUE_CHANCE)
                {
                    p.Imbue = new[] { SUNSTONE, BLACK_OPAL, FIRE_OPAL }[rng.Next(3)];
                    n--;
                    p.Imbuer = Pick(pools, rng, owner, imbuer: true, kind: f.Kind);
                }
                for (int i = 0; i < n; i++) p.Tinkers.Add(Material(f, rng));
                if (p.Tinkers.Count > 0) p.Tinker = Pick(pools, rng, owner, imbuer: false, kind: f.Kind);
            }
            if (f.Crafted && !f.HasCraftsman && rng.NextDouble() < CRAFTED_SIGNED) p.Craftsman = Pick(pools, rng, owner, imbuer: false, roleFirst: false);
            if (inscriptions && f.Inscribable && !f.Inscribed && owner != null && f.Kind != Kind.Other && rng.NextDouble() < INSCRIBE_CHANCE)
            {
                p.Scribe = owner;
                p.Inscription = Inscriptions[rng.Next(Inscriptions.Length)].Replace("{n}", owner);
            }
            return p;
        }

        /// <summary>Retail's materials per kind</summary>
        static uint Material(Facts f, Random rng) => f.Kind switch
        {
            Kind.Armor => STEEL,
            Kind.Melee => new[] { IRON, IRON, IRON, GRANITE, BRASS }[rng.Next(5)],
            Kind.Missile => new[] { MAHOGANY, MAHOGANY, MAHOGANY, BRASS }[rng.Next(4)],
            Kind.Caster => f.Elemental ? GREEN_GARNET : BRASS,
            _ => STEEL,
        };

        public static string Pick(Pools pools, Random rng, string owner, bool imbuer, bool roleFirst = true, Kind kind = Kind.Other)
        {
            if (owner != null && NameOk(owner) && rng.NextDouble() < SELF_TINKER) return owner;
            IEnumerable<IReadOnlyList<string>> order = roleFirst
                ? (imbuer ? new[] { pools.Imbuers, pools.Tinkers, pools.Darktide, pools.Other } : new[] { pools.Tinkers, pools.Darktide, pools.Other })
                : new[] { pools.Darktide, pools.Other };
            foreach (var l in order)
            {
                if (l == null || l.Count == 0) continue;
                // a tinker by trade 70% of the time, so the trade names don't sign everything
                if (roleFirst && (l == pools.Tinkers || l == pools.Imbuers) && rng.NextDouble() > 0.7) continue;
                int start = rng.Next(l.Count);
                for (int i = 0; i < Math.Min(l.Count, 50); i++)
                {
                    var n = l[(start + i) % l.Count];
                    if (!TradeFits(n, kind)) continue;
                    if (NameOk(n) && (pools.InUse == null || !pools.InUse(n))) return n;
                }
            }
            return owner != null && NameOk(owner) ? owner : null;
        }

        /// <summary>Plain lines a player cut into his own gear.</summary>
        public static readonly string[] Inscriptions =
        {
            "Property of {n}", "{n}", "If found, return to {n}", "Do not sell", "Main hand", "Keep", "Spare",
            "For the hunt", "Swap set", "Back up", "Do not salvage", "Bought with my first pyreals",
        };

        /// <summary>An "Brask Armor Tink" tinkers armour, a "Velda Weapon Tink" weapons (the name says the trade).</summary>
        public static bool TradeFits(string n, Kind kind)
        {
            bool armour = Regex.IsMatch(n, @"armou?r", RegexOptions.IgnoreCase), weapons = Regex.IsMatch(n, @"weap", RegexOptions.IgnoreCase);
            return kind == Kind.Other || (kind == Kind.Armor ? !weapons : !armour);
        }

        /// <summary>A trade name in the names file: tink, tinker, imbue (not a storage set, not salvage).</summary>
        public static bool TinkerName(string n) => n != null && Regex.IsMatch(n, @"tink|tinker", RegexOptions.IgnoreCase) && !Regex.IsMatch(n, @"\bset\b|salvag|junk|urself|\bgo\b", RegexOptions.IgnoreCase);
        public static bool ImbuerName(string n) => n != null && Regex.IsMatch(n, @"\bimbu", RegexOptions.IgnoreCase) && !Regex.IsMatch(n, @"\bset\b|salvag|^imbued$", RegexOptions.IgnoreCase);

        /// <summary>The house capsule note's rule (TimeCapsuleRule.NameOk): letters, space ' -. only, 3-30, not crude.</summary>
        public static bool NameOk(string n) => TimeCapsuleRule.NameOk(n);

        /// <summary>A plan is consistent: count matches tinkers, at most ten, a tinker name only with a tinker</summary>
        public static bool Consistent(Facts f, Plan p) =>
            p.Count <= MAX_TINKERS && (p.Tinker != null) == (p.Tinkers.Count > 0) && (p.Imbuer != null) == (p.Imbue != 0)
            && (p.Imbue == 0 || f.Kind == Kind.Melee || f.Kind == Kind.Missile) && (p.Count == 0 || (f.Workmanship > 0 && !f.Salvage))
            && (p.Craftsman == null || f.Crafted);
    }

    /// <summary>The capsule house's covenant crystal names its last owner while it stands empty with his things inside.</summary>
    public static class HouseSignRule
    {
        public static string Signed(string owner, string baseName) => string.IsNullOrWhiteSpace(owner) ? baseName : $"{owner}'s {baseName}";

        /// <summary>Show a name only on an empty house whose last owner's pieces are up this vacancy, with a real name.</summary>
        public static bool Show(bool on, bool owned, bool piecesUp, string owner) =>
            on && !owned && piecesUp && !string.IsNullOrWhiteSpace(owner) && owner != TimeCapsuleRule.FallbackNames[0];

        /// <summary>The last owner's name: Darktide people first, else the chat-corpus names the notes always used.</summary>
        public static string LastOwner(IReadOnlyList<string> darktide, IReadOnlyList<string> other, ulong roll, Func<string, bool> inUse, Func<string, bool> ok) =>
            LastOwner(null, darktide, other, roll, inUse, ok);

        /// <summary>Share of capsule houses whose last owner comes from the home list first</summary>
        public const int HOMES_PCT = 10;

        public static string LastOwner(IReadOnlyList<string> homes, IReadOnlyList<string> darktide, IReadOnlyList<string> other, ulong roll, Func<string, bool> inUse, Func<string, bool> ok)
        {
            bool homeFirst = homes != null && homes.Count > 0 && (int)((roll >> 17) % 100) < HOMES_PCT;
            var order = homeFirst ? new[] { homes, darktide, other } : new[] { darktide, other };
            foreach (var l in order)
            {
                if (l == null || l.Count == 0) continue;
                int start = (int)(roll % (ulong)l.Count);
                for (int i = 0; i < l.Count; i++)
                {
                    var n = l[(start + i) % l.Count];
                    if (ok(n) && (inUse == null || !inUse(n))) return n;
                }
            }
            return TimeCapsuleRule.FallbackNames[0];
        }
    }

    /// <summary>On this day: a dated line of real Asheron's Call history, Darktide first, from OnThisDay.json.</summary>
    public static class OnThisDayRule
    {
        public sealed class Entry { public string Md; public int Year; public bool Darktide; public string Text; public string Title; public string Url; }

        /// <summary>Today's entry (UTC month-day): Darktide first, then the oldest unsaid.</summary>
        public static Entry Pick(IReadOnlyList<Entry> all, DateTime utc, int said)
        {
            var md = utc.ToString("MM-dd");
            var day = all.Where(e => e.Md == md).OrderByDescending(e => e.Darktide).ThenBy(e => e.Year).ToList();
            return said < day.Count ? day[said] : null;
        }

        /// <summary>"On this day, twenty-two years ago, &lt;text&gt;".</summary>
        public static string Line(Entry e, DateTime utc)
        {
            int n = utc.Year - e.Year;
            return $"On this day, {Words(n)} {(n == 1 ? "year" : "years")} ago, {e.Text}";
        }

        static readonly string[] s_Ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven",
            "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        static readonly string[] s_Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
        public static string Words(int n) => n < 0 ? n.ToString() : n < 20 ? s_Ones[n] : n < 100 ? s_Tens[n / 10] + (n % 10 == 0 ? "" : "-" + s_Ones[n % 10]) : n.ToString();

        /// <summary>When today's lines are said</summary>
        public static readonly int[] HOURS = { 2, 19 };
        public static int Due(DateTime utc, int said)
        {
            if (said >= HOURS.Length) return -1;
            int minute = (utc.DayOfYear * 37 + said * 11) % 50 + 5;
            var at = utc.Date.AddHours(HOURS[said]).AddMinutes(minute);
            return utc >= at ? said : -1;
        }

        /// <summary>The data's rules: one sentence, short, no dashes, no meta words, the year not repeated.</summary>
        public static string Problem(Entry e)
        {
            if (!Regex.IsMatch(e.Md ?? "", @"^(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$")) return "bad md";
            if (e.Year < 1999 || e.Year > 2017) return "year out of retail";
            if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Length > 170) return "length";
            if (e.Text.IndexOfAny(new[] { '—', '–', ';', '"' }) >= 0 || e.Text.Contains(" - ")) return "dash, semicolon or quote";
            if (!e.Text.EndsWith(".")) return "no full stop";
            if (Regex.IsMatch(e.Text, @"\b(wiki|forum|website|Turbine|Microsoft|Zone|patch notes|developers?|dev|Hollowtide|bots?)\b", RegexOptions.IgnoreCase)) return "meta word";
            if (e.Text.Contains(e.Year.ToString())) return "year repeated";
            if (string.IsNullOrWhiteSpace(e.Url) || !e.Url.StartsWith("https://asheron.fandom.com/")) return "source_url";
            return null;
        }
    }
}
