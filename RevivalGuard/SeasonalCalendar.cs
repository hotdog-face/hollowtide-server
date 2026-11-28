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
    /// <summary>The calendar turns, so the world turns with it.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static partial class SeasonalCalendar
    {

        /// <summary>How often the calendar is re-asserted.</summary>
        const double CHECK_SECONDS = 60.0;

        /// <summary>The override file, beside the mod dll.</summary>
        const string OVERRIDE_FILE = "SeasonalOverrides.json";


        /// <summary>One forced state, as stored in SeasonalOverrides.json.</summary>
        sealed class Override
        {
            [JsonPropertyName("on")] public bool On { get; set; }
            [JsonPropertyName("by")] public string By { get; set; }
            [JsonPropertyName("at")] public string At { get; set; }
            // No override stays for ever: one expires OVERRIDE_HOURS after it was set (At) unless set with "keep".
            [JsonPropertyName("keep")] public bool Keep { get; set; }
        }

        /// <summary>Forced states, by festival key: On true means forced on, false forced off, absent means follow the calendar.</summary>
        static readonly Dictionary<string, Override> s_Forced =
            new Dictionary<string, Override>(StringComparer.OrdinalIgnoreCase);

        static readonly object s_Lock = new object();
        static double s_NextCheck;

        /// <summary>Every row the calendar turns, on or off.</summary>
        internal static HashSet<string> OwnedRows() =>
            new HashSet<string>(Calendar.SelectMany(f => f.On.Concat(f.Off)), StringComparer.OrdinalIgnoreCase);

        internal static void Register()
        {
            // Player access on the status read
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
            try { Apply(); }
            catch (Exception e) { TickGuard.Report("SeasonalCalendar", e); }
            // The start/end/reminder broadcasts and Discord posts, remembered across restarts (FestivalNotices)
            try { FestivalNotices.Tick(); }
            catch (Exception e) { TickGuard.Report("FestivalNotices", e); }
            // Azimar's festival theme follows the same calendar (AzimarTheme)
            try { AzimarTheme.Tick(); }
            catch (Exception e) { TickGuard.Report("AzimarTheme", e); }
            // The running festivals, for the client (FestivalChannel)
            try { FestivalChannel.Tick(); }
            catch (Exception e) { TickGuard.Report("FestivalChannel", e); }
        }

        /// <summary>The local date the calendar is evaluated against.</summary>
        static DateTime RetailNow() => DateTime.UtcNow.AddHours(RETAIL_UTC_OFFSET_HOURS);

        /// <summary>A forced state for the notices: true forced on, false forced off, null the calendar's.</summary>
        internal static bool? ForcedOn(Festival f)
        {
            if (!Installed(f)) return false;   // a Pending festival without its rows is off for the notices too
            lock (s_Lock) return s_Forced.TryGetValue(f.Key, out var o) ? o.On : (bool?)null;
        }

        /// <summary>A Pending festival counts only once every row it turns on exists in ace_world</summary>
        internal static bool Installed(Festival f) => (!f.Pending || f.On.All(EventManager.IsEventAvailable)) && Switched(f);

        /// <summary>A festival behind a staff switch as well: Bael'Zharon's Rampage, rg_bz_rampage.</summary>
        static bool Switched(Festival f) =>
            !f.Key.Equals(BaelRampage.FESTIVAL, StringComparison.OrdinalIgnoreCase) || BaelRampage.SwitchOn;

        /// <summary>A file beside the dll, next to SeasonalOverrides.json (FestivalNotices keeps its state there).</summary>
        internal static string FileBeside(string name) => Path.Combine(Path.GetDirectoryName(OverridePath()), name);

        static bool IsActive(Festival f, DateTime local)
        {
            if (!Installed(f)) return false;
            List<Festival> forcedOn;
            lock (s_Lock)
            {
                if (s_Forced.TryGetValue(f.Key, out var forced)) return forced.On;
                forcedOn = Calendar.Where(x => s_Forced.TryGetValue(x.Key, out var o) && o.On).ToList();
            }
            // a festival with another town dressing forced on keeps this one's dressing down
            return InWindow(f, local) && !Displaced(f, false, forcedOn);
        }

        /// <summary>Is the festival with this key running now (calendar or forced)?</summary>
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

        static DateTime? ParseAt(string at)
        {
            if (string.IsNullOrWhiteSpace(at)) return null;
            return DateTime.TryParseExact(at.Replace(" UTC", ""), "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        }

        // ---- persistence -------------------------------------------------------------------

        /// <summary>Beside the dll, resolved through ACE's own mod container so it is right wherever Config.js puts ModsDirectory.</summary>
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
                        // A key the calendar does not know is logged and dropped rather than kept as a phantom override.
                        if (Calendar.Any(f => f.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) && kv.Value != null)
                            s_Forced[kv.Key] = kv.Value;
                        else
                            Mod.Log.Warn($"[RevivalGuard] seasonal: {OVERRIDE_FILE} names unknown festival '{kv.Key}'; ignored");
                    }
                }
            }
            catch (Exception e)
            {
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

        /// <summary>Said once at startup so nobody has to ask the shard what season it thinks it is.</summary>
        static void LogStartupState()
        {
            var local = RetailNow();
            int active = 0;
            foreach (var f in Calendar)
            {
                if (!Installed(f)) { Mod.Log.Info($"[RevivalGuard] seasonal: {f.Name} is Pending, its rows are not in ace_world (off)"); continue; }
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
                var next = Calendar.Where(Installed).Select(f => new { f, d = DaysUntil(f, local) })
                                   .Where(x => x.d >= 0).OrderBy(x => x.d).FirstOrDefault();
                Mod.Log.Info("[RevivalGuard] seasonal: no festival is active at startup"
                    + (next != null ? $"; next is {next.f.Name} in {next.d} day{(next.d == 1 ? "" : "s")}" : ""));
            }
        }

        // ---- the scheduler -----------------------------------------------------------------

        /// <summary>Work out what every event row this file knows about should be, then move only the ones that are wrong.</summary>
        static void Apply()
        {
            ExpireOverrides();
            var local = RetailNow();
            var active = Calendar.Where(f => IsActive(f, local)).ToList();
            // On beats OFF; a row a festival suppresses is On outside it (SeasonalCalendar.Table.cs Desired)
            var (wantOn, wantOff) = Desired(active);
            // a Pending festival's missing rows are not news every minute (its datafix is not applied yet)
            foreach (var f in Calendar) if (f.Pending && !f.On.All(EventManager.IsEventAvailable)) wantOff.ExceptWith(f.On);

            foreach (var name in wantOn) Set(name, true);
            foreach (var name in wantOff) Set(name, false);
        }

        /// <summary>Move one event row, if it is ours to move.</summary>
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

        /// <summary>What @festival tells a player, as lines for the Holtburg Notice Board</summary>
        internal static List<string> BoardLines(int lookAheadDays = 45)
        {
            var local = RetailNow();
            var lines = new List<string>();
            var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var months = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat;
            foreach (var f in Calendar.Where(f => IsActive(f, local)))
            {
                if (!said.Add(f.Name)) continue;
                lines.Add($"{f.Name} is open now, until {months.GetMonthName(f.EndMonth)} {f.EndDay}."
                    + (string.IsNullOrWhiteSpace(f.Hint) ? "" : " " + f.Hint.Trim()));
            }
            var coming = Calendar.Where(f => !IsActive(f, local))
                                 .Select(f => new { f, d = DaysUntil(f, local) })
                                 .Where(x => x.d > 0 && x.d <= lookAheadDays).OrderBy(x => x.d);
            foreach (var x in coming)
            {
                if (!said.Add(x.f.Name)) continue;
                lines.Add($"{x.f.Name} comes in {x.d} day{(x.d == 1 ? "" : "s")}.");
            }
            return lines;
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
                // Forcing is for testing, so it does not broadcast
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
                var next = Calendar.Where(Installed).Select(f => new { f, d = DaysUntil(f, local) })
                                   .Where(x => x.d >= 0).OrderBy(x => x.d).FirstOrDefault();
                Say(session, "No festival is running.");
                if (next != null)
                    Say(session, $"Next is {next.f.Name}, in {next.d} day{(next.d == 1 ? "" : "s")}.");
                var night = PkNight.FestivalLine();   // PK Night (every other Sunday), when it is on
                if (night != null) Say(session, night);
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
