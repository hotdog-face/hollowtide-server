using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE SCAVENGER HUNT (retail beta 1999-06-14; revived Jun 14 yearly, docs/EVENT-TESTS/scavenger-hunt.md). The
    /// advocates "arranged a contest to recognize adventurers bringing them the most valuable collection of specified
    /// monster trophies within a six-hour span" (wiki rev 475541); nothing else of it survives, so all but the trophies
    /// and the six hours is ours. While the calendar's 'scavengerhunt' is on, from 18:00 to midnight retail time
    /// (UTC-5), the row ScavengerHunt stands a Herald of the Advocates in Holtburg, Shoushi and Yaraq
    /// (2027-06-14-scavenger-1-hunt.sql). Hand a herald a trophy on the list and its points are added to your tally
    /// (any herald, one tally); standings go out on the hour, and at the end the three best tallies each get a Gem of
    /// Wealth (pool item 11825, the owner's yes pending: docs/EVENT-TESTS/event-rewards.md) through the world-boss
    /// reward queue. Mercenaries are not counted. The tally survives a restart (ScavengerHunt.json); a run that has
    /// ended does not start again the same day. Admins: @scavengerhunt [start [minutes] | stop | status].
    /// </summary>
    internal static class ScavengerHunt
    {
        const string FESTIVAL = "scavengerhunt", ROW = "ScavengerHunt", STATE_FILE = "ScavengerHunt.json";
        internal const uint HERALD = 904030;
        const uint PRIZE = 11825;                  // Gem of Wealth (unreleased pool)
        const int START_HOUR = 18, HOURS = 6;      // retail local time; the six-hour span is retail's

        /// <summary>The list (retail's trophies; the points are ours). Keep in step with gen_retired_jun14.py.</summary>
        internal static readonly (uint wcid, string name, int points)[] TROPHIES =
        {
            (3669, "Drudge Charm", 1), (3693, "Banderling Scalp", 1), (3687, "Skeleton's Skull", 2), (4239, "Reedshark Hide", 3),
            (4235, "Thin Gromnie Hide", 3), (9259, "Large Sclavus Hide", 4), (8154, "Broken Virindi Mask", 8), (28984, "Bloodied Burun Hide", 10),
        };

        sealed class State
        {
            public string Day { get; set; }             // the retail day the current or last run belongs to
            public double EndsAt { get; set; }          // unix seconds; 0 = not running
            public bool Finished { get; set; }
            public double NextStanding { get; set; }
            public Dictionary<string, Entry> Scores { get; set; } = new Dictionary<string, Entry>();
        }

        sealed class Entry
        {
            public string Name { get; set; }
            public int Points { get; set; }
            public int Trophies { get; set; }
        }

        static readonly object s_Lock = new object();
        static State s_State = new State();
        static double s_NextTick;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        static double Now => Time.GetUnixTime();
        static DateTime Local => DateTime.UtcNow.AddHours(SeasonalCalendar.RETAIL_UTC_OFFSET_HOURS);
        internal static bool Running { get { lock (s_Lock) return s_State.EndsAt > Now && !s_State.Finished; } }

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "scavengerhunt", AccessLevel.Admin, CommandHandlerFlag.None,
                "The Jun 14 Scavenger Hunt (RevivalGuard): status, or start [minutes] | stop.", "[start [minutes] | stop | status]");
            Load();
        }

        // ---- the clock ----------------------------------------------------------------------------

        internal static void Tick()
        {
            var now = Now;
            if (now < s_NextTick) return;
            s_NextTick = now + 5;
            lock (s_Lock)
            {
                var local = Local;
                string day = local.ToString("yyyy-MM-dd");
                bool running = s_State.EndsAt > now && !s_State.Finished;
                if (running)
                {
                    if (!IsOn()) EventManager.StartEvent(ROW, null, null);   // after a restart
                    if (now >= s_State.NextStanding) { s_State.NextStanding = now + 3600; Standings(false); Save(); }
                    return;
                }
                if (s_State.EndsAt > 0 && !s_State.Finished) { Finish("the six hours are up"); return; }
                // The calendar's start: its day, from 18:00, once.
                if (SeasonalCalendar.IsFestivalActive(FESTIVAL) && local.Hour >= START_HOUR && s_State.Day != day
                    && EventManager.IsEventAvailable(ROW))
                {
                    var end = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0).AddDays(1);   // midnight
                    Begin(day, now + Math.Min(HOURS * 3600, (end - local).TotalSeconds), "calendar");
                }
                else if (IsOn() && s_State.EndsAt <= 0) EventManager.StopEvent(ROW, null, null);   // left on by hand
            }
        }

        static bool IsOn() => EventManager.GetEventStatus(ROW) == GameEventState.On;

        static void Begin(string day, double endsAt, string by)
        {
            s_State = new State { Day = day, EndsAt = endsAt, NextStanding = Now + 3600 };
            EventManager.StartEvent(ROW, null, null);
            Save();
            int mins = (int)Math.Round((endsAt - Now) / 60);
            Broadcast("The Advocates have called a Scavenger Hunt! Bring monster trophies to the Herald of the Advocates in Holtburg, "
                + $"Shoushi or Yaraq in the next {Hours(mins)}. The three best collections will be rewarded.");
            Broadcast("Wanted: " + string.Join(", ", TROPHIES.Select(t => $"{t.name} ({t.points})")) + ".");
            Mod.Log.Info($"[RevivalGuard] ScavengerHunt: STARTED for {day} ({by}) until {DateTimeOffset.FromUnixTimeSeconds((long)endsAt):u}");
        }

        static string Hours(int mins) => mins >= 120 ? $"{mins / 60} hours" : mins >= 60 ? "hour" : $"{mins} minutes";

        static List<KeyValuePair<string, Entry>> Ranked() =>
            s_State.Scores.Where(kv => kv.Value.Points > 0).OrderByDescending(kv => kv.Value.Points).ThenBy(kv => kv.Value.Name).ToList();

        static void Standings(bool final)
        {
            var top = Ranked().Take(3).ToList();
            if (top.Count == 0) { if (!final) Broadcast("Scavenger Hunt: no trophies yet. The heralds in Holtburg, Shoushi and Yaraq are waiting."); return; }
            string list = string.Join(", ", top.Select((kv, i) => $"{i + 1}. {kv.Value.Name} ({kv.Value.Points})"));
            Broadcast(final ? "The Scavenger Hunt is over. The Advocates reward the best collections: " + list + "."
                            : "Scavenger Hunt standings: " + list + ".");
        }

        static void Finish(string why)
        {
            s_State.Finished = true;
            if (IsOn()) EventManager.StopEvent(ROW, null, null);
            Standings(true);
            int place = 0;
            foreach (var kv in Ranked().Take(3))
            {
                place++;
                if (uint.TryParse(kv.Key, System.Globalization.NumberStyles.HexNumber, null, out var guid))
                    WorldBossSchedule.Grant(guid, PRIZE, $"place {place} in the Scavenger Hunt");
            }
            if (place == 0) Broadcast("No one brought the Advocates a single trophy.");
            Save();
            Mod.Log.Info($"[RevivalGuard] ScavengerHunt: ENDED ({why}); {s_State.Scores.Count} hunters, {place} prize(s)");
        }

        // ---- the herald ---------------------------------------------------------------------------

        /// <summary>A gift to the herald: a listed trophy is counted (the whole stack) and taken; anything else is handed back.</summary>
        internal static bool Give(Player p, WorldObject target, WorldObject item)
        {
            if (p?.Session == null || target?.WeenieClassId != HERALD || item == null) return true;
            // Shown, not taken: the same pair ACE sends for a Refuse emote keeps the client's drag honest.
            p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You allow {target.Name} to examine your {item.NameWithMaterial}.", ChatMessageType.Broadcast));
            p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, item.Guid.Full, WeenieError.TradeAiRefuseEmote));
            var t = TROPHIES.FirstOrDefault(x => x.wcid == item.WeenieClassId);
            if (!Running) { Tell(p, target, "The hunt is not on. Come back when the Advocates call it."); return false; }
            if (t.wcid == 0) { Tell(p, target, "That is not on the Advocates' list."); return false; }
            if (Mercenaries.IsMerc(p)) { Tell(p, target, "The Advocates count only the trophies of free adventurers."); return false; }
            int n = Math.Max(1, item.StackSize ?? 1);
            if (!p.TryConsumeFromInventoryWithNetworking(item, n)) { Mod.Log.Warn($"[RevivalGuard] ScavengerHunt: could not take {item.Name} from {p.Name}"); return false; }
            int total;
            lock (s_Lock)
            {
                var key = p.Guid.Full.ToString("X8");
                if (!s_State.Scores.TryGetValue(key, out var e)) s_State.Scores[key] = e = new Entry();
                e.Name = p.Name;
                e.Points += t.points * n;
                e.Trophies += n;
                total = e.Points;
                Save();
            }
            Tell(p, target, $"{(n > 1 ? $"{n} of them, " : "")}{t.points * n} point{(t.points * n == 1 ? "" : "s")}. Your collection is worth {total}.");
            Mod.Log.Info($"[RevivalGuard] ScavengerHunt: {p.Name} handed in {n} {t.name} (+{t.points * n}, {total})");
            return false;
        }

        static void Tell(Player p, WorldObject npc, string text) =>
            p.Session.Network.EnqueueSend(new GameEventTell(npc, text, p, ChatMessageType.Tell));

        static void Broadcast(string text) =>
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(text, ChatMessageType.WorldBroadcast));

        // ---- admin --------------------------------------------------------------------------------

        static void Cmd(Session session, params string[] parameters)
        {
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";
            void act()
            {
                lock (s_Lock)
                {
                    switch (verb)
                    {
                        case "start":
                            if (!EventManager.IsEventAvailable(ROW)) { Say(session, "[ScavengerHunt] ace_world has no row ScavengerHunt (apply its datafix)."); return; }
                            double mins = parameters.Length > 1 && double.TryParse(parameters[1], out var m) ? Math.Clamp(m, 1, 360) : HOURS * 60;
                            Begin(Local.ToString("yyyy-MM-dd"), Now + mins * 60, "by hand");
                            Say(session, $"[ScavengerHunt] started for {mins:0} minutes; 'stop' ends it now with the prizes.");
                            return;
                        case "stop":
                            if (!(s_State.EndsAt > Now && !s_State.Finished)) { Say(session, "[ScavengerHunt] not running."); return; }
                            Finish("stopped by " + (session?.Player?.Name ?? "console"));
                            Say(session, "[ScavengerHunt] stopped.");
                            return;
                        default:
                            var r = Ranked();
                            Say(session, $"[ScavengerHunt] {(s_State.EndsAt > Now && !s_State.Finished ? $"running, {(s_State.EndsAt - Now) / 60:0} min left" : "not running")}; "
                                + $"day {s_State.Day ?? "none"}; festival {(SeasonalCalendar.IsFestivalActive(FESTIVAL) ? "on" : "off")}; {r.Count} hunter(s)"
                                + (r.Count > 0 ? ": " + string.Join(", ", r.Take(5).Select(kv => $"{kv.Value.Name} {kv.Value.Points}")) : ""));
                            return;
                    }
                }
            }
            if (session != null) act(); else WorldManager.EnqueueAction(new ActionEventDelegate(act));
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        // ---- persistence --------------------------------------------------------------------------

        static string StatePath()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(folder)) folder = Path.Combine(ModManager.ModPath, "RevivalGuard");
            return Path.Combine(folder, STATE_FILE);
        }

        static void Load()
        {
            var path = StatePath();
            try
            {
                if (!File.Exists(path)) return;
                var st = JsonSerializer.Deserialize<State>(File.ReadAllText(path), s_Json);
                if (st == null) return;
                st.Scores ??= new Dictionary<string, Entry>();
                lock (s_Lock) s_State = st;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ScavengerHunt: could not read {path}: {e.Message}"); }
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
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ScavengerHunt: could not write {path}: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class ScavengerHuntTick
    {
        static void Postfix()
        {
            try { ScavengerHunt.Tick(); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ScavengerHunt tick: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), "GiveObjectToNPC")]
    static class ScavengerHuntGive
    {
        static bool Prefix(Player __instance, WorldObject target, WorldObject item)
        {
            try { return ScavengerHunt.Give(__instance, target, item); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ScavengerHunt give: {e.Message}"); return true; }
        }
    }
}
