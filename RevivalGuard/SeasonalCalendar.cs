using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE CALENDAR TURNS, SO THE WORLD TURNS WITH IT.
    ///
    /// docs/GAP-ANALYSIS.md §1 found that nothing in this revival is driven by a date.
    /// `ACE.Server/Managers/EventManager.cs` is a dictionary loaded from `ace_world.event` and
    /// toggled by StartEvent/StopEvent; an event runs only if its stored State is already On, or if
    /// an emote or a dev command turns it on by name. `ace_world.event` has 700 rows, 608 of them
    /// Off, and among them is every pumpkin, scarecrow, zombie and gift box retail put on the
    /// landscape for one month a year. This file is the thing that turns them.
    ///
    /// WHERE THE MAPPING CAME FROM, AND WHY IT IS NOT GUESSWORK. Retail shipped its own festival
    /// switches and they survived into `ace_world`: a family of weenies named `...stopgap!` whose
    /// Generation emote (EmoteCategory 9) fires StartEvent on a bundle of event names and whose
    /// Death emote (category 3) fires StopEvent on the same bundle. Each is spawned by a matching
    /// `...stopgapgen` generator that carries `GeneratorTimeType = RealTime` (PropertyInt 142 = 1)
    /// with `GeneratorStartTime`/`GeneratorEndTime` (143/144) holding the actual retail window.
    /// Those generators are placed at Hebian-To (landblock 0xE74E), which is the one landblock our
    /// shard's Config.js permaloads, so they do tick. The bundles and the windows in the table
    /// below are read straight off those rows; docs/archive/SEASONAL-EVENTS.md shows the queries.
    ///
    /// NAMES. The October event is Festival Season: that is what retail's NPCs call it (Ungrim the
    /// Unpleasant Smelling: "Festival Season is one of my favorite times of year", and the Al-Jalima
    /// town description's "autumn festival season brings rise to these undead"), 63 hits in the wiki
    /// corpus. "Hollowtide" is this SHARD'S name (`WorldName` in Config.js), not Turbine's name for
    /// the event, and it never appears in the corpus. Players and the log see Festival Season.
    ///
    /// SO WHY A SCHEDULER AT ALL, IF THE DATA ALREADY HAS DATES?
    ///   1. Those windows are ABSOLUTE unix timestamps for one single year (2026, or 2026-12 into
    ///      2027 for Frostfell). They are not a calendar. When 2027 arrives, every one of them has
    ///      expired and Dereth never has another Festival Season.
    ///   2. Nothing announces anything. Retail's only in-world signal was local: the Al-Jalima town
    ///      crier is swapped for a zombified one who says "Brains! Grrr!". A player in Holtburg had
    ///      no way to learn the festival had started.
    ///   3. It is untestable. A seasonal event you can only see in October cannot be regression
    ///      tested in March, and this project's whole method is "observe the failing scenario".
    ///   4. The world-data path is fragile in a way that is nobody's fault but is worth routing
    ///      around: `WorldObject_Generators.HandleStatusStaged` applies a status change only on the
    ///      SECOND heartbeat after the condition flips, and the whole thing depends on one landblock
    ///      staying permaloaded and one generator instance staying alive.
    /// This scheduler asserts the same bundles against a RECURRING calendar, every minute, from the
    /// world loop, so none of the four matter.
    ///
    /// IT DOES NOT WRITE TO THE DATABASE. `EventManager.StartEvent` mutates the in-memory `Event`
    /// entity's State field and nothing in ACE.Database ever writes an Event back (there is no
    /// SaveEvent and no caller; `GetAllEvents` is read once at startup, Program.cs:281). So every
    /// change this makes is process-local and disappears on restart, which is exactly right: the
    /// calendar is the source of truth and it reasserts itself within a minute of boot.
    ///
    /// COMMANDS
    ///   @festival                  what is running now, and what is next
    ///   @festival list             every festival, its window, and its event rows
    ///   @festival on &lt;key&gt;         force one on, regardless of the date
    ///   @festival off &lt;key&gt;        force one off, regardless of the date
    ///   @festival auto &lt;key|all&gt;   hand it back to the calendar
    ///
    /// OVERRIDES SURVIVE A RESTART. The first version held them in memory on the theory that a
    /// forgotten override was a silent lie and a restart was the cheapest way to be sure. The
    /// opposite happened: Festival Season was forced on for a playtest on 2026-09-20, the shard
    /// restarted for an unrelated fix on 2026-09-22, and the festival went off with no announcement
    /// and nobody noticing until the pumpkins were gone. A restart is not a decision anyone made
    /// about the festival, so it should not change the festival. Overrides now live in
    /// SeasonalOverrides.json beside the mod dll (the deploy script copies only the dll, so it
    /// survives deploys too), with who set them and when, and the startup log says which festival
    /// is active and whether that is the calendar or a person. `@festival auto` is the way to clear
    /// one, and deleting the file clears them all.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class SeasonalCalendar
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
        const int RETAIL_UTC_OFFSET_HOURS = -5;

        /// <summary>How often the calendar is re-asserted. A festival boundary is a date, so a
        /// minute of latency is invisible, and this runs on the world loop where cheap matters.</summary>
        const double CHECK_SECONDS = 60.0;

        /// <summary>The override file, beside the mod dll. See OverridePath.</summary>
        const string OVERRIDE_FILE = "SeasonalOverrides.json";

        sealed class Festival
        {
            public string Key;              // what @festival takes
            public string Name;             // what a player is told
            public int StartMonth, StartDay, EndMonth, EndDay;   // inclusive both ends, local to UTC-5
            public string[] On = Array.Empty<string>();          // ace_world.event rows to start
            public string[] Off = Array.Empty<string>();         // rows to stop for the duration
            public string Announce;         // the world broadcast when it opens
            public string Evidence;         // the weenie this was read off, shown by @festival list

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
        static readonly Festival[] Calendar =
        {
            new Festival
            {
                Key = "festivalseason", Name = "Festival Season",
                StartMonth = 10, StartDay = 1, EndMonth = 11, EndDay = 1,
                On  = new[] { "EventFallFestival" },
                Off = new[] { "normaltowncrier" },
                Announce = "The harvest has turned. Pumpkin Lords and Harvest Reapers walk the "
                         + "landscape, the dead are stirring in Al-Jalima, and the Majestic Pumpkin "
                         + "is waiting in Glenden Wood.",
                Evidence = "ace80069-fallfestivaleventsstopgapgen, Oct 1 to Nov 1",
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
                Evidence = "ace80018-holidayeventsstopgapgen, Dec 4 to Jan 1",
            },
            new Festival
            {
                Key = "spring", Name = "the Spring Festival",
                StartMonth = 4, StartDay = 1, EndMonth = 5, EndDay = 1,
                On  = new[] { "springbabies", "springbunnyevent", "SpringEasterEggs", "IHOPopen" },
                Announce = "Spring has come to Dereth. Young creatures are on the landscape, "
                         + "coloured eggs are turning up, and the House of Pancakes has opened.",
                Evidence = "ace87093-springeventsstopgapgen, Apr 1 to May 1",
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
                Evidence = "ace23631 April 2003 Raining Mad Cows Gen's own window, Apr 1 only",
            },
            new Festival
            {
                Key = "wedding", Name = "the Royal Wedding Anniversary",
                StartMonth = 5, StartDay = 1, EndMonth = 6, EndDay = 1,
                On  = new[] { "ReceptionGames" },
                Announce = "The Royal Wedding anniversary is being kept. The reception games have "
                         + "begun again.",
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
                         + "Cragstone, Hebian-To, Sanamar and Zaikhal.",
                Evidence = "ace69997-nightclubattendantgenerator's own window, Nov 2 to Dec 1",
            },
            new Festival
            {
                Key = "newyear", Name = "the New Year",
                StartMonth = 1, StartDay = 1, EndMonth = 2, EndDay = 1,
                On  = new[] { "EventNewYear" },
                Announce = "A new year begins in Dereth.",
                Evidence = "ace80222-newyeareventsstopgapgen, Jan 1 to Feb 1",
            },
            new Festival
            {
                Key = "newyearsday", Name = "New Year's Day",
                StartMonth = 1, StartDay = 1, EndMonth = 1, EndDay = 1,
                On  = new[] { "Fireworks" },
                Announce = "Fireworks over Dereth for the new year.",
                Evidence = "ace80224-newyearsdayeventsstopgapgen, Jan 1 only",
            },
        };

        /// <summary>One forced state, as stored in SeasonalOverrides.json. By and At are for the
        /// startup log and @festival, so a forced festival is never anonymous.</summary>
        sealed class Override
        {
            [JsonPropertyName("on")] public bool On { get; set; }
            [JsonPropertyName("by")] public string By { get; set; }
            [JsonPropertyName("at")] public string At { get; set; }
        }

        /// <summary>Forced states, by festival key: On true means forced on, false forced off,
        /// absent means follow the calendar. Mirrored to SeasonalOverrides.json on every change
        /// and loaded from it in Register, so a restart does not clear them.</summary>
        static readonly Dictionary<string, Override> s_Forced =
            new Dictionary<string, Override>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Which festivals we last saw as running, so a transition can be announced once
        /// rather than every minute.</summary>
        static readonly HashSet<string> s_Running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static readonly object s_Lock = new object();
        static double s_NextCheck;

        /// <summary>Every row the calendar turns, on or off. EventDirector never touches these: the
        /// calendar re-asserts them every minute and would fight it.</summary>
        internal static HashSet<string> OwnedRows() =>
            new HashSet<string>(Calendar.SelectMany(f => f.On.Concat(f.Off)), StringComparer.OrdinalIgnoreCase);
        static bool s_FirstPass = true;

        internal static void Register()
        {
            // Player access on the status read: "what festival is on" is in-world information that
            // retail gave away with a town crier, and a player who cannot see it has no calendar.
            // The two that change the world stay at Developer, beside @setevent, which is what they
            // are. CommandManager.TryAddCommand checks access on every invocation itself.
            CommandManager.TryAddCommand(FestivalCmd, "festival", AccessLevel.Player,
                CommandHandlerFlag.None, "What festival is running, and force one on or off.",
                "[list | on <key> | off <key> | auto <key|all>]");

            LoadOverrides();
            LogStartupState();
        }

        static void Postfix()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_NextCheck) return;
            s_NextCheck = now + CHECK_SECONDS;
            Apply(announce: true);
        }

        /// <summary>The local date the calendar is evaluated against. See RETAIL_UTC_OFFSET_HOURS.</summary>
        static DateTime RetailNow() => DateTime.UtcNow.AddHours(RETAIL_UTC_OFFSET_HOURS);

        /// <summary>
        /// Is this (month, day) inside the festival's window? Both ends are inclusive days, and the
        /// comparison is on MMDD so that Frostfell, which starts in December and ends in January,
        /// works without knowing which year either end is in.
        /// </summary>
        static bool InWindow(Festival f, DateTime local)
        {
            int cur = local.Month * 100 + local.Day;
            int start = f.StartMonth * 100 + f.StartDay;
            int end = f.EndMonth * 100 + f.EndDay;
            return start <= end ? (cur >= start && cur <= end) : (cur >= start || cur <= end);
        }

        static bool IsActive(Festival f, DateTime local)
        {
            lock (s_Lock)
                if (s_Forced.TryGetValue(f.Key, out var forced)) return forced.On;
            return InWindow(f, local);
        }

        /// <summary>Is the festival with this key running now (calendar or forced)? Unknown keys are
        /// false. For SeasonalDrops, which holds festival items back outside their season.</summary>
        internal static bool IsFestivalActive(string key)
        {
            var f = Calendar.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            return f != null && IsActive(f, RetailNow());
        }

        internal static bool IsFestivalKey(string key) =>
            Calendar.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        static Override ForcedState(Festival f)
        {
            lock (s_Lock) return s_Forced.TryGetValue(f.Key, out var o) ? o : null;
        }

        // ---- persistence -------------------------------------------------------------------

        /// <summary>
        /// Beside the dll, resolved through ACE's own mod container so it is right wherever
        /// Config.js puts ModsDirectory. GetModContainerByName is exact-match here on purpose; the
        /// partial match would also take a mod merely named like ours. If the container cannot be
        /// found (it always can at Register time, but a wrong answer here must not throw inside
        /// Mod.Initialize) fall back to ModPath/RevivalGuard, which is the same folder by ACE's
        /// convention: FolderName is the dll name.
        /// </summary>
        static string OverridePath()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(folder)) folder = Path.Combine(ModManager.ModPath, "RevivalGuard");
            return Path.Combine(folder, OVERRIDE_FILE);
        }

        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        static void LoadOverrides()
        {
            var path = OverridePath();
            try
            {
                if (!File.Exists(path)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Override>>(File.ReadAllText(path), s_Json)
                             ?? new Dictionary<string, Override>();
                lock (s_Lock)
                {
                    s_Forced.Clear();
                    foreach (var kv in loaded)
                    {
                        // A key the calendar does not know (a renamed festival, a typo edited in by
                        // hand) is logged and dropped rather than kept as a phantom override.
                        if (Calendar.Any(f => f.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) && kv.Value != null)
                            s_Forced[kv.Key] = kv.Value;
                        else
                            Mod.Log.Warn($"[RevivalGuard] seasonal: {OVERRIDE_FILE} names unknown festival '{kv.Key}'; ignored");
                    }
                }
            }
            catch (Exception e)
            {
                // A corrupt file must not take the mod down with it; the calendar still runs.
                Mod.Log.Error($"[RevivalGuard] seasonal: could not read {path}: {e.Message}");
            }
        }

        static void SaveOverrides()
        {
            var path = OverridePath();
            try
            {
                Dictionary<string, Override> snapshot;
                lock (s_Lock) snapshot = new Dictionary<string, Override>(s_Forced);
                if (snapshot.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                // Write beside, then move over: a crash mid-write leaves the old file, not half of a new one.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, s_Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] seasonal: could not write {path}: {e.Message}; the override is in memory only until the next successful save");
            }
        }

        /// <summary>
        /// Said once at startup so nobody has to ask the shard what season it thinks it is. A forced
        /// festival names who forced it and when, because the point of persisting it is that a
        /// restart is not a decision; a person's decision should still be visible as one.
        /// </summary>
        static void LogStartupState()
        {
            var local = RetailNow();
            int active = 0;
            foreach (var f in Calendar)
            {
                var forced = ForcedState(f);
                bool natural = InWindow(f, local);
                if (forced == null)
                {
                    if (!natural) continue;
                    active++;
                    Mod.Log.Info($"[RevivalGuard] seasonal: {f.Name} is ACTIVE at startup, natural window {f.Window} ({f.Evidence})");
                    continue;
                }
                if (forced.On) active++;
                Mod.Log.Info($"[RevivalGuard] seasonal: {f.Name} is {(forced.On ? "ACTIVE" : "OFF")} at startup, FORCED {(forced.On ? "on" : "off")}"
                    + $" by {forced.By ?? "unknown"} at {forced.At ?? "unknown"} (natural window {f.Window}, which is {(natural ? "open" : "closed")} today);"
                    + $" @festival auto {f.Key} hands it back to the calendar");
            }
            if (active == 0)
            {
                var next = Calendar.Select(f => new { f, d = DaysUntil(f, local) })
                                   .Where(x => x.d >= 0).OrderBy(x => x.d).FirstOrDefault();
                Mod.Log.Info("[RevivalGuard] seasonal: no festival is active at startup"
                    + (next != null ? $"; next is {next.f.Name} in {next.d} day{(next.d == 1 ? "" : "s")}" : ""));
            }
        }

        // ---- the scheduler -----------------------------------------------------------------

        /// <summary>
        /// Work out what every event row this file knows about should be, then move only the ones
        /// that are wrong. Deliberately a full re-assertion rather than an edge trigger: something
        /// else on the shard can turn these rows (a stopgap object dying, an admin's @setevent), and
        /// re-asserting means the calendar wins within a minute instead of silently losing.
        /// </summary>
        static void Apply(bool announce)
        {
            var local = RetailNow();
            var active = Calendar.Where(f => IsActive(f, local)).ToList();

            // ON beats OFF. Only Festival Season currently turns anything off, but if a future
            // window overlapped one that wanted the same row on, the festival that adds content
            // should win over the one that takes it away.
            var wantOn = new HashSet<string>(active.SelectMany(f => f.On), StringComparer.OrdinalIgnoreCase);
            var wantOff = new HashSet<string>(active.SelectMany(f => f.Off), StringComparer.OrdinalIgnoreCase);
            wantOff.ExceptWith(wantOn);

            foreach (var f in Calendar)
            {
                foreach (var name in f.On)
                    if (!wantOn.Contains(name)) wantOff.Add(name);

                // A row that some festival SUPPRESSES is part of the world's ordinary state, so
                // outside that festival it belongs On, not merely "not started". normaltowncrier is
                // the only one of these today and it ships as state 4 (On) for exactly that reason.
                foreach (var name in f.Off)
                    if (!wantOff.Contains(name)) wantOn.Add(name);
            }

            // Restated after the restore pass for the same reason as the first time: nothing may end
            // up in both sets, or the two loops below would fight each other every minute.
            wantOff.ExceptWith(wantOn);

            foreach (var name in wantOn) Set(name, true);
            foreach (var name in wantOff) Set(name, false);

            if (!announce) return;

            lock (s_Lock)
            {
                foreach (var f in active)
                {
                    if (!s_Running.Add(f.Key)) continue;
                    // The first pass after a restart is not a festival starting, it is the server
                    // catching up with the date (or with a persisted override). Announcing it would
                    // tell everyone online that Festival Season had just begun every time the shard
                    // bounced.
                    if (s_FirstPass)
                    {
                        Mod.Log.Info($"[RevivalGuard] seasonal: {f.Name} rows asserted"
                            + (s_Forced.ContainsKey(f.Key) ? " (forced)" : $" (in its window, {f.Evidence})"));
                        continue;
                    }
                    Broadcast($"{f.Name} has begun. {f.Announce}");
                    Mod.Log.Info($"[RevivalGuard] seasonal: {f.Name} started");
                }
                foreach (var key in s_Running.Where(k => active.All(f => !f.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    s_Running.Remove(key);
                    var f = Calendar.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (f == null || s_FirstPass) continue;
                    Broadcast($"{f.Name} has ended for this year.");
                    Mod.Log.Info($"[RevivalGuard] seasonal: {f.Name} ended");
                }
                s_FirstPass = false;
            }
        }

        /// <summary>
        /// Move one event row, if it is ours to move. A row whose state is Disabled (2) was switched
        /// off deliberately by whoever built the world database and EventManager refuses to touch it
        /// anyway; a name that is not in the table at all means this shard's `ace_world.event` does
        /// not carry that content, which is worth a log line once and not worth failing over.
        /// </summary>
        static void Set(string name, bool on)
        {
            if (!EventManager.IsEventAvailable(name))
            {
                Mod.Log.Warn($"[RevivalGuard] seasonal: ace_world.event has no row named '{name}'; skipping it");
                return;
            }
            var status = EventManager.GetEventStatus(name);
            if (status == GameEventState.Disabled) return;
            if (on && status == GameEventState.On) return;
            if (!on && status != GameEventState.On) return;

            if (on) EventManager.StartEvent(name, null, null);
            else EventManager.StopEvent(name, null, null);
            Mod.Log.Info($"[RevivalGuard] seasonal: {name} -> {(on ? "On" : "Off")}");
        }

        static void Broadcast(string text) =>
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(text, ChatMessageType.WorldBroadcast));

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        /// <summary>Days until this festival's window next opens, for the "what is next" line.</summary>
        static int DaysUntil(Festival f, DateTime local)
        {
            for (int i = 0; i <= 366; i++)
                if (InWindow(f, local.AddDays(i))) return i;
            return -1;
        }

        static string StateOf(string name)
        {
            if (!EventManager.IsEventAvailable(name)) return $"{name}=MISSING";
            return $"{name}={EventManager.GetEventStatus(name)}";
        }

        static void FestivalCmd(Session session, params string[] parameters)
        {
            var local = RetailNow();
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            string who = session?.Player?.Name ?? "console";

            if (verb == "on" || verb == "off" || verb == "auto")
            {
                if (session != null && session.AccessLevel < AccessLevel.Developer)
                {
                    Say(session, "@festival on/off/auto needs Developer access. @festival on its own "
                        + "shows what is running.");
                    return;
                }
                string key = parameters.Length > 1 ? parameters[1] : null;
                if (string.IsNullOrWhiteSpace(key))
                {
                    Say(session, $"@festival {verb} needs a festival key: "
                        + string.Join(", ", Calendar.Select(f => f.Key)));
                    return;
                }
                if (verb == "auto" && key.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    lock (s_Lock) s_Forced.Clear();
                    SaveOverrides();
                    Apply(announce: false);
                    Say(session, "Every festival is back on the calendar.");
                    Mod.Log.Info($"[RevivalGuard] seasonal: all overrides cleared by {who}");
                    return;
                }
                var f = Calendar.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (f == null)
                {
                    // The shard is called Hollowtide, so this is the first thing anyone types.
                    if (key.Equals("hollowtide", StringComparison.OrdinalIgnoreCase))
                        Say(session, "Hollowtide is the shard's name. The October event is Festival Season: @festival "
                            + verb + " festivalseason.");
                    else
                        Say(session, $"No festival '{key}'. Keys: " + string.Join(", ", Calendar.Select(c => c.Key)));
                    return;
                }
                lock (s_Lock)
                {
                    if (verb == "auto") s_Forced.Remove(f.Key);
                    else s_Forced[f.Key] = new Override { On = verb == "on", By = who, At = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC" };
                }
                SaveOverrides();
                // Forcing is for testing, so it does not broadcast: an admin proving the spawns work
                // in March should not tell every player that Festival Season has begun.
                Apply(announce: false);
                lock (s_Lock) { if (verb == "on") s_Running.Add(f.Key); else s_Running.Remove(f.Key); }
                Say(session, verb == "auto"
                    ? $"{f.Name} follows the calendar again, and is currently {(IsActive(f, local) ? "on" : "off")}."
                    : $"{f.Name} forced {verb}. This survives a restart; @festival auto {f.Key} hands it back to the calendar.");
                foreach (var n in f.On.Concat(f.Off)) Say(session, "  " + StateOf(n));
                Mod.Log.Info($"[RevivalGuard] seasonal: {f.Key} {(verb == "auto" ? "handed back to the calendar" : "forced " + verb)} by {who}");
                return;
            }

            if (verb == "list")
            {
                Say(session, "--- the seasonal calendar (dates are retail's own, at UTC-5) ---");
                foreach (var f in Calendar)
                {
                    var forced = ForcedState(f);
                    Say(session, $"{f.Key,-14} {f.Window}"
                        + $"  {(IsActive(f, local) ? "RUNNING" : "off")}"
                        + (forced != null ? $" (FORCED {(forced.On ? "on" : "off")} by {forced.By} at {forced.At})" : "")
                        + $"   {f.Evidence}");
                    Say(session, "    " + string.Join("  ", f.On.Concat(f.Off).Select(StateOf)));
                }
                return;
            }

            var running = Calendar.Where(f => IsActive(f, local)).ToList();
            if (running.Count == 0)
            {
                var next = Calendar.Select(f => new { f, d = DaysUntil(f, local) })
                                   .Where(x => x.d >= 0).OrderBy(x => x.d).FirstOrDefault();
                Say(session, "No festival is running.");
                if (next != null)
                    Say(session, $"Next is {next.f.Name}, in {next.d} day{(next.d == 1 ? "" : "s")}.");
            }
            else
            {
                foreach (var f in running)
                {
                    var forced = ForcedState(f);
                    Say(session, $"{f.Name} is running (through {f.EndMonth:00}-{f.EndDay:00})."
                        + (forced != null ? $"  FORCED by {forced.By} at {forced.At}, not the calendar." : ""));
                    Say(session, "  " + f.Announce);
                }
            }
            if (session == null || session.AccessLevel >= AccessLevel.Developer)
                Say(session, "@festival list for all of them, @festival on <key> to force one for testing.");
        }
    }
}
