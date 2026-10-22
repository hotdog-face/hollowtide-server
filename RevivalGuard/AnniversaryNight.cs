using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ANNIVERSARY NIGHT: A ONE-COMMAND PARTY KIT FOR STAFF (owner 2026-10-21, seasonal improvement #3). The shard's
    /// birthday is the Anniversary (Nov 2-5) and the Night Club month (Nov 2 - Dec 1). Retail's live events were run by
    /// hand by staff; this is the kit for one evening of it:
    ///
    ///   @anniversary start [minutes]   (Developer; default 180 minutes, then it ends by itself)
    ///     * the Anniversary and the Night Club festivals on: outside their dates they are forced through
    ///       SeasonalCalendar's own @festival path, and handed back to the calendar at the end. That brings retail's
    ///       fireworks (the Fireworks Generator at 31 places, Holtburg and Cragstone among them), the Festival Stones,
    ///       the Anniversary Costumer and the Night Club Attendants;
    ///     * the Night Club is open to all: its attendants send anyone in (festival-goals.sql), and the third night
    ///       there pays the Night Club Regular trophy;
    ///     * more fireworks: retail's Anniversary Effect over Holtburg's square and on the Night Club's floor while the
    ///       party runs (only where a player has the landblock loaded; gone at the end);
    ///     * a festive crier line to everyone every 20 minutes, and a welcome for whoever logs in;
    ///     * the founders' toast: Founders.cs's granted list, the founders online named in a toast at the start (and on
    ///       @anniversary toast), and a word for each founder who logs in during the party;
    ///     * the Gift Concierge at Hotel Swank (vendor 901920, built on another branch) named in the opening line when
    ///       that weenie exists.
    ///   @anniversary stop | toast | status (no word: status)
    /// The party survives a restart (AnniversaryNight/state.json beside the dll); one that ran out while the shard
    /// was down is closed quietly at start-up. docs/EVENT-DIRECTOR.md "Anniversary Night" is the staff page.
    /// </summary>
    static class AnniversaryNight
    {
        const uint EFFECT = 8359;          // Anniversary Effect: what retail's Fireworks Generator (19457) spawns
        const uint CONCIERGE = 901920;     // the Gift Concierge, Hotel Swank (worktree-agent-a63b501587deb964d)
        const double CRIER_S = 20 * 60, CHECK_S = 10;
        const int DEFAULT_MIN = 180, MAX_MIN = 24 * 60;

        // Where the kit adds fireworks: Holtburg's lifestone square (docs/SEASONAL-UPGRADE.md showcase stop) and where
        // the attendants' Disco Inferno Portal Sending (spell 4023) lands on the Night Club's floor.
        static readonly (string where, uint cell, float x, float y, float z)[] Spots =
        {
            ("Holtburg", 0xA9B40019, 88f, 14f, 98f),
            ("the Night Club", 0x8A040230, 160f, -30f, 15f),
        };

        static readonly string[] Crier =
        {
            "Attention Citizens of Dereth! It is Anniversary Night: fireworks over Holtburg, and the Night Club is open to everyone. Any Night Club Attendant will send you in.",
            "Attention Citizens of Dereth! Another year of Dereth! Dance three nights at the Night Club this month and its attendants have balloons for your house.",
            "Attention Citizens of Dereth! The party goes on: raise a glass to the founders who were here from the start, and to everyone who came after.",
        };

        sealed class State
        {
            public bool On { get; set; }
            public double Ends { get; set; }
            public string By { get; set; }
            public List<string> Forced { get; set; } = new List<string>();
        }

        static State s_State = new State();
        static readonly object s_Lock = new object();
        static readonly Dictionary<int, WorldObject> s_Fireworks = new Dictionary<int, WorldObject>();
        static double s_NextCrier, s_NextCheck;
        static int s_CrierIdx;
        static MethodInfo s_FestivalCmd;

        internal static bool On { get { lock (s_Lock) return s_State.On; } }
        static double Now => ACE.Common.Time.GetUnixTime();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "anniversary", AccessLevel.Developer, CommandHandlerFlag.None,
                "Anniversary Night: the staff party kit (fireworks, the Night Club open to all, a crier, the founders' toast).",
                "[start [minutes] | stop | toast | status]");
            s_FestivalCmd = AccessTools.Method(typeof(SeasonalCalendar), "FestivalCmd");
            if (s_FestivalCmd == null)
                Mod.Log.Warn("[RevivalGuard] AnniversaryNight: SeasonalCalendar.FestivalCmd not found; outside Nov 2-5 the kit cannot force the festivals on");
            Load();
            Mod.Log.Info("[RevivalGuard] AnniversaryNight: @anniversary start|stop|toast|status"
                + (s_State.On ? $"; a party is on record until {Stamp(s_State.Ends)}" : ""));
        }

        /// <summary>The first tick, not Register: Register runs inside PatchAll, before SeasonalCalendar has loaded its
        /// overrides, and handing a festival back then would save an empty override file over everyone's.</summary>
        static void Boot()
        {
            lock (s_Lock)
            {
                if (s_State.On && s_State.Ends <= Now)
                {
                    Mod.Log.Info("[RevivalGuard] AnniversaryNight: the party ran out while the shard was down; closing it");
                    Stop(null, announce: false);
                }
                else if (s_State.On)
                {
                    s_NextCrier = Now + CRIER_S;
                    Mod.Log.Info($"[RevivalGuard] AnniversaryNight: resumed after the restart, until {Stamp(s_State.Ends)}");
                }
            }
        }

        // ---- the kit ----------------------------------------------------------------------------------------------

        static void Start(Session s, int minutes)
        {
            string who = s?.Player?.Name ?? "console";
            lock (s_Lock)
            {
                if (s_State.On) { Say(s, $"Anniversary Night is already running, until {Stamp(s_State.Ends)}. @anniversary stop ends it."); return; }
                s_State = new State { On = true, Ends = Now + minutes * 60.0, By = who };
                foreach (var key in new[] { "anniversary", "nightclub" })
                {
                    if (SeasonalCalendar.IsFestivalActive(key)) continue;
                    if (Festival(s, "on", key)) s_State.Forced.Add(key);
                    else Say(s, $"Could not turn the festival '{key}' on; do it by hand with @festival on {key}.");
                }
                s_NextCrier = Now + CRIER_S;
                s_NextCheck = 0;
                Save();
            }
            bool concierge = DatabaseManager.World.GetCachedWeenie(CONCIERGE) != null;
            Broadcast("Attention Citizens of Dereth! Anniversary Night has begun. Fireworks light the sky over Holtburg, and the "
                + "Night Club is open to everyone: any Night Club Attendant, in Cragstone, Hebian-To, Sanamar or Zaikhal, will send you in."
                + (concierge ? " The Gift Concierge at Hotel Swank has gifts on offer." : "") + " Come and celebrate!");
            Toast(s);
            Say(s, $"Anniversary Night started for {minutes} minutes (until {Stamp(Now + minutes * 60.0)})."
                + (s_State.Forced.Count > 0 ? $" Forced on for it, handed back at the end: {string.Join(", ", s_State.Forced)}." : ""));
            Mod.Log.Info($"[RevivalGuard] AnniversaryNight: started by {who} for {minutes} min; forced {string.Join(",", s_State.Forced)}; concierge {(concierge ? "present" : "absent")}");
        }

        /// <summary>Call with s_Lock held.</summary>
        static void Stop(Session s, bool announce)
        {
            var forced = s_State.Forced.ToList();
            s_State = new State();
            Save();
            foreach (var key in forced) Festival(s, "auto", key);
            var spawned = s_Fireworks.Values.ToList();
            s_Fireworks.Clear();
            if (spawned.Count > 0)
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    foreach (var wo in spawned) try { if (!wo.IsDestroyed) wo.Destroy(); } catch (Exception) { }
                }));
            if (announce)
                Broadcast("Anniversary Night is over. Thank you for celebrating with us!"
                    + (SeasonalCalendar.IsFestivalActive("nightclub") ? " The Night Club stays open to everyone until December 1." : ""));
            Mod.Log.Info($"[RevivalGuard] AnniversaryNight: ended{(s == null ? "" : " by " + (s.Player?.Name ?? "console"))}; handed back {string.Join(",", forced)}");
        }

        /// <summary>SeasonalCalendar's own @festival path (private; reached by name so its file is not touched): forcing
        /// is persisted there, re-asserted every minute and handed back with "auto".</summary>
        static bool Festival(Session s, string verb, string key)
        {
            if (s_FestivalCmd == null) return false;
            try { s_FestivalCmd.Invoke(null, new object[] { s, new[] { verb, key } }); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AnniversaryNight: @festival {verb} {key}: {e.InnerException?.Message ?? e.Message}"); return false; }
            return verb != "on" || SeasonalCalendar.IsFestivalActive(key);
        }

        // ---- the founders' toast ----------------------------------------------------------------------------------

        static HashSet<uint> FounderAccounts()
        {
            var set = new HashSet<uint>();
            try
            {
                var dir = ModFolder.Sub("Founders");
                var f = dir == null ? null : Path.Combine(dir, "granted.txt");   // Founders.cs's list, one account a line
                if (f != null && File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                        if (uint.TryParse(line.Trim(), out uint id)) set.Add(id);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AnniversaryNight: founders list: {e.Message}"); }
            return set;
        }

        static bool IsFounder(Player p, HashSet<uint> founders)
        {
            uint acct = p.Account?.AccountId ?? p.Character?.AccountId ?? 0;
            return acct != 0 && founders.Contains(acct) && !Mercenaries.IsMerc(p);
        }

        static void Toast(Session s)
        {
            var founders = FounderAccounts();
            var here = PlayerManager.GetAllOnline().Where(p => IsFounder(p, founders)).Select(p => p.Name).OrderBy(n => n).ToList();
            string names = here.Count == 0 ? null
                : here.Count == 1 ? here[0]
                : here.Count <= 12 ? string.Join(", ", here.Take(here.Count - 1)) + " and " + here[^1]
                : string.Join(", ", here.Take(12)) + $" and {here.Count - 12} more";
            Broadcast(names != null
                ? $"A toast to Hollowtide's founders, here from the very start: {names}! Raise a glass, Dereth."
                : $"A toast to Hollowtide's {founders.Count} founders, who were here from the very start! Raise a glass, Dereth.");
        }

        internal static void OnLogin(Player p)
        {
            if (!On || p == null || Mercenaries.IsMerc(p)) return;
            Tell(p, "It is Anniversary Night! Fireworks over Holtburg, and the Night Club is open to everyone: any Night Club Attendant will send you in.");
            if (IsFounder(p, FounderAccounts()))
                Tell(p, "Happy anniversary, founder. Dereth would not be here without you: tonight's toast is yours.");
        }

        // ---- the tick ---------------------------------------------------------------------------------------------

        static bool s_Booted;

        internal static void Tick()
        {
            var now = Now;
            if (now < s_NextCheck) return;
            s_NextCheck = now + CHECK_S;
            if (!s_Booted) { s_Booted = true; Boot(); }
            lock (s_Lock)
            {
                if (!s_State.On) return;
                if (now >= s_State.Ends) { Stop(null, announce: true); return; }
                if (now >= s_NextCrier)
                {
                    s_NextCrier = now + CRIER_S;
                    Broadcast(Crier[s_CrierIdx++ % Crier.Length]);
                }
                for (int i = 0; i < Spots.Length; i++)
                {
                    if (s_Fireworks.TryGetValue(i, out var wo) && wo != null && !wo.IsDestroyed) continue;
                    var sp = Spots[i];
                    if (!LandblockManager.IsLoaded(new LandblockId((sp.cell & 0xFFFF0000) | 0xFFFF))) continue;
                    var fw = WorldObjectFactory.CreateNewWorldObject(EFFECT);
                    if (fw == null) { Mod.Log.Warn("[RevivalGuard] AnniversaryNight: no weenie 8359 (Anniversary Effect)"); s_NextCheck = now + 600; return; }
                    fw.Location = new Position(sp.cell, sp.x, sp.y, sp.z, 0f, 0f, 0f, 1f);
                    fw.Lifespan = (int)Math.Max(60, s_State.Ends - now + 60);   // gone by itself if a stop is missed
                    s_Fireworks[i] = fw;
                    int idx = i;
                    WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        try { if (!fw.EnterWorld()) { lock (s_Lock) s_Fireworks.Remove(idx); } }
                        catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AnniversaryNight: fireworks over {Spots[idx].where}: {e.Message}"); }
                    }));
                }
            }
        }

        // ---- commands ---------------------------------------------------------------------------------------------

        static void Cmd(Session session, params string[] parameters)
        {
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";
            try
            {
                switch (verb)
                {
                    case "start":
                        int minutes = DEFAULT_MIN;
                        if (parameters.Length > 1 && (!int.TryParse(parameters[1], out minutes) || minutes < 1 || minutes > MAX_MIN))
                        { Say(session, $"@anniversary start [minutes]: 1 to {MAX_MIN} (default {DEFAULT_MIN})."); return; }
                        Start(session, minutes);
                        return;
                    case "stop":
                        lock (s_Lock)
                        {
                            if (!s_State.On) { Say(session, "Anniversary Night is not running."); return; }
                            Stop(session, announce: true);
                        }
                        Say(session, "Anniversary Night stopped.");
                        return;
                    case "toast":
                        Toast(session);
                        return;
                    default:
                        lock (s_Lock)
                        {
                            if (!s_State.On) Say(session, "Anniversary Night is not running. @anniversary start [minutes] begins it (default 180).");
                            else Say(session, $"Anniversary Night is running, started by {s_State.By}, until {Stamp(s_State.Ends)}."
                                + $" Fireworks up: {s_Fireworks.Values.Count(w => w != null && !w.IsDestroyed)} of {Spots.Length} (a spot needs its landblock loaded)."
                                + (s_State.Forced.Count > 0 ? $" Forced on: {string.Join(", ", s_State.Forced)}." : " The festivals are in their own dates."));
                        }
                        Say(session, $"Anniversary {(SeasonalCalendar.IsFestivalActive("anniversary") ? "on" : "off")}, Night Club "
                            + $"{(SeasonalCalendar.IsFestivalActive("nightclub") ? "open" : "closed")}, founders on record {FounderAccounts().Count}, "
                            + $"Gift Concierge {(DatabaseManager.World.GetCachedWeenie(CONCIERGE) != null ? "present" : "not in this world database")}.");
                        return;
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AnniversaryNight {verb}: {e.Message}"); Say(session, "That did not work: " + e.Message); }
        }

        // ---- persistence and plumbing -----------------------------------------------------------------------------

        static string StatePath()
        {
            var dir = ModFolder.Sub("AnniversaryNight");
            return dir == null ? null : Path.Combine(dir, "state.json");
        }

        static void Load()
        {
            try
            {
                var f = StatePath();
                if (f != null && File.Exists(f)) s_State = JsonSerializer.Deserialize<State>(File.ReadAllText(f)) ?? new State();
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AnniversaryNight: state: {e.Message}"); s_State = new State(); }
        }

        static void Save()
        {
            try { var f = StatePath(); if (f != null) File.WriteAllText(f, JsonSerializer.Serialize(s_State)); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AnniversaryNight: save: {e.Message}"); }
        }

        static string Stamp(double unix) => DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC";

        static void Broadcast(string text) =>
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(text, ChatMessageType.WorldBroadcast));

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void Tell(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class AnniversaryNightTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done)
            {
                s_Done = true;
                try { AnniversaryNight.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] AnniversaryNight: register: {e.Message}"); }
            }
            return true;
        }

        static void Postfix()
        {
            try { AnniversaryNight.Tick(); }
            catch (Exception e) { TickGuard.Report("AnniversaryNight", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class AnniversaryNightLogin
    {
        static void Postfix(Player __instance)
        {
            try { AnniversaryNight.OnLogin(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AnniversaryNight login: {e.Message}"); }
        }
    }
}
