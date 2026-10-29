using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// HOUSE TIME CAPSULES, the pure part (no ACE types, so NetTest runs it: tools/nettest/TimeCapsuleCase.cs).
    /// Ours, not retail (owner 2026-10-29): retail houses kept whatever the last owner left, and buying an old house
    /// could turn up things you could no longer get. Some empty houses here look like a real player lived there and
    /// stopped paying rent: hooks mostly full (furniture where it belongs, the owner's theme pieces, pack clutter hung
    /// when packs were full), a stuffed storage chest, sometimes a note signed by a retail player. Lived-in houses
    /// cluster: some neighbourhoods are busy, and neighbours may be one owner's or friends' houses.
    /// Data: tools/house-capsules/themes.py -> TimeCapsuleThemes.cs (generated) and docs/HOUSE-TIME-CAPSULES.md.
    /// </summary>
    static partial class TimeCapsuleRule
    {
        internal enum Tier { Common, Uncommon, Rare }

        /// <summary>Parchment, retail's blank writable note: the previous owner's note is one page on it.</summary>
        internal const uint NOTE_WCID = 365;
        internal const long DAY = 86400;
        /// <summary>A released house must stand empty this long before it shows anything.</summary>
        internal const long MIN_VACANT = 7 * DAY;
        /// <summary>One chest find per account in this window (apartments have no 30-day purchase cooldown).</summary>
        internal const long ACCOUNT_WINDOW = 30 * DAY;
        internal const int COMMON_PCT = 78, UNCOMMON_PCT = 21;   // rare = the rest (1%: a house holds ~30 things now)
        internal const int NOTE_PCT = 40;
        /// <summary>Neighbourhoods (landblocks): this share is "lived-in", where hits are HOOD_HI times likelier;
        /// elsewhere HOOD_LO times. 0.35 x 2.0 + 0.65 x 0.4615 = 1, so each house size keeps its overall chance.</summary>
        internal const int HOOD_PCT = 35;
        internal const double HOOD_HI = 2.0, HOOD_LO = 0.4615;
        /// <summary>In a lived-in neighbourhood, this share of house groups is one owner's (same theme and signer).</summary>
        internal const int SHARED_PCT = 50;
        internal const int WALL = 2, FLOOR = 1, CEILING = 4, YARD = 8, ROOF = 16;

        internal readonly struct Entry
        {
            public readonly uint Wcid; public readonly Tier Tier; public readonly int Min, Max; public readonly bool Hook, Salvage; public readonly int HookType;
            public Entry(uint wcid, Tier tier, int min, int max, bool hook, bool salvage, int hookType = 0)
            { Wcid = wcid; Tier = tier; Min = min; Max = max; Hook = hook; Salvage = salvage; HookType = hookType; }
        }

        /// <summary>Household furniture: most copies in one house, and where it can hang.</summary>
        internal readonly struct Deco
        {
            public readonly uint Wcid; public readonly int MaxCopies, HookType;
            public Deco(uint wcid, int maxCopies, int hookType) { Wcid = wcid; MaxCopies = maxCopies; HookType = hookType; }
        }

        /// <summary>Pack clutter: a stack size range and where it can hang.</summary>
        internal readonly struct Clut
        {
            public readonly uint Wcid; public readonly int Min, Max, HookType;
            public Clut(uint wcid, int min, int max, int hookType) { Wcid = wcid; Min = min; Max = max; HookType = hookType; }
        }

        internal sealed class Theme
        {
            public readonly string Key, Name; public readonly int Weight;
            public readonly uint[][] Cores; public readonly Entry[] Fill; public readonly string[] Notes;
            public Theme(string key, string name, int weight, uint[][] cores, Entry[] fill, string[] notes)
            { Key = key; Name = name; Weight = weight; Cores = cores; Fill = fill; Notes = notes; }
        }

        internal sealed class Pick
        {
            public uint Wcid; public Tier Tier; public int Count = 1; public int HookType;
            public bool Salvage; public int Units, Workmanship10, NumItems;   // a salvage bag: units, avg workmanship x10, items salvaged
            public string Layer = "theme";                                   // theme, household or clutter (for logs and tests)
        }

        internal sealed class Stash
        {
            public Theme Theme;
            public readonly List<Pick> Chest = new List<Pick>();
            /// <summary>The theme's own hook pieces, in order; PlanHooks hangs them first, then furnishes and fills.</summary>
            public readonly List<Pick> Hooks = new List<Pick>();
            public ulong HookSeed;
            public int Note = -1;          // index into NoteTexts(Theme), or -1
            public ulong NameRoll;         // picks the previous owner's name from the retail names list
            public string Signer;          // a hand-authored house's fixed signature (else NameRoll picks one)
            public bool Fixed;             // hand-authored
            public bool Hit;               // this vacancy's chance came up (else the house stands empty)
            public bool LivedIn, Shared;   // its neighbourhood is a busy one; it is one of a single owner's houses there
            public int AgeDays;            // how long ago the "previous owner" left: items are dated before that
        }

        internal static int HitPct(int houseType) => houseType == 4 ? 25 : houseType == 1 ? 33 : houseType == 2 ? 40 : houseType == 3 ? 50 : 33;

        /// <summary>Chest items by house size (ACE's HouseType: 1 cottage, 2 villa, 3 mansion, 4 apartment).</summary>
        internal static (int min, int max) ChestSize(int houseType)
        {
            switch (houseType)
            {
                case 4: return (5, 8);
                case 1: return (8, 12);
                case 2: return (10, 15);
                case 3: return (12, 18);
                default: return (5, 8);
            }
        }

        /// <summary>How many of the theme's own pieces go up before the household furniture and clutter.</summary>
        internal static int ThemeHooks(int houseType) => houseType == 3 ? 10 : houseType == 2 ? 8 : houseType == 1 ? 6 : 2;

        internal static bool HangNow(bool owned, bool alreadyHung, bool eligible, bool hit) => !owned && !alreadyHung && eligible && hit;

        /// <summary>Whether a purchase finds the chest stash. vacantSince 0 = never owned (or released before tracking):
        /// eligible. existingItems: what storage and hooks already hold of a real owner's.</summary>
        internal static bool Eligible(long now, long vacantSince, int existingItems, long lastAccountFind, bool exempt, out string why)
        {
            if (existingItems > 0) { why = $"storage or hooks already hold {existingItems} item(s) (the last owner's own)"; return false; }
            if (vacantSince > 0 && now - vacantSince < MIN_VACANT)
            {
                why = $"empty only {(now - vacantSince) / 3600.0:F1} h of the {MIN_VACANT / 3600} h needed"; return false;
            }
            if (!exempt && lastAccountFind > 0 && now - lastAccountFind < ACCOUNT_WINDOW)
            {
                why = $"this account found a stash {(now - lastAccountFind) / (double)DAY:F1} days ago (one per {ACCOUNT_WINDOW / DAY} days)"; return false;
            }
            why = vacantSince > 0 ? $"empty {(now - vacantSince) / (double)DAY:F1} days" : "never owned since tracking began";
            return true;
        }

        /// <summary>Neighbourhood and owner group of a house, from its guid alone (stable across vacancies). Houses of a
        /// settlement are numbered in a row (cottages one apart, apartments about six apart), so a group is 3 houses
        /// in a row or about 4 apartments of one wing.</summary>
        internal static (bool livedIn, bool shared, ulong groupKey) Hood(uint houseGuid, int houseType)
        {
            uint lb = (houseGuid >> 12) & 0xFFFF;
            bool livedIn = Mix(lb ^ 0x6A09_E667_F3BC_C908UL) % 100 < (ulong)HOOD_PCT;
            uint grp = (houseGuid & 0xFFF) / (uint)(houseType == 4 ? 24 : 3);
            ulong groupKey = Mix(((ulong)lb << 20) ^ grp ^ 0xBB67_AE85_84CA_A73BUL);
            bool shared = livedIn && groupKey % 100 < (ulong)SHARED_PCT;
            return (livedIn, shared, groupKey);
        }

        /// <summary>The same house and vacancy always roll the same stash (a restart or @capsule info agrees with
        /// the purchase). vacancyKey is the vacancy's start (0 for a never-owned house).</summary>
        internal static Stash Roll(uint houseGuid, long vacancyKey, int houseType)
        {
            var fixedStash = FixedStash(houseGuid, houseType);
            if (fixedStash != null) return fixedStash;

            var (livedIn, shared, groupKey) = Hood(houseGuid, houseType);
            ulong h = Mix(((ulong)houseGuid << 32) ^ (ulong)vacancyKey ^ 0x4817_C0DE_5EED_0001UL);
            double pct = HitPct(houseType) * (livedIn ? HOOD_HI : HOOD_LO);
            ulong s = Mix(((ulong)houseGuid << 32) ^ (ulong)vacancyKey ^ 0x7C4F_A11E_D0C5_EEDUL);
            var stash = new Stash
            {
                Hit = (h % 10000) < (ulong)(pct * 100), LivedIn = livedIn, Shared = shared,
                AgeDays = 30 + (int)((h >> 16) % 900),
            };

            // One owner's houses share a theme and a signature; everything else is the house's own.
            ulong ts = shared ? groupKey : s;
            int total = Themes.Sum(t => t.Weight), r = (int)(Mix(ts ^ 0x7EE7) % (ulong)total);
            foreach (var t in Themes) { if (r < t.Weight) { stash.Theme = t; break; } r -= t.Weight; }
            Fill(stash, ref s, houseType);
            if ((int)(Next(ref s) % 100) < NOTE_PCT) stash.Note = (int)(Next(ref s) % (ulong)NoteTexts(stash.Theme).Count);
            stash.NameRoll = shared ? Mix(groupKey ^ 0x5161) : Next(ref s);
            stash.HookSeed = Next(ref s);
            return stash;
        }

        /// <summary>The chest (theme items, a matched core when the theme has one, then pack clutter) and the theme's
        /// own hook pieces.</summary>
        static void Fill(Stash stash, ref ulong s, int houseType)
        {
            var theme = stash.Theme;
            var used = new HashSet<uint>(stash.Chest.Select(p => p.Wcid).Concat(stash.Hooks.Select(p => p.Wcid)));
            var (min, max) = ChestSize(houseType);
            int n = min + (int)(Next(ref s) % (ulong)(max - min + 1));
            if (theme.Cores.Length > 0)
                foreach (var w in theme.Cores[(int)(Next(ref s) % (ulong)theme.Cores.Length)])
                    if (used.Add(w)) stash.Chest.Add(new Pick { Wcid = w, Tier = Tier.Common });
            int themed = Math.Max(stash.Chest.Count + 1, n * 6 / 10);
            for (int guard = 0; stash.Chest.Count < themed && guard < 200; guard++)
            {
                var p = Draw(theme, ref s, used, hookOnly: false);
                if (p != null) stash.Chest.Add(p);
            }
            for (int guard = 0; stash.Chest.Count < Math.Max(n, themed) && guard < 200; guard++)
            {
                var c = Clutter[(int)(Next(ref s) % (ulong)Clutter.Length)];
                if (!used.Add(c.Wcid)) continue;
                stash.Chest.Add(new Pick { Wcid = c.Wcid, Count = c.Min + (int)(Next(ref s) % (ulong)(c.Max - c.Min + 1)), HookType = c.HookType, Layer = "clutter" });
            }
            int want = ThemeHooks(houseType);
            for (int guard = 0; stash.Hooks.Count < want && guard < want * 4; guard++)
            {
                var p = Draw(theme, ref s, used, hookOnly: true);
                if (p != null) stash.Hooks.Add(p);
            }
        }

        /// <summary>One item of the theme: a tier roll (falling back to the next tier down when the theme has none, or
        /// for a common roll with no commons, an uncommon), then an entry of that tier not used yet. Null on a duplicate.
        /// fits: only entries whose HookType shares a bit with it (0 = any).</summary>
        static Pick Draw(Theme theme, ref ulong s, HashSet<uint> used, bool hookOnly, int fits = 0, bool allowRare = true)
        {
            int pct = (int)(Next(ref s) % 100);
            var tier = pct < COMMON_PCT ? Tier.Common : pct < COMMON_PCT + UNCOMMON_PCT || !allowRare ? Tier.Uncommon : Tier.Rare;
            bool Ok(Entry e) => (!hookOnly || e.Hook) && (fits == 0 || (e.HookType & fits) != 0);
            List<Entry> list = null;
            for (var t = tier; t >= Tier.Common; t--)
            {
                list = theme.Fill.Where(e => e.Tier == t && Ok(e)).ToList();
                if (list.Count > 0) break;
            }
            if (list.Count == 0 && tier == Tier.Common)
                list = theme.Fill.Where(e => e.Tier == Tier.Uncommon && Ok(e)).ToList();
            if (list == null || list.Count == 0) return null;
            var e = list[(int)(Next(ref s) % (ulong)list.Count)];
            if (!used.Add(e.Wcid)) return null;
            var p = new Pick { Wcid = e.Wcid, Tier = e.Tier, HookType = e.HookType };
            p.Count = e.Min + (int)(Next(ref s) % (ulong)(e.Max - e.Min + 1));
            if (e.Salvage)
            {
                p.Salvage = true; p.Count = 1;
                p.Units = 15 + (int)(Next(ref s) % 46);              // 15-60 units
                p.Workmanship10 = 30 + (int)(Next(ref s) % 51);      // 3.0-8.0
                p.NumItems = 2 + (int)(Next(ref s) % 5);             // from 2-6 items
            }
            return p;
        }

        /// <summary>Which item goes on which free hook (owner 2026-10-29: "players would fill all or most hooks",
        /// "decorate like a real house", "random items on the hooks too"). hookTypes: the house's free hooks in a
        /// stable order; usable: how many more ACE allows. Fills 70-100% of that (a hand-authored house at least 80%):
        /// the theme's pieces first (yard and roof first for what can stand outside), then for each remaining hook,
        /// by its kind, household furniture, pack clutter or more of the theme. Returns (hook index, item).</summary>
        internal static List<(int hook, Pick pick)> PlanHooks(Stash stash, IReadOnlyList<int> hookTypes, int usable)
        {
            var plan = new List<(int, Pick)>();
            int cap = Math.Min(hookTypes.Count, Math.Max(0, usable));
            if (cap == 0) return plan;
            ulong s = stash.HookSeed;
            int target = stash.Fixed ? Math.Max(Math.Min(cap, stash.Hooks.Count), (cap * 8 + 9) / 10)
                                     : (int)Math.Round(cap * (70 + (int)(Next(ref s) % 31)) / 100.0);
            var order = Enumerable.Range(0, hookTypes.Count).ToList();
            for (int i = order.Count - 1; i > 0; i--) { int j = (int)(Next(ref s) % (ulong)(i + 1)); (order[i], order[j]) = (order[j], order[i]); }
            var taken = new bool[hookTypes.Count];
            var used = new HashSet<uint>(stash.Chest.Select(p => p.Wcid));
            var copies = new Dictionary<uint, int>();
            // A theme piece already in the house counts against the furniture's copies (no second hanging rug).
            foreach (var p in stash.Hooks.Concat(stash.Chest)) copies[p.Wcid] = 99;

            foreach (var p in stash.Hooks)
            {
                if (plan.Count >= target) break;
                bool outside = (p.HookType & (YARD | ROOF)) != 0;
                int idx = order.Where(i => !taken[i] && (hookTypes[i] & p.HookType) != 0)
                               .OrderBy(i => outside && (hookTypes[i] & (YARD | ROOF)) != 0 ? 0 : 1).DefaultIfEmpty(-1).First();
                if (idx < 0) continue;
                taken[idx] = true; used.Add(p.Wcid); plan.Add((idx, p));
            }

            foreach (int idx in order)
            {
                if (plan.Count >= target) break;
                if (taken[idx]) continue;
                int ht = hookTypes[idx];
                // Floors take furniture; walls take as much clutter as decor; ceilings, yards and roofs decor.
                int r = (int)(Next(ref s) % 100);
                string[] layers = (ht & WALL) != 0 ? (r < 50 ? new[] { "clutter", "household", "theme" } : r < 80 ? new[] { "household", "clutter", "theme" } : new[] { "theme", "household", "clutter" })
                                : (ht & FLOOR) != 0 ? (r < 60 ? new[] { "household", "clutter", "theme" } : r < 85 ? new[] { "clutter", "household", "theme" } : new[] { "theme", "household", "clutter" })
                                : (r < 80 ? new[] { "household", "theme", "clutter" } : new[] { "theme", "household", "clutter" });
                Pick pick = null;
                foreach (var layer in layers)
                {
                    pick = layer == "household" ? PickDeco(ht, ref s, copies)
                         : layer == "clutter" ? PickClutter(ht, ref s, used)
                         : Draw(stash.Theme, ref s, used, hookOnly: true, fits: ht, allowRare: false);
                    if (pick != null) break;
                }
                if (pick == null) continue;
                taken[idx] = true; plan.Add((idx, pick));
            }
            return plan;
        }

        static Pick PickDeco(int ht, ref ulong s, Dictionary<uint, int> copies)
        {
            var list = Household.Where(d => (d.HookType & ht) != 0 && (copies.TryGetValue(d.Wcid, out var c) ? c : 0) < d.MaxCopies).ToList();
            if (list.Count == 0) return null;
            var d = list[(int)(Next(ref s) % (ulong)list.Count)];
            copies[d.Wcid] = (copies.TryGetValue(d.Wcid, out var n) ? n : 0) + 1;
            return new Pick { Wcid = d.Wcid, HookType = d.HookType, Layer = "household" };
        }

        static Pick PickClutter(int ht, ref ulong s, HashSet<uint> used)
        {
            var list = Clutter.Where(c => (c.HookType & ht) != 0 && !used.Contains(c.Wcid)).ToList();
            if (list.Count == 0) return null;
            var c = list[(int)(Next(ref s) % (ulong)list.Count)];
            used.Add(c.Wcid);
            return new Pick { Wcid = c.Wcid, Count = c.Min + (int)(Next(ref s) % (ulong)(c.Max - c.Min + 1)), HookType = c.HookType, Layer = "clutter" };
        }

        /// <summary>The theme's own notes, then the general ones.</summary>
        internal static List<string> NoteTexts(Theme theme) => theme.Notes.Concat(GeneralNotes).ToList();

        internal static readonly string[] GeneralNotes =
        {
            "To whoever buys this place next. The chest sticks in damp weather, so kick the left corner. Look after what I left.",
            "Rent is due again and the allegiance has scattered. Keep whatever is here. Some of it meant a lot to me.",
            "Gone hunting in the Direlands. If I am not back by the next Morningthaw, the house is yours, and so is everything in it.",
            "Welcome home, stranger. Someone lived here before you and someone will after. Leave something behind when you go.",
        };

        // ---- hand-authored houses: always these pieces and this note, then furnished like any lived-in house ----

        /// <summary>Owner 2026-10-29: "add a house in new canthalo that used to be owned by a player named xanzabar."
        /// New Cannthalo (66.5N 57.6E, landblock C7D2, the Olthoi Brood Hive settlement), the north cottage.
        /// "xanzabar's is the lower limit of what a house would be decorated like": his pieces, then filled like the rest.</summary>
        internal const uint XANZABAR_HOUSE = 0x7C7D21A4;

        internal static readonly Theme Xanzabar = new Theme("xanzabar", "Xanzabar's cottage", 0, new uint[0][],
            new[]
            {
                new Entry(7046, Tier.Common, 1, 1, false, false),            // Sclavus Tongue: New Cannthalo's own trophy
                new Entry(6412, Tier.Common, 1, 1, false, false),            // A Journal
                new Entry(9169, Tier.Common, 1, 1, false, false, 9),         // Plush Tusker
                new Entry(31421, Tier.Common, 1, 1, false, false, 1),        // Ursuin Toy
                new Entry(34267, Tier.Common, 1, 1, false, false, 9),        // Mystical Mug
                new Entry(353, Tier.Common, 1, 1, false, false, 2),          // Tachi
                new Entry(377, Tier.Common, 5, 5, false, false, 11),         // Potion of Healing x5
                new Entry(3679, Tier.Common, 1, 1, false, false, 2),         // Olthoi Claw: from the hive below
                new Entry(31366, Tier.Rare, 1, 1, true, false, 2),           // Decorative Ursuin Head (wall)
                new Entry(8152, Tier.Common, 1, 1, true, false, 2),          // Tusker Mask (wall)
                new Entry(22874, Tier.Uncommon, 1, 1, true, false, 9),       // Log Chair
                new Entry(246, Tier.Uncommon, 1, 1, true, false, 1),         // Hearth (floor)
                new Entry(25952, Tier.Uncommon, 1, 1, true, false, 24),      // Homecoming Pennant (yard or roof)
                new Entry(8149, Tier.Common, 1, 1, true, false, 2),          // Drudge Mask (wall)
                new Entry(3746, Tier.Common, 1, 1, true, false, 2),          // Auroch Horn Spear (wall)
                new Entry(25768, Tier.Uncommon, 1, 1, true, false, 2),       // Aluvian Hanging Rug (wall)
                new Entry(25758, Tier.Common, 1, 1, true, false, 9),         // Book Shelf
                new Entry(22845, Tier.Common, 1, 1, true, false, 9),         // Log Table
            },
            new[]
            {
                "To whoever moves in next,\n\nThis cottage was mine for more years than I like to count. Most nights I went down into the brood hive " +
                "and came home to this fire, tired and happy. The tusker on the wall was my first real trophy. The chair by the hearth is the " +
                "comfortable one. The pennant went up the year so many of us came home to Dereth.\n\nKeep what suits you. Dereth was good to me. " +
                "I hope it is good to you.\n\nXanzabar",
            });

        internal static Stash FixedStash(uint houseGuid, int houseType = 1)
        {
            if (houseGuid != XANZABAR_HOUSE) return null;
            var t = Xanzabar;
            ulong s = Mix(houseGuid ^ 0x5A4E_5A41_4252UL);
            var st = new Stash { Theme = t, Note = 0, Signer = "Xanzabar", Fixed = true, Hit = true, AgeDays = 400, LivedIn = true };
            foreach (var e in t.Fill)
                (e.Hook ? st.Hooks : st.Chest).Add(new Pick { Wcid = e.Wcid, Tier = e.Tier, Count = e.Min, HookType = e.HookType });
            // A real chest besides his keepsakes: his old pack clutter, to the size of any cottage's.
            var used = new HashSet<uint>(t.Fill.Select(e => e.Wcid));
            var (_, max) = ChestSize(houseType);
            for (int guard = 0; st.Chest.Count < max && guard < 200; guard++)
            {
                var c = Clutter[(int)(Next(ref s) % (ulong)Clutter.Length)];
                if (!used.Add(c.Wcid)) continue;
                st.Chest.Add(new Pick { Wcid = c.Wcid, Count = c.Min + (int)(Next(ref s) % (ulong)(c.Max - c.Min + 1)), HookType = c.HookType, Layer = "clutter" });
            }
            st.HookSeed = Next(ref s);
            return st;
        }

        // ---- the previous owner's name (owner 2026-10-29: real retail player names from our captures) ----

        /// <summary>Crude or joke names from the capture corpus that should never sign a note in someone's house.</summary>
        static readonly string[] s_Crude =
        {
            "chlamyd", "cawk", "bawl", "notsee", "wun-hung", "pregnant", "wench", "stoner", "freaky", "schtupp", "hentai", "xxx",
            "sniffer", "fuck", "shit", "dick", "cock", "penis", "porn", "nazi", "hitler", "rape", "fag", "cum ", "boob", "tits",
        };

        /// <summary>A name fit to sign a note: letters, spaces and ' - . only, 3-30 characters, not crude, not staff (+).</summary>
        internal static bool NameOk(string n)
        {
            if (string.IsNullOrWhiteSpace(n) || n.Length < 3 || n.Length > 30 || n.StartsWith("+")) return false;
            if (n.Any(c => !(char.IsLetter(c) || c == ' ' || c == '\'' || c == '-' || c == '.'))) return false;
            var l = n.ToLowerInvariant() + " ";
            return !s_Crude.Any(l.Contains);
        }

        /// <summary>Used when the retail names list is missing on the host (it never ships in git).</summary>
        internal static readonly string[] FallbackNames = { "a previous owner" };

        /// <summary>The name for this stash: walks the list from the roll, skipping names inUse says are taken here.</summary>
        internal static string PickName(IReadOnlyList<string> names, ulong roll, Func<string, bool> inUse)
        {
            if (names == null || names.Count == 0) return FallbackNames[0];
            int start = (int)(roll % (ulong)names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[(start + i) % names.Count];
                if (NameOk(n) && (inUse == null || !inUse(n))) return n;
            }
            return FallbackNames[0];
        }

        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        static ulong Next(ref ulong s) { s += 0x9E3779B97F4A7C15UL; return Mix(s); }
    }
}
