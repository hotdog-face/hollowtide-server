using System;
using System.Runtime.CompilerServices;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.Network.Structure;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// FALLS, TRACED (FallWatch, 7 days to 2026-10-24: 188 hurt landings). A PK's server body is simulated between reports
    /// (ACE FastTick) and each report only moves it, keeping its velocity. This records, per player, the client's own
    /// reports (AutonomousPosition / MoveToState, contact byte, z) and jumps, for FallWatch's line and FallGuard's rule.
    /// With rg_fall_trace on (default off; test shards only) it also logs every report, jump and moving server tick of
    /// PK players: the server body's spot, velocity and contact beside what the client said.
    /// </summary>
    static class FallTrace
    {
        internal const string P_TRACE = "rg_fall_trace";
        internal sealed class St
        {
            public DateTime AutoUtc, MtsUtc, JumpUtc;
            public bool AutoContact;
            public readonly FallGuardState Guard = new FallGuardState();
            public uint TraceFlags; public float TraceVz = float.NaN;
        }
        internal static readonly ConditionalWeakTable<Player, St> s_State = new ConditionalWeakTable<Player, St>();
        [ThreadStatic] internal static bool t_InReport;     // UpdatePlayerPosition is applying a client report

        internal static bool On => PropertyManager.GetBool(P_TRACE, false).Item;
        internal static double Now => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        static bool s_Registered;

        /// <summary>Known to @modifybool (ACE only modifies registered keys).</summary>
        internal static void Register()
        {
            if (s_Registered) return; s_Registered = true;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_TRACE)) b[P_TRACE] = new Property<bool>(false, "RevivalGuard FallTrace: log PK server bodies, reports and jumps (test shards only)");
                if (!b.ContainsKey(FallGuard.P_ON)) b[FallGuard.P_ON] = new Property<bool>(false, "RevivalGuard FallGuard: fall damage at most what the client's own reports show (off: ACE's, logged as a shadow)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FallTrace: switch not registered ({e.Message})"); }
        }

        internal static string Body(PhysicsObj po)
        {
            if (po == null) return "none";
            var o = po.Position.Frame.Origin; var v = po.Velocity;
            bool c = (po.TransientState & TransientStateFlags.Contact) != 0, w = (po.TransientState & TransientStateFlags.OnWalkable) != 0;
            return $"0x{po.Position.ObjCellID:X8} [{o.X:F2} {o.Y:F2} {o.Z:F2}] v=({v.X:F1},{v.Y:F1},{v.Z:F1}) c={(c ? 1 : 0)}{(w ? 1 : 0)}";
        }

        internal static void Report(Player p, ACE.Entity.Position pos, bool contact, bool autonomous)
        {
            var st = s_State.GetOrCreateValue(p);
            var now = DateTime.UtcNow;
            if (autonomous) { st.AutoUtc = now; st.AutoContact = contact; } else st.MtsUtc = now;
            if (On && p.IsPKType)
                Mod.Log.Info($"[RevivalGuard] FallTrace: {p.Name} {(autonomous ? "AUTOPOS" : "MTS")} contact={(contact ? 1 : 0)} "
                             + $"client {pos?.ToLOCString()} body {Body(p.PhysicsObj)}");
            if (pos != null && !p.Teleporting)
            {
                st.Guard.Report(Now, pos.PositionZ, autonomous && contact);
                FallGuard.AfterReport(p, st);
            }
        }
    }

    [HarmonyPatch(typeof(GameActionAutonomousPosition), nameof(GameActionAutonomousPosition.Handle))]
    static class FallTraceAutoPos
    {
        static bool Prepare() { FallTrace.Register(); return true; }
        static void Postfix(Session session)
        {
            try { var p = session?.Player; if (p != null) FallTrace.Report(p, p.RequestedLocation, p.LastContact, true); }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] FallTrace: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(GameActionMoveToState), nameof(GameActionMoveToState.Handle))]
    static class FallTraceMoveToState
    {
        static void Postfix(Session session)
        {
            try { var p = session?.Player; if (p != null && !p.PKLogout) FallTrace.Report(p, p.CurrentMoveToState?.Position, false, false); }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] FallTrace: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionJump))]
    static class FallTraceJump
    {
        static void Prefix(Player __instance, JumpPack jump)
        {
            try
            {
                var st = FallTrace.s_State.GetOrCreateValue(__instance);
                st.JumpUtc = DateTime.UtcNow;
                if (jump != null) st.Guard.Jump(FallTrace.Now, jump.Velocity.Z);
                if (FallTrace.On && __instance.IsPKType)
                    Mod.Log.Info($"[RevivalGuard] FallTrace: {__instance.Name} JUMP extent={jump?.Extent:F2} v=({jump?.Velocity.X:F1},{jump?.Velocity.Y:F1},{jump?.Velocity.Z:F1}) body {FallTrace.Body(__instance.PhysicsObj)}");
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] FallTrace: " + e.Message); }
        }
    }

    /// <summary>Marks the span in which UpdatePlayerPosition applies a client report (so a landing found there is a
    /// "report" landing, not a tick landing), and traces the body before and after.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    static class FallTraceUpdate
    {
        static void Prefix(Player __instance, ACE.Entity.Position newPosition, bool forceUpdate)
        {
            if (forceUpdate) return;
            FallTrace.t_InReport = true;
            try
            {
                if (FallTrace.On && __instance.IsPKType)
                    Mod.Log.Info($"[RevivalGuard] FallTrace: {__instance.Name} APPLY {newPosition?.ToLOCString()} body {FallTrace.Body(__instance.PhysicsObj)}");
            }
            catch { }
        }

        static void Postfix(Player __instance, bool forceUpdate)
        {
            if (forceUpdate) return;
            FallTrace.t_InReport = false;
            try
            {
                if (FallTrace.On && __instance.IsPKType)
                    Mod.Log.Info($"[RevivalGuard] FallTrace: {__instance.Name} APPLIED body {FallTrace.Body(__instance.PhysicsObj)}");
            }
            catch { }
        }

        static Exception Finalizer(Exception __exception, bool forceUpdate) { if (!forceUpdate) FallTrace.t_InReport = false; return __exception; }
    }

    /// <summary>Server ticks of a moving PK body (between reports), traced when the contact state or the vertical
    /// speed band changes, so a fall the client never made shows its whole path.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPhysics))]
    static class FallTraceTick
    {
        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance; var po = p.PhysicsObj;
                if (po == null || !p.IsPKType || !FallTrace.On) return;
                var st = FallTrace.s_State.GetOrCreateValue(p);
                uint f = (uint)po.TransientState & 0x3;
                float vz = po.Velocity.Z;
                if (f == st.TraceFlags && !float.IsNaN(st.TraceVz) && Math.Abs(vz - st.TraceVz) < 2f) return;
                st.TraceFlags = f; st.TraceVz = vz;
                Mod.Log.Info($"[RevivalGuard] FallTrace: {p.Name} TICK body {FallTrace.Body(po)}");
            }
            catch { }
        }
    }
}
