using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Festival = RevivalGuard.SeasonalCalendar.Festival;

// tools/seasons/check (FestivalCheck) runs the pure half of this file against the built dll
[assembly: InternalsVisibleTo("FestivalCheck")]

namespace RevivalGuard
{
    /// <summary>
    /// TELL PEOPLE (seasonal improvement #1, owner-approved 2026-10-21). Festivals used to turn on with one
    /// broadcast to whoever was online at that minute, and none at all if the shard restarted across the boundary.
    ///
    ///   Login: every player, while a festival runs, "Festival Season is on in Dereth until Nov 1 (11 days left)."
    ///          plus where to find it (the table's Hint); within 7 days before one, "X begins in N days."
    ///   World: a broadcast when a festival starts, 24 h before it ends (festivals longer than a day), and when it
    ///          ends. Starts and ends also go to Discord #announcements (DiscordBridge.Announce).
    ///
    /// WHAT WAS SAID IS REMEMBERED in SeasonalAnnounced.json beside SeasonalOverrides.json, per festival: which
    /// window (its first day) and whether its start, reminder and end were said. So a restart mid-festival says
    /// nothing, and a start or end that fell while the shard was down is said when it comes back (an end only
    /// within 48 h). No file (the first boot with this code): the current state is recorded silently.
    ///
    /// PUBLIC STATE is the calendar's window, minus a festival a person forced off. Forcing one ON outside its
    /// window is a staff test and is never announced; a forced-off festival is not said to end.
    /// </summary>
    public static class FestivalNotices
    {
        const string STATE_FILE = "SeasonalAnnounced.json";
        internal const int UPCOMING_DAYS = 7;
        internal const double REMIND_HOURS = 24;
        internal const double END_GRACE_HOURS = 48;
        const double LOGIN_DELAY_S = 8;   // after the login's own lines and Mail (6 s), so it is not lost in them

        internal sealed class Record
        {
            [JsonPropertyName("window")] public string Window { get; set; }      // first day of the window, yyyy-MM-dd
            [JsonPropertyName("started")] public bool Started { get; set; }
            [JsonPropertyName("reminded")] public bool Reminded { get; set; }
            [JsonPropertyName("ended")] public bool Ended { get; set; }
            [JsonPropertyName("at")] public string At { get; set; }              // UTC of the last change, for people
        }

        internal enum Kind { Start, Remind, End }

        internal sealed class Notice
        {
            public Festival F; public Kind Kind; public DateTime Last; public double HoursLeft;
            public string Game => Kind == Kind.Start ? StartLine(F, Last) : Kind == Kind.End ? EndLine(F) : RemindLine(F, HoursLeft);
            public string Discord => Kind == Kind.Start ? DiscordStart(F, Last) : Kind == Kind.End ? DiscordEnd(F) : null;
        }

        static Dictionary<string, Record> s_State;
        static bool s_Seed;
        static readonly object s_Lock = new object();
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        // ---- the pure half: dates and words (no ACE; tools/seasons/check runs it) -------------------------------

        internal static DateTime LocalOf(DateTime utc) => utc.AddHours(SeasonalCalendar.RETAIL_UTC_OFFSET_HOURS);

        /// <summary>The window containing this local day: its first and last days. False outside every window.</summary>
        internal static bool Window(Festival f, DateTime day, out DateTime first, out DateTime last)
        {
            first = last = day.Date;
            if (!SeasonalCalendar.InWindow(f, day.Date)) return false;
            for (int i = 0; i < 370 && SeasonalCalendar.InWindow(f, first.AddDays(-1)); i++) first = first.AddDays(-1);
            for (int i = 0; i < 370 && SeasonalCalendar.InWindow(f, last.AddDays(1)); i++) last = last.AddDays(1);
            return true;
        }

        /// <summary>The window's end: midnight after its last day, at UTC-5.</summary>
        internal static DateTime EndUtc(DateTime last) => last.Date.AddDays(1).AddHours(-SeasonalCalendar.RETAIL_UTC_OFFSET_HOURS);

        static bool OneDay(Festival f) => f.StartMonth == f.EndMonth && f.StartDay == f.EndDay;

        internal static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        internal static string Date(DateTime d) => d.ToString("MMM d", CultureInfo.InvariantCulture);
        static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
        static string WithHint(string line, Festival f) => string.IsNullOrWhiteSpace(f.Hint) ? line : line + " " + f.Hint;

        internal static string LoginRunning(Festival f, DateTime today, DateTime last)
        {
            int left = (last.Date - today.Date).Days;
            string tail = left <= 0 ? "last day" : Plural(left, "day") + " left";
            return WithHint($"{Cap(f.Name)} is on in Dereth until {Date(last)} ({tail}).", f);
        }

        internal static string LoginUpcoming(Festival f, int days) =>
            days == 1 ? $"{Cap(f.Name)} begins tomorrow." : $"{Cap(f.Name)} begins in {days} days.";

        internal static string StartLine(Festival f, DateTime last) =>
            (OneDay(f) ? $"{Cap(f.Name)} has begun, for today only. " : $"{Cap(f.Name)} has begun and runs until {Date(last)}. ") + f.Announce;

        internal static string RemindLine(Festival f, double hoursLeft) =>
            WithHint($"{Cap(f.Name)} ends in {Plural(Math.Max(1, (int)Math.Round(hoursLeft)), "hour")}. Last chance this year!", f);

        internal static string EndLine(Festival f) => $"{Cap(f.Name)} has ended for this year. Thank you for celebrating!";

        internal static string DiscordStart(Festival f, DateTime last) =>
            (OneDay(f) ? $"**{Cap(f.Name)}** is on in Dereth, today only! " : $"**{Cap(f.Name)}** has begun in Dereth and runs until {Date(last)}! ")
            + f.Announce;

        internal static string DiscordEnd(Festival f) => $"**{Cap(f.Name)}** has ended for this year. Thanks to everyone who came out!";

        /// <summary>Is it on for players: in its window and not forced off.</summary>
        static bool Public(Festival f, DateTime today, Func<Festival, bool?> forced, out DateTime first, out DateTime last) =>
            Window(f, today, out first, out last) && forced(f) != false;

        /// <summary>The login lines at this moment: running festivals, then those starting within a week.</summary>
        internal static List<string> LoginLines(DateTime utcNow, Func<Festival, bool?> forced)
        {
            var today = LocalOf(utcNow).Date;
            var lines = new List<string>();
            foreach (var f in SeasonalCalendar.Calendar)
                if (Public(f, today, forced, out _, out var last)) lines.Add(LoginRunning(f, today, last));
            foreach (var f in SeasonalCalendar.Calendar)
            {
                if (Window(f, today, out _, out _)) continue;
                int d = SeasonalCalendar.DaysUntil(f, today);
                if (d >= 1 && d <= UPCOMING_DAYS) lines.Add(LoginUpcoming(f, d));
            }
            return lines;
        }

        /// <summary>
        /// Compare the calendar with what was last said and return what to say now, updating state. seed: record
        /// the present silently (no state file yet). changed: whether state needs saving.
        /// </summary>
        internal static List<Notice> Decide(Dictionary<string, Record> state, DateTime utcNow, Func<Festival, bool?> forced, bool seed, out bool changed)
        {
            changed = false;
            var notices = new List<Notice>();
            var today = LocalOf(utcNow).Date;
            string at = utcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
            foreach (var f in SeasonalCalendar.Calendar)
            {
                state.TryGetValue(f.Key, out var rec);
                bool inWindow = Window(f, today, out var first, out var last);
                if (inWindow && forced(f) != false)
                {
                    string win = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    double hoursLeft = (EndUtc(last) - utcNow).TotalHours;
                    bool remindDue = OneDay(f) || hoursLeft <= REMIND_HOURS;
                    if (rec == null || rec.Window != win)
                    {
                        // a window not yet announced; its reminder is folded into a late start
                        state[f.Key] = new Record { Window = win, Started = true, Reminded = remindDue, At = at };
                        changed = true;
                        if (!seed) notices.Add(new Notice { F = f, Kind = Kind.Start, Last = last, HoursLeft = hoursLeft });
                    }
                    else if (!rec.Reminded && remindDue)
                    {
                        rec.Reminded = true; rec.At = at; changed = true;
                        if (!seed) notices.Add(new Notice { F = f, Kind = Kind.Remind, Last = last, HoursLeft = hoursLeft });
                    }
                }
                else if (rec != null && rec.Started && !rec.Ended && !inWindow)
                {
                    // its window is over (a forced-off festival inside its window waits: that is a person's test)
                    rec.Ended = true; rec.At = at; changed = true;
                    bool known = DateTime.TryParseExact(rec.Window, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var w);
                    DateTime end = known && Window(f, w, out _, out var wl) ? EndUtc(wl) : utcNow;
                    if (!seed && forced(f) == null && (utcNow - end).TotalHours <= END_GRACE_HOURS)
                        notices.Add(new Notice { F = f, Kind = Kind.End });
                }
            }
            return notices;
        }

        // ---- the shard half -----------------------------------------------------------------------------------

        /// <summary>Once a minute from SeasonalCalendar's tick, after the rows are set.</summary>
        internal static void Tick()
        {
            List<Notice> notices;
            lock (s_Lock)
            {
                if (s_State == null) s_State = Load(out s_Seed);
                notices = Decide(s_State, DateTime.UtcNow, SeasonalCalendar.ForcedOn, s_Seed, out bool changed);
                if (s_Seed)
                {
                    Mod.Log.Info($"[RevivalGuard] FestivalNotices: no {STATE_FILE}; recorded the present silently ("
                        + (s_State.Count == 0 ? "nothing running" : string.Join(", ", s_State.Keys)) + ")");
                    s_Seed = false; changed = true;
                }
                if (changed) Save(s_State);
            }
            foreach (var n in notices)
            {
                PlayerManager.BroadcastToAll(new GameMessageSystemChat(n.Game, ChatMessageType.WorldBroadcast));
                string d = n.Discord;
                bool posted = d != null && DiscordBridge.Announce(d);
                Mod.Log.Info($"[RevivalGuard] FestivalNotices: {n.F.Key} {n.Kind.ToString().ToLowerInvariant()} said"
                    + (d == null ? "" : posted ? "; Discord queued" : "; Discord off (no bridge or announceChannelId)"));
            }
        }

        static Dictionary<string, Record> Load(out bool seed)
        {
            var path = SeasonalCalendar.FileBeside(STATE_FILE);
            seed = false;
            try
            {
                if (!File.Exists(path)) { seed = true; return NewState(); }
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(path), s_Json);
                var s = NewState();
                if (loaded != null) foreach (var kv in loaded) if (kv.Value != null) s[kv.Key] = kv.Value;
                return s;
            }
            catch (Exception e)
            {
                // unreadable: start over silently rather than announce every running festival again
                Mod.Log.Error($"[RevivalGuard] FestivalNotices: could not read {path} ({e.Message}); recording the present silently");
                seed = true;
                return NewState();
            }
        }

        static Dictionary<string, Record> NewState() => new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);

        static void Save(Dictionary<string, Record> state)
        {
            var path = SeasonalCalendar.FileBeside(STATE_FILE);
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, s_Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] FestivalNotices: could not write {path}: {e.Message}"); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p == null || Mercenaries.IsMerc(p)) return;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(LOGIN_DELAY_S);
                    chain.AddAction(p, () =>
                    {
                        try
                        {
                            foreach (var line in LoginLines(DateTime.UtcNow, SeasonalCalendar.ForcedOn))
                                p.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
                        }
                        catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalNotices login: {e.Message}"); }
                    });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalNotices enter: {e.Message}"); }
            }
        }

        // ---- @festival say ------------------------------------------------------------------------------------

        static void Out(Session s, string text)
        {
            if (s == null) Console.WriteLine(text);   // the server console
            else s.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        /// <summary>@festival say [key start|end|remind|login]: the exact lines, to the asker only.</summary>
        internal static void Preview(Session session, string[] args)
        {
            var utcNow = DateTime.UtcNow;
            var today = LocalOf(utcNow).Date;
            if (args.Length < 1)
            {
                Out(session, "@festival say <key> start|end|remind|login shows what players are told, to you only. Keys: "
                    + string.Join(", ", SeasonalCalendar.Calendar.Select(c => c.Key)));
                var now = LoginLines(utcNow, SeasonalCalendar.ForcedOn);
                Out(session, now.Count == 0 ? "A player logging in now is told nothing." : "A player logging in now is told:");
                foreach (var l in now) Out(session, "  " + l);
                lock (s_Lock)
                {
                    if (s_State == null) Out(session, $"{STATE_FILE}: not read yet (the first tick reads it).");
                    else if (s_State.Count == 0) Out(session, $"{STATE_FILE}: nothing announced yet.");
                    else foreach (var kv in s_State)
                        Out(session, $"  {kv.Key}: window {kv.Value.Window}, started {kv.Value.Started}, reminded {kv.Value.Reminded}, ended {kv.Value.Ended} ({kv.Value.At})");
                }
                return;
            }
            var f = SeasonalCalendar.Calendar.FirstOrDefault(x => x.Key.Equals(args[0], StringComparison.OrdinalIgnoreCase));
            if (f == null) { Out(session, $"No festival '{args[0]}'. Keys: " + string.Join(", ", SeasonalCalendar.Calendar.Select(c => c.Key))); return; }

            // the window it is in, or the next one
            if (!Window(f, today, out var first, out var last))
            {
                int d = SeasonalCalendar.DaysUntil(f, today);
                Window(f, today.AddDays(Math.Max(0, d)), out first, out last);
            }
            string what = args.Length > 1 ? args[1].ToLowerInvariant() : "all";
            bool all = what == "all";
            if (all || what == "start")
            {
                Out(session, "[world, at the start] " + StartLine(f, last));
                Out(session, "[Discord, at the start] " + DiscordStart(f, last));
            }
            if (all || what == "remind")
                Out(session, OneDay(f) ? "[no reminder: a one-day festival]" : "[world, 24 h before the end] " + RemindLine(f, REMIND_HOURS));
            if (all || what == "end")
            {
                Out(session, "[world, at the end] " + EndLine(f));
                Out(session, "[Discord, at the end] " + DiscordEnd(f));
            }
            if (all || what == "login")
            {
                Out(session, $"[login, first day {Date(first)}] " + LoginRunning(f, first, last));
                if (last > first) Out(session, $"[login, last day {Date(last)}] " + LoginRunning(f, last, last));
                Out(session, "[login, the week before] " + LoginUpcoming(f, 5) + " / " + LoginUpcoming(f, 1));
            }
            if (!all && what != "start" && what != "end" && what != "remind" && what != "login")
                Out(session, "Say which: start, end, remind or login (or nothing for all four).");
        }
    }
}
