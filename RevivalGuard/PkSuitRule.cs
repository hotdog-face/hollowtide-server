using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>The PK suit plan (pure C#, nettest PkSuitCase; server side PkSuit.cs).</summary>
    public static class PkSuitRule
    {
        public enum Kind { Armor, Shield, Jewelry, Clothing, Melee, Missile, Caster, Cloak, Other }

        public const int CAP = 4;
        public const int MAX_TINKERS = 10;
        public const int STEEL_AL = 20;   // ACE 38000011 - Steel.txt: ArmorLevel += 20

        // ---- retail's legendary spells (id, name as ace_world.spell has it)
        public static readonly Dictionary<uint, string> NAMES = new Dictionary<uint, string>
        {
            { 6095, "Legendary Impenetrability" }, { 6088, "Legendary Acid Bane" }, { 6090, "Legendary Bludgeoning Bane" },
            { 6092, "Legendary Flame Bane" }, { 6093, "Legendary Frost Bane" }, { 6096, "Legendary Piercing Bane" },
            { 6097, "Legendary Slashing Bane" }, { 6099, "Legendary Storm Bane" },
            { 6089, "Legendary Blood Thirst" }, { 6094, "Legendary Heart Thirst" }, { 6091, "Legendary Defender" },
            { 6100, "Legendary Swift Hunter" }, { 6098, "Legendary Spirit Thirst" }, { 6087, "Legendary Hermetic Link" },
            { 6102, "Legendary Armor" }, { 6080, "Legendary Acid Ward" }, { 6081, "Legendary Bludgeoning Ward" },
            { 6082, "Legendary Flame Ward" }, { 6083, "Legendary Frost Ward" }, { 6084, "Legendary Piercing Ward" },
            { 6085, "Legendary Slashing Ward" }, { 6079, "Legendary Storm Ward" },
            { 6107, "Legendary Strength" }, { 6104, "Legendary Endurance" }, { 6103, "Legendary Coordination" },
            { 6106, "Legendary Quickness" }, { 6105, "Legendary Focus" }, { 6101, "Legendary Willpower" },
            { 6055, "Legendary Invulnerability" }, { 6054, "Legendary Impregnability" }, { 6063, "Legendary Magic Resistance" },
            { 6072, "Legendary Heavy Weapon Aptitude" }, { 6043, "Legendary Light Weapon Aptitude" },
            { 6047, "Legendary Finesse Weapon Aptitude" }, { 6044, "Legendary Missile Weapon Aptitude" },
            { 6073, "Legendary Two Handed Combat Aptitude" }, { 6050, "Legendary Dual Wield Aptitude" },
            { 6069, "Legendary Shield Aptitude" }, { 6049, "Legendary Dirty Fighting Prowess" },
            { 6067, "Legendary Recklessness Prowess" }, { 6070, "Legendary Sneak Attack Prowess" },
            { 6075, "Legendary War Magic Aptitude" }, { 6074, "Legendary Void Magic Aptitude" },
            { 6060, "Legendary Life Magic Aptitude" }, { 6046, "Legendary Creature Enchantment Aptitude" },
            { 6056, "Legendary Item Enchantment Aptitude" }, { 6064, "Legendary Mana Conversion Prowess" },
            { 6125, "Legendary Summoning Prowess" }, { 6053, "Legendary Healing Prowess" },
            { 6077, "Legendary Health Gain" }, { 6076, "Legendary Stamina Gain" }, { 6078, "Legendary Mana Gain" },
        };

        public static readonly uint[] ARMOR_ITEM = { 6095, 6088, 6090, 6092, 6093, 6096, 6097, 6099 };
        public static readonly uint[] MELEE_ITEM = { 6089, 6094, 6091, 6100 };
        public static readonly uint[] CASTER_ITEM = { 6098, 6087, 6091 };

        // wearer groups, in placing order
        public const int G_PROTECT = 0, G_ATTR = 1, G_DEF = 2, G_SKILL = 3, G_GAIN = 4;
        public static readonly uint[] PROTECT = { 6102, 6080, 6081, 6082, 6083, 6084, 6085, 6079 };
        public static readonly uint[] ATTR = { 6107, 6104, 6103, 6106, 6105, 6101 };
        public static readonly uint[] DEF = { 6055, 6054, 6063 };
        public static readonly uint[] GAIN = { 6077, 6076, 6078 };

        // ACE.Entity.Enum.Skill values (PkSuit.cs checks them at load) -> the legendary aptitude
        public static readonly Dictionary<int, uint> SKILL_SPELL = new Dictionary<int, uint>
        {
            { 44, 6072 }, { 45, 6043 }, { 46, 6047 }, { 47, 6044 }, { 41, 6073 }, { 49, 6050 }, { 48, 6069 },
            { 52, 6049 }, { 50, 6067 }, { 51, 6070 },                                    // combat
            { 34, 6075 }, { 43, 6074 }, { 33, 6060 }, { 31, 6046 }, { 32, 6056 }, { 16, 6064 }, { 54, 6125 },   // magic
            { 21, 6053 },                                                                // healing (PK heals)
        };

        // which wearer groups each kind takes, best first
        static readonly Dictionary<Kind, int[]> PREFS = new Dictionary<Kind, int[]>
        {
            { Kind.Armor,    new[] { G_PROTECT, G_DEF, G_ATTR, G_SKILL, G_GAIN } },
            { Kind.Shield,   new[] { G_PROTECT, G_DEF, G_ATTR, G_SKILL, G_GAIN } },
            { Kind.Jewelry,  new[] { G_ATTR, G_DEF, G_GAIN, G_SKILL, G_PROTECT } },
            { Kind.Clothing, new[] { G_SKILL, G_ATTR, G_GAIN, G_DEF, G_PROTECT } },
        };

        public static uint[] ItemSpells(Kind k) => k switch
        {
            Kind.Armor or Kind.Shield => ARMOR_ITEM,
            Kind.Melee or Kind.Missile => MELEE_ITEM,
            Kind.Caster => CASTER_ITEM,
            _ => Array.Empty<uint>(),
        };

        public static bool TakesWearerSpells(Kind k) => PREFS.ContainsKey(k);

        /// <summary>The wearer spells, in placing order: protections, attributes, defenses, a skill aptitude for each trained skill, gains.</summary>
        public static List<(uint id, int group)> WearerSpells(IEnumerable<int> trainedSkills)
        {
            var have = new HashSet<int>(trainedSkills ?? Enumerable.Empty<int>());
            var list = new List<(uint, int)>();
            list.AddRange(PROTECT.Select(s => (s, G_PROTECT)));
            list.AddRange(ATTR.Select(s => (s, G_ATTR)));
            list.AddRange(DEF.Select(s => (s, G_DEF)));
            foreach (var kv in SKILL_SPELL) if (have.Contains(kv.Key)) list.Add((kv.Value, G_SKILL));
            list.AddRange(GAIN.Select(s => (s, G_GAIN)));
            return list;
        }

        public sealed class Result
        {
            public Dictionary<int, List<uint>> Spells = new Dictionary<int, List<uint>>();   // item key -> spells to have
            public List<uint> Unplaced = new List<uint>();                                    // wearer spells with no room
        }

        /// <summary>Every item's spells.</summary>
        public static Result Plan(IList<(int key, Kind kind)> items, IEnumerable<int> trainedSkills)
        {
            var r = new Result();
            var load = new Dictionary<int, int>();
            foreach (var (key, kind) in items)
            {
                r.Spells[key] = new List<uint>(ItemSpells(kind));
                load[key] = 0;
            }
            foreach (var (id, group) in WearerSpells(trainedSkills))
            {
                int best = -1, bestRank = int.MaxValue, bestLoad = int.MaxValue;
                foreach (var (key, kind) in items)
                {
                    if (!PREFS.TryGetValue(kind, out var prefs) || load[key] >= CAP) continue;
                    int rank = Array.IndexOf(prefs, group);
                    if (rank < bestRank || (rank == bestRank && load[key] < bestLoad))
                    { best = key; bestRank = rank; bestLoad = load[key]; }
                }
                if (best < 0) { r.Unplaced.Add(id); continue; }
                r.Spells[best].Add(id);
                load[best]++;
            }
            return r;
        }

        // ---- tinkers: ACE's mutation ids (DarktideFeelRule.SignedLootRule constants)
        public sealed class Tinkers { public uint Imbue; public List<uint> Steps = new List<uint>(); }

        public static Tinkers TinkerPlan(Kind k, int timesTinkered, bool imbued, bool elemental)
        {
            var t = new Tinkers();
            int room = Math.Max(0, MAX_TINKERS - Math.Max(0, timesTinkered));
            if (room == 0) return t;
            switch (k)
            {
                case Kind.Armor:
                case Kind.Shield:
                    for (int i = 0; i < room; i++) t.Steps.Add(SignedLootRule.STEEL);
                    break;
                case Kind.Melee:
                    if (!imbued) { t.Imbue = SignedLootRule.SUNSTONE; room--; }
                    for (int i = 0; i < room; i++) t.Steps.Add(i == 0 ? SignedLootRule.GRANITE : SignedLootRule.IRON);
                    break;
                case Kind.Missile:
                    if (!imbued) { t.Imbue = SignedLootRule.SUNSTONE; room--; }
                    for (int i = 0; i < room; i++) t.Steps.Add(SignedLootRule.MAHOGANY);
                    break;
                case Kind.Caster:
                    if (!imbued) { t.Imbue = SignedLootRule.BLACK_OPAL; room--; }
                    for (int i = 0; i < room; i++) t.Steps.Add(elemental ? SignedLootRule.GREEN_GARNET : SignedLootRule.BRASS);
                    break;
            }
            return t;
        }

        public static int EveningMana(int currentMax, double burnPerSecond)
        {
            double need = Math.Min(2_000_000_000, Math.Max(5000, Math.Abs(burnPerSecond) * 12 * 3600));   // no int overflow (Codex review 11-23)
            int m = (int)Math.Ceiling(need / 100.0) * 100;
            return Math.Max(m, currentMax);
        }

        /// <summary>Burn rate for an item that had none (ours: one mana per 30 s, so the floor above lasts days).</summary>
        public const double DEFAULT_MANA_RATE = -1.0 / 30;
    }
}
