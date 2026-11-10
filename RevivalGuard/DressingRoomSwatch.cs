using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// The Dressing Room's colour swatches (owner ticket 261109-0030: "many color options dont look like their results. a
    /// grey square to show a blue tinted item"). A swatch was ONE palette entry: the middle of the first range of one
    /// sub-palette. A range is a dark-to-light ramp, and its middle entry is often near black or grey (Platemail
    /// Breastplate's Blue template gave #000000). Now a swatch is the average of EVERY entry the template writes into the
    /// item's dyed ranges (the sub-palettes whose palette set changes from template to template, so a trim every template
    /// shares does not grey it out), from the palette the shade picks, averaged in linear light so a ramp averages to the
    /// tone the eye sees. Pure: no ACE, so tools/dressing-room/swatchtest checks it against the real DAT palettes.
    /// </summary>
    public static class DressingRoomSwatch
    {
        /// <summary>The sub-palette positions a template choice changes: those whose palette set differs between templates.
        /// sets[t][j] = template t's j-th sub-palette's palette set. None differ (one template): every position.</summary>
        public static List<int> Dyed(IReadOnlyList<IReadOnlyList<uint>> sets)
        {
            int most = sets.Count == 0 ? 0 : sets.Max(s => s.Count);
            var r = new List<int>();
            for (int j = 0; j < most; j++)
                if (sets.Where(s => s.Count > j).Select(s => s[j]).Distinct().Count() > 1) r.Add(j);
            if (r.Count == 0) for (int j = 0; j < most; j++) r.Add(j);
            return r;
        }

        /// <summary>The palette a shade picks from a set of n (ACE PaletteSet.GetPaletteID).</summary>
        public static int PaletteIndex(int n, double shade)
        {
            if (n <= 0) return -1;
            int i = (int)Math.Floor(Math.Clamp(shade, 0, 1) * (n - 0.000001));
            return Math.Clamp(i, 0, n - 1);
        }

        /// <summary>The average colour (0xRRGGBB) of the palette entries in these ranges, in linear light. colours are the
        /// palette's ARGB entries; a range past the palette's end is cut. -1 when nothing is in range.</summary>
        public static int Average(IEnumerable<(IReadOnlyList<uint> Colors, uint Offset, uint Count)> ranges)
        {
            double r = 0, g = 0, b = 0; long n = 0;
            foreach (var (c, off, cnt) in ranges)
            {
                if (c == null) continue;
                long end = Math.Min((long)off + cnt, c.Count);
                for (long i = off; i < end; i++)
                {
                    uint v = c[(int)i];
                    r += Lin((v >> 16) & 255); g += Lin((v >> 8) & 255); b += Lin(v & 255); n++;
                }
            }
            if (n == 0) return -1;
            return (Srgb(r / n) << 16) | (Srgb(g / n) << 8) | Srgb(b / n);
        }

        public static string Hex(int rgb) => rgb < 0 ? "" : "#" + rgb.ToString("X6");

        static double Lin(uint c) => Math.Pow(c / 255.0, 2.2);
        static int Srgb(double l) => (int)Math.Round(Math.Clamp(Math.Pow(l, 1 / 2.2), 0, 1) * 255);
    }
}
