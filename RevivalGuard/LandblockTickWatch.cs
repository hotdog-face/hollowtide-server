using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// SLOW LANDBLOCK TICKS, NAMED (2026-10-20: world-tick stalls of 1-4 s, rising, inside the landblock tick). Each
    /// landblock's TickMultiThreadedWork / TickSingleThreadedWork is timed; one over 500 ms logs the landblock, its
    /// phases (ACE reports each phase's time to ServerPerformanceMonitor.AddToCumulativeEvent, read here) and the
    /// slowest generator calls (GeneratorUpdate / GeneratorRegeneration). Fast ticks cost a few timestamps on the
    /// ticking thread (landblock groups tick in parallel, so the state is per thread). At most one line per
    /// landblock per 30 s, with a count of the ones held back.
    /// </summary>
    static class LandblockTickWatch
    {
        internal const double DEFAULT_SLOW_MS = 500;
        static double s_SlowMs = -1;   // tunables.json "ticks.slow_landblock_ms" (read once; a box test can lower it)
        internal static double SlowMs => s_SlowMs >= 0 ? s_SlowMs : (s_SlowMs = Tunables.D("ticks.slow_landblock_ms", DEFAULT_SLOW_MS));
        static double NoteMs => Math.Min(20, SlowMs / 10);   // a phase or generator call worth naming
        const double EVERY_S = 30;

        internal sealed class Frame
        {
            public long Start;
            public readonly List<(string phase, double ms)> Phases = new List<(string, double)>(8);
            public readonly List<(string what, double ms)> Gens = new List<(string, double)>(4);
            public int PhaseCalls, GenCalls;   // every phase report / generator call, named or not
            public double GenMs;               // all generator calls together
            public void Reset(long start) { Start = start; Phases.Clear(); Gens.Clear(); PhaseCalls = GenCalls = 0; GenMs = 0; }
        }

        [ThreadStatic] internal static Frame t_Frame;   // non-null only while a landblock tick runs on this thread
        [ThreadStatic] static Frame t_Spare;
        static readonly Dictionary<uint, (double last, int held)> s_Said = new Dictionary<uint, (double, int)>();

        internal static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

        internal static void Begin()
        {
            var f = t_Spare ??= new Frame();
            f.Reset(Stopwatch.GetTimestamp());
            t_Frame = f;
        }

        internal static void End(Landblock lb, string kind)
        {
            var f = t_Frame;
            t_Frame = null;
            if (f == null) return;
            double ms = Ms(f.Start);
            if (ms < SlowMs) return;
            try
            {
                uint id = lb?.Id.Raw ?? 0;
                double now = ACE.Common.Time.GetUnixTime();
                int held;
                lock (s_Said)
                {
                    s_Said.TryGetValue(id >> 16, out var s);
                    if (now - s.last < EVERY_S) { s_Said[id >> 16] = (s.last, s.held + 1); return; }
                    held = s.held;
                    s_Said[id >> 16] = (now, 0);
                }
                var sb = new StringBuilder(256);
                sb.Append($"[RevivalGuard] slow landblock tick: 0x{id >> 16:X4} {kind} {ms:F0} ms");
                int objs = -1;
                try { objs = lb?.GetAllWorldObjectsForDiagnostics().Count ?? -1; } catch { }
                if (objs >= 0) sb.Append($", {objs} objects");
                sb.Append("; phases ");
                // .NET may inline ServerPerformanceMonitor.AddToCumulativeEvent into the tick, and then no phase reaches here
                sb.Append(f.PhaseCalls == 0 ? "(not seen)" : f.Phases.Count == 0 ? $"(none over {NoteMs:F0} ms)"
                    : string.Join(", ", f.Phases.OrderByDescending(p => p.ms).Select(p => $"{p.phase} {p.ms:F0}")));
                if (f.GenCalls > 0) sb.Append($"; {f.GenCalls} generator calls {f.GenMs:F0} ms");
                if (f.Gens.Count > 0)
                    sb.Append("; slowest ").Append(string.Join(", ", f.Gens.OrderByDescending(g => g.ms).Take(3).Select(g => $"{g.what} {g.ms:F0} ms")));
                if (held > 0) sb.Append($" ({held} more slow ticks here since the last line)");
                Mod.Log.Warn(sb.ToString());
            }
            catch (Exception e) { TickGuard.Report("LandblockTickWatch", e); }
        }

        internal static void Phase(string phase, double seconds)
        {
            var f = t_Frame;
            if (f == null) return;
            f.PhaseCalls++;
            if (seconds * 1000 < NoteMs) return;
            f.Phases.Add((phase.StartsWith("Landblock_Tick_") ? phase.Substring(15) : phase, seconds * 1000));
        }

        internal static void Generator(WorldObject wo, string kind, long start)
        {
            var f = t_Frame;
            if (f == null || start == 0) return;
            double ms = Ms(start);
            f.GenCalls++; f.GenMs += ms;
            if (ms < NoteMs) return;
            try
            {
                string what = $"{kind} '{wo?.Name}' wcid {wo?.WeenieClassId} 0x{wo?.Guid.Full:X8}"
                    + $" ({wo?.GeneratorProfiles?.Count ?? 0} profiles, {(wo?.GeneratorProfiles != null ? wo.CurrentCreate : 0)} spawned)";
                if (f.Gens.Count < 8) f.Gens.Add((what, ms));
                else
                {
                    int min = 0;
                    for (int i = 1; i < f.Gens.Count; i++) if (f.Gens[i].ms < f.Gens[min].ms) min = i;
                    if (ms > f.Gens[min].ms) f.Gens[min] = (what, ms);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch]
    static class LandblockTickWatchMulti
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Landblock), nameof(Landblock.TickMultiThreadedWork), new[] { typeof(double) });
        static bool Prepare(MethodBase original)
        {
            bool ok = original != null || TargetMethod() != null;
            if (original == null)
                Mod.Log.Info(ok ? $"[RevivalGuard] landblock tick watch: ticks over {LandblockTickWatch.DEFAULT_SLOW_MS} ms (tunable ticks.slow_landblock_ms) are logged with phase and generators"
                                : "[RevivalGuard] landblock tick watch: Landblock.TickMultiThreadedWork not found, off");
            return ok;
        }
        static void Prefix() { try { LandblockTickWatch.Begin(); } catch { } }
        static void Postfix(Landblock __instance) { try { LandblockTickWatch.End(__instance, "multi"); } catch { } }
    }

    [HarmonyPatch]
    static class LandblockTickWatchSingle
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Landblock), nameof(Landblock.TickSingleThreadedWork), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix() { try { LandblockTickWatch.Begin(); } catch { } }
        static void Postfix(Landblock __instance) { try { LandblockTickWatch.End(__instance, "single"); } catch { } }
    }

    [HarmonyPatch]
    static class LandblockTickWatchPhase
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(ServerPerformanceMonitor), nameof(ServerPerformanceMonitor.AddToCumulativeEvent));
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix(ServerPerformanceMonitor.CumulativeEventHistoryType eventHistoryType, double seconds)
        {
            try { if (LandblockTickWatch.t_Frame != null) LandblockTickWatch.Phase(eventHistoryType.ToString(), seconds); } catch { }
        }
    }

    [HarmonyPatch]
    static class LandblockTickWatchGenUpdate
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(WorldObject), nameof(WorldObject.GeneratorUpdate), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix(out long __state) => __state = LandblockTickWatch.t_Frame != null ? Stopwatch.GetTimestamp() : 0;
        static void Postfix(WorldObject __instance, long __state) { try { LandblockTickWatch.Generator(__instance, "update", __state); } catch { } }
    }

    [HarmonyPatch]
    static class LandblockTickWatchGenRegen
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(WorldObject), nameof(WorldObject.GeneratorRegeneration), new[] { typeof(double) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix(out long __state) => __state = LandblockTickWatch.t_Frame != null ? Stopwatch.GetTimestamp() : 0;
        static void Postfix(WorldObject __instance, long __state) { try { LandblockTickWatch.Generator(__instance, "regen", __state); } catch { } }
    }
}
