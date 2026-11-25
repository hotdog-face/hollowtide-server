namespace RevivalGuard
{
    /// <summary>MonsterAi's stock areas, pure (no ACE types), so tools/nettest compiles and checks it by path. A creature
    /// whose home is in one of these landblocks gets no MonsterAi tactic at all: stock ACE, as players there expect
    /// (owner 2026-11-25: "maybe we dont change too much about how a highly played area like withered works. but keep
    /// that change on other areas players play less in"). Server property monster_ai_stock_landblocks overrides the list.
    /// Evidence for the default: docs/findings/monster-ai-busy-areas-2026-11-25.md.</summary>
    public static class MonsterAreaRule
    {
        /// <summary>Default list: Withered Atoll plus the most played hunting areas, 2026-11-04..25 live logs.</summary>
        public const string DEFAULT_LIST =
            // Withered Atoll (every outdoor landblock with a Withered spawn, the atoll and its north end)
            "1A13,1A1E,1B12,1B13,1B14,1C12,1C13,1C14,1C15,1C1A,1D12,1D13,1D14,1E13,2111," +
            "0031," +                                   // Creepy Canyons
            "0103,0104," +                              // Black Spawn Den
            "5948,5A4A,5A48,5B48,5B4A," +               // Tusker Armory, Pits, Barracks, Holding, Quarters
            "6145,6146,6347," +                         // Matron Hives West, East, North
            "002B,00C8," +                              // Egg Orchard, Egg Orchard West
            "5B43,5B44";                                // Olthoi Arcade, Central Olthoi Arcade

        /// <summary>Parses a landblock list: tokens split on comma, space, semicolon or newline; each a 4-hex landblock
        /// ("1D13"), optionally 0x-prefixed, or an 8-hex cell id ("0x1D13003A", its landblock is used). Anything else is
        /// skipped and counted in <paramref name="bad"/>. The result is indexed by landblock (65536 entries).</summary>
        public static bool[] Parse(string raw, out int count, out int bad)
        {
            var set = new bool[65536];
            count = 0; bad = 0;
            if (string.IsNullOrWhiteSpace(raw)) return set;
            foreach (var part in raw.Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (t.StartsWith("0x") || t.StartsWith("0X")) t = t.Substring(2);
                if ((t.Length != 4 && t.Length != 8)
                    || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
                { bad++; continue; }
                int lb = (int)(t.Length == 8 ? v >> 16 : v);
                if (!set[lb]) { set[lb] = true; count++; }
            }
            return set;
        }

        /// <summary>The landblock a creature is judged by: its home (spawn point) when it has one, so a chase across a
        /// landblock border does not switch tactics on or off mid-fight; else where it stands.</summary>
        public static int Landblock(uint homeCell, uint locationCell) => (int)((homeCell != 0 ? homeCell : locationCell) >> 16);

        public static bool Stock(bool[] set, uint homeCell, uint locationCell)
            => set != null && set[Landblock(homeCell, locationCell)];
    }
}
