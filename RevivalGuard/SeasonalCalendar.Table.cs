using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>The festival table and its rules, PURE</summary>
    public static partial class SeasonalCalendar
    {
        /// <summary>Retail's windows are stored as 05:01 and 04:59 UTC, which is 00:01 and 23:59 at UTC-5</summary>
        internal const int RETAIL_UTC_OFFSET_HOURS = -5;

        internal sealed class Festival
        {
            public string Key;              // what @festival takes
            public string Name;             // what a player is told
            public int StartMonth, StartDay, EndMonth, EndDay;   // inclusive both ends, local to UTC-5
            public string[] On = Array.Empty<string>();          // ace_world.event rows to start
            public string[] Off = Array.Empty<string>();         // rows to stop for the duration
            public string Announce;         // the world broadcast when it opens (seasons.json "about")
            public string Hint = "";
            public string Evidence;         // the weenie this was read off, shown by @festival list
            public string Dressing;
            public bool Pending;

            public string Window => $"{StartMonth:00}-{StartDay:00} to {EndMonth:00}-{EndDay:00}";
        }

        /// <summary>The calendar.</summary>
        internal static readonly Festival[] Calendar =
        {
            new Festival
            {
                Key = "festivalseason", Name = "Festival Season",
                StartMonth = 10, StartDay = 1, EndMonth = 11, EndDay = 1,
                On  = new[] { "EventFallFestival" },
                // AzimarYearRound: Azimar stands in the Pumpkin Buffers' spots the rest of the year; the pumpkins have them for the month
                Off = new[] { "normaltowncrier", "AzimarYearRound" },
                Announce = "The harvest has turned. Pumpkin Lords and Harvest Reapers walk the "
                         + "landscape, the dead are stirring in Al-Jalima, and the Majestic Pumpkin "
                         + "is waiting in Glenden Wood.",
                Hint = "Pumpkins line Holtburg, the Festival Costumer is in Glenden Wood, and the dead walk in Al-Jalima.",
                Evidence = "ace80069-fallfestivaleventsstopgapgen, Oct 1 to Nov 1", Dressing = "autumn",
            },
            new Festival
            {
                // Ours, not a retail stopgap: retail's own Dec 2000 "Bael'Zharon's Defeat" data, run once a year.
                Key = "lightbringer", Name = "the Lightbringer's Week",
                StartMonth = 1, StartDay = 22, EndMonth = 1, EndDay = 31,
                On  = new[] { "LightbringerEvent" },
                Announce = "The Chosen of Asheron have returned to Dereth. They seek Isparians brave enough to "
                         + "face the Hopeslayer's shadow once more.",
                Hint = "Find a Chosen of Asheron near Cragstone, Zaikhal or Hebian-to, and hunt the Herald's beasts on the Direlands summit.",
                Evidence = "ours: the Dec 2000 Bael'Zharon's Defeat event data (8764, 8777-8782, 8822), Dec 1 to Dec 7 at first, Jan 22 to 31 from 2026-11-26",
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
                // AnniversaryFestivalStones is a row this project added: the twenty Festival Stones (5376-5395, "Rejoice!
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
            // Retired live events, revived on the yearly calendar.
            new Festival
            {
                Key = "greatwork", Name = "the Great Work Crystal",
                StartMonth = 2, StartDay = 12, EndMonth = 2, EndDay = 21,
                On  = new[] { "GreatWorkCrystal" },
                Announce = "Sir Joffre Tremblant's adventuring party has gone missing. The group was last seen heading north "
                         + "from Arwic. Some say the sun has grown slightly dimmer, and that it has brought on the sudden chill.",
                Hint = "Mairisa bint Fuda in Plateau Village (44.4N, 43.4W) sends travellers to the Lost City of Frore, where the Gelidite lords and the Great Work wait.",
                Evidence = "retail live event 1999-12-16 (wiki rev 471967), Feb 2 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "burritual", Name = "the Bur Portal Summoning Ritual",
                StartMonth = 3, StartDay = 5, EndMonth = 3, EndDay = 14,
                On  = new[] { "BurPortalRitual" },
                Announce = "Prince Borelean Strathelar will try to summon a portal to Bur and find Asheron, held by the vile "
                         + "Falatacot. All of stout heart are asked to help, for the Falatacot will oppose him.",
                Hint = "Hoshino Kei and the Prince are at the Portal to Bur (67.4N, 30.5E).",
                Evidence = "retail live event 2007-01-22 (wiki rev 443270), Feb 2 to 8 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Children of the Prodigal Lord
                Key = "prodigal", Name = "the Children of the Prodigal Lord",
                StartMonth = 5, StartDay = 14, EndMonth = 5, EndDay = 23,
                On  = new[] { "ProdigalInvestigation" },
                Announce = "Tailen the Tracker has found the trail of Aerbax's altered creatures, from Rithwic to a hidden laboratory "
                         + "near Stonehold, and Aerbax is testing its Prodigal Children in the desert by Yaraq.",
                Hint = "Tailen the Tracker waits by Martine's Retreat in Rithwic (10.6N, 58.3E).",
                Evidence = "retail live events 2007-12-06 and 2008-01-07 (wiki revs 479445, 475683), Feb 9 to 15 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Gauntlet: wiki rev 461903. A quest window on the Singularity Caul
                Key = "gauntlet", Name = "the Gauntlet",
                StartMonth = 6, StartDay = 4, EndMonth = 6, EndDay = 13,
                On  = new[] { "GauntletTrial" },
                Announce = "A presence stirs on the Singularity Caul, challenging the bold to outrun the Shadows to a Shadow "
                         + "Stone on the island's far hill.",
                Hint = "A Presence waits where the Singularity Caul's portal lands you (98.0S, 94.7W).",
                Evidence = "retail live event Feb 2000 (wiki rev 461903), Feb 15 to 21 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Carlo di Cenza's Journal: wiki rev 479111. A quest window
                Key = "carlo", Name = "Carlo di Cenza's Journal",
                StartMonth = 5, StartDay = 14, EndMonth = 6, EndDay = 13,
                On  = new[] { "CarloJournal" },
                Announce = "A Viamontian, Carlo di Cenza, has been found wandering in Rithwic, unable to speak sense. Antius "
                         + "Blackmoor waits in Yaraq for any word of where he came from.",
                Hint = "Carlo di Cenza stands by the Empty Book Binding in Rithwic (10.8N, 58.2E); Antius Blackmoor is by Yaraq's fountain.",
                Evidence = "retail live event Feb 2005 (wiki rev 479111), Feb 16 to 28 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "mhoire", Name = "the House Mhoire Risings",
                StartMonth = 9, StartDay = 17, EndMonth = 9, EndDay = 26,
                On  = new[] { "MhoireRisings" },
                Announce = "The dead of House Mhoire stir in the Graveyard. Lady Tairla Mhoire and a Corrupted Pyre Oak have been "
                         + "seen walking far from it, near Dryreach and Ayan Baqur.",
                Hint = "House Mhoire's corrupted dead raid near Dryreach and Ayan Baqur a few times a day; watch for the broadcast.",
                Evidence = "retail live events 2012-02-25/26 (wiki rev 443457), Feb 20 to Mar 6 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Dranith Menacet in the Convergent Median
                Key = "dranith", Name = "Dranith Menacet in the Convergent Median",
                StartMonth = 7, StartDay = 3, EndMonth = 7, EndDay = 4,
                On  = new[] { "DranithMedian" },
                Announce = "Dranith Menacet, the hollow son Martine made, has been seen in the Convergent Median, weakened and "
                         + "calling for his father.",
                Hint = "The Convergent Median's portal is at 87.0S, 40.3W.",
                Evidence = "retail live events 2002 (Persuasion, Repercussions; wiki revs 443229, 451160), Mar 1 to 7 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "heroraids", Name = "the Hero Token Raids",
                StartMonth = 3, StartDay = 19, EndMonth = 3, EndDay = 28,
                On  = new[] { "HeroRaids" },
                Announce = "The K'nath and the Apostates are gathering south of Hebian-To. Those who hold the town will be paid in "
                         + "Hero Tokens.",
                Hint = "Raids on Hebian-To (39.6S, 83.2E) a few times a day pay a Hero Token to every defender; watch for the broadcast.",
                Evidence = "retail live events 2009-03-16 and 2009-04-01 (wiki rev 443669), Mar 14 to 28 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "bronzestatues", Name = "the Bronze Statues Attack",
                StartMonth = 4, StartDay = 23, EndMonth = 5, EndDay = 2,
                On  = new[] { "BronzeStatuesAttack" },
                Announce = "The bronze statues of Dereth's towns are stirring on their pedestals. Some have already stepped down, "
                         + "and they are not friendly.",
                Hint = "Living bronze statues come off the town pedestals every few hours; their oversized bronze weapons go to the Arcanum Researcher.",
                Evidence = "retail live event 2002-04-01 to 05-16 (wiki revs 446171, 455238), Mar 18 to 31 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Captain Burnja Attacks the Windmill: wiki rev 443260. One director raid, "burnja-windmill"; BurnjaRaids is only the Pending switch.
                Key = "burnja", Name = "Captain Burnja's Windmill Raids",
                StartMonth = 4, StartDay = 9, EndMonth = 4, EndDay = 11,
                On  = new[] { "BurnjaRaids" },
                Announce = "Captain Burnja's drudges have been seen with torches near the old windmill outside Cragstone.",
                Hint = "Captain Burnja raids the windmill near Cragstone (25.0N, 45.5E) a few times a day; his token is for Jarvis Hammerstone.",
                Evidence = "retail live event Apr 2007 (wiki rev 443260), Mar 22 to 31 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "tthuunritual", Name = "the Summoning Ritual of T'thuun",
                StartMonth = 8, StartDay = 15, EndMonth = 8, EndDay = 15,
                On  = new[] { "TthuunRitualAsheron" },
                Announce = "Asheron himself has come to the Patriarch's Valley to watch the Summoning Ritual of T'thuun be broken, "
                         + "and to reward those who break it.",
                Hint = "Break the Summoning Ritual of T'thuun in the Patriarch's Valley today; Asheron waits by the ritual site.",
                Evidence = "retail live event 2008-12-12 (wiki rev 475388), Mar 28 per docs/EVENTS-RETIRED.md (Dec 12 is inside Frostfell)", Pending = true,
            },
            new Festival
            {
                Key = "burunraids", Name = "the Burun Raids",
                StartMonth = 8, StartDay = 27, EndMonth = 9, EndDay = 5,
                On  = new[] { "BurunRaids" },
                Announce = "Burun raiding parties are coming out of the swamps against Hebian-To, Sawato, Shoushi, Tou-Tou and Yanshi.",
                Hint = "Burun raids hit Hebian-To, Sawato, Shoushi, Tou-Tou and Yanshi a few times a day; their leaders carry Burun Idols for Aun Laokhe in Arwic.",
                Evidence = "retail live event 2004-05-12 to 06-02 (wiki rev 447715), Jun 1 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "viridianbaishi", Name = "the Viridian Incursion at Baishi",
                StartMonth = 10, StartDay = 15, EndMonth = 10, EndDay = 24,
                On  = new[] { "ViridianIncursion" },
                Announce = "The Viridian Rise is stirring. Zerzelikyr, its Elder Gromnie Champion, has been seen in the thorns near Baishi.",
                Hint = "Zerzelikyr and the Viridian Rise raid a couple of clicks outside Baishi a few times a day; watch for the broadcast.",
                Evidence = "retail live event 2015-04-24 (wiki rev 473904), Jun 1 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "virindiayan", Name = "the Virindi of Ayan Baqur",
                StartMonth = 7, StartDay = 16, EndMonth = 7, EndDay = 25,
                On  = new[] { "VirindiAyanBaqur" },
                Off = new[] { "AyanBaqurTownsfolk" },
                Announce = "The people of Ayan Baqur are gone, and Virindi stand in their places. Some say the townsfolk were taken "
                         + "to the Panopticon.",
                Hint = "Virindi keep Ayan Baqur's shops; Adirred the Theoretician waits in the Panopticon near the town.",
                Evidence = "retail live event 2001-03-31 to 06-14 (wiki rev 470495), Jun 1 to 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Martine Attacks: wiki rev 443396. Martine and the Virindi Renegades stalking him, at one of two spots a few times a day
                Key = "martineattacks", Name = "Martine Attacks",
                StartMonth = 6, StartDay = 25, EndMonth = 7, EndDay = 4,
                On  = new[] { "MartineAttacks" },
                Announce = "Martine has been seen in the wilds, and Virindi Renegades are hunting him.",
                Hint = "Martine turns up on the Obsidian Plains or Marae Lassel a few times a day; kill the Virindi Renegades and take their tokens to him.",
                Evidence = "retail live event May to Jun 2002 (wiki rev 443396), Jun 1 to 7 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // The Scavenger Hunt: wiki rev 475541. A switch row only: ScavengerHunt.cs runs the six hours and the heralds' row ScavengerHunt itself.
                Key = "scavengerhunt", Name = "the Scavenger Hunt",
                StartMonth = 7, StartDay = 31, EndMonth = 7, EndDay = 31,
                On  = new[] { "ScavengerHuntSeason" },
                Announce = "The Advocates are restless today. Tonight they will call a Scavenger Hunt for monster trophies.",
                Hint = "From 6 PM to midnight, bring monster trophies to the Herald of the Advocates in Holtburg, Shoushi or Yaraq; the three best collections are rewarded.",
                Evidence = "retail beta event 1999-06-14 (wiki rev 475541), Jun 14 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                // Viamontian Invaders of the Meeting Halls (Ashes and Dust, Nov 2005): wiki rev 476535 (Glenden Wood Invaders, Trivia).
                Key = "meetinghalls", Name = "the Viamontian Invaders of the Meeting Halls",
                StartMonth = 11, StartDay = 5, EndMonth = 11, EndDay = 14,
                On  = new[] { "MeetingHallInvaders" },
                Announce = "Viamontian strike forces have seized the Meeting Halls of Rithwic, Cragstone, Zaikhal and Arwic. "
                         + "Tomo Genza of the Glenden Wood Militia is paying for their leaders' heads.",
                Hint = "Viamontians hold the Meeting Halls of Rithwic, Cragstone, Zaikhal and Arwic; kill a Strike Force Leader and see Tomo Genza in Glenden Wood.",
                Evidence = "retail live event Nov 2005 (wiki rev 476535), Nov 6 to 15 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "shardherald", Name = "the Shard of the Herald",
                StartMonth = 11, StartDay = 18, EndMonth = 11, EndDay = 30,
                On  = new[] { "ShardOfTheHerald" },
                Announce = "The undead have gathered at Ithaenc Cathedral. Anadil of Shakrassekor and his spymaster hold a camp "
                         + "before it, and beneath the cathedral they guard the last of the Soul Crystals: the Shard of the Herald.",
                Hint = "Anadil's camp is outside Ithaenc Cathedral (83.3S 93.2E); three silver key pieces open Kelderam's Tomb in the cathedral's basement, and the Shard waits in the catacombs below.",
                Evidence = "retail live event 2000-11-14 (wiki rev 460139), Nov 18 to 30 per docs/EVENTS-RETIRED.md", Pending = true,
            },
            new Festival
            {
                Key = "bzrampage", Name = "Bael'Zharon's Rampage",
                StartMonth = 11, StartDay = 24, EndMonth = 11, EndDay = 30,
                On  = new[] { "BaelZharonRampage" },
                Announce = "Bael'Zharon, the Hopeslayer, walks among the towns of Dereth. He cannot be harmed, and he curses whoever strikes him.",
                Hint = "Bael'Zharon visits a town about twice a day; a broadcast says where.",
                Evidence = "retail live event Dec 2000 (wiki rev 443389, Turbine's video), Nov 24 to 30 per docs/EVENTS-RETIRED.md", Pending = true,
            },
        };

        /// <summary>Is this (month, day) inside the festival's window?</summary>
        internal static bool InWindow(Festival f, DateTime local)
        {
            int cur = local.Month * 100 + local.Day;
            int start = f.StartMonth * 100 + f.StartDay;
            int end = f.EndMonth * 100 + f.EndDay;
            return start <= end ? (cur >= start && cur <= end) : (cur >= start || cur <= end);
        }

        /// <summary>What every event row the table names should be, given which festivals are active</summary>
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

        /// <summary>A forced festival hands itself back to the calendar this many hours after it was set.</summary>
        internal const int OVERRIDE_HOURS = 72;

        /// <summary>Has an override set at at (UTC; null = unknown) run out?</summary>
        internal static bool OverrideExpired(DateTime? at, bool keep, DateTime nowUtc) =>
            !keep && (at == null || nowUtc >= at.Value.AddHours(OVERRIDE_HOURS));

        internal static bool Displaced(Festival f, bool forced, IEnumerable<Festival> forcedOn) =>
            !forced && f.Dressing != null
            && forcedOn.Any(o => o != f && o.Dressing != null && !o.Dressing.Equals(f.Dressing, StringComparison.OrdinalIgnoreCase));

        /// <summary>The festivals whose natural window holds this local date (no overrides).</summary>
        internal static List<Festival> NaturalOn(DateTime local) => Calendar.Where(f => InWindow(f, local)).ToList();
    }
}
