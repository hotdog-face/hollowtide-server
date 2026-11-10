using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// T'THUUN'S MARAUDERS, THE TOWN RAIDS, PURE (no ACE types): TthuunRaids.cs runs them, tools/event-director-test reads
    /// this file. Retail Nov 5, 2008 to Feb 2009 (Seeds of Corruption; wiki "T'Thuun's Marauders" rev 477303), revived on the
    /// calendar from Nov 16 to 29 (docs/EVENT-TESTS/tthuun-marauders.md). Each of the twelve retail sites is an Event Director
    /// entry (tthuun-*) gated on its own Director row; the creatures are retail's own (the Patriarch's Valley pyramid families).
    ///
    /// WAVES, AS RETAIL'S WALKTHROUGH: (1) the site's raiders or marauders (the wave generators of the datafix); once every one
    /// of them is down, (2) four Patriarch's Elite Raiders (raider sites) or Executioners (marauder sites); then (3) one
    /// Patriarch's Inquisitor or Champion; at Arwic, Ayan Baqur and Mayoi then (4) the town's Raid Commander, whose fall leaves
    /// a Spark for 3 minutes that gives anyone a Mana Forge Key. The last boss down is the raid won.
    ///
    /// STONES: every raid creature carries ITS site's Invoking Stone, 1 in 3, the wave 2 to 4 bosses always (wiki: "33% drop
    /// rate", "The bosses and sub bosses will always drop an invoking stone"). The retail creatures' own lists name four
    /// sites' stones (the Patriarch's Valley pyramids' mix); at a raid site those are swapped for the site's own.
    /// </summary>
    static class TthuunRaidsRule
    {
        public const string FESTIVAL = "tthuun";
        public const string SCOUT_ROW = "TthuunMarauders";   // the calendar row: Royal Scouts and pyramids at all twelve sites

        public sealed class Site
        {
            public string Key, Name, Row;
            public bool High;           // marauders (200-220) or raiders (160-185)
            public uint Stone;          // its Invoking Stone
            public uint Commander;      // the town's Raid Commander, 0 for none
            public uint Spark;          // the Spark his fall leaves
            public ushort Block;        // the landblock of its wave generators' centre
            public string Quest;        // its stones' quest prefix on the Royal Scouts (InvokingStone<Quest>30/60/90)
        }

        // wcids: retail creatures; ours from the datafix 2026-11-16-tthuun-1-raids.sql (904160-904181)
        public const uint ROYAL_SCOUT = 904160;
        public const uint SPARK_KAZYK = 904162, SPARK_MINIK = 904163, SPARK_NIVINIZK = 904164;
        public const uint KAZYK_COMMANDER = 88286, MINIK_COMMANDER = 88287, NIVINIZK_COMMANDER = 88288;
        public const uint EXECUTIONER = 39174, CHAMPION = 88300, ELITE_RAIDER = 88302, INQUISITOR = 88307;

        public static readonly Site[] Sites =
        {
            new Site { Key = "tthuun-toutou", Name = "Tou-Tou", Row = "DirectorTthuunToutou", High = true, Stone = 39094, Block = 0xF85D, Quest = "TouTou" },
            new Site { Key = "tthuun-isleoftears", Name = "the Isle of Tears", Row = "DirectorTthuunIsleOfTears", High = false, Stone = 39121, Block = 0xC29A, Quest = "IsleofTears" },
            new Site { Key = "tthuun-silyun", Name = "Silyun", Row = "DirectorTthuunSilyun", High = true, Stone = 39122, Block = 0x27EC, Quest = "Silyun" },
            new Site { Key = "tthuun-asheronsisland", Name = "Asheron's Island", Row = "DirectorTthuunAsheronsIsland", High = false, Stone = 39127, Block = 0xD995, Quest = "AsheronIsland" },
            new Site { Key = "tthuun-candethkeep", Name = "Candeth Keep", Row = "DirectorTthuunCandethKeep", High = true, Stone = 39125, Block = 0x2B12, Quest = "CandethKeep" },
            new Site { Key = "tthuun-colier", Name = "Colier", Row = "DirectorTthuunColier", High = false, Stone = 39124, Block = 0xAFC6, Quest = "Colier" },
            new Site { Key = "tthuun-osteth", Name = "the Southern Osteth Menhir Ring", Row = "DirectorTthuunOsteth", High = true, Stone = 88285, Block = 0xB128, Quest = "Osteth" },
            new Site { Key = "tthuun-esper", Name = "the Mount Esper crater", Row = "DirectorTthuunEsper", High = true, Stone = 39126, Block = 0x8FD1, Quest = "MountEsper" },
            new Site { Key = "tthuun-midsong", Name = "the Festival Stone of Midsong", Row = "DirectorTthuunMidsong", High = true, Stone = 39123, Block = 0x7E7F, Quest = "Festival" },
            new Site { Key = "tthuun-ayanbaqur", Name = "Ayan Baqur", Row = "DirectorTthuunAyanBaqur", High = true, Stone = 39129, Block = 0x1134, Quest = "Ayan", Commander = KAZYK_COMMANDER, Spark = SPARK_KAZYK },
            new Site { Key = "tthuun-arwic", Name = "Arwic", Row = "DirectorTthuunArwic", High = true, Stone = 39128, Block = 0xC6A9, Quest = "Arwic", Commander = MINIK_COMMANDER, Spark = SPARK_MINIK },
            new Site { Key = "tthuun-mayoi", Name = "Mayoi", Row = "DirectorTthuunMayoi", High = false, Stone = 39130, Block = 0xE532, Quest = "Mayoi", Commander = NIVINIZK_COMMANDER, Spark = SPARK_NIVINIZK },
        };

        public static Site ByKey(string key) =>
            key == null ? null : Sites.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        public static Site ByRow(string row) =>
            row == null ? null : Sites.FirstOrDefault(s => s.Row.Equals(row, StringComparison.OrdinalIgnoreCase));

        /// <summary>The twelve Invoking Stones (the Royal Scouts' turn-ins).</summary>
        public static readonly HashSet<uint> STONES = new HashSet<uint>(Sites.Select(s => s.Stone));

        /// <summary>The waves after the first, in order: what spawns and how many.</summary>
        public static (uint wcid, int count)[] Waves(Site s)
        {
            var w = new List<(uint, int)> { (s.High ? EXECUTIONER : ELITE_RAIDER, 4), (s.High ? CHAMPION : INQUISITOR, 1) };
            if (s.Commander != 0) w.Add((s.Commander, 1));
            return w.ToArray();
        }

        /// <summary>Wave 2 on (the Executioners, Elite Raiders, Champions, Inquisitors, Raid Commanders): their stone always.</summary>
        public static bool IsBoss(uint wcid) =>
            wcid == EXECUTIONER || wcid == CHAMPION || wcid == ELITE_RAIDER || wcid == INQUISITOR
            || wcid == KAZYK_COMMANDER || wcid == MINIK_COMMANDER || wcid == NIVINIZK_COMMANDER;

        public const double STONE_CHANCE = 1.0 / 3;

        /// <summary>THE KAREB MASK (owner 2026-11-08, ticket 261108-0035): retail's Kareb Mask 10920, unchanged (attuned,
        /// bonded), on a town-raid Raid Commander's corpse 1 time in 10. Only raid-spawned Commanders: the same weenies stand
        /// in the Patriarch's Valley pyramids all year and drop nothing new there.</summary>
        public const uint KAREB_MASK = 10920;
        public const double KAREB_CHANCE = 0.1;

        // ---- the Royal Scouts' kill tasks (wiki: "three kill tasks ... 50 Moarsmen, 50 Sclavus, and 50 Falatacot ...
        //      100 mill exp and a grand casino chest / mana forge key", "Timer for the kill tasks is 6 days") ----

        public const int TASK_KILLS = 50;
        public const long TASK_XP = 100_000_000, BOSS_XP = 5_000_000;
        public const uint GRAND_CASINO_KEY = 38992, MANA_FORGE_KEY = 38456;
        public const double TASK_WAIT_S = 6 * 24 * 3600;
        public static readonly string[] FAMILIES = { "Moarsman", "Sclavus", "Falatacot" };
        /// <summary>The kill counter a family's raiders and marauders stamp (their KillQuest, the datafix).</summary>
        public static string KillQuest(string family) => $"TthuunRaid{family}Kills";
        public static string PaidQuest(string family) => $"TthuunRaid{family}Paid";
        public const string TASK_WAIT = "TthuunRaidTaskWait";

        // titles (ACE CharacterTitle; awarded by nothing before this): Guardian in the Patriarch Raids 627, Warden 628,
        // Hero 629, Marked by the Patriarchs 630; Minik Ra's Bane 639, Kazyk Ri's Bane 640, Nivinizk's Bane 641
        public const uint TITLE_GUARDIAN = 627, TITLE_HERO = 629, TITLE_MARKED = 630;
        public const uint TITLE_MINIK_BANE = 639, TITLE_KAZYK_BANE = 640, TITLE_NIVINIZK_BANE = 641;

        /// <summary>The boss kills a Royal Scout rewards (wiki, Patriarch's Champion: "speak to a Royal Scout for a one time
        /// xp reward and a title"): the flag a kill stamps, the title, the XP and the scout's line.</summary>
        public static readonly (uint wcid, string flag, uint title, long xp, string line)[] BossCredits =
        {
            (CHAMPION, "TthuunRaidChampionKilled", TITLE_MARKED, BOSS_XP, "Another of the Patriarch's Champions down? You're a killing machine!"),
            (INQUISITOR, "TthuunRaidInquisitorKilled", TITLE_GUARDIAN, BOSS_XP, "One of the Patriarch's Inquisitors, gone for good? The Queen will hear of this."),
            (MINIK_COMMANDER, "TthuunRaidMinikRaKilled", TITLE_MINIK_BANE, 0, "You brought down Minik Ra's Raid Commander? Arwic owes you a great deal."),
            (KAZYK_COMMANDER, "TthuunRaidKazykRiKilled", TITLE_KAZYK_BANE, 0, "You brought down Kazyk Ri's Raid Commander? Ayan Baqur owes you a great deal."),
            (NIVINIZK_COMMANDER, "TthuunRaidNivinizkKilled", TITLE_NIVINIZK_BANE, 0, "You brought down Nivinizk's Raid Commander? Mayoi owes you a great deal."),
        };

        /// <summary>What a Royal Scout says about the kill tasks, given each family's count, whether it is already paid, and
        /// the seconds left on the 6-day wait (0 = none). Pure, for the tests: returns the lines and which families to pay.</summary>
        public static (List<string> lines, List<string> pay, bool start, bool allDone) TaskTalk(IReadOnlyDictionary<string, int> kills,
            IReadOnlyDictionary<string, bool> paid, bool active, double waitLeft)
        {
            var lines = new List<string>();
            var pay = new List<string>();
            if (!active)
            {
                if (waitLeft > 0)
                {
                    lines.Add($"You have done more than enough for now. Come back in {Wait(waitLeft)} and I will have more raiders for you.");
                    return (lines, pay, false, false);
                }
                lines.Add($"Kill {TASK_KILLS} Moarsman raiders or marauders, {TASK_KILLS} Sclavus and {TASK_KILLS} Falatacot, at any raid site. "
                          + "Come back to me as each fifty is done.");
                return (lines, pay, true, false);
            }
            var left = new List<string>();
            foreach (var f in FAMILIES)
            {
                int n = kills.TryGetValue(f, out var k) ? k : 0;
                bool done = paid.TryGetValue(f, out var p) && p;
                if (done) continue;
                if (n >= TASK_KILLS) pay.Add(f);
                else left.Add($"{n} of {TASK_KILLS} {f}");
            }
            if (pay.Count > 0) lines.Add("Let's see if you killed enough of those raiders...");
            bool allDone = left.Count == 0;
            if (allDone) lines.Add("That is all three. The Queen thanks you; come back in 6 days and there will be more of them.");
            else lines.Add("So far: " + string.Join(", ", left) + ".");
            return (lines, pay, false, allDone);
        }

        public static string Wait(double s)
        {
            if (s >= 2 * 86400) return $"{Math.Ceiling(s / 86400):0} days";
            if (s >= 7200) return $"{Math.Ceiling(s / 3600):0} hours";
            return $"{Math.Max(1, Math.Ceiling(s / 60)):0} minutes";
        }
    }
}
