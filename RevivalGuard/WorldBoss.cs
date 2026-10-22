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
    ///   seasonal      (2026-10-21) Jack o' the Harvest, Old Bitterfrost and Mother Thistledown rise on the
    ///                 schedule only while their festival runs (SeasonalCalendar.IsFestivalActive); never by the
    ///                 24 h rule, and their lairs are not preloaded (docs/WORLD-BOSS-SYSTEM.md section 8).
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
            // seasonal bosses (docs/WORLD-BOSS-SYSTEM.md section 8): null Festival = all year
            public string Festival;            // SeasonalCalendar key; he rises only while IsFestivalActive(Festival)
            public string FestivalName;        // what @worldboss calls it ("Frostfell")
            public int SeasonFrom, SeasonTo;   // MMDD, inclusive: a COPY of SeasonalCalendar's window, used only to predict later risings
            public int Enrage;                 // Damage Rating added once in his last phase (22-26%); Bael's own extras are in PhaseExtras
            public bool ScaleHealth;           // max health grows with each new fighter (WorldBossScaling); off for Bael'Zharon, sized as a raid
            public bool YearRound => Festival == null;
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
            // SEASONAL BOSSES (owner-approved 2026-10-21, seasonal improvement #4): one per festival, rising
            // only while SeasonalCalendar has that festival on. World data: tools/gpubox-ace/gen_seasonal_world_bosses.py.
            // Every 5 hours (5 and 24 share no factor, so the risings walk round the clock), anchored on the half
            // hour so a rising never shares a start with Bael'Zharon's whole hours. No KeepAlive in the data: the
            // schedule holds the lair awake only around a window (WorldBossSchedule.KeepAwake).
            [HARVEST] = new Spec
            {
                Wcid = HARVEST, Name = "Jack o' the Harvest", Lair = 0x47CF, Respawn = TimeSpan.FromHours(24), Title = 325,   // Pumpkin Slayer
                Key = "harvest", Where = "the north-western woods", NS = 63.85, EW = -44.65,   // 0x47CF002B (120, 48)
                AnchorLocal = new DateTime(2026, 10, 1, 18, 30, 0), Interval = TimeSpan.FromHours(5),
                Lead = TimeSpan.FromMinutes(30), Window = TimeSpan.FromMinutes(30), Warn = new[] { 30, 10, 2 },
                Stir = "A cold wind rattles the dry stalks of the north-western woods. Jack o' the Harvest is gathering his crop.",
                Escaped = "Jack o' the Harvest sinks back into the rotting field, unbeaten.",
                Kill = WorldBossSchedule.HARVEST_KILL, Weekly = WorldBossSchedule.HARVEST_WEEKLY,
                Festival = "festivalseason", FestivalName = "Festival Season", SeasonFrom = 1001, SeasonTo = 1101, Enrage = 20, ScaleHealth = true,
            },
            [BITTERFROST] = new Spec
            {
                Wcid = BITTERFROST, Name = "Old Bitterfrost", Lair = 0x6CC7, Respawn = TimeSpan.FromHours(24), Title = 840,   // Cold As Ice
                Key = "bitterfrost", Where = "the frozen north", NS = 57.85, EW = -15.15,   // 0x6CC70027 (96, 144)
                AnchorLocal = new DateTime(2026, 12, 4, 19, 30, 0), Interval = TimeSpan.FromHours(5),
                Lead = TimeSpan.FromMinutes(30), Window = TimeSpan.FromMinutes(30), Warn = new[] { 30, 10, 2 },
                Stir = "The snow falls harder over the frozen north. Old Bitterfrost is waking from his drift.",
                Escaped = "Old Bitterfrost crumbles back into the snow, unbeaten.",
                Kill = WorldBossSchedule.BITTERFROST_KILL, Weekly = WorldBossSchedule.BITTERFROST_WEEKLY,
                Festival = "frostfell", FestivalName = "Frostfell", SeasonFrom = 1204, SeasonTo = 101, Enrage = 25, ScaleHealth = true,
            },
            [THISTLEDOWN] = new Spec
            {
                Wcid = THISTLEDOWN, Name = "Mother Thistledown", Lair = 0xB7D2, Respawn = TimeSpan.FromHours(24), Title = 843,   // Springs Into Action
                Key = "thistledown", Where = "the north-eastern meadows", NS = 66.25, EW = 45.05,   // 0xB7D20033 (144, 48)
                AnchorLocal = new DateTime(2027, 4, 1, 17, 30, 0), Interval = TimeSpan.FromHours(5),
                Lead = TimeSpan.FromMinutes(30), Window = TimeSpan.FromMinutes(30), Warn = new[] { 30, 10, 2 },
                He = "She", Him = "her",
                Stir = "The meadows of the north-east are trembling. Mother Thistledown is coming up out of her warren.",
                Escaped = "Mother Thistledown bolts back down her warren, unbeaten.",
                Kill = WorldBossSchedule.THISTLEDOWN_KILL, Weekly = WorldBossSchedule.THISTLEDOWN_WEEKLY,
                Festival = "spring", FestivalName = "the Spring Festival", SeasonFrom = 401, SeasonTo = 501, Enrage = 15, ScaleHealth = true,
            },
        };
        internal const uint HARVEST = 902300, BITTERFROST = 902310, THISTLEDOWN = 902320;

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
                    // temp then move, like the other state files: a crash mid-write cannot leave half a file
                    var tmp = p + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(d));
                    File.Move(tmp, p, true);
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
                if (!s.YearRound) { Say(session, $"[WorldBoss] {s.Name} rises only on the schedule ({WorldBossSchedule.P_ON}), during {s.FestivalName}."); continue; }
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
        internal static bool OnDamagePrefix(EmoteManager mgr, Creature attacker)
        {
            if (!(mgr?.WorldObject is Creature c) || !Bosses.TryGetValue(c.WeenieClassId, out var spec)) return true;
            if (spec.ScaleHealth)
                try { ScaleForFighter(spec, c, attacker); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: scaling {c.Name}: {e.Message}"); }
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

        // ------------------------------------------------------------------ health scales with the fighters
        /// <summary>Per boss life: his weenie's max (the solo size) and the players who have damaged him.</summary>
        sealed class Fighters { public uint BaseMax; public readonly HashSet<uint> Who = new HashSet<uint>(); }
        static readonly ConditionalWeakTable<Creature, Fighters> s_Fighters = new ConditionalWeakTable<Creature, Fighters>();
        internal const string P_SCALE_K = "rg_worldboss_scale_per_fighter", P_SCALE_CAP = "rg_worldboss_scale_cap";

        /// <summary>A player (or a player's pet) damaging him for the first time this life raises his max health to
        /// WorldBossScaling.TargetMax and his current health by the same amount (WorldBossScaling.cs has the formula).
        /// Runs on his landblock thread, from EmoteManager.OnDamage, which ACE calls after each hit has landed.</summary>
        static void ScaleForFighter(Spec s, Creature c, Creature attacker)
        {
            var who = attacker is Player p ? p : (attacker as CombatPet)?.P_PetOwner;
            if (who == null || c.IsDead) return;
            var f = s_Fighters.GetValue(c, _ => new Fighters { BaseMax = c.Health.MaxValue });
            int n;
            lock (f) { if (!f.Who.Add(who.Guid.Full)) return; n = f.Who.Count; }
            double k = PropertyManager.GetDouble(P_SCALE_K, WorldBossScaling.DEFAULT_PER_FIGHTER).Item;
            double cap = PropertyManager.GetDouble(P_SCALE_CAP, WorldBossScaling.DEFAULT_CAP).Item;
            uint oldMax = c.Health.MaxValue, newMax = WorldBossScaling.TargetMax(f.BaseMax, oldMax, n, k, cap);
            if (newMax <= oldMax) return;
            var (max, cur) = WorldBossScaling.Rescale(oldMax, c.Health.Current, newMax);
            c.Health.StartingValue += max - oldMax;   // MaxValue = StartingValue + Endurance/2 (+ enchantments)
            c.UpdateVital(c.Health, cur);
            Mod.Log.Info($"[RevivalGuard] WorldBoss: {c.Name} 0x{c.Guid.Full:X8}: fighter {n} ({who.Name}); max health {oldMax:N0} -> {c.Health.MaxValue:N0}, now {c.Health.Current:N0}");
        }

        static void PhaseExtras(Creature c, float windowMin)
        {
            if (c.WeenieClassId != BAEL)
            {
                // a seasonal boss's last phase: one rage, as Bael'Zharon's (his Damage Rating +30)
                if (Bosses.TryGetValue(c.WeenieClassId, out var sp) && sp.Enrage > 0 && windowMin > 0.15f && windowMin < 0.3f)
                    c.SetProperty(PropertyInt.DamageRating, (c.GetProperty(PropertyInt.DamageRating) ?? 0) + sp.Enrage);
                return;
            }
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
                if (!s.YearRound || DueAt(s) != null || FindAlive(s) != null) continue;   // seasonal bosses need the schedule
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
            if (!s.YearRound) { profile.NextAvailable = DateTime.UtcNow.AddDays(1); return; }   // no 24 h rule for a festival boss: schedule off, he never rises
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
        static bool Prefix(EmoteManager __instance, Creature attacker)
        {
            try { return WorldBoss.OnDamagePrefix(__instance, attacker); }
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
                    if (!s.YearRound) continue;   // a festival boss's lair is loaded when his window opens (WorldBossSchedule.EnsureSpawned)
                    LandblockManager.GetLandblock(new LandblockId((uint)s.Lair << 16 | 0xFFFF), false);
                    Mod.Log.Info($"[RevivalGuard] WorldBoss: lair 0x{s.Lair:X4} of {s.Name} loaded; {(WorldBoss.DueAt(s) is DateTime d ? $"he is due {d:yyyy-MM-dd HH:mm} UTC" : "he is due now")}");
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBoss: lair preload: {e.Message}"); }
        }
    }
}
