using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// The pure half of ElementalKnights (no ACE types; tools/nettest `elementalknight`). ELEMENTAL KNIGHT ARMOUR (owner
    /// 2026-11-27: "the elemental knight armor in its various colors and dyeable. dont do the elemental body skin";
    /// docs/findings/elemental-summons-armor-2026-11-27.md). The spiked plate the level-200 elemental summons and the
    /// Empyrean knights wear, as five cosmetic pieces in retail's four finishes (one texture each, ClothingTables
    /// 0x10000632-635), coloured by the piece's PaletteTemplate: the knights' own element tints, or a dye.
    ///
    /// Ids (tools/gpubox-ace/gen_elemental_knight.py writes the tables and the weenies with the same formulas):
    ///   piece wcid PIECE0 + SLOTS x finish + slot, ClothingTable TABLE0 + SLOTS x finish + slot; tokens TOKEN0 + finish;
    ///   the keeper KEEPER.
    /// The colour: the armour's textures are indexed in its own palette 0x04001DEA, not the player's, so on a player retail
    /// draws them in that natural palette whatever the template (CPartArray::SetPalette; the Red Bronze rule). So the
    /// template is carried into the texture id instead: a worn piece's knight textures become our re-paletted ids
    /// (client AcRepal, PALETTES index Repal(template)) in the wearer's ObjDesc. A template with no entry (21, the
    /// knights' natural one) leaves the retail texture: the natural finish, on every client.
    /// </summary>
    public static class ElementalKnightRule
    {
        public const int FINISHES = 4, SLOTS = 5;
        public const uint PIECE0 = 916400, TOKEN0 = 916420, KEEPER = 916430;
        public const uint TABLE0 = 0x1000F180;
        public static readonly string[] FINISH = { "Steel", "Blackened", "Gilded", "Mossy" };
        public static readonly string[] SLOT = { "Helm", "Breastplate", "Pauldrons", "Bracers", "Greaves" };
        /// <summary>Per finish, retail's texture (ClothingTables 0x10000634, 633, 632, 635 swap 0x05002B9E for it).</summary>
        public static readonly uint[] TEXTURE = { 0x05002B9E, 0x05002BA0, 0x05002BA1, 0x05002BA2 };
        /// <summary>The armour's natural palette template (the knights' 21: no recolour).</summary>
        public const int NATURAL = 21;

        public static uint Piece(int finish, int slot) => PIECE0 + (uint)(SLOTS * finish + slot);
        public static uint Table(int finish, int slot) => TABLE0 + (uint)(SLOTS * finish + slot);
        public static bool IsTable(uint id) => id >= TABLE0 && id < TABLE0 + FINISHES * SLOTS;
        public static bool IsTexture(uint id) => Array.IndexOf(TEXTURE, id) >= 0;

        /// <summary>The client's AcRepal.PALETTES index a template draws in, or -1 (natural). Must match AcRepal: 1-4 the
        /// knights' element tints (templates 14 red, 2 blue, 8 green, 13 purple), 5-14 the retail dyes 84-93.</summary>
        public static int Repal(int? template)
        {
            switch (template)
            {
                case 14: return 1;
                case 2: return 2;
                case 8: return 3;
                case 13: return 4;
            }
            if (template >= 84 && template <= 93) return 5 + template.Value - 84;
            return -1;
        }

        /// <summary>Our re-paletted id: retail texture tex drawn in AcRepal.PALETTES[index] (AcRepal.BASE, bits 14-19).</summary>
        public static uint RepalId(uint tex, int index) => 0x05D00000u | (uint)index << 14 | (tex & 0x3FFF);

        /// <summary>What a worn piece's texture change becomes: the same id when natural or not one of the armour's.</summary>
        public static uint Shown(uint newTexture, int? template)
        {
            int i = Repal(template);
            return i < 0 || !IsTexture(newTexture) ? newTexture : RepalId(newTexture, i);
        }

        // ------------------------------------------------------------------ who wears it (live ace_world 2026-11-27)
        /// <summary>A creature wearing the armour: its finish (by its ClothingBase), the template its drop takes, its rate.</summary>
        public readonly record struct Knight(string Name, int Finish, int Template, double Chance);

        /// <summary>The Guardians' rate (level 500, the rune table's 275-699 band) and the packs' (17-46 placed per spot,
        /// so under the 5% band, as the bronze statues).</summary>
        public const double GUARDIAN = 0.15, PACK = 0.02, TOKEN_CHANCE = 0.10;
        const int STEEL = 0, BLACK = 1, GILT = 2, MOSS = 3;

        /// <summary>The Empyrean knights and guards, each with its own finish and colour. Caustic (acid) ones are natural
        /// in retail; their drops take the acid summon's green (8) so the green tint has a source.</summary>
        public static readonly Dictionary<uint, Knight> Knights = new()
        {
            [31972] = new("Flame Guardian", GILT, 14, GUARDIAN),           // level 500, one placed
            [31971] = new("Water Guardian", GILT, 2, GUARDIAN),            // level 500, one placed
            [31827] = new("Incendiary Knight", GILT, 14, PACK),            // Empyrean Workshop
            [45691] = new("Incendiary Guard", GILT, 14, PACK),
            [31829] = new("Galvanic Knight", GILT, 13, PACK),
            [45698] = new("Galvanic Guard", GILT, 13, PACK),
            [70974] = new("Luminary Knight", GILT, NATURAL, PACK),
            [45710] = new("Luminary Guard", GILT, NATURAL, PACK),
            [46932] = new("Ancient Luminary Knight", GILT, NATURAL, PACK),
            [31830] = new("Tenebrous Knight", BLACK, NATURAL, PACK),
            [45709] = new("Tenebrous Guard", BLACK, NATURAL, PACK),
            [46933] = new("Ancient Tenebrous Knight", BLACK, NATURAL, PACK),
            [71381] = new("Ancient Incendiary Guardian", BLACK, 14, PACK),
            [45706] = new("Glacial Guard", STEEL, 2, PACK),                // Frozen Refuge
            [45702] = new("Aqueous Guard", STEEL, 2, PACK),
            [46931] = new("Ancient Aqueous Knight", STEEL, 2, PACK),
            [73154] = new("Algid Warrior", STEEL, 2, PACK),                // Frigid Grotto
            [31828] = new("Glacial Knight", MOSS, 2, PACK),
            [31832] = new("Caustic Knight", MOSS, 8, PACK),
            [45700] = new("Caustic Guard", MOSS, 8, PACK),
        };

        /// <summary>The Guardians: a kill opens every finish's keeper gate.</summary>
        public static bool IsGate(uint wcid) => wcid == 31971 || wcid == 31972;
        public static string SlainQuest(int finish) => "ElemKnightSlain" + finish;

        /// <summary>Off is 0; a force of 1-100 (elemental_knight_drop_force, tests) replaces the rate.</summary>
        public static double Chance(bool on, uint wcid, long forcePercent)
        {
            if (!on || !Knights.TryGetValue(wcid, out var k)) return 0;
            return forcePercent > 0 ? Math.Min(100, forcePercent) / 100.0 : k.Chance;
        }

        /// <summary>One roll per kill: null on a miss, else the piece (the knight's finish, slot at random) and its template.</summary>
        public static (uint wcid, int template)? Drop(uint wcid, double chance, double roll, double pickSlot)
        {
            if (chance <= 0 || roll >= chance || !Knights.TryGetValue(wcid, out var k)) return null;
            return (Piece(k.Finish, Math.Clamp((int)(pickSlot * SLOTS), 0, SLOTS - 1)), k.Template);
        }

        /// <summary>The token a knight carries: its finish's (the keeper trades nine for a piece of that finish), or 0.</summary>
        public static uint TokenFor(uint wcid) => Knights.TryGetValue(wcid, out var k) ? TOKEN0 + (uint)k.Finish : 0;
    }
}
