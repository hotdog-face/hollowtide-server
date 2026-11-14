using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ONE BAD OBJECT CANNOT STOP THE WORLD (live crash 2026-11-14 04:09 UTC: a NullReferenceException on the world
    /// thread took the whole server down). ACE's world loop (WorldManager.UpdateWorld) has no catch: a throw from any
    /// object's tick, or an AggregateException out of the landblock Parallel.ForEach, ends the world thread and the
    /// process. Each per-object tick ACE runs is wrapped here:
    ///   Landblock.TickPhysics          -> WorldObject/Player.UpdateObjectPhysics          (parallel)
    ///   Landblock.TickMultiThreadedWork -> Creature.Monster_Tick, WorldObject.GeneratorUpdate,
    ///                                     GeneratorRegeneration, Decay                    (parallel)
    ///   Landblock.TickSingleThreadedWork -> Player.Player_Tick, every Heartbeat override  (world thread)
    /// A throw is logged (TickContainmentRule: once in full per object and kind, then paced), that object skips the rest
    /// of its tick, and its next tick time is pushed on so ACE's due-list loops cannot spin on it. Repeated throws
    /// quarantine it: an object leaves the tick lists (it stays in the world, inert; @reloadblock makes it afresh), a
    /// player is logged out cleanly. A last-resort catch on each landblock tick and PlayerManager.Tick covers what
    /// is not one object's (a landblock's action queue, its save). Switch rg_tick_containment (default on; off = stock,
    /// the throw ends the world). Counters and the quarantine list: @tickfaults. A debug throw for the box test:
    /// @tickfaults test, refused unless rg_tick_fault_test is on.
    /// </summary>
    static class TickContainment
    {
        internal const string P_ON = "rg_tick_containment";
        internal const string P_TEST = "rg_tick_fault_test";

        internal static readonly TickFaultLimits Limits = new TickFaultLimits();
        static readonly TickFaultBudget s_Budget = new TickFaultBudget();
        static readonly object s_Lock = new object();
        static readonly ConditionalWeakTable<object, TickFaultRecord> s_Records = new ConditionalWeakTable<object, TickFaultRecord>();

        // the switch, read at most every 2 s: the prefixes run for every object every tick
        static long s_SwitchAt = -10_000;   // not long.MinValue: now - MinValue overflows negative and the switch is never read
        static volatile bool s_On = true;
        internal static bool On
        {
            get
            {
                long t = Environment.TickCount64;
                if (t - Interlocked.Read(ref s_SwitchAt) > 2000)
                {
                    Interlocked.Exchange(ref s_SwitchAt, t);
                    try { s_On = PropertyManager.GetBool(P_ON, true).Item; } catch { }
                }
                return s_On;
            }
        }

        // counters (@tickfaults)
        static long s_Contained, s_Quarantines, s_PlayersOut, s_Backstop;
        static readonly ConcurrentDictionary<string, long> s_BySite = new ConcurrentDictionary<string, long>();
        static readonly DateTime s_Since = DateTime.UtcNow;

        internal sealed class Quarantine
        {
            public string Site, Kind, What;
            public DateTime At;
            public Landblock UnlistedFrom;   // the landblock whose creature/generator lists it was last taken out of
        }
        static readonly ConcurrentDictionary<WorldObject, Quarantine> s_Quarantined =
            new ConcurrentDictionary<WorldObject, Quarantine>(ReferenceEqualityComparer.Instance);
        static readonly ConcurrentQueue<WorldObject> s_ToUnlist = new ConcurrentQueue<WorldObject>();
        static volatile int s_QuarantinedCount;

        // the debug throw: object -> site
        static readonly ConcurrentDictionary<WorldObject, string> s_Test =
            new ConcurrentDictionary<WorldObject, string>(ReferenceEqualityComparer.Instance);
        static volatile int s_TestCount;

        internal sealed class TestThrow : InvalidOperationException
        {
            public TestThrow(string site) : base($"RevivalGuard tick containment test throw ({site})") { }
        }

        /// <summary>Every patched tick's prefix. True: run the tick. False: a quarantined object, skipped. Throws the
        /// armed test exception (inside the patched method, so its finalizer sees it like any other).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Gate(WorldObject wo, string site)
        {
            if (s_QuarantinedCount == 0 && s_TestCount == 0) return true;
            return GateSlow(wo, site);
        }

        static bool GateSlow(WorldObject wo, string site)
        {
            if (wo == null) return true;
            if (s_TestCount > 0 && s_Test.TryGetValue(wo, out var armed) && armed == site) throw new TestThrow(site);
            if (s_QuarantinedCount > 0 && s_Quarantined.TryGetValue(wo, out var q) && On)
            {
                if (!(wo is Player) && q.UnlistedFrom != wo.CurrentLandblock) s_ToUnlist.Enqueue(wo);   // moved landblock: unlist again
                return false;
            }
            return true;
        }

        /// <summary>Every patched tick's finalizer. Null: contained (the world goes on). The exception: stock behaviour
        /// (switch off), or a nested call whose outer tick contains it.</summary>
        internal static Exception Contain(Exception e, WorldObject wo, string site)
        {
            if (e == null) return null;
            if (!On) return e;
            try { Note(e, wo, site); } catch { }
            return null;
        }

        static void Note(Exception e, WorldObject wo, string site)
        {
            Interlocked.Increment(ref s_Contained);
            s_BySite.AddOrUpdate(site, 1, (_, n) => n + 1);
            if (wo == null) return;
            double now = ACE.Common.Time.GetUnixTime();
            string kind = site + ": " + e.GetType().Name;
            TickFaultVerdict v;
            int capHeld;
            lock (s_Lock)
            {
                var rec = s_Records.GetValue(wo, _ => new TickFaultRecord());
                v = rec.Record(kind, now, Limits, s_Budget);
                capHeld = v.Log == TickFaultLog.Silent ? 0 : s_Budget.HeldBack;
                if (v.Log != TickFaultLog.Silent) s_Budget.HeldBack = 0;
            }

            if (v.Quarantine) DoQuarantine(wo, site, kind);
            if (v.Log == TickFaultLog.Silent) return;

            string what = Describe(wo);
            string tail = $" (strike {v.Strikes}/{Limits.QuarantineAfter} in {Limits.WindowS:F0} s; {Interlocked.Read(ref s_Contained)} contained since start"
                        + (v.HeldBack > 0 ? $"; {v.HeldBack} more of this kind not logged" : "")
                        + (capHeld > 0 ? $"; {capHeld} other lines held back by the per-minute cap" : "") + ")";
            string outcome = !v.Quarantine ? "skipped this tick, the world goes on"
                           : wo is Player ? "QUARANTINED: out of the tick and logged out"
                           : "QUARANTINED: out of the tick (still in the world, inert)";
            if (v.Log == TickFaultLog.Full)
                Mod.Log.Warn($"[RevivalGuard] tick containment: {site} of {what} threw {e.GetType().Name}: {e.Message}; {outcome}{tail}", e);
            else
                Mod.Log.Warn($"[RevivalGuard] tick containment: {site} of {what} threw {e.GetType().Name} again: {e.Message}; {outcome}{tail}");
        }

        internal static string Describe(WorldObject wo)
        {
            string name = "?", lb = "?";
            uint guid = 0, wcid = 0;
            try { name = wo.Name; } catch { }
            try { guid = wo.Guid.Full; } catch { }
            try { wcid = wo.WeenieClassId; } catch { }
            try
            {
                var l = wo.CurrentLandblock;
                lb = l != null ? $"0x{l.Id.Landblock:X4}" : wo.Location != null ? $"0x{wo.Location.LandblockId.Landblock:X4} (not on it)" : "none";
            }
            catch { }
            return $"{(wo is Player ? "player " : "")}{name} (0x{guid:X8}, wcid {wcid}, landblock {lb})";
        }

        static void DoQuarantine(WorldObject wo, string site, string kind)
        {
            var q = new Quarantine { Site = site, Kind = kind, What = Describe(wo), At = DateTime.UtcNow };
            if (!s_Quarantined.TryAdd(wo, q)) return;
            Interlocked.Increment(ref s_Quarantines);
            s_QuarantinedCount = s_Quarantined.Count;
            s_Test.TryRemove(wo, out _);
            s_TestCount = s_Test.Count;

            if (wo is Player p)
            {
                // world thread: the physics and monster ticks that can get here run in parallel
                WorldManager.EnqueueAction(new ActionEventDelegate(() => LogOut(p, kind)));
                return;
            }
            // the sorted lists drop a MaxValue time on their next insert; the creature and generator lists go in the sweep
            wo.NextHeartbeatTime = double.MaxValue;
            wo.NextGeneratorRegenerationTime = double.MaxValue;
            s_ToUnlist.Enqueue(wo);
        }

        static readonly MethodInfo s_FinalizeLogout = AccessTools.DeclaredMethod(typeof(Player), "FinalizeLogout", Type.EmptyTypes);

        static void LogOut(Player p, string kind)
        {
            Interlocked.Increment(ref s_PlayersOut);
            try
            {
                p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                    "The server hit an error with your character and is logging you out to keep the world safe. Please log back in. Staff have been told.",
                    ChatMessageType.Broadcast));
            }
            catch { }
            try
            {
                if (p.Session != null) p.Session.LogOffPlayer(true);
                else { p.LogOut(true, true); }
                // ACE's logout ends with a chain on the player's own action queue, which Player_Tick runs, and a
                // quarantined player's ticks are skipped: finish it as ACE's forced logoff does (off the landblock,
                // saved, offline), so the client goes back to character select now, not after the 15 min fallback
                if (p.CurrentLandblock != null)
                {
                    if (s_FinalizeLogout != null) s_FinalizeLogout.Invoke(p, null);
                    else { p.ForcedLogOffRequested = true; p.ForceLogoff(); }
                }
                Mod.Log.Warn($"[RevivalGuard] tick containment: {Describe(p)} logged out ({kind})");
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] tick containment: clean logout of {Describe(p)} threw ({e.Message}); booting the session instead", e);
                try { p.Session?.Terminate(SessionTerminationReason.ForcedLogOffRequested, new GameMessageBootAccount(" because the server hit an error with this character")); }
                catch (Exception e2) { Mod.Log.Warn($"[RevivalGuard] tick containment: boot of {Describe(p)} threw too: {e2.Message}"); }
            }
        }

        // ---- the sweep: once per world tick, after the parallel landblock work (world thread) ----
        static readonly AccessTools.FieldRef<Landblock, LinkedList<Creature>> s_Creatures = Field<LinkedList<Creature>>("sortedCreaturesByNextTick");
        static readonly AccessTools.FieldRef<Landblock, LinkedList<WorldObject>> s_GenUpdates = Field<LinkedList<WorldObject>>("sortedGeneratorsByNextGeneratorUpdate");
        static readonly AccessTools.FieldRef<Landblock, LinkedList<WorldObject>> s_Heartbeats = Field<LinkedList<WorldObject>>("sortedWorldObjectsByNextHeartbeat");
        static readonly AccessTools.FieldRef<Landblock, LinkedList<WorldObject>> s_Regens = Field<LinkedList<WorldObject>>("sortedGeneratorsByNextRegeneration");
        static long s_PruneAt;

        static AccessTools.FieldRef<Landblock, T> Field<T>(string name)
        {
            try
            {
                if (AccessTools.DeclaredField(typeof(Landblock), name)?.FieldType == typeof(T)) return AccessTools.FieldRefAccess<Landblock, T>(name);
            }
            catch { }
            Mod.Log.Warn($"[RevivalGuard] tick containment: Landblock.{name} not found; a quarantined object stays in that list, skipped by its prefix");
            return null;
        }

        internal static void Sweep()
        {
            while (s_ToUnlist.TryDequeue(out var wo))
            {
                try
                {
                    if (!s_Quarantined.TryGetValue(wo, out var q)) continue;
                    var lb = wo.CurrentLandblock;
                    q.UnlistedFrom = lb;
                    if (lb == null) continue;
                    if (wo is Creature c && s_Creatures != null) s_Creatures(lb).Remove(c);
                    if (s_GenUpdates != null) s_GenUpdates(lb).Remove(wo);
                    if (s_Heartbeats != null) s_Heartbeats(lb).Remove(wo);
                    if (s_Regens != null) s_Regens(lb).Remove(wo);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] tick containment: unlisting {Describe(wo)}: {e.Message}"); }
            }
            long t = Environment.TickCount64;
            if (s_QuarantinedCount == 0 || t < s_PruneAt) return;
            s_PruneAt = t + 10_000;
            // forget objects that are gone and players whose session ended, so a fresh login ticks again
            foreach (var wo in s_Quarantined.Keys.ToList())
            {
                bool gone = wo.IsDestroyed || (wo is Player p ? PlayerManager.GetOnlinePlayer(p.Guid.Full) != p : wo.CurrentLandblock == null);
                if (gone) s_Quarantined.TryRemove(wo, out _);
            }
            s_QuarantinedCount = s_Quarantined.Count;
        }

        /// <summary>A landblock tick or PlayerManager.Tick threw past every per-object catch. Paced like an object's.</summary>
        internal static Exception Backstop(Exception e, object key, string site, string what)
        {
            if (e == null) return null;
            if (!On) return e;
            try
            {
                Interlocked.Increment(ref s_Backstop);
                Interlocked.Increment(ref s_Contained);
                s_BySite.AddOrUpdate(site, 1, (_, n) => n + 1);
                var inner = e is AggregateException ae && ae.InnerExceptions.Count == 1 ? ae.InnerExceptions[0] : e;
                TickFaultVerdict v;
                var never = new TickFaultLimits { QuarantineAfter = int.MaxValue, WindowS = Limits.WindowS, RepeatEveryS = Limits.RepeatEveryS, LinesPerMinute = Limits.LinesPerMinute };
                lock (s_Lock) v = s_Records.GetValue(key ?? s_Lock, _ => new TickFaultRecord()).Record(site + ": " + inner.GetType().Name, ACE.Common.Time.GetUnixTime(), never, s_Budget);
                if (v.Log == TickFaultLog.Full)
                    Mod.Log.Warn($"[RevivalGuard] tick containment: {site} of {what} threw {inner.GetType().Name}: {inner.Message}; the rest of its tick skipped, the world goes on ({Interlocked.Read(ref s_Contained)} contained since start)", inner);
                else if (v.Log == TickFaultLog.Summary)
                    Mod.Log.Warn($"[RevivalGuard] tick containment: {site} of {what} threw {inner.GetType().Name} again: {inner.Message} ({v.HeldBack} more not logged)");
            }
            catch { }
            return null;
        }

        // ---- @tickfaults ----
        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard TickContainment: an exception in one object's or player's tick is logged and that object skipped (quarantined after repeats) instead of stopping the server (false: ACE, the throw ends the world)");
            PkCommon.RegisterBool(P_TEST, false, "RevivalGuard TickContainment: allow @tickfaults test, a debug throw from one object's tick (test shards only)");
            CommandManager.TryAddCommand(Handle, "tickfaults", AccessLevel.Admin, CommandHandlerFlag.None,
                "Tick containment: contained exceptions, quarantined objects. 'test <site> <guid|pick|player name>' arms a debug throw (needs rg_tick_fault_test).",
                "[test heartbeat|monster|physics|generator|regen|decay|player <guid hex|pick|name>] [test clear]");
        }

        static readonly string[] SITES = { "heartbeat", "monster", "physics", "generator", "regen", "decay", "player" };

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
            Mod.Log.Info("[RevivalGuard] @tickfaults: " + text.Replace("\n", " | "));
        }

        static void Handle(Session session, params string[] args)
        {
            if (args.Length >= 1 && args[0].Equals("test", StringComparison.OrdinalIgnoreCase)) { Test(session, args.Skip(1).ToArray()); return; }
            var by = string.Join(", ", s_BySite.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));
            var lines = new List<string>
            {
                $"Tick containment {(On ? "ON" : "OFF (stock: a throw ends the world)")} since {s_Since:yyyy-MM-dd HH:mm} UTC: "
                + $"{Interlocked.Read(ref s_Contained)} contained ({(by.Length > 0 ? by : "none")}), {Interlocked.Read(ref s_Backstop)} by the landblock backstop, "
                + $"{Interlocked.Read(ref s_Quarantines)} quarantined, {Interlocked.Read(ref s_PlayersOut)} players logged out.",
            };
            foreach (var kv in s_Quarantined.Take(15))
                lines.Add($"  quarantined {kv.Value.At:HH:mm:ss} UTC: {kv.Value.What} ({kv.Value.Kind})");
            if (s_Quarantined.Count > 15) lines.Add($"  ... and {s_Quarantined.Count - 15} more");
            if (s_TestCount > 0) lines.Add($"  test throw armed on {s_TestCount} object(s)");
            Say(session, string.Join("\n", lines));
        }

        static void Test(Session session, string[] a)
        {
            if (a.Length >= 1 && a[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                s_Test.Clear(); s_TestCount = 0;
                Say(session, "Tick containment test: every test throw disarmed.");
                return;
            }
            if (!PropertyManager.GetBool(P_TEST, false).Item) { Say(session, $"Refused: {P_TEST} is off (test shards only: @modifybool {P_TEST} true)."); return; }
            if (a.Length < 2 || !SITES.Contains(a[0].ToLowerInvariant())) { Say(session, "Usage: @tickfaults test <" + string.Join("|", SITES) + "> <guid hex|pick|player name>"); return; }
            string site = a[0].ToLowerInvariant(), target = string.Join(" ", a.Skip(1));
            // on the world thread: the landblocks' object lists are only safe to walk there
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    var wo = Find(site, target);
                    if (wo == null) { Say(session, $"Tick containment test: no object for {site} '{target}'."); return; }
                    s_Test[wo] = Site(site);
                    s_TestCount = s_Test.Count;
                    Say(session, $"Tick containment test: {Site(site)} of {Describe(wo)} will throw on every call (containment {(On ? "ON" : "OFF")}).");
                }
                catch (Exception e) { Say(session, "Tick containment test failed: " + e.Message); }
            }));
        }

        static string Site(string word) => word switch
        {
            "heartbeat" => S_HEARTBEAT, "monster" => S_MONSTER, "physics" => S_PHYSICS, "generator" => S_GENUPDATE,
            "regen" => S_REGEN, "decay" => S_DECAY, _ => S_PLAYER,
        };

        static WorldObject Find(string site, string target)
        {
            if (site == "player") return PlayerManager.GetOnlinePlayer(target);
            var t = target.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? target.Substring(2) : target;
            var lbs = LandblockManager.GetLoadedLandblocks();
            if (uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var guid) && t.Length == 8)
            {
                foreach (var lb in lbs)
                {
                    var o = lb.GetObject(guid);
                    if (o != null) return o;
                }
                return null;
            }
            if (!target.Equals("pick", StringComparison.OrdinalIgnoreCase)) return null;
            // pick: the first fitting non-player object on an awake landblock, so the call really happens
            foreach (var lb in lbs.Where(l => !l.IsDormant).OrderBy(l => l.Id.Raw))
                foreach (var o in lb.GetAllWorldObjectsForDiagnostics().OrderBy(o => o.Guid.Full))
                {
                    if (o is Player || s_Quarantined.ContainsKey(o)) continue;
                    bool fits = site switch
                    {
                        "monster" => o is Creature c && !c.IsDead,
                        "heartbeat" => o.NextHeartbeatTime < double.MaxValue,
                        "generator" => o.IsGenerator && o.NextGeneratorUpdateTime < double.MaxValue,
                        "regen" => o.IsGenerator && o.NextGeneratorRegenerationTime < double.MaxValue,
                        "decay" => o.IsDecayable(),
                        _ => o.PhysicsObj != null && o.PhysicsObj.is_active(),
                    };
                    if (fits) return o;
                }
            return null;
        }

        internal const string S_HEARTBEAT = "heartbeat", S_MONSTER = "monster tick", S_PHYSICS = "physics",
            S_GENUPDATE = "generator update", S_REGEN = "generator regeneration", S_DECAY = "decay", S_PLAYER = "player tick";

        /// <summary>Every declared override of a virtual in ACE.Server's WorldObject tree (Heartbeat, UpdateObjectPhysics).</summary>
        internal static IEnumerable<MethodBase> Overrides(string name, Type[] args)
        {
            var list = new List<MethodBase>();
            try
            {
                foreach (var t in typeof(WorldObject).Assembly.GetTypes())
                {
                    if (t != typeof(WorldObject) && !t.IsSubclassOf(typeof(WorldObject))) continue;
                    var m = AccessTools.DeclaredMethod(t, name, args);
                    if (m != null && !m.IsAbstract) list.Add(m);
                }
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] tick containment: finding {name} overrides: {e.Message}"); }
            if (list.Count == 0) Mod.Log.Error($"[RevivalGuard] tick containment: no {name} found; that tick is not contained");
            return list;
        }

        internal static MethodBase One(Type t, string name, Type[] args)
        {
            var m = AccessTools.DeclaredMethod(t, name, args);
            if (m == null) Mod.Log.Error($"[RevivalGuard] tick containment: {t.Name}.{name} not found; that tick is not contained");
            return m;
        }
    }

    // ---- the per-object patches. Priority.First: the gate runs before any other prefix on these methods, and each
    // finalizer covers every prefix and postfix as well as ACE's own body. ----

    /// <summary>Heartbeat, every override. Overrides call base.Heartbeat, so only the outermost call contains (the
    /// depth count): the whole object skips its heartbeat, not just the base part.</summary>
    [HarmonyPatch]
    static class TickContainHeartbeat
    {
        [ThreadStatic] static int t_Depth;
        static IEnumerable<MethodBase> TargetMethods() => TickContainment.Overrides(nameof(WorldObject.Heartbeat), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TickContainment.Overrides(nameof(WorldObject.Heartbeat), new[] { typeof(double) }).Any();

        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorldObject __instance, out bool __state)
        {
            __state = true;
            t_Depth++;
            if (TickContainment.Gate(__instance, TickContainment.S_HEARTBEAT)) return true;
            __instance.NextHeartbeatTime = double.MaxValue;   // quarantined: off the heartbeat list at the re-insert
            return false;
        }

        static Exception Finalizer(Exception __exception, WorldObject __instance, double currentUnixTime, bool __state)
        {
            if (__state) t_Depth--;
            if (__exception == null || t_Depth > 0) return __exception;   // nested: the outer heartbeat decides
            var r = TickContainment.Contain(__exception, __instance, TickContainment.S_HEARTBEAT);
            if (r == null && __instance != null && __instance.NextHeartbeatTime <= currentUnixTime)   // else ACE's loop runs it again at once
                __instance.NextHeartbeatTime = currentUnixTime + Math.Max(1.0, __instance.HeartbeatInterval ?? 5.0);
            return r;
        }
    }

    [HarmonyPatch]
    static class TickContainPhysics
    {
        static IEnumerable<MethodBase> TargetMethods() => TickContainment.Overrides(nameof(WorldObject.UpdateObjectPhysics), Type.EmptyTypes);
        static bool Prepare(MethodBase original) => original != null || TickContainment.Overrides(nameof(WorldObject.UpdateObjectPhysics), Type.EmptyTypes).Any();

        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorldObject __instance, ref bool __result)
        {
            if (TickContainment.Gate(__instance, TickContainment.S_PHYSICS)) return true;
            __result = false;
            return false;
        }

        static Exception Finalizer(Exception __exception, WorldObject __instance, ref bool __result)
        {
            var r = TickContainment.Contain(__exception, __instance, TickContainment.S_PHYSICS);
            if (__exception != null && r == null) __result = false;   // no landblock change from a failed update
            return r;
        }
    }

    [HarmonyPatch]
    static class TickContainMonster
    {
        static MethodBase TargetMethod() => TickContainment.One(typeof(Creature), nameof(Creature.Monster_Tick), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        [HarmonyPriority(Priority.First)]
        static bool Prefix(Creature __instance, double currentUnixTime)
        {
            if (TickContainment.Gate(__instance, TickContainment.S_MONSTER)) return true;
            __instance.NextMonsterTickTime = currentUnixTime + 0.2;   // still in the list until the sweep: keep it not due
            return false;
        }

        static Exception Finalizer(Exception __exception, Creature __instance, double currentUnixTime)
        {
            var r = TickContainment.Contain(__exception, __instance, TickContainment.S_MONSTER);
            if (__exception != null && r == null && __instance != null && __instance.NextMonsterTickTime <= currentUnixTime)
                __instance.NextMonsterTickTime = currentUnixTime + 0.2;   // ACE's 0.2 s monsterTickInterval
            return r;
        }
    }

    [HarmonyPatch]
    static class TickContainGeneratorUpdate
    {
        static MethodBase TargetMethod() => TickContainment.One(typeof(WorldObject), nameof(WorldObject.GeneratorUpdate), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorldObject __instance, double currentUnixTime)
        {
            if (TickContainment.Gate(__instance, TickContainment.S_GENUPDATE)) return true;
            __instance.NextGeneratorUpdateTime = currentUnixTime + 5;
            return false;
        }

        static Exception Finalizer(Exception __exception, WorldObject __instance, double currentUnixTime)
        {
            var r = TickContainment.Contain(__exception, __instance, TickContainment.S_GENUPDATE);
            if (__exception != null && r == null && __instance != null && __instance.NextGeneratorUpdateTime <= currentUnixTime)
                __instance.NextGeneratorUpdateTime = currentUnixTime + 5;   // ACE's own GeneratorUpdate step
            return r;
        }
    }

    [HarmonyPatch]
    static class TickContainGeneratorRegeneration
    {
        static MethodBase TargetMethod() => TickContainment.One(typeof(WorldObject), nameof(WorldObject.GeneratorRegeneration), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorldObject __instance)
        {
            if (TickContainment.Gate(__instance, TickContainment.S_REGEN)) return true;
            __instance.NextGeneratorRegenerationTime = double.MaxValue;   // off the sorted list at the re-insert
            return false;
        }

        static Exception Finalizer(Exception __exception, WorldObject __instance, double currentUnixTime)
        {
            var r = TickContainment.Contain(__exception, __instance, TickContainment.S_REGEN);
            if (__exception != null && r == null && __instance != null && __instance.NextGeneratorRegenerationTime <= currentUnixTime)
                __instance.NextGeneratorRegenerationTime = currentUnixTime + Math.Max(5.0, __instance.RegenerationInterval);
            return r;
        }
    }

    [HarmonyPatch]
    static class TickContainDecay
    {
        static MethodBase TargetMethod() => TickContainment.One(typeof(WorldObject), nameof(WorldObject.Decay), new[] { typeof(TimeSpan) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorldObject __instance) => TickContainment.Gate(__instance, TickContainment.S_DECAY);

        static Exception Finalizer(Exception __exception, WorldObject __instance) => TickContainment.Contain(__exception, __instance, TickContainment.S_DECAY);
    }

    [HarmonyPatch]
    static class TickContainPlayerTick
    {
        static MethodBase TargetMethod() => TickContainment.One(typeof(Player), nameof(Player.Player_Tick), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        [HarmonyPriority(Priority.First)]
        static bool Prefix(Player __instance) => TickContainment.Gate(__instance, TickContainment.S_PLAYER);

        static Exception Finalizer(Exception __exception, Player __instance) => TickContainment.Contain(__exception, __instance, TickContainment.S_PLAYER);
    }

    // ---- the backstop: what escapes every per-object catch (a landblock's action queue or save) ----

    [HarmonyPatch]
    static class TickContainLandblock
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var (name, args) in new[]
            {
                ("TickPhysics", new[] { typeof(double), typeof(ConcurrentBag<WorldObject>) }),
                ("TickMultiThreadedWork", new[] { typeof(double) }),
                ("TickSingleThreadedWork", new[] { typeof(double) }),
            })
            {
                var m = TickContainment.One(typeof(Landblock), name, args);
                if (m != null) yield return m;
            }
        }

        static Exception Finalizer(Exception __exception, Landblock __instance, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            string id = "?";
            try { id = $"0x{__instance.Id.Landblock:X4}"; } catch { }
            return TickContainment.Backstop(__exception, __instance, "landblock " + __originalMethod?.Name, "landblock " + id);
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class TickContainPlayerManager
    {
        static Exception Finalizer(Exception __exception) => TickContainment.Backstop(__exception, typeof(PlayerManager), "PlayerManager.Tick", "the logoff queues");
    }

    /// <summary>After the landblock work each world tick: quarantined objects out of the creature and generator lists.</summary>
    [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.UpdateGameWorld))]
    static class TickContainSweep
    {
        static void Postfix()
        {
            try { TickContainment.Sweep(); }
            catch (Exception e) { TickGuard.Report("TickContainment sweep", e); }
        }
    }
}
