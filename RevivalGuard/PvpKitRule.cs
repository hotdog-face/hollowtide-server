using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>The staff PVP kit (pure C#, nettest PvpKitCase; server side PvpKit.cs).</summary>
    public static class PvpKitRule
    {
        // ACE.Entity.Enum.Properties.PropertyInt values (PvpKit.cs checks them at load)
        public static readonly int GEAR_DAMAGE = 370, GEAR_DAMAGE_RESIST = 371, GEAR_CRIT_DAMAGE = 374, GEAR_CRIT_DAMAGE_RESIST = 375,
            GEAR_PK_DAMAGE = 383, GEAR_PK_DAMAGE_RESIST = 384;
        // ACE.Entity.Enum.EquipmentSet values
        public static readonly int SET_DEFENDERS = 16, SET_DEXTEROUS = 20;

        public enum Kind { Armor, Clothing, Cloak, Jewelry, Trinket, Weapon, Ammo, Consumable }

        public sealed class Entry
        {
            public uint Wcid; public string Name; public int Count = 1; public Kind Kind; public int Set;
            public Dictionary<int, int> Ratings = new Dictionary<int, int>();
            public Entry R(int prop, int v) { Ratings[prop] = v; return this; }
        }

        static Entry E(uint wcid, string name, Kind k, int count = 1, int set = 0) =>
            new Entry { Wcid = wcid, Name = name, Kind = k, Count = count, Set = set };

        /// <summary>The kit, in the order it lands in the pack.</summary>
        public static List<Entry> Kit() => new List<Entry>
        {
            // 5 Dexterous (offence: crit damage 34 = 7+7+7+7+6)
            E(42753, "Haebrean Helm", Kind.Armor, set: SET_DEXTEROUS).R(GEAR_CRIT_DAMAGE, 7),
            E(42749, "Haebrean Breastplate", Kind.Armor, set: SET_DEXTEROUS).R(GEAR_CRIT_DAMAGE, 7),
            E(42756, "Haebrean Tassets", Kind.Armor, set: SET_DEXTEROUS).R(GEAR_CRIT_DAMAGE, 7),
            E(42752, "Haebrean Greaves", Kind.Armor, set: SET_DEXTEROUS).R(GEAR_CRIT_DAMAGE, 7),
            E(42755, "Haebrean Boots", Kind.Armor, set: SET_DEXTEROUS).R(GEAR_CRIT_DAMAGE, 6),
            // 4 Defenders (defence: crit damage resist 16 = 4 each)
            E(42754, "Haebrean Pauldrons", Kind.Armor, set: SET_DEFENDERS).R(GEAR_CRIT_DAMAGE_RESIST, 4),
            E(42757, "Haebrean Vambraces", Kind.Armor, set: SET_DEFENDERS).R(GEAR_CRIT_DAMAGE_RESIST, 4),
            E(42750, "Haebrean Gauntlets", Kind.Armor, set: SET_DEFENDERS).R(GEAR_CRIT_DAMAGE_RESIST, 4),
            E(42751, "Haebrean Girth", Kind.Armor, set: SET_DEFENDERS).R(GEAR_CRIT_DAMAGE_RESIST, 4),
            // under the armour and the cloak: damage 54 = 27 + 27, damage resist 23
            E(2589, "Smock", Kind.Clothing).R(GEAR_DAMAGE, 27),
            E(2599, "Trousers", Kind.Clothing).R(GEAR_DAMAGE_RESIST, 23),
            E(44840, "Cloak", Kind.Cloak).R(GEAR_DAMAGE, 27),
            // (the jewelry is in the missile pack, cantrips and ratings on: MissilePackJewelry)
            E(41487, "Mechanical Scarab", Kind.Trinket),
            // weapons: PK 1/1 each, as his Gold Crossbow of the Fallen had
            E(29249, "Frost Crossbow", Kind.Weapon).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            E(31810, "Frost Compound Crossbow", Kind.Weapon).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            E(31824, "Frost Baton", Kind.Weapon).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            E(2472, "Wand", Kind.Weapon).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            // carry
            E(15446, "Deadly Frost Quarrel", Kind.Ammo, count: 1000),
            E(632, "Peerless Healing Kit", Kind.Consumable, count: 3),
            E(2458, "Health Elixir", Kind.Consumable, count: 10),
            E(2461, "Mana Elixir", Kind.Consumable, count: 10),
            E(2470, "Stamina Elixir", Kind.Consumable, count: 10),
            E(27328, "Major Mana Stone", Kind.Consumable, count: 5),
        };

        // the missile pack
        public const uint PACK_WCID = 136;          // "Pack"
        public const int PACK_SLOTS = 24;
        public static readonly int GEAR_HEALING_BOOST = 376, GEAR_MAX_HEALTH = 379;
        /// <summary>Retail loot's best jewelry rating, one family per piece, healing boost or vitality, wield level 180.</summary>
        public const int JEWELRY_RATING = 3, RATED_WIELD_LEVEL = 180;

        /// <summary>Retail's best missile launchers, best first, and the BASE Missile Weapons they need (WieldRequirement RawSkill).</summary>
        public sealed class LauncherTier { public uint Bow, Crossbow; public string BowName, CrossbowName; public int MinBase; }
        public static readonly LauncherTier[] LAUNCHERS =
        {
            // Viridian Rise reward (Forces of Nature, 2016), the copies with Legendary Defender: +22 lightning
            new LauncherTier { Bow = 53321, BowName = "Stormwood Bow", Crossbow = 53322, CrossbowName = "Stormwood Crossbow", MinBase = 385 },
            new LauncherTier { Bow = 43044, BowName = "Paradox-touched Olthoi Bow", Crossbow = 43045, CrossbowName = "Paradox-touched Olthoi Crossbow", MinBase = 360 },
            new LauncherTier { Bow = 30867, BowName = "Bow of the Fallen", Crossbow = 30868, CrossbowName = "Crossbow of the Fallen", MinBase = 335 },
        };

        /// <summary>The best tier this base skill wields; the lowest tier (wieldable false) when none.</summary>
        public static LauncherTier PickLaunchers(int baseMissile, out bool wieldable)
        {
            foreach (var t in LAUNCHERS) if (baseMissile >= t.MinBase) { wieldable = true; return t; }
            wieldable = false;
            return LAUNCHERS[LAUNCHERS.Length - 1];
        }

        /// <summary>Deadly ammo by ACE DamageType: a launcher's element bonus only lands when the ammo's damage type matches.</summary>
        public static readonly Dictionary<int, (uint arrow, string arrowName, uint quarrel, string quarrelName)> AMMO =
            new Dictionary<int, (uint, string, uint, string)>
        {
            { 1, (15433, "Deadly Broadhead Arrow", 15442, "Deadly Broadhead Quarrel") },          // slash
            { 2, (15431, "Deadly Armor Piercing Arrow", 15440, "Deadly Armor Piercing Quarrel") }, // pierce
            { 4, (15432, "Deadly Blunt Arrow", 15441, "Deadly Blunt Quarrel") },                  // bludgeon
            { 8, (15437, "Deadly Frost Arrow", 15446, "Deadly Frost Quarrel") },                  // cold
            { 16, (15435, "Deadly Fire Arrow", 15444, "Deadly Fire Quarrel") },                   // fire
            { 32, (15430, "Deadly Acid Arrow", 15439, "Deadly Acid Quarrel") },                   // acid
            { 64, (15434, "Deadly Lightning Arrow", 15443, "Deadly Lightning Quarrel") },         // electric
        };
        public const int AMMO_COUNT = 1000;

        /// <summary>Jewelry: PK 1/1 each plus the best loot rating: vitality on the necklace and bracelets, healing boost on the rings.</summary>
        public static List<Entry> MissilePackJewelry() => new List<Entry>
        {
            E(623, "Heavy Necklace", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1).R(GEAR_MAX_HEALTH, JEWELRY_RATING),
            E(621, "Heavy Bracelet", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1).R(GEAR_MAX_HEALTH, JEWELRY_RATING),
            E(621, "Heavy Bracelet", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1).R(GEAR_MAX_HEALTH, JEWELRY_RATING),
            E(624, "Ring", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1).R(GEAR_HEALING_BOOST, JEWELRY_RATING),
            E(624, "Ring", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1).R(GEAR_HEALING_BOOST, JEWELRY_RATING),
        };

        /// <summary>The missile pack's contents for a launcher tier and the launchers' ACE damage types.</summary>
        public static List<Entry> MissilePack(LauncherTier t, int bowDamageType, int crossbowDamageType)
        {
            var l = new List<Entry>
            {
                E(t.Bow, t.BowName, Kind.Weapon).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
                E(t.Crossbow, t.CrossbowName, Kind.Weapon).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            };
            l.AddRange(MissilePackJewelry());
            var a = AMMO.TryGetValue(bowDamageType, out var x) ? x : AMMO[2];
            var q = AMMO.TryGetValue(crossbowDamageType, out var y) ? y : AMMO[2];
            l.Add(E(a.arrow, a.arrowName, Kind.Ammo, count: AMMO_COUNT));
            l.Add(E(q.quarrel, q.quarrelName, Kind.Ammo, count: AMMO_COUNT));
            l.Add(E(27669, "Tumerok Salted Meat", Kind.Consumable, count: 100));   // one full stack; heals 120 health each
            l.Add(E(27329, "Massive Mana Charge", Kind.Consumable, count: 10));    // 10,000 mana each; does not stack
            return l;
        }

        /// <summary>Wearer cantrips for the jewelry, PkSuitRule.CAP per piece, best first for an archer-mage</summary>
        public static List<List<uint>> JewelrySpells(IEnumerable<int> trainedSkills, ISet<uint> skip, int pieces = 5)
        {
            var have = new HashSet<int>(trainedSkills ?? Enumerable.Empty<int>());
            var order = new List<uint>();
            order.AddRange(PkSuitRule.ATTR);
            order.AddRange(PkSuitRule.DEF);
            order.AddRange(PkSuitRule.GAIN);
            int[] first = { 47, 34, 33, 16, 21 };
            foreach (var k in first.Concat(PkSuitRule.SKILL_SPELL.Keys.Where(k => !first.Contains(k))))
                if (have.Contains(k) && PkSuitRule.SKILL_SPELL.TryGetValue(k, out var sp)) order.Add(sp);
            order.AddRange(PkSuitRule.PROTECT);
            var pick = order.Where(s => skip == null || !skip.Contains(s)).Distinct().Take(pieces * PkSuitRule.CAP).ToList();
            var r = Enumerable.Range(0, pieces).Select(_ => new List<uint>()).ToList();
            for (int i = 0; i < pick.Count; i++) r[i / PkSuitRule.CAP].Add(pick[i]);
            return r;
        }

        /// <summary>A fresh press with the best launchers: the kit plus the missile pack's contents.</summary>
        public static List<Entry> All() => Kit().Concat(MissilePack(LAUNCHERS[0], 64, 64)).ToList();

        /// <summary>Pack slots the kit needs: a stack is one slot when the weenie stacks, else one per item.</summary>
        public static int Slots(IEnumerable<Entry> kit, System.Func<uint, int> maxStack) =>
            kit.Sum(e => { int m = System.Math.Max(1, maxStack(e.Wcid)); return (e.Count + m - 1) / m; });

        /// <summary>Rating totals with everything worn and one weapon wielded (the sheet's numbers).</summary>
        public static Dictionary<int, int> WornTotals(IEnumerable<Entry> kit)
        {
            var d = new Dictionary<int, int>();
            var weapon = kit.FirstOrDefault(e => e.Kind == Kind.Weapon);
            foreach (var e in kit.Where(e => e.Kind != Kind.Weapon || e == weapon))
                foreach (var kv in e.Ratings) d[kv.Key] = (d.TryGetValue(kv.Key, out var v) ? v : 0) + kv.Value;
            return d;
        }
    }
}
