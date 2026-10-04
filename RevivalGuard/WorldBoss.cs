using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// WORLD BOSSES: the server half of Bael'Zharon (wcid 900200, owner-approved 2026-09-27;
    /// docs/NEW-CONTENT-BAELZHARON-GOLEMS.md). The fight itself is world data
    /// (tools/gpubox-ace/new-content-baelzharon-golems.sql) and works without this file; this adds
    /// what the data cannot say:
    ///
    ///   phases once   ACE runs a WoundedTaunt emote set on EVERY hit inside its health window
    ///                 (EmoteManager.GetEmoteSet), so the phase lines printed 3-4 times per phase in
    ///                 the 2026-09-27 kills (and Aerbax's portal line does the same, QUESTS-BOSSES).
    ///                 A prefix on EmoteManager.OnDamage lets each window's set run once per life.
    ///                 With it, the phase extras: at the 47-51% set Boon of the Demon on himself
    ///                 (retail spell 2048, Magic Defense +200, one of Bael'Zharon's own three
    ///                 beneficial spells); at 22-26% his rage (Damage Rating +30, spells every 1.5 s).
    ///                 Each window also plays PlayScript BaelZharonSmite (0x96) on him: the client's
    ///                 sculpt (expansion/creatures/baelzharon) roars to it with its full CastBig clip.
    ///   every hand    The Death emote titles the killer (Beacon of Hope, 573); a postfix on
    ///                 Creature.OnDeath gives it to every player in his damage history.
    ///   24 h, kept    (rg_world_boss_schedule OFF, the old rule.) Deaths are written to
    ///                 Mods/RevivalGuard/WorldBoss/deaths.json; a postfix on the GeneratorProfile
    ///                 constructor holds the profile until death + 24 h, and the world tick releases it
    ///                 when that passes (the generator's own Delay is 7 days since world-boss-schedule.sql,
    ///                 so it no longer does it alone).
    ///   schedule      (rg_world_boss_schedule ON.) WorldBossSchedule.cs: fixed risings, warnings, a
    ///                 timed window, the map marker and personal rewards (docs/WORLD-BOSS-SYSTEM.md).
    ///   awake lair    His landblock is loaded once on the first world tick, so he spawns and the
    ///                 world broadcast goes out when he is due, not when someone happens to walk in;
    ///                 the Landblock KeepAlive (80007) in the SQL keeps it loaded after that. Since
    ///                 2026-10-01 that is an OUTDOOR landblock (world-boss-outdoor.sql): no portal leads
    ///                 to him, the map marker is the only exact place, and the way there is the players' to find.
    ///
    ///   @worldboss            (Player) when each boss rises next; admins see the state and verbs
    ///   @worldboss respawn    (Admin) schedule on: a window now; off: forget the death, generator now
    ///
    /// Every target is declared where it is patched (Creature.OnDeath, EmoteManager.OnDamage, the
    /// GeneratorProfile constructor, PlayerManager.Tick); a missing one is logged and skipped, never
    /// thrown (a throw in PatchAll leaves every other guard off). Admin feedback is ours.
    /// </summary>
    static class WorldBoss
    {
        /// <summary>One world boss. Adding a boss is one more entry: his weenie and lair generator in
        /// SQL, and these fields (docs/WORLD-BOSS-SYSTEM.md, "Adding a boss").</summary>
        internal sealed class Spec
        {
            public uint Wcid; public string Name; public ushort Lair; public TimeSpan Respawn; public uint Title;
            // the schedule (WorldBossSchedule)
            public string Key;                 // short id on the wire and in schedule.json
            public string Where;               // the region, vaguely, for chat ("the Direlands"); never a route
            public double NS, EW;              // where he stands: the map marker, the only exact place given
            public DateTime AnchorLocal;       // one rising, UTC-5 wall time; the rest follow every Interval
            public TimeSpan Interval, Lead, Window;
            public int[] Warn;                 // minutes before the rising that a broadcast goes out
            public string He = "He", Him = "him";
            public string Stir;                // the warning's first sentence
            public string Escaped;             // the broadcast when his window closes unbeaten
            public WorldBossSchedule.Tier[] Kill, Weekly;   // a roll from Kill for every hand; Weekly once a week each
        }

        internal const uint BAEL = 900200;
        internal static readonly Dictionary<uint, Spec> Bosses = new Dictionary<uint, Spec>
        {
            [BAEL] = new Spec
            {
                // Outdoors since 2026-10-01 (owner: "summon Bael in a outdoor location ... show it on the
                // map but dont tell people the route"). Generator 900202 stands at the site
                // (tools/gpubox-ace/world-boss-outdoor.sql); NS/EW is the marker, Where is all chat says.
                Wcid = BAEL, Name = "Bael'Zharon", Lair = 0x347D, Respawn = TimeSpan.FromHours(24), Title = 573,
                Key = "bael", Where = "the Direlands", NS = -1.5, EW = -60.0,   // 0x347D001D (84, 108)
                AnchorLocal = new DateTime(2026, 9, 28, 20, 0, 0), Interval = TimeSpan.FromHours(7),
                Lead = TimeSpan.FromMinutes(30), Window = TimeSpan.FromMinutes(30), Warn = new[] { 30, 10, 2 },
                Stir = "The sky darkens over the Direlands. Bael'Zharon, the Hopeslayer, stirs somewhere in the wilds.",
                Escaped = "Bael'Zharon fades back into the Shadow, unbeaten.",
                Kill = WorldBossSchedule.BAEL_KILL, Weekly = WorldBossSchedule.BAEL_WEEKLY,
            },
        };

        const uint BOON_OF_THE_DEMON = 2048;

        static readonly ConcurrentDictionary<uint, DateTime> s_Deaths = new ConcurrentDictionary<uint, DateTime>();
        static readonly ConditionalWeakTable<Creature, HashSet<float>> s_Phases = new ConditionalWeakTable<Creature, HashSet<float>>();
        static readonly object s_FileLock = new object();
        static bool s_Loaded, s_Registered;
        internal static bool HookDamage, HookDeath, HookProfile;

        static string FilePath()
        {
            var dir = ModFolder.Sub("WorldBoss");
            return dir == null ? null : Path.Combine(dir, "deaths.json");
        }

        internal static void EnsureLoaded()
        {
            if (s_Loaded) return;
            lock (s_FileLock)
            {
                if (s_Loaded) return;
                s_Loaded = true;
                try
                {
                    var p = FilePath();
                    if (p != null && File.Exists(p))
                        foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(p)))
                            if (uint.TryParse(kv.Key, out var w)) s_Deaths[w] = DateTime.SpecifyKind(kv.Value, DateTimeKind.Utc);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: deaths.json unreadable ({e.Message}); treating every boss as due"); }
            }
        }

        static void Save()
        {
            lock (s_FileLock)
            {
                try
                {
                    var p = FilePath();
                    if (p == null) return;
                    var d = s_Deaths.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                    File.WriteAllText(p, JsonSerializer.Serialize(d));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: could not write deaths.json: {e.Message}"); }
            }
        }

        internal static DateTime? DueAt(Spec s)
        {
            EnsureLoaded();
            if (!s_Deaths.TryGetValue(s.Wcid, out var died)) return null;
            var due = died + s.Respawn;
            return due > DateTime.UtcNow ? due : (DateTime?)null;
        }

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            EnsureLoaded();
            WorldBossSchedule.Register();
            CommandManager.TryAddCommand(Handle, "worldboss", AccessLevel.Player, CommandHandlerFlag.None,
                "World bosses: when each rises next. 'claim' hands over rewards that did not fit in your pack.", "[claim]");
            Mod.Log.Info($"[RevivalGuard] WorldBoss: phases once {(HookDamage ? "on" : "OFF")}, deaths {(HookDeath ? "on" : "OFF")}, 24 h kept {(HookProfile ? "on" : "OFF")}; {Bosses.Count} boss(es), {s_Deaths.Count} recorded death(s)");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static void Handle(Session session, params string[] parameters)
        {
            if (WorldBossSchedule.On || session != null && session.AccessLevel < AccessLevel.Admin
                || parameters.Length > 0 && parameters[0].Equals("claim", StringComparison.OrdinalIgnoreCase))
            {
                WorldBossSchedule.Command(session, parameters ?? Array.Empty<string>());
                return;
            }
            bool respawn = parameters.Length > 0 && parameters[0].Equals("respawn", StringComparison.OrdinalIgnoreCase);
            foreach (var s in Bosses.Values)
            {
                var alive = FindAlive(s);
                if (respawn)
                {
                    s_Deaths.TryRemove(s.Wcid, out _);
                    Save();
                    int freed = 0;
                    foreach (var prof in LairProfiles(s)) { prof.NextAvailable = DateTime.UtcNow; freed++; }
                    Say(session, alive != null
                        ? $"[WorldBoss] {s.Name} is already alive (0x{alive.Guid.Full:X8}); his death record is cleared."
                        : $"[WorldBoss] {s.Name}: death record cleared, {freed} generator profile(s) available now; he appears within a minute (the generator checks every 60 s)." + (freed == 0 ? $" His lair 0x{s.Lair:X4} is not loaded: @reloadblock {s.Lair:X4} loads it." : ""));
                    continue;
                }
                var due = DueAt(s);
                Say(session, alive != null
                    ? $"[WorldBoss] {s.Name} is alive in 0x{s.Lair:X4} at {alive.Health.Current:N0}/{alive.Health.MaxValue:N0} health."
                    : due != null
                        ? $"[WorldBoss] {s.Name} is dead; due {due:yyyy-MM-dd HH:mm} UTC ({(due.Value - DateTime.UtcNow).TotalHours:F1} h)."
                        : $"[WorldBoss] {s.Name} is due now (lair 0x{s.Lair:X4} {(LandblockManager.IsLoaded(new LandblockId((uint)s.Lair << 16 | 0xFFFF)) ? "loaded" : "not loaded")}).");
            }
        }

        internal static Landblock LoadedLair(Spec s)
        {
            var id = new LandblockId((uint)s.Lair << 16 | 0xFFFF);
            return LandblockManager.IsLoaded(id) ? LandblockManager.GetLandblock(id, false) : null;
        }

        /// <summary>His landblock and the loaded ones around it: outdoors he chases (bael-mobile.sql) and
        /// can cross a landblock edge before MonsterAi's leash brings him home.</summary>
        internal static IEnumerable<Landblock> LairAndAdjacent(Spec s)
        {
            var lb = LoadedLair(s);
            if (lb == null) yield break;
            yield return lb;
            foreach (var a in lb.Adjacents.ToList()) if (a != null) yield return a;
        }

        internal static IEnumerable<Creature> AllAlive(Spec s) =>
            LairAndAdjacent(s).SelectMany(lb => lb.GetAllWorldObjectsForDiagnostics().OfType<Creature>())
                .Where(c => c.WeenieClassId == s.Wcid && !c.IsDead);

        internal static Creature FindAlive(Spec s) => AllAlive(s).FirstOrDefault();

        internal static IEnumerable<GeneratorProfile> LairProfiles(Spec s)
        {
            var lb = LoadedLair(s);
            if (lb == null) yield break;
            foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                if (wo.GeneratorProfiles != null)
                    foreach (var p in wo.GeneratorProfiles)
                        if (p.Biota?.WeenieClassId == s.Wcid) yield return p;
        }

        // ------------------------------------------------------------------ phases, once each
        internal static bool OnDamagePrefix(EmoteManager mgr)
        {
            if (!(mgr?.WorldObject is Creature c) || !Bosses.ContainsKey(c.WeenieClassId)) return true;
            var set = mgr.GetEmoteSet(EmoteCategory.WoundedTaunt, useRNG: false);
            if (set == null) return true;
            var key = set.MinHealth ?? -1f;
            var seen = s_Phases.GetOrCreateValue(c);
            lock (seen) { if (!seen.Add(key)) return false; }   // this window has run: skip ACE's repeat
            try { PhaseExtras(c, key); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: phase extra at {key:P0} on {c.Name}: {e.Message}"); }
            Mod.Log.Info($"[RevivalGuard] WorldBoss: {c.Name} 0x{c.Guid.Full:X8} phase at {c.Health.Percent:P0} (window from {key:P0})");
            return true;
        }

        static void PhaseExtras(Creature c, float windowMin)
        {
            if (c.WeenieClassId != BAEL) return;
            // The phase roar: PlayScript BaelZharonSmite (0x96) on him. Not in his retail effect
            // table (0x34000028), so the retail client plays nothing; our client's sculpt maps it to
            // its full CastBig clip (expansion/creatures/baelzharon, scripts[]). One per window.
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.BaelZharonSmite));
            if (windowMin > 0.4f && windowMin < 0.6f)
            {
                var spell = new Spell(BOON_OF_THE_DEMON);
                if (!spell.NotFound) c.TryCastSpell_WithRedirects(spell, c, c, tryResist: false);
            }
            else if (windowMin > 0.15f && windowMin < 0.3f)
            {
                c.SetProperty(PropertyInt.DamageRating, (c.GetProperty(PropertyInt.DamageRating) ?? 0) + 30);
                c.SetProperty(PropertyFloat.AiUseMagicDelay, 1.5);
            }
        }

        // ------------------------------------------------------------------ death: record, titles
        internal static void OnDeathPostfix(Creature c)
        {
            if (c == null || c is Player || !Bosses.TryGetValue(c.WeenieClassId, out var s)) return;
            s_Deaths[s.Wcid] = DateTime.UtcNow;
            Save();
            int titled = 0;
            if (s.Title != 0 && c.DamageHistory != null)
                foreach (var info in c.DamageHistory.TotalDamage.Values.ToList())
                    if (info?.TryGetPetOwnerOrAttacker() is Player p) { p.AddTitle(s.Title); titled++; }
            Mod.Log.Info($"[RevivalGuard] WorldBoss: {s.Name} 0x{c.Guid.Full:X8} died; title {s.Title} to {titled} player(s) in his damage history" + (WorldBossSchedule.On ? "" : $"; due again {DateTime.UtcNow + s.Respawn:yyyy-MM-dd HH:mm} UTC (24 h rule)"));
            try { WorldBossSchedule.OnBossDeath(s, c); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] WorldBossSchedule: rewards for {s.Name}: {e}"); }
        }

        /// <summary>The 24 h rule while the schedule is off: the generator's own Delay is 7 days now,
        /// so once death + 24 h has passed (or no death is recorded) his profile is released here.</summary>
        static double s_NextOldRule;
        internal static void OldRuleTick()
        {
            var now = DateTime.UtcNow;
            if ((now - DateTime.UnixEpoch).TotalSeconds < s_NextOldRule) return;
            s_NextOldRule = (now - DateTime.UnixEpoch).TotalSeconds + 30;
            foreach (var s in Bosses.Values)
            {
                if (DueAt(s) != null || FindAlive(s) != null) continue;
                foreach (var prof in LairProfiles(s))
                    if (prof.NextAvailable > now && prof.Spawned.Count == 0) prof.NextAvailable = now;
            }
        }

        // ------------------------------------------------------------------ the 24 h across restarts
        internal static void ProfilePostfix(GeneratorProfile profile)
        {
            var wcid = profile?.Biota?.WeenieClassId ?? 0;
            if (!Bosses.TryGetValue(wcid, out var s)) return;
            if (WorldBossSchedule.HoldNewProfile(s, profile)) return;   // the schedule owns him
            var due = DueAt(s);
            if (due == null || profile.NextAvailable >= due.Value) return;
            profile.NextAvailable = due.Value;
            Mod.Log.Info($"[RevivalGuard] WorldBoss: {s.Name}'s generator held until {due:yyyy-MM-dd HH:mm} UTC (death recorded in deaths.json)");
        }
    }

    [HarmonyPatch]
    static class WorldBossOnDamage
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(EmoteManager), nameof(EmoteManager.OnDamage), new[] { typeof(Creature) });
            if (m == null) Mod.Log.Error("[RevivalGuard] WorldBoss: EmoteManager.OnDamage(Creature) not found; phases repeat as ACE runs them");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (WorldBoss.HookDamage = TargetMethod() != null);
        static bool Prefix(EmoteManager __instance)
        {
            try { return WorldBoss.OnDamagePrefix(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: OnDamage: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch]
    static class WorldBossOnDeath
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] WorldBoss: Creature.OnDeath(DamageHistoryInfo, DamageType, bool) not found; deaths are not recorded");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (WorldBoss.HookDeath = TargetMethod() != null);
        static void Postfix(Creature __instance)
        {
            try { WorldBoss.OnDeathPostfix(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: OnDeath: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class WorldBossGeneratorProfile
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Constructor(typeof(GeneratorProfile), new[] { typeof(WorldObject), typeof(PropertiesGenerator), typeof(uint) });
            if (m == null) Mod.Log.Error("[RevivalGuard] WorldBoss: GeneratorProfile(WorldObject, PropertiesGenerator, uint) not found; the 24 h resets on restart");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || (WorldBoss.HookProfile = TargetMethod() != null);
        static void Postfix(GeneratorProfile __instance)
        {
            try { WorldBoss.ProfilePostfix(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: GeneratorProfile: {e.Message}"); }
        }
    }

    /// <summary>First world tick: register the command and load each lair once (world thread, as
    /// LandblockReload does its loads).</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class WorldBossLairs
    {
        static bool s_Done;
        static int s_Errors;
        static void Postfix()
        {
            if (s_Done)
            {
                try { WorldBossSchedule.Tick(); if (!WorldBossSchedule.On) WorldBoss.OldRuleTick(); }
                catch (Exception e) { if (++s_Errors <= 5 || s_Errors % 1000 == 0) Mod.Log.Error($"[RevivalGuard] WorldBoss tick threw (error {s_Errors})", e); }
                return;
            }
            s_Done = true;
            try
            {
                WorldBoss.Register();
                foreach (var s in WorldBoss.Bosses.Values)
                {
                    LandblockManager.GetLandblock(new LandblockId((uint)s.Lair << 16 | 0xFFFF), false);
                    Mod.Log.Info($"[RevivalGuard] WorldBoss: lair 0x{s.Lair:X4} of {s.Name} loaded; {(WorldBoss.DueAt(s) is DateTime d ? $"he is due {d:yyyy-MM-dd HH:mm} UTC" : "he is due now")}");
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: lair preload: {e.Message}"); }
        }
    }
}
