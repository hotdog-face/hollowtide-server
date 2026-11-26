using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// THE STAFF PVP KIT (pure C#, nettest PvpKitCase; server side PvpKit.cs). Owner 2026-11-25: his retail end-game
    /// an admin character kit (docs/plans/an admin account-pvp-build-2026-11-25.md sections 4 and 6) handed to an admin, Bonded and
    /// Attuned, to wear and then press "pk suit" (@pksuit) for the legendary cantrips, tinks and imbues.
    ///
    /// Sets: the 5 Dexterous + 4 Defenders Haebrean pieces carry their EquipmentSetId (ACE counts worn pieces per set).
    /// Ratings: his June 2016 sheet (AC Character Compendium.pdf p.2): Dmg/CritDmg Rating 54/34, Dmg/CritDmg Resist
    /// 23/16, PK Dmg/Res Rating 6/6. Each item gets only the rating families retail loot put on its kind (ACE
    /// LootGenerationFactory_Clothing.TryMutateGearRating): armour crit damage or crit damage resist, clothing and
    /// cloaks damage or damage resist; PK ratings on the weapons (his crossbow had PK 1/1) and jewelry. The sizes are the
    /// sheet's, not loot's (loot rolls 1-2 on armour, 1-3 on clothing): retail reached those totals with luminance
    /// auras, Enlightenment and society, none of which this kit can give. Totals hold with ONE weapon wielded.
    /// </summary>
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

        /// <summary>The kit, in the order it lands in the pack. Weenie ids and names as ace_world has them (box, 11-25).</summary>
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
            // jewelry: PK 1/1 each (5/5), the wielded weapon brings the sixth
            E(623, "Heavy Necklace", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            E(621, "Heavy Bracelet", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            E(621, "Heavy Bracelet", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            E(624, "Ring", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
            E(624, "Ring", Kind.Jewelry).R(GEAR_PK_DAMAGE, 1).R(GEAR_PK_DAMAGE_RESIST, 1),
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

        /// <summary>Pack slots the kit needs: a stack is one slot when the weenie stacks, else one per item.</summary>
        public static int Slots(IEnumerable<Entry> kit, System.Func<uint, int> maxStack) =>
            kit.Sum(e => { int m = System.Math.Max(1, maxStack(e.Wcid)); return (e.Count + m - 1) / m; });

        /// <summary>Rating totals with everything worn and ONE weapon wielded (the sheet's numbers).</summary>
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
