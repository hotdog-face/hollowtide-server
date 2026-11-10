using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE EVENT DIRECTOR: ROTATING LIVE EVENTS OUT OF CONTENT THE WORLD DATABASE ALREADY HAS.
    /// Owner-approved 2026-09-27; docs/EVENT-DIRECTOR.md has the catalogue, the evidence for each
    /// entry and how to add one. NOT retail: retail's live events were run by hand by staff (the
    /// wiki's "Broadcast from +Name>" transcripts), and a scheduler is ours (docs/DIVERGENCES.md).
    ///
    /// HOW AN EVENT RUNS. ACE's EventManager holds one state per `ace_world.event` row. A generator
    /// whose GeneratorTimeType is Event (PropertyInt 142 = 3) and whose GeneratorEvent
    /// (PropertyString 34) names a row spawns only while that row is On; it checks every 5 s and
    /// flips after two agreeing checks (WorldObject_Generators.HandleStatusStaged), so spawns follow
    /// a start within about 10 s plus one RegenerationInterval (60 s for everything catalogued).
    /// Starting and stopping are EventManager.StartEvent / StopEvent, the calls @event makes.
    ///
    /// CLEAN STOP. ACE's own stop runs each generator's GeneratorEndDestructionType, and several
    /// catalogued generators carry Nothing or Undef (every Erupt*Gen, both Witshire gens), which
    /// leaves their monsters standing after the event. So a stop also walks the loaded landblocks
    /// for generators gated on the stopped rows and destroys what they spawned (children of
    /// generators first), then repeats on the same generators at +12, +30 and +75 s to catch
    /// anything spawned inside ACE's two-check window.
    ///
    /// RESTARTS. Event state is process-local (nothing in ACE writes an Event row back; see
    /// SeasonalCalendar), and spawned monsters are never saved, so a shutdown cannot leave an event
    /// behind. The schedule is saved to EventDirectorState.json beside the dll on every transition;
    /// a running event resumes after a restart for the time it had left. On a shutdown countdown the
    /// current event is stopped and despawned without an announcement, and started again if the
    /// shutdown is cancelled.
    ///
    ///   @events                        players: what is running now and what is next
    ///   @eventdirector                 admins/bots: status (list | on | off | next | skip | stop | start key)
    ///
    /// LIVE SWITCH. rg_event_director, default off: a shard runs no live events until staff say
    /// `@eventdirector on`. FESTIVAL EVENTS (Festivals set) run only while SeasonalCalendar has one of
    /// their festivals on, and get one slot after every event_director_festival_every (3) year-round events (EventDirectorRules.Choose). The rotation holds
    /// during Anniversary Night and keeps clear of a world boss's window in his region.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class EventDirector
    {
        sealed class Entry
        {
            public string Key, Title, Where, Levels, Evidence;
            public string[] Regions;               // no two running entries may share one
            public string[] Start;                 // rows started at the beginning
            public string[] Stop;                  // rows stopped at the end: Start plus whatever the event's own emotes chain into
            public (string ev, string text)[] Stages = Array.Empty<(string, string)>();   // announced when that row turns On mid-event
            public string[] Festivals;             // SeasonalCalendar keys: in the rotation only while one is on; null: all year
            public double? Minutes;                // how long it runs; null: event_director_duration_min
            public (int x0, int y0, int x1, int y1) Box;   // landblock X/Y rectangle for "is anyone there"
            public string Announce, Victory, Withdraw;
            public string Lost, Defeat;            // a stage row that means the players lost, and what is said then (the Present Raids)
            public bool HeldWins;                  // still running at the end of its time = the players held it (the Hero Token raids)
            public bool IsFestival => Festivals != null && Festivals.Length > 0;
        }

        const string LEAD = "Attention Citizens of Dereth! ";

        /// <summary>
        /// THE CATALOGUE. Every row name, level and place below was read from ace_world on
        /// 2026-09-27 (docs/EVENT-DIRECTOR.md, "How each entry was verified"). The Olthoi swarm
        /// sites are the ones the map already names ("Far Direlands Olothi Swarm", "North Tethana
        /// Olothi Swarm", "Far West Osteth Olothi Swarm"), and their escalation is the creatures'
        /// own Death emotes (a 1 in 20 chance per kill of the site's own worker, soldier or noble to
        /// move the swarm on a stage); the stage lines are those emotes' own LocalBroadcast text,
        /// sent world-wide. The eruptions are the Lady Aerfalle quest's volcano chain; Mount
        /// Tenkarrdun runs WITHOUT its Hellfire (EruptTenkarrdunBossGen), because the Hellfire's
        /// death erupts the port whose Relic Watchman starts three Aerfalle quest rows
        /// (AerfalleUberGen, SluiceGolemGen, AerfalleKeepStopgapGen) that are not ours to run.
        /// </summary>
        static readonly Entry[] Catalogue =
        {
            new Entry
            {
                Key = "witshire", Title = "Hollow Minions at Fort Witshire", Where = "25.0N, 37.7E", Levels = "23",
                Regions = new[] { "witshire" }, Box = (0xAD, 0x9D, 0xAF, 0x9F),
                Start = new[] { "WitshireRegicideHollows", "WitshireRegicideHollowsBoss" },
                Stop = new[] { "WitshireRegicideHollows", "WitshireRegicideHollowsBoss" },
                Announce = LEAD + "Hollow Minions are raiding Fort Witshire, near 25.0N, 37.7E. The fort needs young defenders. Who will heed the call?",
                Victory = "The Hollow Minions at Fort Witshire have been beaten back. Well fought!",
                Withdraw = "The Hollow Minions have slipped away from Fort Witshire.",
                Evidence = "hollowminionregicidewitshiregen / ...bossgen (14462/14463) at 0xAE9E, level 23 minions; the boss gen's event minion (14464) stops both rows when it dies",
            },
            new Entry
            {
                Key = "swarm-far", Title = "Olthoi Swarm in the far Direlands", Where = "17.0N, 87.0W", Levels = "60 to 90",
                Regions = new[] { "direlands-far" }, Box = (0x11, 0x92, 0x15, 0x96),
                Start = new[] { "Dires1FullInvasion" },
                Stop = new[] { "Dires1FullInvasion", "Dires1SwarmA", "Dires1SwarmB", "Dires1SwarmC" },
                Stages = new[]
                {
                    ("Dires1SwarmA", "The Olthoi in the far Direlands have begun to swarm!"),
                    ("Dires1SwarmB", "The Olthoi swarm in far Direlands is intensifying!"),
                    ("Dires1SwarmC", "The Olthoi swarm is overrunning the far Direlands!"),
                },
                Announce = LEAD + "Olthoi Workers are boiling up out of the ground in the far Direlands, near 17.0N, 87.0W. Cull them before the swarm grows: soldiers follow the workers, and nobles follow the soldiers. Who will heed the call?",
                Victory = "The chittering of the swarming Olthoi begins to subside... the danger in the far Direlands has been averted.",
                Withdraw = "The Olthoi of the far Direlands have gone back underground.",
                Evidence = "dires1mastergen-xp (11192) x45 over 17 landblocks; workers 60-70, soldiers 79-80, nobles 90",
            },
            new Entry
            {
                Key = "swarm-tethana", Title = "Olthoi Swarm north of Fort Tethana", Where = "22.0N, 72.0W", Levels = "60 to 90",
                Regions = new[] { "direlands-tethana" }, Box = (0x22, 0x97, 0x27, 0x9C),
                Start = new[] { "Dires2FullInvasion" },
                Stop = new[] { "Dires2FullInvasion", "Dires2SwarmA", "Dires2SwarmB", "Dires2SwarmC" },
                Stages = new[]
                {
                    ("Dires2SwarmA", "The Olthoi to the north of Fort Tethana have begun to swarm!"),
                    ("Dires2SwarmB", "The Olthoi swarm to the north of Fort Tethana is intensifying!"),
                    ("Dires2SwarmC", "The Olthoi swarm is overrunning the coast north of Tethana!"),
                },
                Announce = LEAD + "Olthoi Workers are boiling up out of the ground north of Fort Tethana, near 22.0N, 72.0W. Cull them before the swarm grows: soldiers follow the workers, and nobles follow the soldiers. Who will heed the call?",
                Victory = "The chittering of the swarming Olthoi begins to subside... the danger to the north of Fort Tethana has been averted.",
                Withdraw = "The Olthoi north of Fort Tethana have gone back underground.",
                Evidence = "dires2mastergen-xp (11193) x71 over 30 landblocks; workers 60-70, soldiers 79-80, nobles 90",
            },
            new Entry
            {
                Key = "swarm-osteth", Title = "Olthoi Swarm west of Osteth", Where = "26.7N, 54.2W", Levels = "60 to 90",
                Regions = new[] { "direlands-osteth" }, Box = (0x3A, 0x9F, 0x3F, 0xA3),
                Start = new[] { "Dires3FullInvasion" },
                Stop = new[] { "Dires3FullInvasion", "Dires3SwarmA", "Dires3SwarmB", "Dires3SwarmC" },
                Stages = new[]
                {
                    ("Dires3SwarmA", "The Olthoi in the west of Osteth have begun to swarm!"),
                    ("Dires3SwarmB", "The Olthoi swarm in the west of Osteth is intensifying!"),
                    ("Dires3SwarmC", "The Olthoi swarm is overrunning the west of Osteth!"),
                },
                Announce = LEAD + "Olthoi Workers are boiling up out of the ground far to the west of Osteth, near 26.7N, 54.2W. Cull them before the swarm grows: soldiers follow the workers, and nobles follow the soldiers. Who will heed the call?",
                Victory = "The chittering of the swarming Olthoi begins to subside... the danger to Osteth has been averted.",
                Withdraw = "The Olthoi west of Osteth have gone back underground.",
                Evidence = "dires3mastergen-xp (11194) x50 over 19 landblocks; workers 60-70, soldiers 79-80, nobles 90",
            },
            new Entry
            {
                Key = "erupt-tenkarrdun", Title = "Mount Tenkarrdun Erupts", Where = "90.2N, 46.1E", Levels = "80 to 105",
                Regions = new[] { "tenkarrdun" }, Box = (0xB4, 0xEA, 0xBD, 0xF1),
                Start = new[] { "EruptTenkarrdunGen", "EruptTenkarrdunFXGen", "EruptTenkarrdunFlareFXGen", "MegaMagmaGen" },
                Stop = new[] { "EruptTenkarrdunGen", "EruptTenkarrdunFXGen", "EruptTenkarrdunFlareFXGen", "MegaMagmaGen" },
                Announce = LEAD + "Mount Tenkarrdun on Aerlinthe has erupted! Magma and Nubilous Golems are pouring down its slopes near 90.2N, 46.1E, and the Behemoth of Tenkarrdun has risen from the crater. Who will heed the call?",
                Victory = "The fires of Mount Tenkarrdun have gone out.",
                Withdraw = "The fires of Mount Tenkarrdun have cooled.",
                Evidence = "erupttenkarrdungen (7365) x55 over 12 landblocks, FX gens 7364/7426, MegaMagmaGen 7367 (Behemoth of Tenkarrdun, 105); golems and firestorms 80-100",
            },
            new Entry
            {
                Key = "erupt-esper", Title = "Mount Esper Erupts", Where = "66.0N, 13.0E", Levels = "80 to 100",
                Regions = new[] { "esper", "lethe" }, Box = (0x8D, 0xCF, 0x91, 0xD3),
                Start = new[] { "EruptEsperGen", "EruptEsperFXGen", "EruptEsperPlumeFXGen", "EruptEsperBossGen" },
                Stop = new[] { "EruptEsperGen", "EruptEsperFXGen", "EruptEsperPlumeFXGen", "EruptEsperBossGen",
                               "EruptLetheGen", "EruptLetheFXGen", "EruptLetheFlareFXGen", "EruptLetheBossGen" },
                Stages = new[]
                {
                    ("EruptLetheGen", "The Mount Esper Firestorm is slain, and the lake of Mount Esper is peaceful again. But Mount Lethe has erupted! Its Hellfire walks the crater near 34.4S, 84.9W."),
                },
                Announce = LEAD + "Mount Esper has erupted! Firestorms and golems are surfacing from the vents under the crater lake near 66.0N, 13.0E, led by the Mount Esper Firestorm. Who will heed the call?",
                Victory = "Mount Esper and Mount Lethe are peaceful again... or at least as peaceful as they ever get.",
                Withdraw = "The fires of Mount Esper have cooled.",
                Evidence = "eruptespergen (7355) x28 over 10 landblocks, boss gen 7353 (Mount Esper Firestorm, whose death erupts Mount Lethe); 80-100",
            },
            new Entry
            {
                Key = "erupt-lethe", Title = "Mount Lethe Erupts", Where = "34.4S, 84.9W", Levels = "100",
                Regions = new[] { "lethe" }, Box = (0x13, 0x53, 0x16, 0x56),
                Start = new[] { "EruptLetheGen", "EruptLetheFXGen", "EruptLetheFlareFXGen", "EruptLetheBossGen" },
                Stop = new[] { "EruptLetheGen", "EruptLetheFXGen", "EruptLetheFlareFXGen", "EruptLetheBossGen" },
                Announce = LEAD + "Mount Lethe has erupted! Diamond and Magma Golems are climbing out of the crater near 34.4S, 84.9W, led by the Mount Lethe Hellfire. Who will heed the call?",
                Victory = "The Mount Lethe Hellfire is slain. Mount Lethe is peaceful again... or at least as peaceful as it ever gets.",
                Withdraw = "The fires of Mount Lethe have cooled.",
                Evidence = "eruptlethegen (7360) x15 over 4 landblocks, boss gen 7357 (Mount Lethe Hellfire); 100",
            },
            new Entry
            {
                Key = "presents-tufa", Title = "Gurogs Raid the Presents at Tufa", Where = "14.9S, 5.6E", Levels = "220 to 300",
                Regions = new[] { "tufa", "presentraids" }, Box = (0x85, 0x6B, 0x87, 0x6D), Festivals = new[] { "frostfell", "newyear" }, Minutes = 30,
                Start = new[] { "PresentRaidsTufa" },
                Stop = new[] { "PresentRaidsTufa", "PresentRaidsDead" },
                Stages = new[] { ("PresentRaidsDead", "The Holiday Present at Tufa has been destroyed!") },
                Lost = "PresentRaidsDead", Defeat = "The Gurogs smashed the Holiday Present at Tufa and went home. Gather more defenders for the next raid!",
                Announce = LEAD + "Gurogs are raiding Tufa! A huge Holiday Present has appeared in the town, near 14.9S, 5.6E, and waves of Gurog Grumps are coming to smash it. Keep it standing for 30 minutes and every defender is rewarded.",
                Victory = "The Holiday Present at Tufa still stands! The Gurogs have given up. Well defended!",
                Withdraw = "The Gurogs have left Tufa.",
                Evidence = "ace73219-presentraidstufagen at 0x866C; Gurog Grumps 220, Gurog Mastermind and Max 300; Frostfell content (wiki: Gurog Present Raids)",
            },
            new Entry
            {
                Key = "presents-yaraq", Title = "Drudges Raid the Presents at Yaraq", Where = "21.7S, 1.3W", Levels = "200",
                Regions = new[] { "yaraq", "presentraids" }, Box = (0x7C, 0x63, 0x7E, 0x65), Festivals = new[] { "frostfell", "newyear" }, Minutes = 30,
                Start = new[] { "PresentRaidsYaraq" },
                Stop = new[] { "PresentRaidsYaraq", "PresentRaidsDead" },
                Stages = new[] { ("PresentRaidsDead", "The Holiday Presents at Yaraq have been stolen!") },
                Lost = "PresentRaidsDead", Defeat = "The Drudges made off with the Holiday Presents at Yaraq. Gather more defenders for the next raid!",
                Announce = LEAD + "Drudges are raiding Yaraq! A pile of Holiday Presents has appeared in the town, near 21.7S, 1.3W, and Drudge Pilferers are dropping from balloons to steal it. Keep them off it for 30 minutes and every defender is rewarded.",
                Victory = "The Holiday Presents at Yaraq are safe! The Drudges have given up. Well defended!",
                Withdraw = "The Drudges have left Yaraq.",
                Evidence = "ace73230-presentraidsyaraqgen at 0x7D64; Drudge Pilferers 200; Frostfell content (wiki: Drudge Present Raids)",
            },
            // FESTIVAL EVENTS (owner 2026-10-22, "Seasonal Event Director runs"; docs/EVENT-DIRECTOR.md "Festival events").
            // Retail festival creatures on rows of the director's own (Director*), from
            // tools/public-shard/datafixes/2026-10-22-festival-director-events.sql. The festivals' own rows
            // (EventFallFestival, springbunnyevent ...) stay SeasonalCalendar's: those creatures keep spawning all month
            // where retail put them, and these entries only ever start and stop rows the calendar never turns.
            new Entry
            {
                Key = "fs-pumpkin-glenden", Title = "Pumpkin Lords March on Glenden Wood", Where = "29.7N, 27.2E", Levels = "15 to 60",
                Regions = new[] { "glenden" }, Box = (0xA0, 0xA3, 0xA2, 0xA5), Festivals = new[] { "festivalseason" }, Minutes = 60,
                Start = new[] { "DirectorPumpkinLordsGlenden" }, Stop = new[] { "DirectorPumpkinLordsGlenden" },
                Announce = LEAD + "Pumpkin Lords are marching on Glenden Wood, near 29.7N, 27.2E, each with a crowd of Pumpkin Kin at his heels. Young adventurers can thin the Kin; the Lords want a seasoned hand. Who will heed the call?",
                Victory = "The Pumpkin Lords have been driven from Glenden Wood. Well fought!",
                Withdraw = "The Pumpkin Lords have wandered back into the fields around Glenden Wood.",
                Evidence = "902400, a copy of retail's Pumpkin Lord Generator 87048 (EventFallFestival, nested in the landscape tables), x5 around Glenden Wood; Pumpkin Lord 32186 L60 with 5 Pumpkin Kin 32203 L15",
            },
            new Entry
            {
                Key = "fs-dead-zaikhal", Title = "The Dead March on Zaikhal", Where = "13.7N, 0.6E", Levels = "60",
                Regions = new[] { "zaikhal" }, Box = (0x7F, 0x8F, 0x81, 0x91), Festivals = new[] { "festivalseason" }, Minutes = 60,
                Start = new[] { "DirectorRisenDeadZaikhal" }, Stop = new[] { "DirectorRisenDeadZaikhal" },
                Announce = LEAD + "The festival season has stirred the old graves north of Al-Jalima. The dead are clawing their way up around Zaikhal, near 13.7N, 0.6E. Who will heed the call?",
                Victory = "The dead around Zaikhal have been put back to rest. Well fought!",
                Withdraw = "The dead around Zaikhal have sunk back into their graves.",
                Evidence = "902401, a copy of retail's Low Old Gravestone Gen 73187 (EventFallFestival), x4 around Zaikhal; each Old Gravestone raises 10 Old Zombies 34102 L60",
            },
            new Entry
            {
                Key = "fs-incursion-hebian", Title = "Undead Incursion at Hebian-To", Where = "39.1S, 83.2E", Levels = "110 to 160",
                Regions = new[] { "hebian" }, Box = (0xE6, 0x4D, 0xE8, 0x4F), Festivals = new[] { "festivalseason" }, Minutes = 60,
                Start = new[] { "DirectorUndeadIncursionHebian" }, Stop = new[] { "DirectorUndeadIncursionHebian" },
                Announce = LEAD + "An undead incursion has reached Hebian-To! Lich Oppressors, Dark Guardians and Shambling Adherents are closing on the town near 39.1S, 83.2E, and a Harvest Reaper walks with them. Who will heed the call?",
                Victory = "The undead incursion at Hebian-To has been broken. Well fought!",
                Withdraw = "The undead have withdrawn from Hebian-To.",
                Evidence = "902402, a copy of retail's Zombie Incursion Gen 73176 (EventFallFestival, the Al-Jalima incursion, placed x11 there), x5 around Hebian-To: Old Zombie 73181 L110, Shambling Adherent / Lich Oppressor / Dark Guardian L160; 902403, a copy of the Harvest Reaper Generator 87049 (36443 L160), x1",
            },
            new Entry
            {
                Key = "ff-snowmen-holtburg", Title = "Unhappy Snowmen in Holtburg", Where = "42.3N, 33.6E", Levels = "8 to 15",
                Regions = new[] { "holtburg" }, Box = (0xA8, 0xB3, 0xAA, 0xB5), Festivals = new[] { "frostfell" }, Minutes = 60,
                Start = new[] { "DirectorSnowmenHoltburg" }, Stop = new[] { "DirectorSnowmenHoltburg" },
                Announce = LEAD + "The snowmen around Holtburg are very unhappy about something, and some of them have grown a second head. Young adventurers are needed near 42.3N, 33.6E. Who will heed the call?",
                Victory = "The snowmen around Holtburg have been calmed. Well fought!",
                Withdraw = "The snowmen around Holtburg have settled down... for now.",
                Evidence = "902404, a copy of retail's Unhappy Snowman Generator 5859 (unplaced), x6 around Holtburg, 3 each: Snowman 5766 L8, Two Headed Snowman 14466 L15",
            },
            new Entry
            {
                Key = "ff-scold-stonehold", Title = "The Scold Descend on Stonehold", Where = "68.8N, 21.6W", Levels = "40 to 80",
                Regions = new[] { "stonehold" }, Box = (0x63, 0xD4, 0x65, 0xD6), Festivals = new[] { "frostfell" }, Minutes = 60,
                Start = new[] { "DirectorScoldStonehold" }, Stop = new[] { "DirectorScoldStonehold" },
                Announce = LEAD + "Scold Lumps, Scold Chunks and a great Scold have come down out of the ice around Stonehold, near 68.8N, 21.6W. Who will heed the call?",
                Victory = "The Scold around Stonehold have been broken up. Well fought!",
                Withdraw = "The Scold have drifted back into the snow around Stonehold.",
                Evidence = "902405-902407, copies of the Assault on Frost Haven's Scold generators 87127/87125/87123, around Stonehold: Scold Lump 87126 L40 (x2 gens of 8), Scold Chunk 87124 L60 (x2 of 4), Scold 87122 L80 (x1)",
            },
            new Entry
            {
                Key = "sp-rabbits-eastham", Title = "Black Rabbits Loose near Eastham", Where = "17.6N, 63.4E", Levels = "2 to 115",
                Regions = new[] { "eastham" }, Box = (0xCD, 0x94, 0xCF, 0x96), Festivals = new[] { "spring" }, Minutes = 60,
                Start = new[] { "DirectorBlackRabbitsEastham" }, Stop = new[] { "DirectorBlackRabbitsEastham" },
                Announce = LEAD + "Black Rabbits are loose in the fields around Eastham, near 17.6N, 63.4E, and their Black Bunnies are everywhere. Every Black Bunny counts for Spring Cleaning, but mind what hops out when one falls. Who will heed the call?",
                Victory = "The Black Rabbits around Eastham have been rounded up. Well fought!",
                Withdraw = "The Black Rabbits have hopped away from Eastham.",
                Evidence = "902408, the Black Rabbit half of retail's Asheron's Island Harmless Gen 87091, x5 around Eastham, 2 each: Black Rabbit 87089 L4 (its Black Bunnies 87090 L2 come only while springbunnyevent is on); a Black Bunny's death leaves a Hulking Bunny 87088 L115 (3 xp), 1 in 12 a White Rabbit 87094 L666",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/bur-ritual.md): retail's Jan 2007 Bur Portal Summoning
            // Ritual, Feb 2 to 8. Rows and gens from tools/gpubox-ace/gen_retired_feb2.py (2027-02-02-bur-1-ritual.sql).
            new Entry
            {
                Key = "bur-ritual", Title = "The Falatacot Attack the Bur Portal Ritual", Where = "67.4N, 30.5E", Levels = "185",
                Regions = new[] { "burportal" }, Box = (0xA4, 0xD2, 0xA6, 0xD4), Festivals = new[] { "burritual" }, Minutes = 60,
                Start = new[] { "DirectorBurRitual" }, Stop = new[] { "DirectorBurRitual" },
                Announce = LEAD + "The Falatacot are attacking Prince Borelean Strathelar's ritual at the Portal to Bur, near 67.4N, 30.5E, and Urdhinivix the Bloodletter comes with them. Speak with Hoshino Kei, then stand with the Prince. Who will heed the call?",
                Victory = "Urdhinivix has fallen and the portal to Bur holds. Prince Borelean Strathelar has medals for those who defended him.",
                Withdraw = "The Falatacot have fallen back from the Portal to Bur.",
                Evidence = "903738 at 0xA5D30026 (the Portal to Bur's pedestals): Urdhinivix 903732 (a copy of Matriarch Mirta'kir 34755, L185, the wiki's stats) and 6 Falatacot Raiders 39171 L185",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/prodigal.md): retail's Jan 2008 Aerbax's Prodigal
            // Children, Feb 9 to 15: retail's three waves in the desert by Yaraq. Each wave's leader (ours) stops its row and
            // starts the next as it dies; the Queen's death stops the last, which is the win (ProdigalChildren.Won: a Modified
            // Hero Token to everyone near). Rows and gens from tools/gpubox-ace/gen_retired_feb9.py (2027-02-09-prodigal-1-*.sql).
            new Entry
            {
                Key = "prodigal-children", Title = "Aerbax's Prodigal Children near Yaraq", Where = "22.4S, 1.5W", Levels = "160 to 999",
                Regions = new[] { "yaraq" }, Box = (0x7C, 0x62, 0x7E, 0x64), Festivals = new[] { "prodigal" }, Minutes = 90,
                Start = new[] { "DirectorProdigal1" },
                Stop = new[] { "DirectorProdigal1", "DirectorProdigal2", "DirectorProdigal3" },
                Stages = new[]
                {
                    ("DirectorProdigal2", "The Prodigal Monouga has fallen. Aerbax's second wave rises from the sands south of Yaraq: Children of the Prodigal Lord, Shadows, and the Prodigal Harbinger!"),
                    ("DirectorProdigal3", "The Prodigal Harbinger has fallen. Aerbax sends its last and greatest creations: Claude the Dark Archmage and the Paradox-touched Olthoi Queen!"),
                },
                Announce = LEAD + "Aerbax has summoned its Prodigal Children in the desert just south of Yaraq, near 22.4S, 1.5W, to test them against the people of Dereth. Three waves are coming. Who will heed the call?",
                Victory = "The Paradox-touched Olthoi Queen has fallen and Aerbax's Prodigal Children are scattered. Aerbax was amused. Those who stood near Yaraq have a Modified Hero Token for Vincadi in Old Yanshi.",
                Withdraw = "Aerbax has called its Prodigal Children back from the desert near Yaraq.",
                Evidence = "gens 903750-903752 at 0x7D630024 (rows DirectorProdigal1-3): retail's wave lists (wiki rev 475683); leaders Prodigal Monouga 903753 (ours), Prodigal Harbinger 903754 (copy of 36178), Paradox-touched Olthoi Queen 903755 (copy of 35888)",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/mhoire.md): retail's Feb 25-26 2012 House Mhoire
            // Risings, Feb 20 to Mar 6: two raids, retail's creature lists. The boss's death stops its row and starts a spoils
            // row (retail's reward kit: a Luminance Wisp, a Chest of Mana Forge Keys and five Hero Tokens on the ground, each
            // gone after 15 minutes); the spoils row is in Stop, so the entry runs its hour and ends with the Withdraw line
            // either way. Rows and gens from tools/gpubox-ace/gen_retired_feb20.py (2027-02-20-mhoire-1-risings.sql).
            new Entry
            {
                Key = "mhoire-tairla", Title = "Lady Tairla Mhoire Attacks near Dryreach", Where = "8.7S, 73.1E", Levels = "220 to 300",
                Regions = new[] { "dryreach" }, Box = (0xD9, 0x73, 0xDB, 0x75), Festivals = new[] { "mhoire" }, Minutes = 60,
                Start = new[] { "DirectorMhoireTairla" }, Stop = new[] { "DirectorMhoireTairla", "DirectorMhoireTairlaSpoils" },
                Stages = new[]
                {
                    ("DirectorMhoireTairlaSpoils", "Lady Tairla Mhoire has fallen near Dryreach! Hero Tokens, a Chest of Mana Forge Keys and a Luminance Wisp wait where she fell, for a quarter of an hour."),
                },
                Announce = LEAD + "Lady Tairla Mhoire has risen from the Graveyard and attacks just south of Dryreach, near 8.7S, 73.1E, with Pyre Skeletons and Wights. Who will heed the call?",
                Victory = "Lady Tairla Mhoire and her dead are laid to rest near Dryreach.",
                Withdraw = "The corrupted dead of House Mhoire have crumbled away near Dryreach.",
                Evidence = "903802 at 0xDA740035 (row DirectorMhoireTairla): Lady Tairla Mhoire 903800 (copy of 35393, L300, 200,000 health), 6 Pyre Skeletons 35097, 4 Wights 35091 (wiki rev 443457); spoils 903803 (DirectorMhoireTairlaSpoils): retail's kit 80520, 80523, 80525",
            },
            new Entry
            {
                Key = "mhoire-pyreoak", Title = "A Corrupted Pyre Oak near Ayan Baqur", Where = "60.6S, 87.3W", Levels = "200 to 300",
                Regions = new[] { "ayanbaqur" }, Box = (0x11, 0x32, 0x13, 0x34), Festivals = new[] { "mhoire" }, Minutes = 60,
                Start = new[] { "DirectorMhoirePyreOak" }, Stop = new[] { "DirectorMhoirePyreOak", "DirectorMhoirePyreOakSpoils" },
                Stages = new[]
                {
                    ("DirectorMhoirePyreOakSpoils", "The Corrupted Pyre Oak has fallen near Ayan Baqur! Hero Tokens, a Chest of Mana Forge Keys and a Luminance Wisp wait where it stood, for a quarter of an hour."),
                },
                Announce = LEAD + "A Corrupted Pyre Oak has risen just east of Ayan Baqur, near 60.6S, 87.3W, with Pyre Skeletons, Pyre Champions, Pyre Minions and Corrupted Dreads. Who will heed the call?",
                Victory = "The Corrupted Pyre Oak and its dead are laid to rest near Ayan Baqur.",
                Withdraw = "The corrupted dead of House Mhoire have crumbled away near Ayan Baqur.",
                Evidence = "903804 at 0x12330016 (row DirectorMhoirePyreOak): Corrupted Pyre Oak 903801 (copy of 38807, L300, 200,000 health), 4 Pyre Skeletons 35097, 2 Pyre Champions 35098, 4 Pyre Minions 35099, 2 Corrupted Dreads 35400 (wiki rev 443457); spoils 903805 (DirectorMhoirePyreOakSpoils): retail's kit 80520, 80523, 80525",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/hero-raids.md): retail's 2009 Hero Token raids, Mar 14
            // to 28: the two with retail creature lists, both at Hebian-To. No boss: holding the site to the end of the hour is
            // the win (HeldWins), and HeroRaids.Won gives each defender near a Hero Token. Rows and gens from
            // tools/gpubox-ace/gen_retired_mar14.py (2027-03-14-heroraids-1-hebian.sql).
            new Entry
            {
                Key = "hero-knath-hebian", Title = "The K'nath Invade Hebian-To", Where = "39.6S, 83.3E", Levels = "220",
                Regions = new[] { "hebian" }, Box = (0xE6, 0x4C, 0xE8, 0x4E), Festivals = new[] { "heroraids" }, Minutes = 60,
                Start = new[] { "DirectorHeroKnath" }, Stop = new[] { "DirectorHeroKnath" }, HeldWins = true,
                Announce = LEAD + "The K'nath are swarming into Hebian-To from the south, near 39.6S, 83.3E. Hold the town for the hour and every defender earns a Hero Token. Who will heed the call?",
                Victory = "Hebian-To held against the K'nath! Every defender who stood there has a Hero Token for Pevilo ibn Djimin in Arwic.",
                Withdraw = "The K'nath have drawn back from Hebian-To.",
                Evidence = "903830 at 0xE74D0028 (row DirectorHeroKnath): retail's Apr 1 2009 list (wiki rev 443669), K'nath N'da 38009, R'ajed 38010, Y'nda 38011, Y'bot 38016 (L220)",
            },
            new Entry
            {
                Key = "hero-apostate-hebian", Title = "Apostates Spill into Hebian-To", Where = "39.6S, 83.1E", Levels = "185 to 499",
                Regions = new[] { "hebian" }, Box = (0xE6, 0x4C, 0xE8, 0x4E), Festivals = new[] { "heroraids" }, Minutes = 60,
                Start = new[] { "DirectorHeroApostate" }, Stop = new[] { "DirectorHeroApostate" }, HeldWins = true,
                Announce = LEAD + "Apostates, Gotrok miners and a Tremendous Baby Monouga are pouring into Hebian-To from the south, near 39.6S, 83.1E. Hold the town for the hour and every defender earns a Hero Token. Who will heed the call?",
                Victory = "Hebian-To held against the Apostates! Every defender who stood there has a Hero Token for Pevilo ibn Djimin in Arwic.",
                Withdraw = "The Apostates have fled Hebian-To.",
                Evidence = "903831 at 0xE74D0018 (row DirectorHeroApostate): retail's Mar 16 2009 'Not a Dungeon Filled With Monsters' list (wiki rev 465495), Apostate Servant 40499, Hea Apostate Shock Trooper 40495, Gotrok Aetherium Miner 40492, Tremendous Baby Monouga 80150",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/burnja.md): retail's Apr 2007 Captain Burnja Attacks the
            // Windmill, Mar 22 to 31: Burnja and his escorts at the old windmill's spot; his death stops the row (won). His
            // windmill token goes to Jarvis Hammerstone for Knight of the Windmill (503). Rows and gens from
            // tools/gpubox-ace/gen_retired_mar22.py (2027-03-22-burnja-1-windmill.sql).
            new Entry
            {
                Key = "burnja-windmill", Title = "Captain Burnja Attacks the Windmill", Where = "25.0N, 45.5E", Levels = "135",
                Regions = new[] { "windmill" }, Box = (0xB7, 0x9D, 0xB9, 0x9F), Festivals = new[] { "burnja" }, Minutes = 60,
                Start = new[] { "DirectorBurnjaWindmill" }, Stop = new[] { "DirectorBurnjaWindmill" },
                Announce = LEAD + "Captain Burnja and his Drudge Lurkers and Stalkers have come to the old windmill near Cragstone, at 25.0N, 45.5E, torches in hand. Who will heed the call?",
                Victory = "Captain Burnja has fallen at the windmill. Jarvis Hammerstone in Cragstone will want to see his token.",
                Withdraw = "Captain Burnja and his drudges have slunk away from the windmill.",
                Evidence = "903902 at 0xB89E0016 (row DirectorBurnjaWindmill): Captain Burnja 903900 (copy of 87383, L135), 4 Drudge Lurkers 1608, 4 Drudge Stalkers 1609 (wiki rev 443260); his death stops the row",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/burun-raids.md): retail's May 2004 Burun Raids, Jun 1 to
            // 14: retail's own raid generators and rows (Event<Town>Vagrants High / Extreme / Uber) outside five towns, with a leader
            // per tier (copies of Barluuk, Urgluuk, Jorgluuk) whose death stops his row; the third down wins. Rows and gens from
            // tools/gpubox-ace/gen_retired_jun1.py (2027-06-01-burun-1-raids.sql).
            new Entry
            {
                Key = "burun-hebianto", Title = "The Burun Raid Hebian-To", Where = "39.7S, 83.4E", Levels = "95 to 140",
                Regions = new[] { "hebian" }, Box = (0xE6, 0x4C, 0xE8, 0x4E), Festivals = new[] { "burunraids" }, Minutes = 60,
                Start = new[] { "EventHebiantoVagrantsHigh", "EventHebiantoVagrantsExtreme", "EventHebiantoVagrantsUber" }, Stop = new[] { "EventHebiantoVagrantsHigh", "EventHebiantoVagrantsExtreme", "EventHebiantoVagrantsUber" },
                Announce = LEAD + "A Burun raiding party has come out of the swamps to raid Hebian-To, near 39.7S, 83.4E, led by Barluuk, Urgluuk and Jorgluuk. Who will heed the call?",
                Victory = "The Burun raid on Hebian-To is broken and its leaders are dead. Their Burun Idols are for Translator Aun Laokhe in Arwic.",
                Withdraw = "The Burun have gone back to the swamps from Hebian-To.",
                Evidence = "retail's raid generators for Hebian-To (the EventHebiantoVagrants rows) twice each, and the three leader copies 903930-903932 (wiki rev 447715)",
            },
            new Entry
            {
                Key = "burun-sawato", Title = "The Burun Raid Sawato", Where = "28.3S, 58.9E", Levels = "95 to 140",
                Regions = new[] { "sawato" }, Box = (0xC8, 0x5B, 0xCA, 0x5D), Festivals = new[] { "burunraids" }, Minutes = 60,
                Start = new[] { "EventSawatoVagrantsHigh", "EventSawatoVagrantsExtreme", "EventSawatoVagrantsUber" }, Stop = new[] { "EventSawatoVagrantsHigh", "EventSawatoVagrantsExtreme", "EventSawatoVagrantsUber" },
                Announce = LEAD + "A Burun raiding party has come out of the swamps to raid Sawato, near 28.3S, 58.9E, led by Barluuk, Urgluuk and Jorgluuk. Who will heed the call?",
                Victory = "The Burun raid on Sawato is broken and its leaders are dead. Their Burun Idols are for Translator Aun Laokhe in Arwic.",
                Withdraw = "The Burun have gone back to the swamps from Sawato.",
                Evidence = "retail's raid generators for Sawato (the EventSawatoVagrants rows) twice each, and the three leader copies 903930-903932 (wiki rev 447715)",
            },
            new Entry
            {
                Key = "burun-shoushi", Title = "The Burun Raid Shoushi", Where = "33.4S, 72.4E", Levels = "95 to 140",
                Regions = new[] { "shoushi" }, Box = (0xD8, 0x54, 0xDA, 0x56), Festivals = new[] { "burunraids" }, Minutes = 60,
                Start = new[] { "EventShoushiVagrantsHigh", "EventShoushiVagrantsExtreme", "EventShoushiVagrantsUber" }, Stop = new[] { "EventShoushiVagrantsHigh", "EventShoushiVagrantsExtreme", "EventShoushiVagrantsUber" },
                Announce = LEAD + "A Burun raiding party has come out of the swamps to raid Shoushi, near 33.4S, 72.4E, led by Barluuk, Urgluuk and Jorgluuk. Who will heed the call?",
                Victory = "The Burun raid on Shoushi is broken and its leaders are dead. Their Burun Idols are for Translator Aun Laokhe in Arwic.",
                Withdraw = "The Burun have gone back to the swamps from Shoushi.",
                Evidence = "retail's raid generators for Shoushi (the EventShoushiVagrants rows) twice each, and the three leader copies 903930-903932 (wiki rev 447715)",
            },
            new Entry
            {
                Key = "burun-toutou", Title = "The Burun Raid Tou-Tou", Where = "27.8S, 95.5E", Levels = "95 to 140",
                Regions = new[] { "toutou" }, Box = (0xF5, 0x5B, 0xF7, 0x5D), Festivals = new[] { "burunraids" }, Minutes = 60,
                Start = new[] { "EventTouTouVagrantsHigh", "EventTouTouVagrantsExtreme", "EventTouTouVagrantsUber" }, Stop = new[] { "EventTouTouVagrantsHigh", "EventTouTouVagrantsExtreme", "EventTouTouVagrantsUber" },
                Announce = LEAD + "A Burun raiding party has come out of the swamps to raid Tou-Tou, near 27.8S, 95.5E, led by Barluuk, Urgluuk and Jorgluuk. Who will heed the call?",
                Victory = "The Burun raid on Tou-Tou is broken and its leaders are dead. Their Burun Idols are for Translator Aun Laokhe in Arwic.",
                Withdraw = "The Burun have gone back to the swamps from Tou-Tou.",
                Evidence = "retail's raid generators for Tou-Tou (the EventTouTouVagrants rows) twice each, and the three leader copies 903930-903932 (wiki rev 447715)",
            },
            new Entry
            {
                Key = "burun-yanshi", Title = "The Burun Raid Yanshi", Where = "13.1S, 46.0E", Levels = "95 to 140",
                Regions = new[] { "yanshi" }, Box = (0xB7, 0x6E, 0xB9, 0x70), Festivals = new[] { "burunraids" }, Minutes = 60,
                Start = new[] { "EventYanshiVagrantsHigh", "EventYanshiVagrantsExtreme", "EventYanshiVagrantsUber" }, Stop = new[] { "EventYanshiVagrantsHigh", "EventYanshiVagrantsExtreme", "EventYanshiVagrantsUber" },
                Announce = LEAD + "A Burun raiding party has come out of the swamps to raid Yanshi, near 13.1S, 46.0E, led by Barluuk, Urgluuk and Jorgluuk. Who will heed the call?",
                Victory = "The Burun raid on Yanshi is broken and its leaders are dead. Their Burun Idols are for Translator Aun Laokhe in Arwic.",
                Withdraw = "The Burun have gone back to the swamps from Yanshi.",
                Evidence = "retail's raid generators for Yanshi (the EventYanshiVagrants rows) twice each, and the three leader copies 903930-903932 (wiki rev 447715)",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/viridian-baishi.md): retail's Apr 24 2015 Viridian
            // incursion at Baishi, Jun 1 to 14: Zerzelikyr and the Viridian Rise's camp lists outside Baishi; his death stops the
            // row (won). Rows and gens from tools/gpubox-ace/gen_retired_jun1b.py (2027-06-01-viridian-1-baishi.sql).
            new Entry
            {
                Key = "viridian-baishi", Title = "Zerzelikyr Leads the Viridian Rise against Baishi", Where = "48.3S, 63.0E", Levels = "265 to 400",
                Regions = new[] { "baishi" }, Box = (0xCD, 0x42, 0xCF, 0x44), Festivals = new[] { "viridianbaishi" }, Minutes = 60,
                Start = new[] { "DirectorViridianBaishi" }, Stop = new[] { "DirectorViridianBaishi" },
                Announce = LEAD + "Zerzelikyr, the Elder Gromnie Champion of the Viridian Rise, has come out of the thorns a couple of clicks from Baishi, near 48.3S, 63.0E, and the Rise's creatures are following him. Who will heed the call?",
                Victory = "Zerzelikyr has fallen outside Baishi, and the Viridian Rise's creatures scatter back to the thorns.",
                Withdraw = "Zerzelikyr and the Viridian Rise have withdrawn from Baishi.",
                Evidence = "903951 at 0xCE430009 (row DirectorViridianBaishi): Zerzelikyr 903950 (copy of 72002, L400, 200,200 health, his loot) and copies of retail's camp lists 70842-70845 (A'nekshen, Zefir, Gromnie, Brier Wasp, L265-300) two each (wiki rev 473904)",
            },
            // RETIRED LIVE EVENT (docs/EVENTS-RETIRED.md, docs/EVENT-TESTS/martine-attacks.md): retail's May to Jun 2002 Martine
            // Attacks, Jun 1 to 7: Martine and four Virindi Renegades at one of two spots; the hour runs out (Martine vanishes).
            // Rows and gens from tools/gpubox-ace/gen_retired_jun1d.py (2027-06-01-martine-1-attacks.sql).
            new Entry
            {
                Key = "martine-obsidian", Title = "Martine on the Obsidian Plains", Where = "64.1S, 63.7W", Levels = "69",
                Regions = new[] { "obsidian" }, Box = (0x2E, 0x2E, 0x30, 0x30), Festivals = new[] { "martineattacks" }, Minutes = 60,
                Start = new[] { "DirectorMartineObsidian" }, Stop = new[] { "DirectorMartineObsidian" },
                Announce = LEAD + "Martine has been seen in the Obsidian Plains crater, near 64.1S, 63.7W, and Virindi Renegades are hunting him. He pays for their tokens.",
                Victory = "The Virindi Renegades in the Obsidian Plains crater are dead, and Martine has vanished again.",
                Withdraw = "Martine has vanished from the Obsidian Plains crater, and the Virindi Renegades with him.",
                Evidence = "904022-904023 at 0x2F2F0033 (row DirectorMartineObsidian): Martine 904021 (copy of 20028, L750, only fights back, takes Renegade's Token 20033) and four Virindi Renegades 904020 (renamed copy of 11622, L69, the token on every corpse; owner yes 2026-11-09) (wiki rev 443396)",
            },
            new Entry
            {
                Key = "martine-marae", Title = "Martine on Marae Lassel", Where = "55.1N, 71.1W", Levels = "69",
                Regions = new[] { "marae-martine" }, Box = (0x25, 0xC3, 0x27, 0xC5), Festivals = new[] { "martineattacks" }, Minutes = 60,
                Start = new[] { "DirectorMartineMarae" }, Stop = new[] { "DirectorMartineMarae" },
                Announce = LEAD + "Martine has been seen on Marae Lassel, near 55.1N, 71.1W, and Virindi Renegades are hunting him. He pays for their tokens.",
                Victory = "The Virindi Renegades on Marae Lassel are dead, and Martine has vanished again.",
                Withdraw = "Martine has vanished from Marae Lassel, and the Virindi Renegades with him.",
                Evidence = "904024-904025 at 0x26C40023 (row DirectorMartineMarae): Martine 904021 (copy of 20028, L750, only fights back, takes Renegade's Token 20033) and four Virindi Renegades 904020 (renamed copy of 11622, L69, the token on every corpse; owner yes 2026-11-09) (wiki rev 443396)",
            },
            // T'THUUN'S MARAUDERS (retail Nov 2008, revived 2026-11-01 for the owner's review; docs/EVENT-TESTS/tthuun-marauders.md).
            // The twelve retail raid sites, one at a time while the calendar's "tthuun" festival runs (Nov 16 to 29). Rows and
            // wave generators: tools/public-shard/datafixes/2026-11-16-tthuun-1-raids.sql; the waves after the first, the
            // site stones, the Royal Scouts' kill tasks and the Sparks: TthuunRaids.cs (sites in TthuunRaidsRule.cs).
            new Entry
            {
                Key = "tthuun-toutou", Title = "T'thuun's Marauders Raid Tou-Tou", Where = "26.9S, 96.8E", Levels = "200 to 220",
                Regions = new[] { "toutou", "tthuun" }, Box = (0xF7, 0x5C, 0xF9, 0x5E), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunToutou" }, Stop = new[] { "DirectorTthuunToutou" },
                Announce = LEAD + "T'thuun's Marauders are raiding Tou-Tou! A Patriarchs' device has opened by the lighthouse, near 26.9S, 96.8E, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion. The Royal Scout by the mage tower pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Tou-Tou has gone dark. Well fought!",
                Withdraw = "The marauders at Tou-Tou have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunToutou; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-isleoftears", Title = "T'thuun's Raiders Raid The Isle of Tears", Where = "21.6N, 53.7E", Levels = "160 to 185",
                Regions = new[] { "isleoftears", "tthuun" }, Box = (0xC1, 0x99, 0xC3, 0x9B), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunIsleOfTears" }, Stop = new[] { "DirectorTthuunIsleOfTears" },
                Announce = LEAD + "T'thuun's Raiders are raiding the Isle of Tears! A Patriarchs' device has opened by the Underground City, near 21.6N, 53.7E, and Moarsman, Sclavus and Falatacot raiders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Inquisitor. The Royal Scout at the portal from Cragstone pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at the Isle of Tears has gone dark. Well fought!",
                Withdraw = "The raiders at the Isle of Tears have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (raiders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunIsleOfTears; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-silyun", Title = "T'thuun's Marauders Raid Silyun", Where = "87.4N, 70.6W", Levels = "200 to 220",
                Regions = new[] { "silyun", "tthuun" }, Box = (0x26, 0xEB, 0x28, 0xED), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunSilyun" }, Stop = new[] { "DirectorTthuunSilyun" },
                Announce = LEAD + "T'thuun's Marauders are raiding Silyun! A Patriarchs' device has opened in the middle of the town, near 87.4N, 70.6W, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion. The Royal Scout by the town pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Silyun has gone dark. Well fought!",
                Withdraw = "The marauders at Silyun have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunSilyun; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-asheronsisland", Title = "T'thuun's Raiders Raid Asheron's Island", Where = "17.3N, 71.7E", Levels = "160 to 185",
                Regions = new[] { "asheronsisland", "tthuun" }, Box = (0xD8, 0x94, 0xDA, 0x96), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunAsheronsIsland" }, Stop = new[] { "DirectorTthuunAsheronsIsland" },
                Announce = LEAD + "T'thuun's Raiders are raiding Asheron's Island! A Patriarchs' device has opened on top of the hill, near 17.3N, 71.7E, and Moarsman, Sclavus and Falatacot raiders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Inquisitor. The Royal Scout by the portal drop from Eastham (16.2N, 69.5E) pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Asheron's Island has gone dark. Well fought!",
                Withdraw = "The raiders at Asheron's Island have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (raiders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunAsheronsIsland; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-candethkeep", Title = "T'thuun's Marauders Raid Candeth Keep", Where = "87.5S, 67.1W", Levels = "200 to 220",
                Regions = new[] { "candethkeep", "tthuun" }, Box = (0x2A, 0x11, 0x2C, 0x13), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunCandethKeep" }, Stop = new[] { "DirectorTthuunCandethKeep" },
                Announce = LEAD + "T'thuun's Marauders are raiding Candeth Keep! A Patriarchs' device has opened in the middle of the town, near 87.5S, 67.1W, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion. The Royal Scout on the outer wall pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Candeth Keep has gone dark. Well fought!",
                Withdraw = "The marauders at Candeth Keep have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunCandethKeep; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-colier", Title = "T'thuun's Raiders Raid Colier", Where = "56.8N, 38.4E", Levels = "160 to 185",
                Regions = new[] { "colier", "tthuun" }, Box = (0xAE, 0xC5, 0xB0, 0xC7), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunColier" }, Stop = new[] { "DirectorTthuunColier" },
                Announce = LEAD + "T'thuun's Raiders are raiding Colier! A Patriarchs' device has opened in the middle of the town, near 56.8N, 38.4E, and Moarsman, Sclavus and Falatacot raiders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Inquisitor. The Royal Scout by the Holtburg portal pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Colier has gone dark. Well fought!",
                Withdraw = "The raiders at Colier have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (raiders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunColier; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-osteth", Title = "T'thuun's Marauders Raid The Southern Osteth Menhir Ring", Where = "69.7S, 40.1E", Levels = "200 to 220",
                Regions = new[] { "osteth", "tthuun" }, Box = (0xB0, 0x27, 0xB2, 0x29), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunOsteth" }, Stop = new[] { "DirectorTthuunOsteth" },
                Announce = LEAD + "T'thuun's Marauders are raiding the Southern Osteth Menhir Ring! A Patriarchs' device has opened in the middle of the stone ring, near 69.7S, 40.1E, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion. The Royal Scout on the hill beside it pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at the Southern Osteth Menhir Ring has gone dark. Well fought!",
                Withdraw = "The marauders at the Southern Osteth Menhir Ring have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunOsteth; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-esper", Title = "T'thuun's Marauders Raid The Mount Esper crater", Where = "65.8N, 13.1E", Levels = "200 to 220",
                Regions = new[] { "esper", "tthuun" }, Box = (0x8E, 0xD0, 0x90, 0xD2), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunEsper" }, Stop = new[] { "DirectorTthuunEsper" },
                Announce = LEAD + "T'thuun's Marauders are raiding the Mount Esper crater! A Patriarchs' device has opened on the crater lake's western shore, near 65.8N, 13.1E, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion. The Royal Scout by the housing portals pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at the Mount Esper crater has gone dark. Well fought!",
                Withdraw = "The marauders at the Mount Esper crater have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunEsper; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-midsong", Title = "T'thuun's Marauders Raid The Festival Stone of Midsong", Where = "0.3S, 0.5W", Levels = "200 to 220",
                Regions = new[] { "midsong", "tthuun" }, Box = (0x7D, 0x7E, 0x7F, 0x80), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunMidsong" }, Stop = new[] { "DirectorTthuunMidsong" },
                Announce = LEAD + "T'thuun's Marauders are raiding the Festival Stone of Midsong! A Patriarchs' device has opened beside the Festival Stone, near 0.3S, 0.5W, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion. The Royal Scout by the stone pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at the Festival Stone of Midsong has gone dark. Well fought!",
                Withdraw = "The marauders at the Festival Stone of Midsong have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunMidsong; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-ayanbaqur", Title = "T'thuun's Marauders Raid Ayan Baqur", Where = "59.9S, 87.9W", Levels = "200 to 220",
                Regions = new[] { "ayanbaqur", "tthuun" }, Box = (0x10, 0x33, 0x12, 0x35), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunAyanBaqur" }, Stop = new[] { "DirectorTthuunAyanBaqur" },
                Announce = LEAD + "T'thuun's Marauders are raiding Ayan Baqur! A Patriarchs' device has opened under the pyramid over the town, near 59.9S, 87.9W, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion and Kazyk Ri's Raid Commander himself. The Royal Scout south of the town pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Ayan Baqur has gone dark and Kazyk Ri's Raid Commander is slain. Well fought!",
                Withdraw = "The marauders at Ayan Baqur have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site, Kazyk Ri's Raid Commander); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunAyanBaqur; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-arwic", Title = "T'thuun's Marauders Raid Arwic", Where = "33.6N, 56.8E", Levels = "200 to 220",
                Regions = new[] { "arwic", "tthuun" }, Box = (0xC5, 0xA8, 0xC7, 0xAA), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunArwic" }, Stop = new[] { "DirectorTthuunArwic" },
                Announce = LEAD + "T'thuun's Marauders are raiding Arwic! A Patriarchs' device has opened under the pyramid over the town, near 33.6N, 56.8E, and Moarsman, Sclavus and Falatacot marauders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Champion and Minik Ra's Raid Commander himself. The Royal Scout by the south entrance pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Arwic has gone dark and Minik Ra's Raid Commander is slain. Well fought!",
                Withdraw = "The marauders at Arwic have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (marauders site, Minik Ra's Raid Commander); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunArwic; waves after the first: TthuunRaids.cs",
            },
            new Entry
            {
                Key = "tthuun-mayoi", Title = "T'thuun's Raiders Raid Mayoi", Where = "61.5S, 81.9E", Levels = "160 to 210",
                Regions = new[] { "mayoi", "tthuun" }, Box = (0xE4, 0x31, 0xE6, 0x33), Festivals = new[] { "tthuun" }, Minutes = 60,
                Start = new[] { "DirectorTthuunMayoi" }, Stop = new[] { "DirectorTthuunMayoi" },
                Announce = LEAD + "T'thuun's Raiders are raiding Mayoi! A Patriarchs' device has opened under the pyramid over the town, near 61.5S, 81.9E, and Moarsman, Sclavus and Falatacot raiders are pouring through. Clear them all to draw out the Patriarchs' lieutenants, then a Patriarch's Inquisitor and Nivinizk's Raid Commander himself. The Royal Scout by the lifestone pays for every one you put down. Who will heed the call?",
                Victory = "The Patriarchs' device at Mayoi has gone dark and Nivinizk's Raid Commander is slain. Well fought!",
                Withdraw = "The raiders at Mayoi have slipped back through the Patriarchs' device.",
                Evidence = "wiki T'Thuun's Marauders rev 477303 (raiders site, Nivinizk's Raid Commander); wave gens 2026-11-16-tthuun-1-raids.sql on DirectorTthuunMayoi; waves after the first: TthuunRaids.cs",
            },
            // Season 1 beat 1, "Omens" (docs/SEASON-THE-SHADOW-RETURNS.md, docs/MONSTERS-LORE-FACTIONS.md): Aerbax's
            // Umbral Legion raids three towns. Ours, not retail: rows and gens from tools/gpubox-ace/lore-factions.sql.
            // In the rotation only while event_director_omens is on (off by default); @eventdirector start works regardless.
            new Entry
            {
                Key = "omens-arwic", Title = "Shadows Gather over Arwic", Where = "33.7N, 57.6E", Levels = "80 to 90",
                Regions = new[] { "arwic", "omens" }, Box = (0xC6, 0xA8, 0xC8, 0xAA),
                Start = new[] { "OmensRaidArwic" }, Stop = new[] { "OmensRaidArwic" },
                Announce = LEAD + "Shadows gather over Arwic! A herald of Aerbax is sounding the raid just east of the town, near 33.7N, 57.6E. Who will heed the call?",
                Victory = "The shadows over Arwic have broken. Well fought!",
                Withdraw = "The shadows have withdrawn from Arwic.",
                Evidence = "acomensraidarwicgen (901332) at 0xC7A9; Umbral Herald of Aerbax 901331 L90, Pandemonium Shadows 22910 L80",
            },
            new Entry
            {
                Key = "omens-tufa", Title = "Shadows Gather over Tufa", Where = "15.4S, 6.5E", Levels = "80 to 90",
                Regions = new[] { "tufa", "omens" }, Box = (0x86, 0x6A, 0x88, 0x6C),
                Start = new[] { "OmensRaidTufa" }, Stop = new[] { "OmensRaidTufa" },
                Announce = LEAD + "Shadows gather over Tufa! A herald of Aerbax is sounding the raid south-east of the town, near 15.4S, 6.5E. Who will heed the call?",
                Victory = "The shadows over Tufa have broken. Well fought!",
                Withdraw = "The shadows have withdrawn from Tufa.",
                Evidence = "acomensraidtufagen (901333) at 0x876B; Umbral Herald of Aerbax 901331 L90, Pandemonium Shadows 22910 L80",
            },
            new Entry
            {
                Key = "omens-cragstone", Title = "Shadows Gather over Cragstone", Where = "25.1N, 48.5E", Levels = "100 to 160",
                Regions = new[] { "cragstone", "omens" }, Box = (0xBB, 0x9D, 0xBD, 0x9F),
                Start = new[] { "OmensRaidCragstone" }, Stop = new[] { "OmensRaidCragstone" },
                Announce = LEAD + "Shadows gather over Cragstone! A herald of Aerbax is sounding the raid south of the town, near 25.1N, 48.5E. Who will heed the call?",
                Victory = "The shadows over Cragstone have broken. Well fought!",
                Withdraw = "The shadows have withdrawn from Cragstone.",
                Evidence = "acomensraidcragstonegen (901334) at 0xBC9E; Umbral Herald of Aerbax 901330 L150, Twisted Shadows 32791 L160, Maelstrom Shadows 22909 L100",
            },
        };

        // ---- switches (ordinary ACE server properties; @modifybool / @modifydouble / @modifystring) ----

        // The master switch is rg_event_director, default OFF (2026-10-22, before the first live run). It replaced
        // event_director_enabled (default on), which is no longer read: a fresh name cannot inherit a stored value, so
        // a deploy never starts the rotation on a shard where nobody turned it on.
        const string P_ENABLED = "rg_event_director", P_DURATION = "event_director_duration_min",
                     P_GAP = "event_director_gap_min", P_IDLE = "event_director_idle_end_min",
                     P_MINPLAYERS = "event_director_min_players", P_SKIP = "event_director_skip",
                     P_OMENS = "event_director_omens", P_FESTIVALS = "event_director_festivals",
                     P_FEST_EVERY = "event_director_festival_every";

        /// <summary>Landblocks around an event's box that a world boss's lair must stay clear of during his window.</summary>
        const int BOSS_MARGIN = 4;

        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            (P_DURATION, 120, "EventDirector: minutes each live event runs (fractions allowed, for tests)"),
            (P_GAP, 90, "EventDirector: quiet minutes between one live event and the next"),
            (P_IDLE, 60, "EventDirector: end an event early after this many minutes with nobody near it (0 = never)"),
            (P_MINPLAYERS, 1, "EventDirector: players that must be online before an event starts"),
            (P_FEST_EVERY, 3, "EventDirector: in season, a festival event after every N year-round events (1 alternates; 3 is about 2 a day)"),
        };

        // ---- state (persisted) ----------------------------------------------------------------

        sealed class State
        {
            [JsonPropertyName("current")] public string Current { get; set; }
            [JsonPropertyName("startedAt")] public double StartedAt { get; set; }
            [JsonPropertyName("endsAt")] public double EndsAt { get; set; }
            [JsonPropertyName("nextAt")] public double NextAt { get; set; }
            [JsonPropertyName("rotation")] public int Rotation { get; set; }
            [JsonPropertyName("festRotation")] public int FestRotation { get; set; }
            [JsonPropertyName("sinceFest")] public int SinceFest { get; set; } = 1000;   // year-round events since the last festival one
            [JsonPropertyName("manual")] public bool Manual { get; set; }   // the current event was started by staff: it runs out even with the rotation off
            [JsonPropertyName("lastRun")] public Dictionary<string, double> LastRun { get; set; } = new Dictionary<string, double>();
        }

        const string STATE_FILE = "EventDirectorState.json";
        static State s_State = new State();
        static readonly object s_Lock = new object();
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        // runtime-only
        static bool s_Booted;
        static bool s_Suspended;             // stopped for a shutdown countdown
        static bool s_PartyHold;             // held for Anniversary Night (@anniversary start)
        static bool? s_WasEnabled;
        static double s_NextTick, s_LastSeenAt, s_NextPresence;
        static int s_AllOffChecks;
        static readonly HashSet<string> s_StagesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<(WeakReference<WorldObject> gen, string ev)> s_SweepGens = new List<(WeakReference<WorldObject>, string)>();
        static readonly Queue<double> s_SweepAt = new Queue<double>();

        internal static void Register()
        {
            RegisterProperties();
            CommandManager.TryAddCommand(EventsCmd, "events", AccessLevel.Player, CommandHandlerFlag.None,
                "What live event is running in Dereth, and what is next.", "");
            CommandManager.TryAddCommand(DirectorCmd, "eventdirector", AccessLevel.Admin, CommandHandlerFlag.None,
                "Live event director (RevivalGuard): status, or list | on | off | next | skip | stop | start <key>.",
                "[list | on | off | next | skip | stop | start <key>]");
            LoadState();
            Mod.Log.Info($"[RevivalGuard] EventDirector: {Catalogue.Length} live events in the catalogue, {Catalogue.Count(e => e.IsFestival)} of them festival-only (@events, @eventdirector; {P_ENABLED} default off)");
        }

        static bool Enabled => PropertyManager.GetBool(P_ENABLED, false).Item;
        const string P_FROSTFELL = "rg_event_director_frostfell";
        const string P_TTHUUN = "rg_event_director_tthuun";
        static bool FrostfellSwitch => PropertyManager.GetBool(P_FROSTFELL, true).Item;
        /// <summary>A self-running festival's own switch (EventDirectorRules.SELF_RUN): rg_event_director_frostfell,
        /// rg_event_director_tthuun, or rg_event_director_retired for every revived retired event; all default on.</summary>
        static bool SelfRunSwitch(string festival) =>
            festival.Equals(EventDirectorRules.TTHUUN, StringComparison.OrdinalIgnoreCase) ? PropertyManager.GetBool(P_TTHUUN, true).Item
            : festival.Equals(EventDirectorRules.FROSTFELL, StringComparison.OrdinalIgnoreCase) ? FrostfellSwitch
            : EventDirectorRules.RETIRED.Contains(festival, StringComparer.OrdinalIgnoreCase) && RetiredSwitch;
        /// <summary>The director runs: rg_event_director on, or a self-running festival (Frostfell, T'thuun's Marauders, a
        /// revived retired event) active with its switch on (EventDirectorRules.DirectorOn; then only that festival's own entries).</summary>
        static bool Running => EventDirectorRules.DirectorOn(Enabled, f => SelfRunSwitch(f) && SeasonalCalendar.IsFestivalActive(f));
        const string P_RETIRED = "rg_event_director_retired";
        static bool RetiredSwitch => PropertyManager.GetBool(P_RETIRED, true).Item;
        /// <summary>Which self-running festivals run their own events now (status lines).</summary>
        static string SelfRunNow => string.Join(" and ", EventDirectorRules.SELF_RUN.Where(f => SelfRunSwitch(f) && SeasonalCalendar.IsFestivalActive(f)));

        /// <summary>The running event's key, start and end (unix seconds), or null: for Frostfell's raid timer.</summary>
        internal static (string key, double startedAt, double endsAt)? CurrentEvent()
        {
            var s = s_State;
            return s?.Current == null ? ((string, double, double)?)null : (s.Current, s.StartedAt, s.EndsAt);
        }
        static bool FestivalsOn => PropertyManager.GetBool(P_FESTIVALS, true).Item;
        static int FestEvery => (int)Math.Clamp(Math.Round(Minutes(P_FEST_EVERY, 3)), 1, 50);
        static double Minutes(string key, double def) => PropertyManager.GetDouble(key, def).Item;
        static double Now => Time.GetUnixTime();
        static double Duration(Entry e) => Math.Max(0.1, e.Minutes ?? Minutes(P_DURATION, 120));
        static double Gap => Math.Max(0.1, Minutes(P_GAP, 90));

        static void Postfix()
        {
            var now = Now;
            if (now < s_NextTick) return;
            s_NextTick = now + 5.0;
            try { Tick(now); }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] EventDirector tick threw; will retry", e); s_NextTick = now + 60; }
        }

        static void Tick(double now)
        {
            RunSweeps(now);

            lock (s_Lock)
            {
                if (!s_Booted) { s_Booted = true; Boot(now); }

                var cur = Find(s_State.Current);

                // A shutdown countdown: stop quietly, keep the schedule, pick it up if cancelled.
                if (ServerManager.ShutdownInitiated)
                {
                    if (cur != null && !s_Suspended)
                    {
                        s_Suspended = true;
                        StopRows(cur, now);
                        Mod.Log.Info($"[RevivalGuard] EventDirector: {cur.Key} stopped for the shutdown; it resumes after the restart until {Stamp(s_State.EndsAt)}");
                    }
                    return;
                }
                if (s_Suspended)
                {
                    s_Suspended = false;
                    if (cur != null) { StartRows(cur); Mod.Log.Info($"[RevivalGuard] EventDirector: shutdown cancelled; {cur.Key} restarted"); }
                }

                bool enabled = Running;
                if (s_WasEnabled != enabled)
                {
                    // Switched on (by @eventdirector on or @modifybool): the first event comes in ten minutes, not at
                    // once from a schedule that fell due while it was off; time enough to switch it off again.
                    if (enabled && s_WasEnabled == false && s_State.NextAt < now + 600) { s_State.NextAt = now + 600; Save(); }
                    if (s_WasEnabled != null) Mod.Log.Info($"[RevivalGuard] EventDirector: switched {(enabled ? "ON" : "OFF")}; next {(enabled ? Stamp(s_State.NextAt) : "none")}");
                    s_WasEnabled = enabled;
                }

                if (!enabled)
                {
                    // Off: the rotation stops and its event ends; one an admin started by hand runs out its time.
                    if (cur != null && !s_State.Manual) End(cur, now, "director turned off", announce: true, victory: false);
                    else if (cur != null) WatchRunning(cur, now);
                    return;
                }

                // Anniversary Night (@anniversary start): the party has the world's attention. The rotation holds, an
                // event it started closes with its withdraw line, and the next one comes a full gap after the party.
                if (AnniversaryNight.On)
                {
                    if (!s_PartyHold) { s_PartyHold = true; Mod.Log.Info("[RevivalGuard] EventDirector: holding for Anniversary Night"); }
                    if (cur != null && !s_State.Manual) End(cur, now, "Anniversary Night began", announce: true, victory: false);
                    else if (cur != null) WatchRunning(cur, now);
                    s_State.NextAt = Math.Max(s_State.NextAt, now + Gap * 60);
                    return;
                }
                if (s_PartyHold)
                {
                    s_PartyHold = false;
                    s_State.NextAt = Math.Max(s_State.NextAt, now + Gap * 60);
                    Save();
                    Mod.Log.Info($"[RevivalGuard] EventDirector: Anniversary Night is over; next live event {Stamp(s_State.NextAt)}");
                }

                if (cur != null) { WatchRunning(cur, now); return; }

                if (now < s_State.NextAt) return;
                if (Mercenaries.RealOnline() < Math.Max(0, Minutes(P_MINPLAYERS, 1)))
                {
                    s_State.NextAt = now + 60;   // look again in a minute; nobody to see it
                    return;
                }
                var next = PickNext(now);
                if (next == null) { s_State.NextAt = now + 600; Save(); return; }
                Begin(next, now, manual: false);
            }
        }

        static void Boot(double now)
        {
            var cur = Find(s_State.Current);
            if (cur != null && s_State.EndsAt > now + 60)
            {
                StartRows(cur);
                s_LastSeenAt = now;
                Mod.Log.Info($"[RevivalGuard] EventDirector: resumed {cur.Key} after the restart, until {Stamp(s_State.EndsAt)}");
                return;
            }
            if (cur != null)
            {
                Mod.Log.Info($"[RevivalGuard] EventDirector: {cur.Key} ran out while the shard was down");
                s_State.Current = null;
                s_State.Manual = false;
            }
            // A deploy or restart never fires an event straight away: at least one gap first, and a
            // schedule that fell due while the shard was down waits five minutes for the world to settle.
            if (s_State.NextAt <= 0) s_State.NextAt = now + Gap * 60;
            else if (s_State.NextAt < now + 300) s_State.NextAt = now + 300;
            Save();
            Mod.Log.Info($"[RevivalGuard] EventDirector: {(Enabled ? "ON" : $"OFF ({P_ENABLED})")}{(!Enabled ? $", Frostfell's and T'thuun's Marauders' own events run while they are on ({P_FROSTFELL}, {P_TTHUUN})" : "")}; next live event {Stamp(s_State.NextAt)}");
        }

        // ---- choosing ---------------------------------------------------------------------------

        static Entry Find(string key) =>
            key == null ? null : Catalogue.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        static bool IsOn(string ev) => EventManager.GetEventStatus(ev) == GameEventState.On;

        static string FestivalNames(Entry e) => string.Join(" or ", e.Festivals);

        /// <summary>A world boss whose window (warning included) meets [from, to) with his lair near this event, or null.</summary>
        static string BossInTheWay(Entry e, double from, double to)
        {
            foreach (var (name, lair, wFrom, wTo) in WorldBossSchedule.WindowsBetween(from, to))
                if (EventDirectorRules.LairNear(e.Box, lair, BOSS_MARGIN) && EventDirectorRules.Overlaps(from, to, wFrom, wTo))
                    return $"{name}'s window ({Stamp(wFrom)} to {Stamp(wTo)}) is in its region";
            return null;
        }

        /// <summary>Why this entry cannot start right now, or null if it can. `ignore` is the entry being
        /// replaced (its rows are about to stop, so they do not count as busy). `manual` (an admin's start)
        /// ignores the season, the skip list and the opt-in switches, never a busy region or a boss's window.</summary>
        static string Blocked(Entry e, Entry ignore, bool manual, double now)
        {
            foreach (var ev in e.Start)
            {
                if (!EventManager.IsEventAvailable(ev)) return $"ace_world.event has no row '{ev}'";
                if (EventManager.GetEventStatus(ev) == GameEventState.Disabled) return $"'{ev}' is Disabled in ace_world";
            }
            var seasonal = SeasonalCalendar.OwnedRows();
            foreach (var ev in e.Stop)
                if (seasonal.Contains(ev)) return $"'{ev}' belongs to the seasonal calendar";
            if (!manual)
            {
                if (!EventDirectorRules.FestivalOpen(e.Festivals, SeasonalCalendar.IsFestivalActive)) return $"out of season ({FestivalNames(e)} is not on)";
                if (e.IsFestival && !FestivalsOn) return $"{P_FESTIVALS} is off";
                if (!EventDirectorRules.EntryAllowed(Enabled, e.Festivals, SelfRunSwitch)) return $"{P_ENABLED} is off (only Frostfell's, T'thuun's Marauders' and the retired events' own events run, {P_FROSTFELL}, {P_TTHUUN}, {P_RETIRED})";
                var skip = (PropertyManager.GetString(P_SKIP, "").Item ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (skip.Any(s => s.Equals(e.Key, StringComparison.OrdinalIgnoreCase))) return $"listed in {P_SKIP}";
                if (e.Key.StartsWith("omens-", StringComparison.OrdinalIgnoreCase) && !PropertyManager.GetBool(P_OMENS, false).Item)
                    return $"{P_OMENS} is off";
                if (e.Regions.Contains(EventDirectorRules.RAID_REGION, StringComparer.OrdinalIgnoreCase)
                    && EventDirectorRules.TooSoon(now, Catalogue.Where(c => c.Regions.Contains(EventDirectorRules.RAID_REGION, StringComparer.OrdinalIgnoreCase))
                        .Select(c => s_State.LastRun.TryGetValue(c.Key, out var t) ? t : 0), EventDirectorRules.RAID_APART_S))
                    return "a Present Raid ran less than 4 hours ago (retail: one every 4 hours)";
            }
            // The same region, or the same rows, already busy: something outside the director (a
            // player killing the Witshire minion, an admin's @event) or another entry is running.
            foreach (var other in Catalogue)
            {
                if (other == ignore) continue;
                bool running = other.Stop.Any(IsOn);
                if (!running) continue;
                if (other == e) return "already running (started outside the director)";
                if (other.Regions.Intersect(e.Regions, StringComparer.OrdinalIgnoreCase).Any()) return $"its region is busy with {other.Key}";
                if (other.Stop.Intersect(e.Stop, StringComparer.OrdinalIgnoreCase).Any()) return $"shares rows with {other.Key}, which is running";
            }
            // A world boss rising nearby while it runs: his fight is the region's event.
            return BossInTheWay(e, now, now + Duration(e) * 60);
        }

        static EventDirectorRules.Pick Plan(double now, Entry ignore, Entry exclude = null)
        {
            var fest = Catalogue.Select(c => c.IsFestival).ToArray();
            var ok = Catalogue.Select(c => c != exclude && Blocked(c, ignore, manual: false, now) == null).ToArray();
            return EventDirectorRules.Choose(fest, ok, s_State.Rotation, s_State.FestRotation, s_State.SinceFest, FestEvery);
        }

        /// <summary>The next entry by the two round robins (EventDirectorRules.Choose), moving the rotation.</summary>
        static Entry PickNext(double now)
        {
            var p = Plan(now, Find(s_State.Current));
            if (p.Index < 0)
            {
                var why = string.Join("; ", Catalogue.Select(e => $"{e.Key}: {Blocked(e, null, manual: false, now)}"));
                Mod.Log.Info($"[RevivalGuard] EventDirector: nothing can start ({why})");
                return null;
            }
            return Catalogue[p.Index];
        }

        /// <summary>What would start next, without moving the rotation.</summary>
        static Entry PeekNext(double now)
        {
            var p = Plan(now, Find(s_State.Current));
            return p.Index < 0 ? null : Catalogue[p.Index];
        }

        // ---- running ------------------------------------------------------------------------------

        static void Begin(Entry e, double now, bool manual)
        {
            StartRows(e);
            s_State.Current = e.Key;
            s_State.Manual = manual;
            s_State.StartedAt = now;
            s_State.EndsAt = now + Duration(e) * 60;
            s_State.LastRun[e.Key] = now;
            // The rotation moves past whatever just started, however it was started (a manual
            // `start witshire` left the pointer on witshire and the next pick ran it again).
            var p = new EventDirectorRules.Pick { Rotation = s_State.Rotation, FestRotation = s_State.FestRotation, SinceFest = s_State.SinceFest };
            EventDirectorRules.Advance(ref p, Array.IndexOf(Catalogue, e), e.IsFestival, Catalogue.Length);
            s_State.Rotation = p.Rotation; s_State.FestRotation = p.FestRotation; s_State.SinceFest = p.SinceFest;
            s_LastSeenAt = now;
            s_NextPresence = 0;
            s_AllOffChecks = 0;
            s_StagesSeen.Clear();
            Save();
            WorldBroadcast(e.Announce);
            Mod.Log.Info($"[RevivalGuard] EventDirector: STARTED {e.Key} ({string.Join(", ", e.Start)}) until {Stamp(s_State.EndsAt)}{(manual ? " (by hand)" : "")}");
        }

        static void WatchRunning(Entry cur, double now)
        {
            // Stage announcements: a row of this event that has just turned On (the creatures' own
            // emotes did it: a worker died, a Hellfire fell).
            foreach (var (ev, text) in cur.Stages)
            {
                if (IsOn(ev)) { if (s_StagesSeen.Add(ev)) { WorldBroadcast(text); Mod.Log.Info($"[RevivalGuard] EventDirector: {cur.Key} stage {ev}"); } }
            }

            // Ended by the players: every row off for two checks running (the emotes that hand one
            // stage to the next stop one row and start the other within a second or two).
            if (!cur.Stop.Any(IsOn))
            {
                // A Present Raid's rows also go off by themselves when the present is destroyed (its death starts
                // PresentRaidsDead and the reset stopgap stops it all): a loss, not a win (box 2026-10-29: an undefended
                // raid ended after 3 minutes "won by the players", with prizes for whoever stood near).
                bool lost = cur.Lost != null && s_StagesSeen.Contains(cur.Lost);
                if (++s_AllOffChecks >= 2)
                {
                    if (lost) { End(cur, now, "lost: " + cur.Lost, announce: false, victory: false); WorldBroadcast(cur.Defeat ?? cur.Withdraw); }
                    else End(cur, now, "won by the players", announce: true, victory: true);
                    return;
                }
            }
            else s_AllOffChecks = 0;

            // A Present Raid held to the end, its present still standing: the defenders won.
            if (now >= s_State.EndsAt)
            {
                bool held = (cur.Lost != null && !s_StagesSeen.Contains(cur.Lost)) || cur.HeldWins;
                End(cur, now, held ? "held to the end" : "time is up", announce: true, victory: held);
                return;
            }

            if (now >= s_NextPresence)
            {
                s_NextPresence = now + 60;
                // Its festival ended (the calendar's last day, or an admin's @festival off): a festival entry goes with it.
                if (!s_State.Manual && !EventDirectorRules.FestivalOpen(cur.Festivals, SeasonalCalendar.IsFestivalActive))
                { End(cur, now, $"{FestivalNames(cur)} is over", announce: true, victory: false); return; }
                // A boss window opened nearby after the start (an admin's @worldboss spawn, a changed rhythm): make way.
                var boss = BossInTheWay(cur, now, now + 1);
                if (boss != null) { End(cur, now, $"making way: {boss}", announce: true, victory: false); return; }
                double idle = Minutes(P_IDLE, 60);
                if (idle > 0)
                {
                    if (AnyoneNear(cur)) s_LastSeenAt = now;
                    else if (now - s_LastSeenAt >= idle * 60) End(cur, now, $"nobody near it for {idle:0.##} min", announce: true, victory: false);
                }
            }
        }

        static bool AnyoneNear(Entry e) => PlayersNear(e).Any();

        static IEnumerable<Player> PlayersNear(Entry e)
        {
            const int MARGIN = 2;   // landblocks either side of the box
            foreach (var p in PlayerManager.GetAllOnline())
            {
                var loc = p?.Location;
                if (loc == null) continue;
                int x = (int)loc.LandblockX, y = (int)loc.LandblockY;
                if (x >= e.Box.x0 - MARGIN && x <= e.Box.x1 + MARGIN && y >= e.Box.y0 - MARGIN && y <= e.Box.y1 + MARGIN) yield return p;
            }
        }

        static void End(Entry cur, double now, string why, bool announce, bool victory)
        {
            StopRows(cur, now);
            // T'thuun's raids: the waves after the generators' are spawned by TthuunRaids, not a generator; they go too
            if (TthuunRaidsRule.ByKey(cur.Key) != null)
                try { TthuunRaids.EventEnded(cur.Key, victory); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventDirector: tthuun cleanup: {e.Message}"); }
            s_State.Current = null;
            s_State.Manual = false;
            s_State.NextAt = now + Gap * 60;
            Save();
            if (announce) WorldBroadcast(victory ? cur.Victory : cur.Withdraw);
            if (victory) JokeScrollRewards.EventWon(cur.Key, PlayersNear(cur).ToList());   // off unless rg_joke_scroll_rewards
            if (victory && HeroRaids.IsHeroRaid(cur.Key))
                try { HeroRaids.Won(cur.Key, PlayersNear(cur).ToList()); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventDirector: hero raid reward: {e.Message}"); }
            if (victory && cur.Key == ProdigalChildren.KEY)
                try { ProdigalChildren.Won(PlayersNear(cur).ToList()); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventDirector: prodigal reward: {e.Message}"); }
            if (victory && cur.Lost != null)
                try { Frostfell.RaidWon(cur.Key, PlayersNear(cur).ToList()); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventDirector: raid reward: {e.Message}"); }
            Mod.Log.Info($"[RevivalGuard] EventDirector: ENDED {cur.Key} ({why}); next {Stamp(s_State.NextAt)}");
        }

        static void StartRows(Entry e)
        {
            foreach (var ev in e.Start) EventManager.StartEvent(ev, null, null);
        }

        static void StopRows(Entry e, double now)
        {
            var names = new HashSet<string>(e.Stop, StringComparer.OrdinalIgnoreCase);
            foreach (var ev in e.Stop) if (IsOn(ev)) EventManager.StopEvent(ev, null, null);
            CollectGenerators(names);
            s_SweepAt.Clear();
            foreach (var d in new[] { 0.0, 12, 30, 75 }) s_SweepAt.Enqueue(now + d);
            RunSweeps(now);
        }

        // ---- the clean stop ---------------------------------------------------------------------

        /// <summary>Every loaded generator gated on one of these rows. One scan per stop; the re-sweeps
        /// reuse the list.</summary>
        static void CollectGenerators(HashSet<string> names)
        {
            s_SweepGens.Clear();
            int blocks = 0;
            foreach (var lb in LandblockManager.GetLoadedLandblocks())
            {
                blocks++;
                foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                {
                    var ge = wo.GeneratorEvent;
                    if (string.IsNullOrEmpty(ge) || !wo.IsGenerator) continue;
                    var ev = EventManager.GetEventName(ge);
                    if (names.Contains(ev)) s_SweepGens.Add((new WeakReference<WorldObject>(wo), ev));
                }
            }
            Mod.Log.Info($"[RevivalGuard] EventDirector: {s_SweepGens.Count} loaded generators on the stopped rows ({blocks} landblocks scanned)");
        }

        static void RunSweeps(double now)
        {
            if (s_SweepAt.Count == 0 || now < s_SweepAt.Peek()) return;
            while (s_SweepAt.Count > 0 && now >= s_SweepAt.Peek()) s_SweepAt.Dequeue();
            int gone = 0;
            foreach (var (wr, ev) in s_SweepGens)
            {
                if (!wr.TryGetTarget(out var gen) || gen.IsDestroyed) continue;
                if (IsOn(ev)) continue;   // restarted since (the next event, or an admin): leave it be
                // ACE notices a stopped event only on the generator's second status check (HandleStatusStaged), so
                // emptying it before then let it refill at once (box 2026-11-01 05:13:27: a fresh Holiday Present and
                // 10 Grumps 4 s after the raid ended). Disabled first, it stays empty; the staged check agrees with
                // it, and the event's next start enables it the usual way.
                gen.GeneratorDisabled = true;
                gone += DestroySpawn(gen, 0);
            }
            if (gone > 0) Mod.Log.Info($"[RevivalGuard] EventDirector: despawned {gone} objects left by stopped event generators");
            if (s_SweepAt.Count == 0) s_SweepGens.Clear();
        }

        /// <summary>Destroy what this generator spawned, children of spawned generators first. Dead
        /// creatures are left alone: their corpses are the players' loot.</summary>
        static int DestroySpawn(WorldObject gen, int depth)
        {
            int n = 0;
            if (gen.GeneratorProfiles == null) return 0;
            foreach (var profile in gen.GeneratorProfiles)
            {
                foreach (var info in profile.Spawned.Values.ToList())
                {
                    var wo = info.TryGetWorldObject();
                    if (wo == null || wo.IsDestroyed) continue;
                    if (wo.IsGenerator && depth < 4) n += DestroySpawn(wo, depth + 1);
                    if (wo is Creature c && c.IsDead) continue;
                    n++;
                }
            }
            gen.ProcessGeneratorDestructionDirective(GeneratorDestruct.Destroy);
            return n;
        }

        // ---- persistence ------------------------------------------------------------------------

        static string StatePath()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(folder)) folder = Path.Combine(ModManager.ModPath, "RevivalGuard");
            return Path.Combine(folder, STATE_FILE);
        }

        static void LoadState()
        {
            var path = StatePath();
            try
            {
                if (!File.Exists(path)) return;
                var st = JsonSerializer.Deserialize<State>(File.ReadAllText(path), s_Json);
                if (st == null) return;
                st.LastRun ??= new Dictionary<string, double>();
                if (st.Current != null && Find(st.Current) == null) st.Current = null;   // renamed/removed entry
                st.Rotation = Math.Clamp(st.Rotation, 0, Catalogue.Length - 1);
                st.FestRotation = Math.Clamp(st.FestRotation, 0, Catalogue.Length - 1);
                lock (s_Lock) s_State = st;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] EventDirector: could not read {path}: {e.Message}; starting a fresh schedule"); }
        }

        static void Save()
        {
            var path = StatePath();
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_State, s_Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] EventDirector: could not write {path}: {e.Message}"); }
        }

        static void RegisterProperties()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ENABLED)) b[P_ENABLED] = new Property<bool>(false, "EventDirector: rotate live events (Olthoi swarms, eruptions, raids, festival events); off until staff turn it on");
                if (!b.ContainsKey(P_FROSTFELL)) b[P_FROSTFELL] = new Property<bool>(true, "EventDirector: while Frostfell runs, its own events (Present Raids, Holtburg snowmen, Stonehold Scold) run even with rg_event_director off");
                if (!b.ContainsKey(P_RETIRED)) b[P_RETIRED] = new Property<bool>(true, "EventDirector: while a revived retired event runs (the Bur Portal Summoning Ritual, Aerbax's Prodigal Children), its own events run even with rg_event_director off");
                if (!b.ContainsKey(P_TTHUUN)) b[P_TTHUUN] = new Property<bool>(true, "EventDirector: while T'thuun's Marauders runs (Nov 16 to 29), its town raids (tthuun-*) run even with rg_event_director off");
                if (!b.ContainsKey(P_FESTIVALS)) b[P_FESTIVALS] = new Property<bool>(true, "EventDirector: festival-only events (Pumpkin Lords, Unhappy Snowmen ...) get a slot after every event_director_festival_every year-round events while their festival runs");
                if (!b.ContainsKey(P_OMENS)) b[P_OMENS] = new Property<bool>(false, "EventDirector: put the Season 1 Omens shadow raids (omens-*) in the rotation");
                var d = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
                var s = MonsterAi.Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_SKIP)) s[P_SKIP] = new Property<string>("", "EventDirector: catalogue keys to leave out of the rotation, comma separated");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventDirector: switches not registered with @modify* ({e.Message}); they are still read"); }
        }

        // ---- words ------------------------------------------------------------------------------

        static void WorldBroadcast(string text) =>
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(text, ChatMessageType.WorldBroadcast));

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static string Stamp(double unix) => unix <= 0 ? "unscheduled"
            : DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

        static string In(double seconds)
        {
            if (seconds < 90) return "in about a minute";
            var m = (int)Math.Round(seconds / 60);
            if (m < 120) return $"in about {m} minutes";
            return $"in about {m / 60} hours";
        }

        static string Left(double seconds)
        {
            var m = (int)Math.Ceiling(Math.Max(0, seconds) / 60);
            return m <= 1 ? "It ends within the minute." : $"About {m} minutes remain.";
        }

        static string Levels(Entry e) => e.Levels.Contains(' ') ? $"levels {e.Levels}" : $"level {e.Levels}";

        /// <summary>What @events says, as lines for the Holtburg Notice Board (NoticeBoard): the event running now
        /// and the next one. Empty when nothing runs and nothing is scheduled.</summary>
        internal static List<string> BoardLines()
        {
            var lines = new List<string>();
            var now = Now;
            lock (s_Lock)
            {
                var st = s_State;
                if (st == null) return lines;
                var cur = Find(st.Current);
                if (cur != null && !s_Suspended)
                    lines.Add($"Live event now: {cur.Title}, near {cur.Where} ({Levels(cur)}). {Left(st.EndsAt - now)}");
                else
                {
                    var natural = Catalogue.FirstOrDefault(e => e.Stop.Any(IsOn));
                    if (natural != null) lines.Add($"Live event now: {natural.Title}, near {natural.Where} ({Levels(natural)}).");
                }
                if (!Running || AnniversaryNight.On) return lines;
                if (cur == null)
                {
                    var next = PeekNext(now);
                    if (next != null && st.NextAt > 0) lines.Add($"Next live event: {next.Title}, {In(st.NextAt - now)}.");
                }
                else lines.Add($"The next live event follows {In(st.EndsAt + Gap * 60 - now)}.");
            }
            return lines;
        }

        // ---- commands ---------------------------------------------------------------------------

        static void EventsCmd(Session session, params string[] parameters)
        {
            var now = Now;
            lock (s_Lock)
            {
                var cur = Find(s_State.Current);
                if (cur != null && !s_Suspended)
                    Say(session, $"Live event now: {cur.Title}, near {cur.Where} ({Levels(cur)}). {Left(s_State.EndsAt - now)}");
                else
                {
                    // Something the players started themselves (the Witshire minion) still counts.
                    var natural = Catalogue.FirstOrDefault(e => e.Stop.Any(IsOn));
                    Say(session, natural != null ? $"Live event now: {natural.Title}, near {natural.Where} ({Levels(natural)})."
                                                 : "No live event is running right now.");
                }
                if (!Running) { Say(session, "No live events are scheduled."); return; }
                if (AnniversaryNight.On) { Say(session, "Live events are resting for Anniversary Night. Enjoy the party!"); return; }
                if (cur == null)
                {
                    var next = PeekNext(now);
                    if (next != null) Say(session, $"Next live event: {next.Title}, {In(s_State.NextAt - now)}.");
                }
                else
                    Say(session, $"The next live event follows {In(s_State.EndsAt + Gap * 60 - now)}.");
            }
        }

        static void DirectorCmd(Session session, params string[] parameters)
        {
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            string who = session?.Player?.Name ?? "console";
            switch (verb)
            {
                case "on":
                case "off":
                    PropertyManager.ModifyBool(P_ENABLED, verb == "on");
                    if (verb == "on") lock (s_Lock) if (s_State.Current == null) { s_State.NextAt = Now + 600; Save(); }
                    Mod.Log.Info($"[RevivalGuard] EventDirector: turned {verb} by {who}");
                    Say(session, verb == "on"
                        ? "[EventDirector] (ours) on. The first live event starts in about 10 minutes; 'next' starts one now, 'off' cancels."
                        : "[EventDirector] (ours) off. The current event, if any, ends within 5 s with its closing broadcast.");
                    if (verb == "off")
                    {
                        // Off is the kill switch: an event started by hand ends too (the tick lets those run out).
                        void halt() { lock (s_Lock) { var c = Find(s_State.Current); if (c != null) End(c, Now, $"turned off by {who}", announce: true, victory: false); } }
                        if (session != null) halt(); else WorldManager.EnqueueAction(new ActionEventDelegate(halt));
                    }
                    return;
                case "next":
                case "stop":
                case "start":
                case "skip":
                    {
                        string key = verb == "start" && parameters.Length > 1 ? parameters[1] : null;
                        if (verb == "start" && Find(key) == null)
                        {
                            Say(session, "[EventDirector] start needs a key: " + string.Join(", ", Catalogue.Select(c => c.Key)));
                            return;
                        }
                        // Player commands arrive on the world thread already; the console's do not.
                        void act() => Act(verb, key, who, session);
                        if (session != null) act();
                        else WorldManager.EnqueueAction(new ActionEventDelegate(act));
                        return;
                    }
                case "list":
                    {
                        var now = Now;
                        lock (s_Lock)
                        {
                            Say(session, "[EventDirector] (ours) the catalogue, in order (> marks each rotation's place):");
                            for (int i = 0; i < Catalogue.Length; i++)
                            {
                                var e = Catalogue[i];
                                var blocked = Blocked(e, Find(s_State.Current), manual: false, now);
                                string last = s_State.LastRun.TryGetValue(e.Key, out var t) ? Stamp(t) : "never";
                                bool mark = e.IsFestival ? i == s_State.FestRotation : i == s_State.Rotation;
                                string tag = e.IsFestival ? $" [{FestivalNames(e)}, {Duration(e):0} min]" : "";
                                Say(session, $"{(mark ? ">" : " ")} {e.Key,-20} {e.Title} ({Levels(e)}) at {e.Where}{tag}; last {last}{(blocked != null ? "; now: " + blocked : "")}");
                            }
                        }
                        return;
                    }
            }

            // status (AcAdminHud parses the first line and the running / idle line: keep their shape)
            {
                var now = Now;
                lock (s_Lock)
                {
                    var cur = Find(s_State.Current);
                    Say(session, $"[EventDirector] (ours) {(Enabled ? "ON" : Running ? $"{SelfRunNow} events only" : "OFF")}; each event {Minutes(P_DURATION, 120):0.##} min (festival ones their own), gap {Gap:0.##} min, idle end {Minutes(P_IDLE, 60):0.##} min, min players {Minutes(P_MINPLAYERS, 1):0}; switch {P_ENABLED}");
                    if (cur != null)
                    {
                        var on = cur.Stop.Where(IsOn).ToList();
                        Say(session, $"[EventDirector] running: {cur.Key}{(s_Suspended ? " (suspended for shutdown)" : "")}{(s_State.Manual ? " (started by hand)" : "")}, ends {Stamp(s_State.EndsAt)} ({(s_State.EndsAt - now) / 60:F1} min); rows on: {(on.Count > 0 ? string.Join(", ", on) : "none")}");
                    }
                    else
                    {
                        var next = PeekNext(now);
                        Say(session, $"[EventDirector] idle; next {(next != null ? next.Key : "(nothing eligible)")} at {Stamp(s_State.NextAt)} ({(s_State.NextAt - now) / 60:F1} min)");
                    }
                    var fests = Catalogue.Where(e => e.IsFestival && EventDirectorRules.FestivalOpen(e.Festivals, SeasonalCalendar.IsFestivalActive)).Select(e => e.Key).ToList();
                    Say(session, $"[EventDirector] festival events in season: {(fests.Count > 0 ? string.Join(", ", fests) : "none")}{(FestivalsOn ? "" : $" ({P_FESTIVALS} is off)")}; a festival event every {FestEvery} year-round events ({P_FEST_EVERY}), {s_State.SinceFest} since the last");
                    if (AnniversaryNight.On) Say(session, "[EventDirector] holding for Anniversary Night: nothing starts until a gap after the party.");
                    var bosses = WorldBossSchedule.WindowsBetween(now, now + 6 * 3600);
                    if (bosses.Count > 0) Say(session, "[EventDirector] boss windows kept clear: " + string.Join("; ", bosses.Select(b => $"{b.name} at 0x{b.lair:X4} {Stamp(b.from)} to {Stamp(b.to)}")));
                    Say(session, "[EventDirector] @eventdirector list | on | off | next | skip | stop | start <key>");
                }
            }
        }

        static void Act(string verb, string key, string who, Session session)
        {
            var now = Now;
            lock (s_Lock)
            {
                var cur = Find(s_State.Current);
                if (verb == "stop")
                {
                    if (cur == null) { Say(session, "[EventDirector] nothing is running."); return; }
                    End(cur, now, $"stopped by {who}", announce: true, victory: false);
                    Say(session, $"[EventDirector] {cur.Key} stopped; next at {Stamp(s_State.NextAt)}.");
                    return;
                }
                if (verb == "skip")
                {
                    // Pass over the queued event this round: its rotation moves on, and whatever follows takes its slot.
                    if (cur != null) { Say(session, $"[EventDirector] {cur.Key} is running: 'stop' ends it, 'next' replaces it."); return; }
                    var queued = PeekNext(now);
                    if (queued == null) { Say(session, "[EventDirector] nothing is queued (see @eventdirector list)."); return; }
                    var p = new EventDirectorRules.Pick { Rotation = s_State.Rotation, FestRotation = s_State.FestRotation, SinceFest = s_State.SinceFest };
                    EventDirectorRules.Advance(ref p, Array.IndexOf(Catalogue, queued), queued.IsFestival, Catalogue.Length);
                    s_State.Rotation = p.Rotation; s_State.FestRotation = p.FestRotation;   // the turn stays: a skipped festival slot goes to the next festival event
                    Save();
                    var then = PeekNext(now);
                    Mod.Log.Info($"[RevivalGuard] EventDirector: {queued.Key} skipped by {who}");
                    Say(session, $"[EventDirector] skipped {queued.Key}; next is {(then != null ? then.Key : "(nothing eligible)")} at {Stamp(s_State.NextAt)}.");
                    return;
                }
                Entry target;
                if (verb == "start")
                {
                    target = Find(key);
                    var b = Blocked(target, cur, manual: true, now);
                    if (b != null) { Say(session, $"[EventDirector] cannot start {target.Key}: {b}."); return; }
                }
                else
                {
                    // "next" moves past whatever is running now, by the same two rotations
                    var p = Plan(now, cur, exclude: cur);
                    target = p.Index < 0 ? null : Catalogue[p.Index];
                    if (target == null) { Say(session, "[EventDirector] nothing else can start now (see @eventdirector list)."); return; }
                }
                if (cur != null) End(cur, now, $"replaced by {who}", announce: true, victory: false);
                Begin(target, now, manual: true);
                Mod.Log.Info($"[RevivalGuard] EventDirector: {target.Key} started by {who} ({verb})");
                Say(session, $"[EventDirector] started {target.Key}; ends {Stamp(s_State.EndsAt)}." + (Enabled ? "" : Running ? $" The rotation runs {SelfRunNow} events only." : " The rotation is off, so nothing follows it."));
            }
        }
    }
}
