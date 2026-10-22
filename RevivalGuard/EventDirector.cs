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
                Regions = new[] { "tufa", "presentraids" }, Box = (0x85, 0x6B, 0x87, 0x6D), Festivals = new[] { "frostfell", "newyear" },
                Start = new[] { "PresentRaidsTufa" },
                Stop = new[] { "PresentRaidsTufa", "PresentRaidsDead" },
                Stages = new[] { ("PresentRaidsDead", "The Holiday Present at Tufa has been destroyed!") },
                Announce = LEAD + "Gurogs are raiding Tufa! A huge Holiday Present has appeared in the town, near 14.9S, 5.6E, and waves of Gurog Grumps are coming to smash it. Defend it and share what is inside.",
                Victory = "The Gurog raid on Tufa is over.",
                Withdraw = "The Gurogs have left Tufa.",
                Evidence = "ace73219-presentraidstufagen at 0x866C; Gurog Grumps 220, Gurog Mastermind and Max 300; Frostfell content (wiki: Gurog Present Raids)",
            },
            new Entry
            {
                Key = "presents-yaraq", Title = "Drudges Raid the Presents at Yaraq", Where = "21.7S, 1.3W", Levels = "200",
                Regions = new[] { "yaraq", "presentraids" }, Box = (0x7C, 0x63, 0x7E, 0x65), Festivals = new[] { "frostfell", "newyear" },
                Start = new[] { "PresentRaidsYaraq" },
                Stop = new[] { "PresentRaidsYaraq", "PresentRaidsDead" },
                Stages = new[] { ("PresentRaidsDead", "The Holiday Presents at Yaraq have been stolen!") },
                Announce = LEAD + "Drudges are raiding Yaraq! A pile of Holiday Presents has appeared in the town, near 21.7S, 1.3W, and Drudge Pilferers are dropping from balloons to steal it. Stop them.",
                Victory = "The Drudge raid on Yaraq is over.",
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

                bool enabled = Enabled;
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
            Mod.Log.Info($"[RevivalGuard] EventDirector: {(Enabled ? "ON" : $"OFF ({P_ENABLED})")}; next live event {Stamp(s_State.NextAt)}");
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
                var skip = (PropertyManager.GetString(P_SKIP, "").Item ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (skip.Any(s => s.Equals(e.Key, StringComparison.OrdinalIgnoreCase))) return $"listed in {P_SKIP}";
                if (e.Key.StartsWith("omens-", StringComparison.OrdinalIgnoreCase) && !PropertyManager.GetBool(P_OMENS, false).Item)
                    return $"{P_OMENS} is off";
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
                if (++s_AllOffChecks >= 2) { End(cur, now, "won by the players", announce: true, victory: true); return; }
            }
            else s_AllOffChecks = 0;

            if (now >= s_State.EndsAt) { End(cur, now, "time is up", announce: true, victory: false); return; }

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
            s_State.Current = null;
            s_State.Manual = false;
            s_State.NextAt = now + Gap * 60;
            Save();
            if (announce) WorldBroadcast(victory ? cur.Victory : cur.Withdraw);
            if (victory) JokeScrollRewards.EventWon(cur.Key, PlayersNear(cur).ToList());   // off unless rg_joke_scroll_rewards
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
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ENABLED)) b[P_ENABLED] = new Property<bool>(false, "EventDirector: rotate live events (Olthoi swarms, eruptions, raids, festival events); off until staff turn it on");
                if (!b.ContainsKey(P_FESTIVALS)) b[P_FESTIVALS] = new Property<bool>(true, "EventDirector: festival-only events (Pumpkin Lords, Unhappy Snowmen ...) get a slot after every event_director_festival_every year-round events while their festival runs");
                if (!b.ContainsKey(P_OMENS)) b[P_OMENS] = new Property<bool>(false, "EventDirector: put the Season 1 Omens shadow raids (omens-*) in the rotation");
                var d = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
                var s = Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_SKIP)) s[P_SKIP] = new Property<string>("", "EventDirector: catalogue keys to leave out of the rotation, comma separated");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventDirector: switches not registered with @modify* ({e.Message}); they are still read"); }
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
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
                if (!Enabled) { Say(session, "No live events are scheduled."); return; }
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
                    Say(session, $"[EventDirector] (ours) {(Enabled ? "ON" : "OFF")}; each event {Minutes(P_DURATION, 120):0.##} min (festival ones their own), gap {Gap:0.##} min, idle end {Minutes(P_IDLE, 60):0.##} min, min players {Minutes(P_MINPLAYERS, 1):0}; switch {P_ENABLED}");
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
                Say(session, $"[EventDirector] started {target.Key}; ends {Stamp(s_State.EndsAt)}." + (Enabled ? "" : " The rotation is off, so nothing follows it."));
            }
        }
    }
}
