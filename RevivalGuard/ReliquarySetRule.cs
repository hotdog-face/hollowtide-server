using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// THE RELIQUARY SET'S NUMBERS, PURE (no ACE types): ReliquarySet.cs runs them, tools/nettest compiles this file by
    /// path (ReliquarySetCase.cs there). Round 2, The Drowned Reliquary (docs/content-review/round2-drowned-reliquary-concept.md
    /// appendix E; docs/content-review/round2/set-bonus.md).
    ///
    /// The set is EquipmentSetId 924 on weenies 924300-924305. A player can wear at most four of them at once: one of the
    /// three weapons (Keeper's Hook, Tidecaller Bow, Brinelight Orb), the Reliquary Ward shield, the Saltglass Amulet and
    /// the Drowned Signet. So pieces are counted by SLOT, not by item: the three weapons are one piece between them, and a
    /// second Signet (the other ring finger) or a second Hook (dual wield) adds nothing. That keeps "full set" meaning
    /// one of each, the way the concept reads, and stops two rings standing in for the shield.
    ///
    /// Tiers, written the way client_portal.dat's SpellSet writes a retail set (each tier the whole list for that count,
    /// a count with no tier of its own takes the tier below, as ACE's SpellSetTiersNoGaps does):
    ///   2 pieces: Minor Focus (2581)
    ///   3 pieces: Minor Focus (2581)
    ///   4 pieces: Minor Focus (2581), Major Frost Ward (2612)
    /// The concept says "Major Cold Ward"; the spell table has no spell by that name. Retail's cold-protection cantrip is
    /// Major Frost Ward (2612, category 289 ColdWardProtection, "Reduces damage the target takes from Cold by 15%").
    /// </summary>
    static class ReliquarySetRule
    {
        public const int SET_ID = 924;
        public const uint MINOR_FOCUS = 2581, MAJOR_FROST_WARD = 2612;
        public const uint HOOK = 924300, BOW = 924301, ORB = 924302, WARD = 924303, AMULET = 924304, SIGNET = 924305;
        public const int FULL = 4;

        static readonly uint[][] TIERS =
        {
            new uint[0],
            new uint[0],
            new uint[] { MINOR_FOCUS },
            new uint[] { MINOR_FOCUS },
            new uint[] { MINOR_FOCUS, MAJOR_FROST_WARD },
        };

        /// <summary>Which slot a set piece fills: 0 weapon (any of the three), 1 shield, 2 amulet, 3 ring; -1 not a piece.</summary>
        public static int Slot(uint wcid)
        {
            switch (wcid)
            {
                case HOOK: case BOW: case ORB: return 0;
                case WARD: return 1;
                case AMULET: return 2;
                case SIGNET: return 3;
                default: return -1;
            }
        }

        /// <summary>Pieces that count: distinct slots filled by the worn set items' weenies.</summary>
        public static int Pieces(IEnumerable<uint> wornWcids)
        {
            if (wornWcids == null) return 0;
            return wornWcids.Select(Slot).Where(s => s >= 0).Distinct().Count();
        }

        /// <summary>The set's spells for this many pieces (0..4; more is clamped to the full set).</summary>
        public static uint[] Tier(int pieces) => TIERS[Math.Clamp(pieces, 0, TIERS.Length - 1)];

        /// <summary>Every spell in any tier (GetSpellSetAll, ItemSetContains).</summary>
        public static uint[] All() => TIERS.SelectMany(t => t).Distinct().ToArray();
    }
}
