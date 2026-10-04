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
    ///   @eventdirector                 admins/bots: status (list | on | off | next | stop | start key)
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
            public int[] Months;                   // null: all year
            public (int x0, int y0, int x1, int y1) Box;   // landblock X/Y rectangle for "is anyone there"
            public string Announce, Victory, Withdraw;
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
                Regions = new[] { "tufa", "presentraids" }, Box = (0x85, 0x6B, 0x87, 0x6D), Months = new[] { 12, 1 },
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
                Regions = new[] { "yaraq", "presentraids" }, Box = (0x7C, 0x63, 0x7E, 0x65), Months = new[] { 12, 1 },
                Start = new[] { "PresentRaidsYaraq" },
                Stop = new[] { "PresentRaidsYaraq", "PresentRaidsDead" },
                Stages = new[] { ("PresentRaidsDead", "The Holiday Presents at Yaraq have been stolen!") },
                Announce = LEAD + "Drudges are raiding Yaraq! A pile of Holiday Presents has appeared in the town, near 21.7S, 1.3W, and Drudge Pilferers are dropping from balloons to steal it. Stop them.",
                Victory = "The Drudge raid on Yaraq is over.",
                Withdraw = "The Drudges have left Yaraq.",
                Evidence = "ace73230-presentraidsyaraqgen at 0x7D64; Drudge Pilferers 200; Frostfell content (wiki: Drudge Present Raids)",
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

        const string P_ENABLED = "event_director_enabled", P_DURATION = "event_director_duration_min",
                     P_GAP = "event_director_gap_min", P_IDLE = "event_director_idle_end_min",
                     P_MINPLAYERS = "event_director_min_players", P_SKIP = "event_director_skip",
                     P_OMENS = "event_director_omens";

        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            (P_DURATION, 120, "EventDirector: minutes each live event runs (fractions allowed, for tests)"),
            (P_GAP, 90, "EventDirector: quiet minutes between one live event and the next"),
            (P_IDLE, 60, "EventDirector: end an event early after this many minutes with nobody near it (0 = never)"),
            (P_MINPLAYERS, 1, "EventDirector: players that must be online before an event starts"),
        };

        // ---- state (persisted) ----------------------------------------------------------------

        sealed class State
        {
            [JsonPropertyName("current")] public string Current { get; set; }
            [JsonPropertyName("startedAt")] public double StartedAt { get; set; }
            [JsonPropertyName("endsAt")] public double EndsAt { get; set; }
            [JsonPropertyName("nextAt")] public double NextAt { get; set; }
            [JsonPropertyName("rotation")] public int Rotation { get; set; }
            [JsonPropertyName("lastRun")] public Dictionary<string, double> LastRun { get; set; } = new Dictionary<string, double>();
        }

        const string STATE_FILE = "EventDirectorState.json";
        static State s_State = new State();
        static readonly object s_Lock = new object();
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        // runtime-only
        static bool s_Booted;
        static bool s_Suspended;             // stopped for a shutdown countdown
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
                "Live event director (RevivalGuard): status, or list | on | off | next | stop | start <key>.",
                "[list | on | off | next | stop | start <key>]");
            LoadState();
            Mod.Log.Info($"[RevivalGuard] EventDirector: {Catalogue.Length} live events in the rotation (@events, @eventdirector; event_director_* server properties)");
        }

        static bool Enabled => PropertyManager.GetBool(P_ENABLED, true).Item;
        static double Minutes(string key, double def) => PropertyManager.GetDouble(key, def).Item;
        static double Now => Time.GetUnixTime();

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

                if (!Enabled)
                {
                    if (cur != null) End(cur, now, "director turned off", announce: true, victory: false);
                    return;
                }

                if (cur != null) { WatchRunning(cur, now); return; }

                if (now < s_State.NextAt) return;
                if (PlayerManager.GetOnlineCount() < Math.Max(0, Minutes(P_MINPLAYERS, 1)))
                {
                    s_State.NextAt = now + 60;   // look again in a minute; nobody to see it
                    return;
                }
                var next = PickNext(out _);
                if (next == null) { s_State.NextAt = now + 600; Save(); return; }
                Begin(next, now);
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
            }
            // A deploy or restart never fires an event straight away: at least one gap first, and a
            // schedule that fell due while the shard was down waits five minutes for the world to settle.
            if (s_State.NextAt <= 0) s_State.NextAt = now + Minutes(P_GAP, 90) * 60;
            else if (s_State.NextAt < now + 300) s_State.NextAt = now + 300;
            Save();
            Mod.Log.Info($"[RevivalGuard] EventDirector: {(Enabled ? "on" : "OFF")}; next live event {Stamp(s_State.NextAt)}");
        }

        // ---- choosing ---------------------------------------------------------------------------

        static Entry Find(string key) =>
            key == null ? null : Catalogue.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        static bool IsOn(string ev) => EventManager.GetEventStatus(ev) == GameEventState.On;

        /// <summary>Why this entry cannot start right now, or null if it can. `ignore` is the entry being
        /// replaced (its rows are about to stop, so they do not count as busy).</summary>
        static string Blocked(Entry e, Entry ignore, bool manual)
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
                if (e.Months != null && !e.Months.Contains(DateTime.UtcNow.Month)) return "out of season";
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
            return null;
        }

        /// <summary>Round robin through the catalogue from the saved position, so every entry gets its
        /// turn in a fixed order however often the shard restarts.</summary>
        static Entry PickNext(out string why)
        {
            why = null;
            var reasons = new List<string>();
            for (int i = 0; i < Catalogue.Length; i++)
            {
                int idx = (s_State.Rotation + i) % Catalogue.Length;
                var e = Catalogue[idx];
                var b = Blocked(e, Find(s_State.Current), manual: false);
                if (b == null) { s_State.Rotation = (idx + 1) % Catalogue.Length; return e; }
                reasons.Add($"{e.Key}: {b}");
            }
            why = string.Join("; ", reasons);
            Mod.Log.Info($"[RevivalGuard] EventDirector: nothing can start ({why})");
            return null;
        }

        /// <summary>What would start next, without moving the rotation.</summary>
        static Entry PeekNext()
        {
            for (int i = 0; i < Catalogue.Length; i++)
            {
                var e = Catalogue[(s_State.Rotation + i) % Catalogue.Length];
                if (Blocked(e, Find(s_State.Current), manual: false) == null) return e;
            }
            return null;
        }

        // ---- running ------------------------------------------------------------------------------

        static void Begin(Entry e, double now)
        {
            StartRows(e);
            s_State.Current = e.Key;
            s_State.StartedAt = now;
            s_State.EndsAt = now + Math.Max(0.1, Minutes(P_DURATION, 120)) * 60;
            s_State.LastRun[e.Key] = now;
            // The rotation moves past whatever just started, however it was started (a manual
            // `start witshire` left the pointer on witshire and the next pick ran it again).
            s_State.Rotation = (Array.IndexOf(Catalogue, e) + 1) % Catalogue.Length;
            s_LastSeenAt = now;
            s_NextPresence = 0;
            s_AllOffChecks = 0;
            s_StagesSeen.Clear();
            Save();
            WorldBroadcast(e.Announce);
            Mod.Log.Info($"[RevivalGuard] EventDirector: STARTED {e.Key} ({string.Join(", ", e.Start)}) until {Stamp(s_State.EndsAt)}");
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

            double idle = Minutes(P_IDLE, 60);
            if (idle > 0 && now >= s_NextPresence)
            {
                s_NextPresence = now + 60;
                if (AnyoneNear(cur)) s_LastSeenAt = now;
                else if (now - s_LastSeenAt >= idle * 60) End(cur, now, $"nobody near it for {idle:0.##} min", announce: true, victory: false);
            }
        }

        static bool AnyoneNear(Entry e)
        {
            const int MARGIN = 2;   // landblocks either side of the box
            foreach (var p in PlayerManager.GetAllOnline())
            {
                var loc = p?.Location;
                if (loc == null) continue;
                int x = (int)loc.LandblockX, y = (int)loc.LandblockY;
                if (x >= e.Box.x0 - MARGIN && x <= e.Box.x1 + MARGIN && y >= e.Box.y0 - MARGIN && y <= e.Box.y1 + MARGIN) return true;
            }
            return false;
        }

        static void End(Entry cur, double now, string why, bool announce, bool victory)
        {
            StopRows(cur, now);
            s_State.Current = null;
            s_State.NextAt = now + Math.Max(0.1, Minutes(P_GAP, 90)) * 60;
            Save();
            if (announce) WorldBroadcast(victory ? cur.Victory : cur.Withdraw);
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
                if (!b.ContainsKey(P_ENABLED)) b[P_ENABLED] = new Property<bool>(true, "EventDirector: rotate live events (Olthoi swarms, eruptions, raids)");
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
                if (cur == null)
                {
                    var next = PeekNext();
                    if (next != null) Say(session, $"Next live event: {next.Title}, {In(s_State.NextAt - now)}.");
                }
                else
                    Say(session, $"The next live event follows {In(s_State.EndsAt + Minutes(P_GAP, 90) * 60 - now)}.");
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
                    Mod.Log.Info($"[RevivalGuard] EventDirector: turned {verb} by {who}");
                    Say(session, verb == "on"
                        ? "[EventDirector] (ours) on. The next live event starts when the gap runs out; 'next' starts one now."
                        : "[EventDirector] (ours) off. The current event, if any, ends within 5 s with its closing broadcast.");
                    return;
                case "next":
                case "stop":
                case "start":
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
                    lock (s_Lock)
                    {
                        Say(session, "[EventDirector] (ours) the rotation, in order:");
                        for (int i = 0; i < Catalogue.Length; i++)
                        {
                            var e = Catalogue[i];
                            var blocked = Blocked(e, Find(s_State.Current), manual: false);
                            string last = s_State.LastRun.TryGetValue(e.Key, out var t) ? Stamp(t) : "never";
                            Say(session, $"{(i == s_State.Rotation ? ">" : " ")} {e.Key,-16} {e.Title} ({Levels(e)}) at {e.Where}; last {last}{(blocked != null ? "; now: " + blocked : "")}");
                        }
                    }
                    return;
            }

            // status
            var now = Now;
            lock (s_Lock)
            {
                var cur = Find(s_State.Current);
                Say(session, $"[EventDirector] (ours) {(Enabled ? "ON" : "OFF")}; each event {Minutes(P_DURATION, 120):0.##} min, gap {Minutes(P_GAP, 90):0.##} min, idle end {Minutes(P_IDLE, 60):0.##} min, min players {Minutes(P_MINPLAYERS, 1):0}");
                if (cur != null)
                {
                    var on = cur.Stop.Where(IsOn).ToList();
                    Say(session, $"[EventDirector] running: {cur.Key}{(s_Suspended ? " (suspended for shutdown)" : "")}, ends {Stamp(s_State.EndsAt)} ({(s_State.EndsAt - now) / 60:F1} min); rows on: {(on.Count > 0 ? string.Join(", ", on) : "none")}");
                }
                else
                {
                    var next = PeekNext();
                    Say(session, $"[EventDirector] idle; next {(next != null ? next.Key : "(nothing eligible)")} at {Stamp(s_State.NextAt)} ({(s_State.NextAt - now) / 60:F1} min)");
                }
                Say(session, "[EventDirector] @eventdirector list | on | off | next | stop | start <key>");
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
                Entry target;
                if (verb == "start")
                {
                    target = Find(key);
                    var b = Blocked(target, cur, manual: true);
                    if (b != null) { Say(session, $"[EventDirector] cannot start {target.Key}: {b}."); return; }
                }
                else
                {
                    target = null;
                    // "next" moves past whatever is running now
                    for (int i = 0; i < Catalogue.Length && target == null; i++)
                    {
                        int idx = (s_State.Rotation + i) % Catalogue.Length;
                        var e = Catalogue[idx];
                        if (e == cur) continue;
                        if (Blocked(e, cur, manual: false) == null) { target = e; s_State.Rotation = (idx + 1) % Catalogue.Length; }
                    }
                    if (target == null) { Say(session, "[EventDirector] nothing else can start now (see @eventdirector list)."); return; }
                }
                if (cur != null) End(cur, now, $"replaced by {who}", announce: true, victory: false);
                Begin(target, now);
                Mod.Log.Info($"[RevivalGuard] EventDirector: {target.Key} started by {who} ({verb})");
                Say(session, $"[EventDirector] started {target.Key}; ends {Stamp(s_State.EndsAt)}.");
            }
        }
    }
}
