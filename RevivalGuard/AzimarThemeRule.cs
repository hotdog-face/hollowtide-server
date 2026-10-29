using System;

namespace RevivalGuard
{
    /// <summary>AzimarTheme's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class AzimarThemeRule
    {
        /// <summary>The themes, in the order tried when two festivals overlap (New Year's Day is also Frostfell's last).
        /// Festival Season has none: the Pumpkin Buffer has his spots that month.</summary>
        public static readonly string[] Themes = { "newyear", "frostfell", "spring", "wedding", "anniversary" };

        /// <summary>The theme showing: a staff preview ("none" = no theme) or else the first running festival of Themes.
        /// "" = none.</summary>
        public static string Resolve(Func<string, bool> festivalOn, string preview)
        {
            if (!string.IsNullOrEmpty(preview)) return preview == "none" ? "" : preview;
            foreach (var t in Themes) if (festivalOn(t)) return t;
            return "";
        }

        /// <summary>HOTEL SWANK REVIEW COPIES (owner 2026-10-25: "stage them for my review in hotel swank on live"): one Azimar
        /// per festival look, each wearing it all year (its palette in its own weenie rows, gen_buff_zefir.py REVIEW; its props
        /// from our client, AcAzimar.FIXED; its festival lines from AzimarVoice). The calendar never changes them.</summary>
        public static readonly System.Collections.Generic.Dictionary<uint, string> Fixed = new()
        {
            { 901633, "frostfell" }, { 901634, "spring" }, { 901635, "wedding" }, { 901636, "anniversary" }, { 901637, "newyear" },
        };

        public const uint WCID = 901631;

        /// <summary>The year-round Azimar or one of his review copies.</summary>
        public static bool IsAzimar(uint wcid) => wcid == WCID || Fixed.ContainsKey(wcid);

        /// <summary>A review copy's look, or null for the year-round Azimar (who follows the calendar).</summary>
        public static string FixedTheme(uint wcid) => Fixed.TryGetValue(wcid, out var t) ? t : null;

        /// <summary>His palette rows per theme (sub palette, first 8-colour unit, units); null = his own (the weenie's gold).
        /// The ramps are the DAT's smooth dark-to-bright family beside his gold 0x0400152C: 0x0400152B icy blue,
        /// 0x0400152D silver.</summary>
        public static (uint pal, ushort off, ushort len)[] PaletteFor(string theme) => theme switch
        {
            "frostfell" => new[] { (0x0400152Bu, (ushort)0, (ushort)224) },
            "wedding" => new[] { (0x0400152Du, (ushort)0, (ushort)224) },
            "newyear" => new[] { (0x0400152Du, (ushort)0, (ushort)224) },   // silver; the gold is his hat and sparkles
            _ => null,
        };
    }
}
