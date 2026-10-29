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
    ///   @festival say [&lt;key&gt; start|end|remind|login]   preview what players are told (FestivalNotices)
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
    public static partial class SeasonalCalendar
    {

        /// <summary>How often the calendar is re-asserted. A festival boundary is a date, so a
        /// minute of latency is invisible, and this runs on the world loop where cheap matters.</summary>
        const double CHECK_SECONDS = 60.0;

        /// <summary>The override file, beside the mod dll. See OverridePath.</summary>
        const string OVERRIDE_FILE = "SeasonalOverrides.json";


        /// <summary>One forced state, as stored in SeasonalOverrides.json. By and At are for the
        /// startup log and @festival, so a forced festival is never anonymous.</summary>
        sealed class Override
        {
            [JsonPropertyName("on")] public bool On { get; set; }
            [JsonPropertyName("by")] public string By { get; set; }
            [JsonPropertyName("at")] public string At { get; set; }
            // NO OVERRIDE STAYS FOR EVER (owner 2026-10-25: the yearly buffer swap must not be left stuck by a test): one
            // expires OVERRIDE_HOURS after it was set (At) unless set with "keep". Older files have neither: they expire too.
            [JsonPropertyName("keep")] public bool Keep { get; set; }
        }

        /// <summary>Forced states, by festival key: On true means forced on, false forced off,
        /// absent means follow the calendar. Mirrored to SeasonalOverrides.json on every change
        /// and loaded from it in Register, so a restart does not clear them.</summary>
        static readonly Dictionary<string, Override> s_Forced =
            new Dictionary<string, Override>(StringComparer.OrdinalIgnoreCase);

        static readonly object s_Lock = new object();
        static double s_NextCheck;

        /// <summary>Every row the calendar turns, on or off. EventDirector never touches these: the
        /// calendar re-asserts them every minute and would fight it.</summary>
        internal static HashSet<string> OwnedRows() =>
            new HashSet<string>(Calendar.SelectMany(f => f.On.Concat(f.Off)), StringComparer.OrdinalIgnoreCase);

        internal static void Register()
        {
            // Player access on the status read: "what festival is on" is in-world information that
            // retail gave away with a town crier, and a player who cannot see it has no calendar.
            // The two that change the world stay at Developer, beside @setevent, which is what they
            // are. CommandManager.TryAddCommand checks access on every invocation itself.
            CommandManager.TryAddCommand(FestivalCmd, "festival", AccessLevel.Player,
                CommandHandlerFlag.None, "What festival is running, and force one on or off.",
                "[list | on <key> [keep] | off <key> [keep] | auto <key|all> | say <key> start|end|remind|login]");

            LoadOverrides();
            LogStartupState();
        }

        static void Postfix()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_NextCheck) return;
            s_NextCheck = now + CHECK_SECONDS;
            // Apply reaches EventManager.StartEvent/StopEvent; a throw there must not end the world loop
            try { Apply(); }
            catch (Exception e) { TickGuard.Report("SeasonalCalendar", e); }
            // The start/end/reminder broadcasts and Discord posts, remembered across restarts (FestivalNotices)
            try { FestivalNotices.Tick(); }
            catch (Exception e) { TickGuard.Report("FestivalNotices", e); }
            // Azimar's festival theme follows the same calendar (AzimarTheme)
            try { AzimarTheme.Tick(); }
            catch (Exception e) { TickGuard.Report("AzimarTheme", e); }
        }

        /// <summary>The local date the calendar is evaluated against. See RETAIL_UTC_OFFSET_HOURS.</summary>
        static DateTime RetailNow() => DateTime.UtcNow.AddHours(RETAIL_UTC_OFFSET_HOURS);

        /// <summary>A forced state for the notices: true forced on, false forced off, null the calendar's.</summary>
        internal static bool? ForcedOn(Festival f)
        {
            lock (s_Lock) return s_Forced.TryGetValue(f.Key, out var o) ? o.On : (bool?)null;
        }

        /// <summary>A file beside the dll, next to SeasonalOverrides.json (FestivalNotices keeps its state there).</summary>
        internal static string FileBeside(string name) => Path.Combine(Path.GetDirectoryName(OverridePath()), name);

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

        /// <summary>Drops every override past its expiry (OverrideExpired), says so in the log, and saves.</summary>
        static void ExpireOverrides()
        {
            var now = DateTime.UtcNow;
            List<(string key, Override o)> gone = new();
            lock (s_Lock)
            {
                foreach (var kv in s_Forced)
                    if (OverrideExpired(ParseAt(kv.Value.At), kv.Value.Keep, now)) gone.Add((kv.Key, kv.Value));
                foreach (var g in gone) s_Forced.Remove(g.key);
            }
            if (gone.Count == 0) return;
            SaveOverrides();
            foreach (var (key, o) in gone)
                Mod.Log.Info($"[RevivalGuard] seasonal: {key} forced {(o.On ? "on" : "off")} by {o.By ?? "unknown"} at {o.At ?? "unknown"} "
                    + $"expired after {OVERRIDE_HOURS} h; back on the calendar");
        }

        /// <summary>"2026-10-25 15:03 UTC" as written by FestivalCmd; null when unreadable (then it expires at once).</summary>
        static DateTime? ParseAt(string at)
        {
            if (string.IsNullOrWhiteSpace(at)) return null;
            return DateTime.TryParseExact(at.Replace(" UTC", ""), "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
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
        static void Apply()
        {
            ExpireOverrides();
            var local = RetailNow();
            var active = Calendar.Where(f => IsActive(f, local)).ToList();
            // ON beats OFF; a row a festival suppresses is On outside it (SeasonalCalendar.Table.cs Desired)
            var (wantOn, wantOff) = Desired(active);

            foreach (var name in wantOn) Set(name, true);
            foreach (var name in wantOff) Set(name, false);
            // Telling players (start, end, reminder, login) is FestivalNotices, which remembers what it
            // last said in SeasonalAnnounced.json so a restart neither repeats nor misses a transition.
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

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        /// <summary>Days until this festival's window next opens, for the "what is next" line.</summary>
        internal static int DaysUntil(Festival f, DateTime local)
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

            if (verb == "say")
            {
                if (session != null && session.AccessLevel < AccessLevel.Developer)
                {
                    Say(session, "@festival say needs Developer access.");
                    return;
                }
                FestivalNotices.Preview(session, parameters.Skip(1).ToArray());
                return;
            }

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
                    Apply();
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
                    else s_Forced[f.Key] = new Override { On = verb == "on", By = who, At = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC",
                                                          Keep = parameters.Length > 2 && parameters[2].Equals("keep", StringComparison.OrdinalIgnoreCase) };
                }
                SaveOverrides();
                // Forcing is for testing, so it does not broadcast: an admin proving the spawns work
                // in March should not tell every player that Festival Season has begun.
                Apply();
                Say(session, verb == "auto"
                    ? $"{f.Name} follows the calendar again, and is currently {(IsActive(f, local) ? "on" : "off")}."
                    : $"{f.Name} forced {verb}. This survives a restart and "
                      + (parameters.Length > 2 && parameters[2].Equals("keep", StringComparison.OrdinalIgnoreCase) ? "has no expiry" : $"ends by itself in {OVERRIDE_HOURS} h")
                      + $"; @festival auto {f.Key} hands it back to the calendar now.");
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
                Say(session, "@festival list for all of them, @festival on <key> to force one for testing, "
                    + "@festival say <key> start|end|remind|login to preview what players are told.");
        }
    }
}
