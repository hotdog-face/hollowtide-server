using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// LAND CONTROL (retail, Ancient Powers 2008): society player killers take Northwatch Castle or
    /// Freebooter Keep from the Creeping Blight by holding its three banners. docs/LAND-CONTROL.md.
    ///
    /// THE RULES ARE RETAIL'S AND ALREADY LIVE IN ace_world, NOT HERE. The banners, the keep soldiers,
    /// Mitsuhara, the Buffing Array, the Black Market portals, the Crystal Array and the 68-hour hold
    /// are weenies, event-linked generators and emotes (the 80310-80430 block and the 72 "Keep..."
    /// rows of the event table). The hold timer is a countdown in the MyQuest registry of an invisible
    /// "Stopgap!" creature on Hebian-To (landblock E74E, permaloaded in Config.js), 5 per 5-second
    /// heartbeat from 244800 (68 hours), with the Crystal Array at 147600 (27 hours in) and the world
    /// warnings at 1 h, 30, 15, 10 and 5 minutes.
    ///
    /// WHAT ACE LOSES, AND THIS FILE KEEPS. ACE holds event states and a creature's MyQuest registry in
    /// memory only (EventManager.Initialize reloads the database's states, which say "Creeping
    /// Blight"), so every restart, including the nightly one, handed both keeps back to the Blight and
    /// restarted any hold. With `rg_land_control` on:
    ///   * every 30 s the "Keep..." event states and each hold's end time are written to
    ///     Mods/RevivalGuard/LandControl/state.json (beside the dll; a deploy leaves it alone);
    ///   * on the first world tick after a start they are put back, and when a holder's Stopgap
    ///     spawns again its Generation emote (the "has claimed" world line, the Mitsuhara reward
    ///     window) is skipped and its countdown set to the time left on the wall clock. A reward
    ///     window or Crystal Array milestone crossed while the shard was down is applied then.
    ///   * a society banner that respawns while its "...Claimed" event is on (a restart, or the keep's
    ///     landblock unloading when everyone left) settles at once instead of re-running the 5-minute
    ///     contest it already won.
    ///   * monsters leave the banners and Crystal Arrays to the players: ACE's faction-mob scan let
    ///     the Creeping Blight kill claimed society banners (see Protected).
    /// With the switch off none of that happens and the retail data runs exactly as ACE runs it.
    ///
    /// `@landcontrol` (admin, also `@lc`) reads the state and forces it for testing: capture, revert,
    /// time left, settle, and clearing a player's banner/reward/array waits. Its replies are ours and
    /// say so. Registered from a Harmony Prepare(), so Mod.cs needs no line.
    /// </summary>
    public static class LandControl
    {
        public const string P_ON = "rg_land_control";
        public static bool On => PropertyManager.GetBool(P_ON, false).Item;

        const string WAIT = "KeepBannerClaimedWait";   // the countdown quest, on banners and stopgaps alike
        const string SUPPLY_WAIT = "KeepSupplyWait";   // on a Stopgap: one supply crate roll per 20 h
        const int HOLD = 244800, REWARDS_END = 244200, ARRAY_AT = 147600, BANNER_SETTLE = 300;
        const ushort STOPGAP_LB = 0xE74E;

        internal sealed class Soc
        {
            public string Key, Name, Short; public int Bit;
            public Soc(string key, string name, string shortName, int bit) { Key = key; Name = name; Short = shortName; Bit = bit; }
        }
        static readonly Soc[] SOCS =
        {
            new Soc("Celhan", "Celestial Hand", "ch", 1),
            new Soc("Eldweb", "Eldrytch Web", "ew", 2),
            new Soc("Radblo", "Radiant Blood", "rb", 4),
        };
        static readonly string[] LOCS = { "Courtyard", "Spire", "Tower" };

        internal sealed class Keep
        {
            public string Key, Name; public ushort Lb;
            public uint[] Stopgap;          // by SOCS index
            public uint[,] Banner;          // [soc, loc]
            public uint[] Blight;           // by loc
            public uint[] ResetBanner;      // by soc: the Banner Crystal a dead Crystal Array leaves
            public string Ev(string tail) => "Keep" + Key + tail;
        }
        static readonly Keep[] KEEPS =
        {
            new Keep
            {
                Key = "Freebooter", Name = "Freebooter Keep", Lb = 0xF92F,
                Stopgap = new uint[] { 80352, 80353, 80354 },
                Banner = new uint[,] { { 80312, 80314, 80315 }, { 80320, 80321, 80322 }, { 80323, 80324, 80325 } },
                Blight = new uint[] { 37544, 37547, 37550 },
                ResetBanner = new uint[] { 80343, 80347, 80348 },
            },
            new Keep
            {
                Key = "Northwatch", Name = "Northwatch Castle", Lb = 0x9EE5,
                Stopgap = new uint[] { 80400, 80401, 80402 },
                Banner = new uint[,] { { 80409, 80410, 80411 }, { 80412, 80413, 80414 }, { 80415, 80416, 80417 } },
                Blight = new uint[] { 38107, 38113, 38119 },
                ResetBanner = new uint[] { 80406, 80407, 80408 },
            },
        };
        static readonly uint[] ARRAYS = { 40543, 80341, 80342 };   // by soc; the same weenie serves both keeps

        /// <summary>Banners, Crystal Arrays and Banner Crystals: only players may fight them.</summary>
        static readonly HashSet<uint> s_Protected = BuildProtected();
        static HashSet<uint> BuildProtected()
        {
            var h = new HashSet<uint>(ARRAYS);
            foreach (var k in KEEPS)
            {
                foreach (var w in k.Banner) h.Add(w);
                foreach (var w in k.Blight) h.Add(w);
                foreach (var w in k.ResetBanner) h.Add(w);
            }
            return h;
        }

        /// <summary>A keep's banner, array or Banner Crystal, standing in its keep. ACE's faction-mob
        /// scan (Monster_Awareness.FactionMob_CheckMonsters) sets every creature of another faction on
        /// any creature without one, so the Creeping Blight (Faction1Bits 8) killed claimed society
        /// banners on staging within minutes ("Blighted Ardent Moarsman has destroyed the Celestial
        /// Hand Banner of the Courtyard!"). Retail's banner says who may: "You must be a Player Killer
        /// to be able to destroy this banner."</summary>
        internal static bool Protected(WorldObject wo)
        {
            if (wo == null || !s_Protected.Contains(wo.WeenieClassId) || !On) return false;
            var lb = wo.CurrentLandblock?.Id.Landblock ?? (ushort)(wo.Location?.LandblockId.Landblock ?? 0);
            return lb == 0xF92F || lb == 0x9EE5;
        }

        static bool s_Registered, s_Restored;
        static double s_NextSave;
        static string s_LastWritten;
        /// <summary>Stopgap wcid -> the hold's end (unix seconds), waiting for that Stopgap to spawn.</summary>
        static readonly Dictionary<uint, long> s_PendingHold = new Dictionary<uint, long>();
        /// <summary>Stopgap wcid -> when it last rolled a supply crate, to put back with the hold.</summary>
        static readonly Dictionary<uint, long> s_PendingSupply = new Dictionary<uint, long>();
        static readonly object s_Lock = new object();

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard Land Control: keep the keeps' holders and timers across restarts (retail Land Control itself is world data)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LandControl: property default not registered: {e.Message}"); }
            foreach (var verb in new[] { "landcontrol", "lc" })
                CommandManager.TryAddCommand(Command, verb, AccessLevel.Admin, CommandHandlerFlag.None,
                    "Land Control (ours): the keeps' state, and test controls.",
                    "[status | capture <keep> <ch|ew|rb> | revert <keep> | timeleft <keep> <27h|90m|3605s> | settle <keep> | resetwait <player> | save]");
            Mod.Log.Info($"[RevivalGuard] LandControl: {(On ? "ON" : "off")} ({P_ON}); @landcontrol / @lc; state {StatePath() ?? "(no folder)"}");
        }

        // ------------------------------------------------------------------ persistence

        sealed class Saved
        {
            public long SavedAt { get; set; }
            public Dictionary<string, bool> Events { get; set; } = new Dictionary<string, bool>();
            public Dictionary<string, long> HoldEnds { get; set; } = new Dictionary<string, long>();
            /// <summary>Keep -> when its Stopgap last rolled a supply crate (KeepSupplyWait, 20 h), unix.</summary>
            public Dictionary<string, long> SupplyAt { get; set; } = new Dictionary<string, long>();
        }

        static string StatePath()
        {
            var dir = ModFolder.Sub("LandControl");
            return dir == null ? null : Path.Combine(dir, "state.json");
        }

        static IEnumerable<string> KeepEvents() =>
            EventManager.Events.Keys.Where(k => k.StartsWith("Keep", StringComparison.OrdinalIgnoreCase)).ToList();

        static bool EventOn(string name) => EventManager.GetEventStatus(name) == GameEventState.On;

        /// <summary>World loop, every tick.</summary>
        internal static void Tick()
        {
            if (!On) return;
            var now = ACE.Common.Time.GetUnixTime();
            if (!s_Restored) { s_Restored = true; Restore(); s_NextSave = now + 30; return; }
            if (now < s_NextSave) return;
            s_NextSave = now + 30;
            Save();
        }

        internal static void Save()
        {
            var path = StatePath();
            if (path == null) return;
            try
            {
                var s = new Saved { SavedAt = PkCommon.Now() };
                foreach (var e in KeepEvents()) s.Events[e] = EventOn(e);
                Saved prev = null;
                foreach (var k in KEEPS)
                {
                    int si = HolderIndex(k);
                    if (si < 0) continue;
                    var sg = FindStopgap(k, si);
                    if (sg != null)
                    {
                        s.HoldEnds[k.Key] = s.SavedAt + Remaining(sg);
                        var q = sg.QuestManager.GetQuest(SUPPLY_WAIT);
                        if (q != null) s.SupplyAt[k.Key] = q.LastTimeCompleted;
                    }
                    else
                    {
                        // held but its Stopgap not spawned yet (a start): keep what the file said
                        prev ??= Read(path);
                        lock (s_Lock)
                            if (s_PendingHold.TryGetValue(k.Stopgap[si], out var end)) s.HoldEnds[k.Key] = end;
                            else if (prev != null && prev.HoldEnds.TryGetValue(k.Key, out var pend)) s.HoldEnds[k.Key] = pend;
                        if (prev != null && prev.SupplyAt.TryGetValue(k.Key, out var sup)) s.SupplyAt[k.Key] = sup;
                    }
                }
                // write only on a change: SavedAt aside, most ticks are identical
                var body = JsonSerializer.Serialize(new { s.Events, s.HoldEnds, s.SupplyAt });
                if (body == s_LastWritten) return;
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, path, true);
                s_LastWritten = body;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LandControl: could not write state.json: {e.Message}"); }
        }

        static Saved Read(string path)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) : null; }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LandControl: state.json unreadable ({e.Message}); the keeps start as the database says"); return null; }
        }

        static void Restore()
        {
            var path = StatePath();
            var s = path == null ? null : Read(path);
            if (s == null) { Mod.Log.Info("[RevivalGuard] LandControl: no saved state; the keeps start as the database says"); return; }
            int started = 0, stopped = 0;
            foreach (var kv in s.Events)
            {
                if (!EventManager.IsEventAvailable(kv.Key)) continue;
                bool on = EventOn(kv.Key);
                if (kv.Value && !on) { EventManager.StartEvent(kv.Key, null, null); started++; }
                else if (!kv.Value && on) { EventManager.StopEvent(kv.Key, null, null); stopped++; }
            }
            var holds = new List<string>();
            long now = PkCommon.Now();
            foreach (var k in KEEPS)
            {
                int si = HolderIndex(k);
                if (si < 0 || !s.HoldEnds.TryGetValue(k.Key, out var end)) continue;
                lock (s_Lock)
                {
                    s_PendingHold[k.Stopgap[si]] = end;
                    if (s.SupplyAt.TryGetValue(k.Key, out var sup)) s_PendingSupply[k.Stopgap[si]] = sup;
                }
                // A SUPPLY CRATE OUT AT THE RESTART IS NOT REFILLED: its generator would spawn a fresh
                // crate (a second Mana Forge Key) on every start. The roll's 20-hour wait comes back
                // with the hold, so a restart does not buy a new roll either.
                var supplyEv = k.Ev(SOCS[si].Key + "Supply");
                if (EventOn(supplyEv)) EventManager.StopEvent(supplyEv, null, null);
                holds.Add($"{k.Name} held by the {SOCS[si].Name}, {Dur(end - now)} left");
            }
            Mod.Log.Info($"[RevivalGuard] LandControl: restored from state.json saved {Dur(now - s.SavedAt)} ago: {started} events started, {stopped} stopped"
                + (holds.Count > 0 ? "; " + string.Join("; ", holds) : "; no keep held"));
        }

        // ------------------------------------------------------------------ spawn hook

        /// <summary>A Generation emote is about to run. Returns false to skip it.</summary>
        internal static bool OnGeneration(WorldObject wo)
        {
            if (wo == null || !On) return true;
            uint w = wo.WeenieClassId;
            foreach (var k in KEEPS)
            {
                for (int si = 0; si < SOCS.Length; si++)
                {
                    if (w == k.Stopgap[si])
                    {
                        long end = 0, sup = 0;
                        bool pending, hasSup = false;
                        lock (s_Lock)
                        {
                            pending = s_PendingHold.Remove(w, out end);
                            if (pending) hasSup = s_PendingSupply.Remove(w, out sup);
                        }
                        if (!pending)
                        {
                            // a real capture: let it announce, and do what its "rallied the Creeping
                            // Blight" line says to the other keep
                            RetakeOtherKeep(k);
                            return true;
                        }
                        if (wo is Creature sg)
                        {
                            ApplyTimeLeft(k, si, sg, (int)(end - PkCommon.Now()), "restored");
                            if (hasSup)
                            {
                                sg.QuestManager.Stamp(SUPPLY_WAIT);
                                var q = sg.QuestManager.GetQuest(SUPPLY_WAIT);
                                if (q != null) q.LastTimeCompleted = (uint)sup;
                            }
                        }
                        return false;
                    }
                    for (int li = 0; li < LOCS.Length; li++)
                    {
                        if (w != k.Banner[si, li]) continue;
                        if (!EventOn(k.Ev(LOCS[li] + SOCS[si].Key + "Claimed"))) return true;
                        // already won: settle on the next heartbeat (its own "successfully claimed" path)
                        if (wo is Creature c) c.QuestManager.SetQuestCompletions(WAIT, 5);
                        Mod.Log.Info($"[RevivalGuard] LandControl: {wo.Name} at {k.Name} respawned already claimed; settling it");
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Set a holder Stopgap's countdown, applying the reward window's end and the Crystal
        /// Array's start if the new time skips past them (their checks are exact values).</summary>
        static void ApplyTimeLeft(Keep k, int si, Creature sg, int left, string why)
        {
            var soc = SOCS[si];
            left = Math.Clamp(left / 5 * 5, 5, HOLD);
            sg.QuestManager.SetQuestCompletions(WAIT, left);
            if (left < REWARDS_END && EventOn(k.Ev(soc.Key + "Rewards")))
                EventManager.StopEvent(k.Ev(soc.Key + "Rewards"), sg, null);
            if (left < ARRAY_AT && !EventOn(k.Ev(soc.Key + "Array")))
            {
                EventManager.StartEvent(k.Ev(soc.Key + "Array"), sg, null);
                PkCommon.World($"The Society of the {soc.Name} has erected a Crystal Array at {k.Name} to help keep the Creeping Blight at bay!");
            }
            Mod.Log.Info($"[RevivalGuard] LandControl: {k.Name} ({soc.Name}) hold {why}: {Dur(left)} left");
        }

        /// <summary>A keep was just taken. The Stopgap's Generation emote then says, when the other keep
        /// is the Blight's, "the forces loyal to T'thuun are retaking all of the Banner locations!" but
        /// changes nothing there; this does it: any society banner standing at the other keep (contested
        /// or claimed) goes back to the Creeping Blight.</summary>
        static void RetakeOtherKeep(Keep taken)
        {
            var other = KEEPS.First(x => x != taken);
            if (HolderIndex(other) >= 0 || !EventOn(other.Ev("Blight"))) return;
            bool any = false;
            foreach (var soc in SOCS) foreach (var loc in LOCS)
                if (EventOn(other.Ev(loc + soc.Key)) || EventOn(other.Ev(loc + soc.Key + "Claimed"))) any = true;
            if (!any) return;
            Revert(other, null);
            Mod.Log.Info($"[RevivalGuard] LandControl: {taken.Name} taken; the society banners at {other.Name} went back to the Creeping Blight");
        }

        // ------------------------------------------------------------------ reading the world

        static int HolderIndex(Keep k)
        {
            for (int i = 0; i < SOCS.Length; i++) if (EventOn(k.Ev(SOCS[i].Key))) return i;
            return -1;
        }

        static Landblock Loaded(ushort lb)
        {
            var id = new LandblockId((uint)lb << 16 | 0xFFFF);
            return LandblockManager.IsLoaded(id) ? LandblockManager.GetLandblock(id, false) : null;
        }

        static IEnumerable<WorldObject> Objects(ushort lb) =>
            (IEnumerable<WorldObject>)Loaded(lb)?.GetAllWorldObjectsForDiagnostics() ?? Array.Empty<WorldObject>();

        static Creature FindStopgap(Keep k, int si) =>
            Objects(STOPGAP_LB).OfType<Creature>().FirstOrDefault(c => c.WeenieClassId == k.Stopgap[si] && !c.IsDead);

        static int Remaining(Creature c) => c.QuestManager.GetCurrentSolves(WAIT);

        // ------------------------------------------------------------------ @landcontrol

        static void Command(Session session, params string[] args)
        {
            var a = args ?? Array.Empty<string>();
            string verb = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
            try
            {
                switch (verb)
                {
                    case "status": Reply(session, Status()); return;
                    case "save": Save(); Reply(session, $"[Land Control, ours] saved to {StatePath()}{(On ? "" : $" (note: {P_ON} is off, so nothing saves on its own and nothing is restored at a start)")}"); return;
                    case "capture":
                    {
                        var k = KeepArg(a, 1); var si = SocArg(a, 2);
                        if (k == null || si < 0) { Reply(session, "[Land Control, ours] usage: @landcontrol capture <freebooter|northwatch> <ch|ew|rb>"); return; }
                        var other = KEEPS.First(x => x != k);
                        if (EventOn(other.Ev(SOCS[si].Key)))
                            Reply(session, $"[Land Control, ours] warning: the {SOCS[si].Name} already hold {other.Name}; retail's banners refuse a second keep, forcing it anyway");
                        EventManager.StartEvent(k.Ev(SOCS[si].Key), session?.Player, null);
                        Reply(session, $"[Land Control, ours] started {k.Ev(SOCS[si].Key)}: the Stopgap on Hebian-To spawns within two heartbeats (about 10 s) and announces the capture as retail did.");
                        return;
                    }
                    case "revert":
                    {
                        var k = KeepArg(a, 1);
                        if (k == null) { Reply(session, "[Land Control, ours] usage: @landcontrol revert <freebooter|northwatch>"); return; }
                        Reply(session, Revert(k, session?.Player));
                        return;
                    }
                    case "timeleft":
                    {
                        var k = KeepArg(a, 1);
                        if (k == null || a.Length < 3 || !TryDur(a[2], out var secs)) { Reply(session, "[Land Control, ours] usage: @landcontrol timeleft <keep> <27h|90m|3605s|seconds>"); return; }
                        int si = HolderIndex(k);
                        var sg = si < 0 ? null : FindStopgap(k, si);
                        if (sg == null) { Reply(session, $"[Land Control, ours] {k.Name} is not held (or its Stopgap has not spawned yet)."); return; }
                        new ActionChain().AddAction(sg, () => ApplyTimeLeft(k, si, sg, secs, $"set by {session?.Player?.Name ?? "console"}")).EnqueueChain();
                        Reply(session, $"[Land Control, ours] {k.Name}: hold set to {Dur(Math.Clamp(secs / 5 * 5, 5, HOLD))} left. Milestones (Crystal Array at 27 h in, warnings at 1 h, 30, 15, 10, 5 min) fire when the countdown passes them exactly.");
                        return;
                    }
                    case "settle":
                    {
                        var k = KeepArg(a, 1);
                        if (k == null) { Reply(session, "[Land Control, ours] usage: @landcontrol settle <freebooter|northwatch>"); return; }
                        int n = 0;
                        foreach (var c in Objects(k.Lb).OfType<Creature>().Where(c => IsSocietyBanner(k, c.WeenieClassId) && !c.IsDead).ToList())
                        {
                            int left = Remaining(c);
                            if (left <= 5 || left > BANNER_SETTLE) continue;
                            new ActionChain().AddAction(c, () => c.QuestManager.SetQuestCompletions(WAIT, 5)).EnqueueChain();
                            n++;
                        }
                        Reply(session, n == 0 ? $"[Land Control, ours] {k.Name}: no contested banner in a loaded landblock." : $"[Land Control, ours] {k.Name}: {n} contested banner(s) settle on their next heartbeat.");
                        return;
                    }
                    case "resetwait":
                    {
                        var name = a.Length > 1 ? string.Join(" ", a.Skip(1)) : session?.Player?.Name;
                        var p = name == null ? null : PlayerManager.GetOnlinePlayer(name);
                        if (p == null) { Reply(session, $"[Land Control, ours] {name ?? "(no name)"} is not online."); return; }
                        foreach (var q in new[] { WAIT, "KeepRewardsWait", "KeepBuffingArrayWait" }) p.QuestManager.Erase(q);
                        Reply(session, $"[Land Control, ours] cleared {p.Name}'s banner, reward and Buffing Array waits.");
                        return;
                    }
                    default:
                        Reply(session, "[Land Control, ours] @landcontrol [status | capture <keep> <ch|ew|rb> | revert <keep> | timeleft <keep> <27h|90m|3605s> | settle <keep> | resetwait <player> | save]");
                        return;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] LandControl: @landcontrol {string.Join(" ", a)} failed: {e}");
                Reply(session, $"[Land Control, ours] that failed: {e.Message}");
            }
        }

        static string Revert(Keep k, Player by)
        {
            int si = HolderIndex(k);
            if (si >= 0)
            {
                var sg = FindStopgap(k, si);
                if (sg != null)
                {
                    // the retail path: the countdown ends, the Stopgap kills itself, its Death emote
                    // announces the loss and resets the keep to the Blight
                    new ActionChain().AddAction(sg, () => sg.QuestManager.SetQuestCompletions(WAIT, 5)).EnqueueChain();
                    return $"[Land Control, ours] {k.Name}: the {SOCS[si].Name}'s hold ends on the Stopgap's next heartbeat (about 5 s), with retail's world line.";
                }
                lock (s_Lock) s_PendingHold.Remove(k.Stopgap[si]);
            }
            // not held (or no Stopgap yet): the reset banner's KeepReset, done directly
            foreach (var soc in SOCS)
            {
                foreach (var tail in new[] { "", "Array", "Rewards", "Supply" }) EventManager.StopEvent(k.Ev(soc.Key + tail), by, null);
                foreach (var loc in LOCS) { EventManager.StopEvent(k.Ev(loc + soc.Key), by, null); EventManager.StopEvent(k.Ev(loc + soc.Key + "Claimed"), by, null); }
            }
            EventManager.StartEvent(k.Ev("Blight"), by, null);
            foreach (var loc in LOCS) EventManager.StartEvent(k.Ev(loc + "Blight"), by, null);
            return $"[Land Control, ours] {k.Name}: every society event stopped and the Creeping Blight's started; the generators swap on their next two heartbeats.";
        }

        static bool IsSocietyBanner(Keep k, uint w)
        {
            for (int si = 0; si < SOCS.Length; si++) for (int li = 0; li < LOCS.Length; li++) if (k.Banner[si, li] == w) return true;
            return false;
        }

        static string Status()
        {
            var sb = new StringBuilder($"[Land Control, ours] {P_ON} is {(On ? "ON (holds and timers survive a restart)" : "off (the retail data runs, nothing is saved)")}");
            foreach (var k in KEEPS)
            {
                sb.Append('\n').Append(k.Name).Append(": ");
                int si = HolderIndex(k);
                var lb = Loaded(k.Lb);
                if (si >= 0)
                {
                    var soc = SOCS[si];
                    var sg = FindStopgap(k, si);
                    sb.Append($"held by the {soc.Name}");
                    if (sg != null)
                    {
                        int left = Remaining(sg);
                        sb.Append($", {Dur(left)} left (reverts about {DateTime.UtcNow.AddSeconds(left):MM-dd HH:mm} UTC)");
                        sb.Append(EventOn(k.Ev(soc.Key + "Array")) ? "; Crystal Array up" : $"; Crystal Array in {Dur(Math.Max(0, left - ARRAY_AT))}");
                        if (EventOn(k.Ev(soc.Key + "Rewards"))) sb.Append($"; Mitsuhara rewarding for {Dur(Math.Max(0, left - REWARDS_END))}");
                        if (EventOn(k.Ev(soc.Key + "Supply"))) sb.Append("; supply crate out");
                    }
                    else sb.Append(" (Stopgap not spawned yet)");
                    var arr = lb?.GetAllWorldObjectsForDiagnostics().OfType<Creature>().FirstOrDefault(c => ARRAYS.Contains(c.WeenieClassId) && !c.IsDead);
                    if (arr != null) sb.Append($" (array health {arr.Health.Current:N0}/{arr.Health.MaxValue:N0})");
                    var rb = lb?.GetAllWorldObjectsForDiagnostics().FirstOrDefault(o => k.ResetBanner.Contains(o.WeenieClassId));
                    if (rb != null) sb.Append("; the Banner Crystal is down: a rival society can use it to revert the keep");
                }
                else
                {
                    sb.Append(EventOn(k.Ev("Blight")) ? "the Creeping Blight" : "nobody (no Blight event either)");
                    var objs = lb?.GetAllWorldObjectsForDiagnostics().OfType<Creature>().Where(c => !c.IsDead).ToList();
                    var parts = new List<string>();
                    for (int li = 0; li < LOCS.Length; li++)
                    {
                        string who = null;
                        for (int s = 0; s < SOCS.Length && who == null; s++)
                        {
                            if (EventOn(k.Ev(LOCS[li] + SOCS[s].Key + "Claimed"))) who = $"{SOCS[s].Short.ToUpperInvariant()} claimed";
                            else if (EventOn(k.Ev(LOCS[li] + SOCS[s].Key)))
                            {
                                var b = objs?.FirstOrDefault(c => c.WeenieClassId == k.Banner[s, li]);
                                who = $"{SOCS[s].Short.ToUpperInvariant()} contested" + (b != null ? $" {Dur(Remaining(b))}" : "");
                            }
                        }
                        parts.Add($"{LOCS[li]} {who ?? (EventOn(k.Ev(LOCS[li] + "Blight")) ? "Blight" : "empty")}");
                    }
                    sb.Append("; banners: ").Append(string.Join(", ", parts));
                }
                sb.Append(lb == null ? " [landblock not loaded]" : "");
            }
            return sb.ToString();
        }

        static Keep KeepArg(string[] a, int i)
        {
            if (a.Length <= i) return null;
            var s = a[i].ToLowerInvariant();
            return KEEPS.FirstOrDefault(k => k.Key.ToLowerInvariant().StartsWith(s) || (s.Length >= 2 && k.Name.ToLowerInvariant().Contains(s)));
        }

        static int SocArg(string[] a, int i)
        {
            if (a.Length <= i) return -1;
            var s = a[i].ToLowerInvariant();
            for (int n = 0; n < SOCS.Length; n++)
                if (s == SOCS[n].Short || SOCS[n].Key.ToLowerInvariant().StartsWith(s) || SOCS[n].Name.Replace(" ", "").ToLowerInvariant().StartsWith(s)) return n;
            return -1;
        }

        static bool TryDur(string s, out int secs)
        {
            secs = 0;
            s = s.Trim().ToLowerInvariant();
            if (s.Length == 0) return false;
            int mul = 1;
            char u = s[s.Length - 1];
            if (u == 'h') mul = 3600; else if (u == 'm') mul = 60; else if (u == 's') mul = 1;
            if (!char.IsDigit(u)) s = s.Substring(0, s.Length - 1);
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            secs = (int)Math.Min(int.MaxValue, v * mul);
            return true;
        }

        internal static string Dur(long secs)
        {
            if (secs < 0) secs = 0;
            long h = secs / 3600, m = secs % 3600 / 60, s = secs % 60;
            return h > 0 ? $"{h}h {m}m" : m > 0 ? $"{m}m {s}s" : $"{s}s";
        }

        static void Reply(Session session, string text)
        {
            if (session?.Player == null) { foreach (var line in text.Split('\n')) Console.WriteLine(line); return; }
            foreach (var line in text.Split('\n'))
                session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }
    }

    /// <summary>The world loop: save every 30 s, restore once after a start. Its Prepare registers the
    /// feature, so Mod.cs needs no line for it.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class LandControlTick
    {
        static bool Prepare() { LandControl.Register(); return true; }
        static void Postfix()
        {
            try { LandControl.Tick(); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LandControl tick: {e.Message}"); }
        }
    }

    /// <summary>A monster is being set on a creature (ACE's proximity and faction-mob scans). A keep's
    /// banner or array never sets one on itself.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.AlertMonster))]
    static class LandControlNoAlert
    {
        static bool Prefix(Creature __instance, Creature monster, ref bool __result)
        {
            if (monster is Player || !LandControl.Protected(__instance)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>What a monster may attack: never a keep's banner or array.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.GetAttackTargets))]
    static class LandControlNoTarget
    {
        static void Postfix(Creature __instance, List<Creature> __result)
        {
            if (__instance is Player || __result == null || __result.Count == 0 || !LandControl.On) return;
            __result.RemoveAll(LandControl.Protected);
        }
    }

    /// <summary>A spawned object's Generation emote (EmoteManager declares OnGeneration; nothing
    /// overrides it). Only the keep Stopgaps and society banners are looked at.</summary>
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.OnGeneration))]
    static class LandControlGeneration
    {
        static bool Prefix(EmoteManager __instance)
        {
            try { return LandControl.OnGeneration(__instance.WorldObject); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LandControl generation: {e.Message}"); return true; }
        }
    }
}
