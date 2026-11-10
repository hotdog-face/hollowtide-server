using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// THE FESTIVAL TABLE AND ITS RULES, PURE (no ACE types): SeasonalCalendar.cs runs them on the world loop, tools/nettest
    /// compiles this file by path and steps it through whole years (the yearly buffer swap, owner 2026-10-25), and
    /// tools/seasons/export_calendar.py, tools/seasons/preview.py and tools/journal/seasons.py parse the table's literals.
    /// Every window is a month and day, never a year: the table repeats every year by itself.
    /// </summary>
    public static partial class SeasonalCalendar
    {
        /// <summary>
        /// Retail's windows are stored as 05:01 and 04:59 UTC, which is 00:01 and 23:59 at UTC-5,
        /// in October as well as in December. That is US Eastern with daylight saving ignored, which
        /// is what a hand-built live-ops table looks like. Evaluating the calendar at a fixed -5
        /// reproduces the stored windows exactly instead of drifting an hour twice a year.
        /// (The two New Year rows, 80222 and 80224, were authored in plain UTC by someone else. The
        /// one-minute margins at each end are dropped here; a festival that starts at midnight
        /// rather than 00:01 is not a fidelity question anyone can observe.)
        /// </summary>
        internal const int RETAIL_UTC_OFFSET_HOURS = -5;

        /// <summary>A row of the table. tools/seasons/export_calendar.py parses these literals into
        /// Assets/StreamingAssets/seasons.json for the client's calendar: keep each field a plain literal.</summary>
        internal sealed class Festival
        {
            public string Key;              // what @festival takes
            public string Name;             // what a player is told
            public int StartMonth, StartDay, EndMonth, EndDay;   // inclusive both ends, local to UTC-5
            public string[] On = Array.Empty<string>();          // ace_world.event rows to start
            public string[] Off = Array.Empty<string>();         // rows to stop for the duration
            public string Announce;         // the world broadcast when it opens (seasons.json "about")
            public string Hint = "";        // where to find it, on the login line (FestivalNotices); placements checked on OVH 2026-10-21
            public string Evidence;         // the weenie this was read off, shown by @festival list
            public string Dressing;         // the town dressing it puts up (autumn, winter, spring): two never show at once
            // Waiting for the owner's yes (a revived retired event): off, silent and out of seasons.json until every On row
            // exists in ace_world, so applying its datafix is the switch (SeasonalCalendar.Installed).
            public bool Pending;

            public string Window => $"{StartMonth:00}-{StartDay:00} to {EndMonth:00}-{EndDay:00}";
        }

        /// <summary>
        /// THE CALENDAR. Every window and every bundle below is transcribed from a retail stopgap
        /// weenie; the id in Evidence is the `...stopgapgen` that carries the dates, and its
        /// `...stopgap!` child carries the StartEvent/StopEvent list. Nothing here was invented.
        ///
        /// Two rows are worth pausing on:
        ///  - Festival Season is the only festival that turns something OFF. `normaltowncrier` gates
        ///    `aljalimasign` (weenie 4658) in the three Al-Jalima landblocks, and Al-Jalima is where
        ///    the zombie incursion happens, so retail takes the ordinary town furniture down for the
        ///    month and puts a festival crier up in its place. It must go back On afterwards, which
        ///    is why Off lists are restored rather than merely stopped.
        ///  - Fireworks appears twice, from two different stopgaps (69999 for the anniversary week,
        ///    80223 under the New Year's Day gen). That is why desired state is computed as a union
        ///    over active festivals rather than per festival: two windows can want the same row.
        /// </summary>
        internal static readonly Festival[] Calendar =
        {
            new Festival
            {
                Key = "festivalseason", Name = "Festival Season",
                StartMonth = 10, StartDay = 1, EndMonth = 11, EndDay = 1,
                On  = new[] { "EventFallFestival" },
                // AzimarYearRound (ours, azimar-seasons.sql, owner 2026-10-25): Azimar stands in the Pumpkin Buffers'
                // spots the rest of the year; the pumpkins have them for the month
                Off = new[] { "normaltowncrier", "AzimarYearRound" },
                Announce = "The harvest has turned. Pumpkin Lords and Harvest Reapers walk the "
                         + "landscape, the dead are stirring in Al-Jalima, and the Majestic Pumpkin "
                         + "is waiting in Glenden Wood.",
                Hint = "Pumpkins line Holtburg, the Festival Costumer is in Glenden Wood, and the dead walk in Al-Jalima.",
                Evidence = "ace80069-fallfestivaleventsstopgapgen, Oct 1 to Nov 1", Dressing = "autumn",
            },
            new Festival
            {
                Key = "frostfell", Name = "Frostfell",
                StartMonth = 12, StartDay = 4, EndMonth = 1, EndDay = 1,
                On  = new[] { "assaultonfrosthavenevent", "GiftGopherEvent", "hiddenpresentsevent",
                              "HollyJollyHelperEvent", "SclavusSantaEvent" },
                Announce = "Winter has come to Dereth. Presents are hidden across the towns, Frost "
                         + "Haven is under assault, and something wearing a red suit has been seen "
                         + "in the desert.",
                Hint = "Look for hidden presents in the towns, and the Frostfell Costumer in Holtburg.",
                Evidence = "ace80018-holidayeventsstopgapgen, Dec 4 to Jan 1", Dressing = "winter",
            },
            new Festival
            {
                Key = "spring", Name = "the Spring Festival",
                StartMonth = 4, StartDay = 1, EndMonth = 5, EndDay = 1,
                On  = new[] { "springbabies", "springbunnyevent", "SpringEasterEggs", "IHOPopen" },
                Announce = "Spring has come to Dereth. Young creatures are on the landscape, "
                         + "coloured eggs are turning up, and the House of Pancakes has opened.",
                Hint = "Look for eggs and young creatures on the landscape, the House of Pancakes in Shoushi, and the Spring Costumer in Holtburg.",
                Evidence = "ace87093-springeventsstopgapgen, Apr 1 to May 1", Dressing = "spring",
            },
            new Festival
            {
                // Not a stopgap bundle: the April 2003 Raining Mad Cows Gen (23631, placed 22 times)
                // carried its own RealTime window, 2026-04-01 00:01 to 23:59 at UTC-5, and no event row.
                // Moved onto the calendar 2026-09-28 (owner-approved) on AprilFoolsMadCows, a row this
                // project added: tools/gpubox-ace/stones-madcow-calendar.sql. Inside the Spring Festival's
                // window, which is fine: desired state is a union over active festivals.
                Key = "aprilfools", Name = "April Fools' Day",
                StartMonth = 4, StartDay = 1, EndMonth = 4, EndDay = 1,
                On  = new[] { "AprilFoolsMadCows" },
                Announce = "It is raining cows. They are not happy about it.",
                Hint = "Look up, and mind the cows.",
                Evidence = "ace23631 April 2003 Raining Mad Cows Gen's own window, Apr 1 only",
            },
            new Festival
            {
                Key = "wedding", Name = "the Royal Wedding Anniversary",
                StartMonth = 5, StartDay = 1, EndMonth = 6, EndDay = 1,
                On  = new[] { "ReceptionGames" },
                Announce = "The Royal Wedding anniversary is being kept. The reception games have "
                         + "begun again.",
                Hint = "",
                Evidence = "ace80228-royalweddinganniversarystopgapgen, May 1 to Jun 1",
            },
            new Festival
            {
                // AnniversaryFestivalStones is a row this project added (2026-09-28, owner-approved): the
                // twenty Festival Stones (5376-5395, "Rejoice! The Hopeslayer has been defeated") carried
                // their own RealTime window, 2026-11-02 to 11-05 at UTC-5, the same four days as this
                // stopgap, and would never have come back. tools/gpubox-ace/stones-madcow-calendar.sql.
                Key = "anniversary", Name = "the Anniversary",
                StartMonth = 11, StartDay = 2, EndMonth = 11, EndDay = 5,
                On  = new[] { "Fireworks", "AnniversaryFestivalStones" },
                Announce = "Another year of Dereth. There are fireworks over the towns, and the Festival "
                         + "Stones have risen again.",
                Hint = "Look for fireworks over the towns, the Festival Stones across Dereth, and the Anniversary Costumer in Cragstone.",
                Evidence = "ace69999-fireworksstopgapgen, Nov 2 to Nov 5 (and the Festival Stones' own window)",
            },
            new Festival
            {
                // Not a stopgap bundle: the Night Club Attendant Generator (69997, placed at 0x33D9,
                // 0x8090, 0xBC9F and 0xE74E) carried its OWN RealTime window, 1793595660-1796187540, and
                // no event row, so it would have opened in November 2026 and never again. Moved onto
                // this calendar 2026-09-28 (owner-approved): tools/gpubox-ace/nightclub-calendar.sql makes
                // it an Event generator gated on AnniversaryNightClub, a row this project added. The
                // window is its own, Nov 2 00:01 to Dec 1 23:59 at UTC-5, the Night Club's retail month.
                Key = "nightclub", Name = "the Anniversary Night Club",
                StartMonth = 11, StartDay = 2, EndMonth = 12, EndDay = 1,
                On  = new[] { "AnniversaryNightClub" },
                Announce = "The Night Club is open for the anniversary. Its attendants are waiting in "
                         + "Cragstone, Hebian-To, Sanamar and Zaikhal. Dance on the floor for a minute and the "
                         + "night counts; three nights earn the Anniversary Balloons for your house. Join the DJ's "
                         + "dance calls for a party experience bonus.",
                Hint = "Its attendants are in Cragstone, Hebian-To, Sanamar and Zaikhal. Dance a minute for the night, "
                     + "and join the DJ's calls for a party experience bonus.",
                Evidence = "ace69997-nightclubattendantgenerator's own window, Nov 2 to Dec 1",
            },
            new Festival
            {
                // NOT A RETAIL STOPGAP: T'thuun's Marauders was a staff-run live event (Nov 5, 2008 to Feb 2009; the town
                // pyramids went live Nov 16, 2008), retired into the permanent Patriarch's Valley quest. Revived 2026-11-01
                // (owner: every retired live event on a yearly calendar; docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/
                // tthuun-marauders.md). TthuunMarauders is a row this project added (2026-11-16-tthuun-1-raids.sql): the
                // Royal Scouts and the Falatacot pyramids at the twelve sites. The raids themselves are Event Director
                // entries (tthuun-*), one site at a time, while this festival is on.
                Key = "tthuun", Name = "T'thuun's Marauders",
                StartMonth = 11, StartDay = 16, EndMonth = 11, EndDay = 29,
                On  = new[] { "TthuunMarauders" },
                Announce = "Floating pyramids hang over Arwic, Ayan Baqur and Mayoi, and the Patriarchs' devices have opened "
                         + "at nine more places in Dereth. T'thuun's Marauders are raiding, and the Queen's Royal Scouts "
                         + "are paying for every one put down.",
                Hint = "Look for the pyramids over Arwic, Ayan Baqur and Mayoi and the Royal Scouts at all twelve raid sites; @events says which is raided now.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (no stopgap: a staff-run event), Nov 16 to 29 from the Nov 16, 2008 town raids",
            },
            new Festival
            {
                // Not a stopgap bundle: retail's Fowl Play (Nov 12 2013) made Don't Feed the Fowl seasonal, with its own
                // dialog and the title Cold Turkey; the Spring 2014 Update made the quest permanent and dropped both. The
                // window is ours (owner 2026-11-01: retired events on a yearly calendar; US Thanksgiving week). FowlPlay
                // and FowlPlayColdTurkey are rows this project added (2026-11-20-fowlplay-1-aroyd-gobbles.sql): Ar'Oyd's
                // 2013 lines, Cold Turkey 855 on the feather, Gobbles (Nov 2005 live event) and Jive Turkey 856. No town
                // dressing, so it never displaces the Night Club or Frostfell.
                Key = "fowlplay", Name = "Fowl Play",
                StartMonth = 11, StartDay = 20, EndMonth = 11, EndDay = 30,
                On  = new[] { "FowlPlay", "FowlPlayColdTurkey" },
                Announce = "Farmer Ar'Oyd's feast near Nanto has gone badly wrong. Something huge is loose under his "
                         + "barn, and his prize tom Gobbles has run wild.",
                Hint = "Farmer Ar'Oyd is at Blue Ghost Falls near Nanto (53.1S 80.7E); Gobbles roams his farm.",
                Evidence = "wiki Fowl Play rev 473890 and Don't Feed the Fowl rev 478663 (Retired Seasonal Dialog); window ours, Nov 20 to Nov 30",
            },
            new Festival
            {
                Key = "newyear", Name = "the New Year",
                StartMonth = 1, StartDay = 1, EndMonth = 2, EndDay = 1,
                On  = new[] { "EventNewYear" },
                Announce = "A new year begins in Dereth.",
                Hint = "",
                Evidence = "ace80222-newyeareventsstopgapgen, Jan 1 to Feb 1",
            },
            new Festival
            {
                Key = "newyearsday", Name = "New Year's Day",
                StartMonth = 1, StartDay = 1, EndMonth = 1, EndDay = 1,
                On  = new[] { "Fireworks" },
                Announce = "Fireworks over Dereth for the new year.",
                Hint = "Look up for fireworks over the towns.",
                Evidence = "ace80224-newyearsdayeventsstopgapgen, Jan 1 only",
            },
            // RETIRED LIVE EVENTS, revived on the yearly calendar (owner 2026-11-01; docs/EVENTS-RETIRED.md, its "Year
            // calendar" windows). Not retail stopgaps: retail ran these once, by hand. Their rows and content come from
            // tools/gpubox-ace/gen_retired_feb2.py; Pending until the owner's yes. The two share Feb 2 to 8 without a clash:
            // Frore (0x0138) and the Portal to Bur (0xA5D3) are far apart, and neither puts up town dressing.
            new Festival
            {
                // The Great Work Crystal, Dec 16 1999 (Sudden Season): wiki "The Great Work Crystal" rev 471967. Its bosses
                // (4991-4993, 5867, 5891) were in ace_world but placed nowhere. The Announce is retail's town crier rumors.
                Key = "greatwork", Name = "the Great Work Crystal",
                StartMonth = 2, StartDay = 2, EndMonth = 2, EndDay = 14,
                On  = new[] { "GreatWorkCrystal" },
                Announce = "Sir Joffre Tremblant's adventuring party has gone missing. The group was last seen heading north "
                         + "from Arwic. Some say the sun has grown slightly dimmer, and that it has brought on the sudden chill.",
                Hint = "Mairisa bint Fuda in Plateau Village (44.4N, 43.4W) sends travellers to the Lost City of Frore, where the Gelidite lords and the Great Work wait.",
                Evidence = "retail live event 1999-12-16 (wiki rev 471967), Feb 2 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Bur Portal Summoning Ritual, Jan 22 to Feb 2 2007 (Bridging the Vast Divide): wiki rev 443270. The
                // Announce is retail's town crier proclamation, shortened to one chat line (300 characters with the
                // Discord lead); the Falatacot assault is the director's "bur-ritual".
                Key = "burritual", Name = "the Bur Portal Summoning Ritual",
                StartMonth = 2, StartDay = 2, EndMonth = 2, EndDay = 8,
                On  = new[] { "BurPortalRitual" },
                Announce = "Prince Borelean Strathelar will try to summon a portal to Bur and find Asheron, held by the vile "
                         + "Falatacot. All of stout heart are asked to help, for the Falatacot will oppose him.",
                Hint = "Hoshino Kei and the Prince are at the Portal to Bur (67.4N, 30.5E).",
                Evidence = "retail live event 2007-01-22 (wiki rev 443270), Feb 2 to 8 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Children of the Prodigal Lord (Dec 2007 / Jan 2008): two live events in one window, wiki "Prodigal Human
                // Investigation" rev 479445 and "Aerbax's Prodigal Children" rev 475683. The investigation is a quest window
                // (Tailen the Tracker at Rithwic and at the Hidden Laboratory, row ProdigalInvestigation); the three waves near
                // Yaraq are the director's "prodigal-children". Retail's announcements are not recorded: the Announce is ours.
                // Shares Feb 9 to 14 with the Great Work Crystal: Rithwic, Stonehold and Yaraq are far from Frore, no dressing.
                Key = "prodigal", Name = "the Children of the Prodigal Lord",
                StartMonth = 2, StartDay = 9, EndMonth = 2, EndDay = 15,
                On  = new[] { "ProdigalInvestigation" },
                Announce = "Tailen the Tracker has found the trail of Aerbax's altered creatures, from Rithwic to a hidden laboratory "
                         + "near Stonehold, and Aerbax is testing its Prodigal Children in the desert by Yaraq.",
                Hint = "Tailen the Tracker waits by Martine's Retreat in Rithwic (10.6N, 58.3E).",
                Evidence = "retail live events 2007-12-06 and 2008-01-07 (wiki revs 479445, 475683), Feb 9 to 15 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Gauntlet (Feb 2000, Shadows of the Past): wiki rev 461903. A quest window on the Singularity Caul (row
                // GauntletTrial): A Presence at the arrival hands a 30-minute Shadow Key; retail's prize chest (6423) waits
                // locked on the island's eastern hilltop past retail's Shadows. The Announce is retail's town crier rumor
                // in spirit (its words are not recorded). No dressing.
                Key = "gauntlet", Name = "the Gauntlet",
                StartMonth = 2, StartDay = 15, EndMonth = 2, EndDay = 21,
                On  = new[] { "GauntletTrial" },
                Announce = "A presence stirs on the Singularity Caul, challenging the bold to outrun the Shadows to a Shadow "
                         + "Stone on the island's far hill.",
                Hint = "A Presence waits where the Singularity Caul's portal lands you (98.0S, 94.7W).",
                Evidence = "retail live event Feb 2000 (wiki rev 461903), Feb 15 to 21 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Carlo di Cenza's Journal (Feb 2005, Infiltration): wiki rev 479111. A quest window: Carlo di Cenza in Rithwic
                // and Antius Blackmoor in Yaraq stand for it (row CarloJournal); the pages, the binding and the recipes are in
                // the world all year. Retail's announcement is not recorded: the Announce is ours. No dressing.
                Key = "carlo", Name = "Carlo di Cenza's Journal",
                StartMonth = 2, StartDay = 16, EndMonth = 2, EndDay = 28,
                On  = new[] { "CarloJournal" },
                Announce = "A Viamontian, Carlo di Cenza, has been found wandering in Rithwic, unable to speak sense. Antius "
                         + "Blackmoor waits in Yaraq for any word of where he came from.",
                Hint = "Carlo di Cenza stands by the Empty Book Binding in Rithwic (10.8N, 58.2E); Antius Blackmoor is by Yaraq's fountain.",
                Evidence = "retail live event Feb 2005 (wiki rev 479111), Feb 16 to 28 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The House Mhoire Risings (Feb 25-26 2012, "All Servers: Undead Attacks"): wiki "2012 Live Events" rev 443457.
                // Two director raids, "mhoire-tairla" near Dryreach and "mhoire-pyreoak" near Ayan Baqur; MhoireRisings is
                // only the Pending switch (nothing hangs on it). The Announce is ours (retail's broadcasts are not recorded).
                Key = "mhoire", Name = "the House Mhoire Risings",
                StartMonth = 2, StartDay = 20, EndMonth = 3, EndDay = 6,
                On  = new[] { "MhoireRisings" },
                Announce = "The dead of House Mhoire stir in the Graveyard. Lady Tairla Mhoire and a Corrupted Pyre Oak have been "
                         + "seen walking far from it, near Dryreach and Ayan Baqur.",
                Hint = "House Mhoire's corrupted dead raid near Dryreach and Ayan Baqur a few times a day; watch for the broadcast.",
                Evidence = "retail live events 2012-02-25/26 (wiki rev 443457), Feb 20 to Mar 6 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Dranith Menacet in the Convergent Median (Persuasion / Repercussions, 2002): wiki revs 443229, 451160. A quest
                // window (row DranithMedian): retail's own small Dranith 15272 stands in the Median, with his retail death
                // broadcast. Retail's announcement is not recorded: the Announce is ours. No dressing.
                Key = "dranith", Name = "Dranith Menacet in the Convergent Median",
                StartMonth = 3, StartDay = 1, EndMonth = 3, EndDay = 7,
                On  = new[] { "DranithMedian" },
                Announce = "Dranith Menacet, the hollow son Martine made, has been seen in the Convergent Median, weakened and "
                         + "calling for his father.",
                Hint = "The Convergent Median's portal is at 87.0S, 40.3W.",
                Evidence = "retail live events 2002 (Persuasion, Repercussions; wiki revs 443229, 451160), Mar 1 to 7 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The 2009 Hero Token raids (Mar 16 and Apr 1 2009): wiki "2009 Live Events" rev 443669. Two director raids at
                // Hebian-To ("hero-knath-hebian", "hero-apostate-hebian"); HeroRaids is only the Pending switch. The Announce
                // is ours (retail's broadcasts are not recorded). No dressing.
                Key = "heroraids", Name = "the Hero Token Raids",
                StartMonth = 3, StartDay = 14, EndMonth = 3, EndDay = 28,
                On  = new[] { "HeroRaids" },
                Announce = "The K'nath and the Apostates are gathering south of Hebian-To. Those who hold the town will be paid in "
                         + "Hero Tokens.",
                Hint = "Raids on Hebian-To (39.6S, 83.2E) a few times a day pay a Hero Token to every defender; watch for the broadcast.",
                Evidence = "retail live events 2009-03-16 and 2009-04-01 (wiki rev 443669), Mar 14 to 28 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Bronze Statues Attack (Betrayal / Hidden Vein, Apr 1 to May 16 2002): wiki revs 446171, 455238. A quest
                // window (row BronzeStatuesAttack): retail's living statues come off the pedestals of 33 towns every 3.5 hours
                // and drop the player bronze set. Retail's announcement is not recorded: the Announce is ours. No dressing
                // (the Nullified Statues stay as they are all year).
                Key = "bronzestatues", Name = "the Bronze Statues Attack",
                StartMonth = 3, StartDay = 18, EndMonth = 3, EndDay = 31,
                On  = new[] { "BronzeStatuesAttack" },
                Announce = "The bronze statues of Dereth's towns are stirring on their pedestals. Some have already stepped down, "
                         + "and they are not friendly.",
                Hint = "Living bronze statues come off the town pedestals every few hours; their oversized bronze weapons go to the Arcanum Researcher.",
                Evidence = "retail live event 2002-04-01 to 05-16 (wiki revs 446171, 455238), Mar 18 to 31 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Captain Burnja Attacks the Windmill (Strange Sightings, Apr 2007): wiki rev 443260. One director raid,
                // "burnja-windmill"; BurnjaRaids is only the Pending switch. The Announce is ours. No dressing.
                Key = "burnja", Name = "Captain Burnja's Windmill Raids",
                StartMonth = 3, StartDay = 22, EndMonth = 3, EndDay = 31,
                On  = new[] { "BurnjaRaids" },
                Announce = "Captain Burnja's drudges have been seen with torches near the old windmill outside Cragstone.",
                Hint = "Captain Burnja raids the windmill near Cragstone (25.0N, 45.5E) a few times a day; his token is for Jarvis Hammerstone.",
                Evidence = "retail live event Apr 2007 (wiki rev 443260), Mar 22 to 31 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Summoning Ritual of T'thuun, the first ritual (Sins of the Fathers, Dec 12 2008): wiki rev 475388. One day
                // (row TthuunRitualAsheron): Asheron at the live Summoning T'thuun quest's site gives retail's first-ritual
                // rewards to those Captain Tulmada has just paid. The Announce is ours. No dressing.
                Key = "tthuunritual", Name = "the Summoning Ritual of T'thuun",
                StartMonth = 3, StartDay = 28, EndMonth = 3, EndDay = 28,
                On  = new[] { "TthuunRitualAsheron" },
                Announce = "Asheron himself has come to the Patriarch's Valley to watch the Summoning Ritual of T'thuun be broken, "
                         + "and to reward those who break it.",
                Hint = "Break the Summoning Ritual of T'thuun in the Patriarch's Valley today; Asheron waits by the ritual site.",
                Evidence = "retail live event 2008-12-12 (wiki rev 475388), Mar 28 per docs/EVENTS-RETIRED.md (Dec 12 is inside Frostfell)", Pending = true,
            },
            new Festival
            {
                // The Burun Raids (Mired Hearts, May 12 to Jun 2 2004): wiki rev 447715. Five director raids ("burun-<town>")
                // on retail's own Event<Town>Vagrants rows; BurunRaids is only the Pending switch. The Announce is ours.
                Key = "burunraids", Name = "the Burun Raids",
                StartMonth = 6, StartDay = 1, EndMonth = 6, EndDay = 14,
                On  = new[] { "BurunRaids" },
                Announce = "Burun raiding parties are coming out of the swamps against Hebian-To, Sawato, Shoushi, Tou-Tou and Yanshi.",
                Hint = "Burun raids hit Hebian-To, Sawato, Shoushi, Tou-Tou and Yanshi a few times a day; their leaders carry Burun Idols for Aun Laokhe in Arwic.",
                Evidence = "retail live event 2004-05-12 to 06-02 (wiki rev 447715), Jun 1 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Viridian incursion at Baishi (Harvestgain, Apr 24 2015): wiki rev 473904. One director raid,
                // "viridian-baishi"; ViridianIncursion is only the Pending switch. Shares Jun 1 to 14 with the Burun Raids (other
                // towns). The Announce is ours. No dressing.
                Key = "viridianbaishi", Name = "the Viridian Incursion at Baishi",
                StartMonth = 6, StartDay = 1, EndMonth = 6, EndDay = 14,
                On  = new[] { "ViridianIncursion" },
                Announce = "The Viridian Rise is stirring. Zerzelikyr, its Elder Gromnie Champion, has been seen in the thorns near Baishi.",
                Hint = "Zerzelikyr and the Viridian Rise raid a couple of clicks outside Baishi a few times a day; watch for the broadcast.",
                Evidence = "retail live event 2015-04-24 (wiki rev 473904), Jun 1 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Virindi Invade Ayan Baqur (Spring's Sorrows, Mar 31 to about Jun 14 2001): wiki rev 470495. A town takeover:
                // On VirindiAyanBaqur (retail's Virindi townsfolk on the humans' spots, Adirred in the Panopticon), Off
                // AyanBaqurTownsfolk (the twelve humans, put on that row by the datafix; restored when the window ends).
                // The Announce is ours. No dressing.
                Key = "virindiayan", Name = "the Virindi of Ayan Baqur",
                StartMonth = 6, StartDay = 1, EndMonth = 6, EndDay = 14,
                On  = new[] { "VirindiAyanBaqur" },
                Off = new[] { "AyanBaqurTownsfolk" },
                Announce = "The people of Ayan Baqur are gone, and Virindi stand in their places. Some say the townsfolk were taken "
                         + "to the Panopticon.",
                Hint = "Virindi keep Ayan Baqur's shops; Adirred the Theoretician waits in the Panopticon near the town.",
                Evidence = "retail live event 2001-03-31 to 06-14 (wiki rev 470495), Jun 1 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Martine Attacks (Hidden Vein, May to Jun 2002): wiki rev 443396. Martine and the Virindi Renegades stalking
                // him, at one of two spots a few times a day (two director entries); the Renegade is a renamed copy of retail's
                // L69 Virindi Master (owner 2026-11-09). The Announce is ours. No dressing.
                Key = "martineattacks", Name = "Martine Attacks",
                StartMonth = 6, StartDay = 1, EndMonth = 6, EndDay = 7,
                On  = new[] { "MartineAttacks" },
                Announce = "Martine has been seen in the wilds, and Virindi Renegades are hunting him.",
                Hint = "Martine turns up on the Obsidian Plains or Marae Lassel a few times a day; kill the Virindi Renegades and take their tokens to him.",
                Evidence = "retail live event May to Jun 2002 (wiki rev 443396), Jun 1 to 7 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Scavenger Hunt (beta, 1999-06-14): wiki rev 475541. A switch row only: ScavengerHunt.cs runs the six
                // hours (18:00 to midnight) and the heralds' row ScavengerHunt itself. The Announce is ours. No dressing.
                Key = "scavengerhunt", Name = "the Scavenger Hunt",
                StartMonth = 6, StartDay = 14, EndMonth = 6, EndDay = 14,
                On  = new[] { "ScavengerHuntSeason" },
                Announce = "The Advocates are restless today. Tonight they will call a Scavenger Hunt for monster trophies.",
                Hint = "From 6 PM to midnight, bring monster trophies to the Herald of the Advocates in Holtburg, Shoushi or Yaraq; the three best collections are rewarded.",
                Evidence = "retail beta event 1999-06-14 (wiki rev 475541), Jun 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Viamontian Invaders of the Meeting Halls (Ashes and Dust, Nov 2005): wiki rev 476535 (Glenden Wood Invaders,
                // Trivia). A quest window (row MeetingHallInvaders): Viamontian strike forces and their leaders in the Rithwic,
                // Cragstone, Zaikhal and Arwic Meeting Halls, a captain in each, Tomo Genza pays and Londigul mends the helms.
                // Shares November with the Night Club (no dressing on either). The Announce is ours.
                Key = "meetinghalls", Name = "the Viamontian Invaders of the Meeting Halls",
                StartMonth = 11, StartDay = 6, EndMonth = 11, EndDay = 15,
                On  = new[] { "MeetingHallInvaders" },
                Announce = "Viamontian strike forces have seized the Meeting Halls of Rithwic, Cragstone, Zaikhal and Arwic. "
                         + "Tomo Genza of the Glenden Wood Militia is paying for their leaders' heads.",
                Hint = "Viamontians hold the Meeting Halls of Rithwic, Cragstone, Zaikhal and Arwic; kill a Strike Force Leader and see Tomo Genza in Glenden Wood.",
                Evidence = "retail live event Nov 2005 (wiki rev 476535), Nov 6 to 15 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Shard of the Herald (Should the Stars Fall, Nov 14 2000): wiki rev 460139. A quest window (row
                // ShardOfTheHerald): the Dericost camp outside Ithaenc Cathedral, Lady Aerfalle among the kneeling undead, the
                // Shadows and the Governess on Savao, the Silver Key, Kelderam's Tomb and the Shard in the catacombs; Ketnan's
                // robes. The act before the Lightbringer's Week. Shares November with the Night Club. The Announce is ours.
                Key = "shardherald", Name = "the Shard of the Herald",
                StartMonth = 11, StartDay = 18, EndMonth = 11, EndDay = 30,
                On  = new[] { "ShardOfTheHerald" },
                Announce = "The undead have gathered at Ithaenc Cathedral. Anadil of Shakrassekor and his spymaster hold a camp "
                         + "before it, and beneath the cathedral they guard the last of the Soul Crystals: the Shard of the Herald.",
                Hint = "Anadil's camp is outside Ithaenc Cathedral (83.3S 93.2E); three silver key pieces open Kelderam's Tomb in the cathedral's basement, and the Shard waits in the catacombs below.",
                Evidence = "retail live event 2000-11-14 (wiki rev 460139), Nov 18 to 30 per docs/EVENTS-RETIRED.md", Pending = true,
            },
        };

        /// <summary>
        /// Is this (month, day) inside the festival's window? Both ends are inclusive days, and the
        /// comparison is on MMDD so that Frostfell, which starts in December and ends in January,
        /// works without knowing which year either end is in.
        /// </summary>
        internal static bool InWindow(Festival f, DateTime local)
        {
            int cur = local.Month * 100 + local.Day;
            int start = f.StartMonth * 100 + f.StartDay;
            int end = f.EndMonth * 100 + f.EndDay;
            return start <= end ? (cur >= start && cur <= end) : (cur >= start || cur <= end);
        }

        /// <summary>
        /// What every event row the table names should be, given which festivals are active: ON beats OFF; a row some
        /// festival SUPPRESSES (its Off list) is part of the world's ordinary state, so outside that festival it belongs
        /// On (normaltowncrier, AzimarYearRound). Nothing ends up in both sets. Apply moves the rows that differ.
        /// </summary>
        internal static (HashSet<string> on, HashSet<string> off) Desired(ICollection<Festival> active)
        {
            var wantOn = new HashSet<string>(active.SelectMany(f => f.On), StringComparer.OrdinalIgnoreCase);
            var wantOff = new HashSet<string>(active.SelectMany(f => f.Off), StringComparer.OrdinalIgnoreCase);
            wantOff.ExceptWith(wantOn);
            foreach (var f in Calendar)
            {
                foreach (var name in f.On)
                    if (!wantOn.Contains(name)) wantOff.Add(name);
                foreach (var name in f.Off)
                    if (!wantOff.Contains(name)) wantOn.Add(name);
            }
            wantOff.ExceptWith(wantOn);
            return (wantOn, wantOff);
        }

        /// <summary>A forced festival hands itself back to the calendar this many hours after it was set (owner
        /// 2026-10-25: nothing may leave the yearly buffer swap stuck; the Sep 22 override ran Festival Season for weeks).</summary>
        internal const int OVERRIDE_HOURS = 72;

        /// <summary>Has an override set at <paramref name="at"/> (UTC; null = unknown) run out? "keep" ones never do.</summary>
        internal static bool OverrideExpired(DateTime? at, bool keep, DateTime nowUtc) =>
            !keep && (at == null || nowUtc >= at.Value.AddHours(OVERRIDE_HOURS));

        /// <summary>
        /// TWO SEASONS NEVER MIX (owner's box ticket 261030-0001: "halloween event things are still in holtberg the two
        /// should never mix"). A festival on only by its calendar window stays off while a festival with a different town
        /// dressing is forced on: Frostfell forced in October takes Festival Season's pumpkins, buffers and costumer down
        /// (and Azimar wears Frostfell's look). The calendar windows never overlap, so this matters only for a GM's @festival.
        /// Two forced on are both on: that is the GM's choice.
        /// </summary>
        internal static bool Displaced(Festival f, bool forced, IEnumerable<Festival> forcedOn) =>
            !forced && f.Dressing != null
            && forcedOn.Any(o => o != f && o.Dressing != null && !o.Dressing.Equals(f.Dressing, StringComparison.OrdinalIgnoreCase));

        /// <summary>The festivals whose natural window holds this local date (no overrides).</summary>
        internal static List<Festival> NaturalOn(DateTime local) => Calendar.Where(f => InWindow(f, local)).ToList();
    }
}
