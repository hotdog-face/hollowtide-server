using System;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.Sequence;
using ACE.Server.Physics.Animation;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// MOVEGUARD: speed hacks and Blink-style hops that fit inside MovementGuard's budget (owner's pick, 2026-11-14).
    /// MovementGuard (Mod.cs) refills a bucket at run speed x slack and lets a full bucket out in one report, so a client can
    /// hop several seconds of running at once, again every time the bucket refills, or run steadily up to the slack. Those
    /// are what this catches: MoveGuardRule checks every accepted client position against the reports of the last 30 s and
    /// flags one no honest client could have reached from some earlier report in the time available (the why and the late-
    /// report handling are on MoveGuardRule). It judges only what MovementGuard has already accepted, and is called from it.
    ///
    /// rg_moveguard_mode: 0 off, 1 log (default: a line per flag, and a per-player summary every 15 min and at logout, for
    /// the owner to review before enforcing), 2 enforce (the report is refused and the player snapped back, as MovementGuard
    /// does). Staff are exempt unless rg_moveguard_staff is on (box tests drive admin characters). The thresholds are
    /// Tunables (moveguard.*), not published; the defaults here are the lenient side.
    ///
    /// Top speed is 4 m/s x ACE's run rate for the Run skill with NO burden (exhaustion and pack weight only slow a player),
    /// the highest in the last minute, so a buff that just ran out on the server, not yet on the client, never counts.
    /// Teleports, portals, recalls, deaths, snap-backs and any server move that bumps the teleport or force-position
    /// sequence start the track again from the server's position; knockbacks are credited (Knockback.cs).
    /// </summary>
    static class MoveGuard
    {
        internal const string P_MODE = "rg_moveguard_mode";
        internal const string P_STAFF = "rg_moveguard_staff";
        const double SUMMARY_SECONDS = 900;
        const int LOG_FIRST = 5, LOG_EVERY = 10;   // per summary period: the first flags, then every tenth

        static readonly DateTime s_Epoch = DateTime.UtcNow;
        static double Clock(DateTime t) => (t - s_Epoch).TotalSeconds;

        static MoveGuardParams Params() => new MoveGuardParams
        {
            Tol = Tunables.D("moveguard.tol", 1.25),
            Lateness = Tunables.D("moveguard.lateness", 2.0),
            Margin = Tunables.D("moveguard.margin", 6.0),
            Window = Tunables.D("moveguard.window", 30.0),
            Flush = Tunables.D("moveguard.flush", 3.0),
            Thin = Tunables.D("moveguard.thin", 0.5),
        };
        static MoveGuardParams s_Params;
        static MoveGuardParams P => s_Params ??= Params();

        internal static long Mode
        {
            get { try { return MoveGuardRule.Mode(PropertyManager.GetLong(P_MODE, MoveGuardRule.LOG).Item); } catch { return MoveGuardRule.LOG; } }
        }

        sealed class State
        {
            public readonly MoveTrack Track = new MoveTrack();
            public bool Pending = true; public double PendingSince = double.NaN;   // NaN: since the next report (login)
            public int Fs = -1, Ts = -1;                    // the force-position and teleport sequences at the last report
            public double Rate, RateAt;                     // the top run rate in the last minute
            public int Reports, Flags; public double Worst; public string WorstText; public double PeriodStart = double.NaN;
        }
        static readonly ConditionalWeakTable<Player, State> s_State = new ConditionalWeakTable<Player, State>();

        internal static void Register()
        {
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_MODE)) l[P_MODE] = new ACE.Server.Managers.Property<long>(MoveGuardRule.LOG,
                    "RevivalGuard MoveGuard: 0 off, 1 log flagged moves and a per-player summary, 2 refuse them and snap back");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {P_MODE}: not registered with @modifylong ({e.Message}); read with its default"); }
            PkCommon.RegisterBool(P_STAFF, false, "RevivalGuard MoveGuard: judge staff characters too (box tests)");
        }

        /// <summary>The server moved the player (teleport, snap, forced update): the next report starts a new track.</summary>
        internal static void Restart(Player p)
        {
            if (p == null) return;
            var st = s_State.GetOrCreateValue(p);
            st.Pending = true; st.PendingSince = Clock(DateTime.UtcNow);
        }

        internal static void Credit(Player p, double metres)
        {
            if (p != null && s_State.TryGetValue(p, out var st)) MoveGuardRule.Credit(st.Track, Clock(DateTime.UtcNow), metres);
        }

        static int Seq(Player p, SequenceType t)
        {
            try { var b = p.Sequences.GetCurrentSequence(t); return b != null && b.Length >= 2 ? BitConverter.ToUInt16(b, 0) : -1; }
            catch { return -1; }
        }

        static (double x, double y) Global(ACE.Entity.Position pos) =>
            (pos.LandblockId.LandblockX * 192.0 + pos.PositionX, pos.LandblockId.LandblockY * 192.0 + pos.PositionY);

        static double TopRate(Player p, State st, double now)
        {
            double r = 1.0;
            try { r = Math.Max(1.0, MovementSystem.GetRunRate(0f, (int)p.GetCreatureSkill(Skill.Run).Current, 1f)); } catch { }
            if (r >= st.Rate || now - st.RateAt > 60) { st.Rate = r; st.RateAt = now; }
            return st.Rate;
        }

        /// <summary>Called by MovementGuard for a report it is about to accept. False: refuse it (enforce mode only).</summary>
        internal static bool Allow(Player p, ACE.Entity.Position pos, DateTime at)
        {
            try
            {
                long mode = Mode;
                if (mode == MoveGuardRule.OFF || p == null || pos == null || p.Location == null) return true;
                if (p.Session != null && p.Session.AccessLevel > AccessLevel.Player && !PkCommon.On(P_STAFF)) return true;
                var st = s_State.GetOrCreateValue(p);
                double now = Clock(at);
                int fs = Seq(p, SequenceType.ObjectForcePosition), ts = Seq(p, SequenceType.ObjectTeleport);
                if (!st.Pending && (fs != st.Fs || ts != st.Ts)) { st.Pending = true; st.PendingSince = now; }
                if (st.Pending || st.Track.Empty)
                {
                    var (bx, by) = Global(p.Location);
                    MoveGuardRule.Reset(st.Track, double.IsNaN(st.PendingSince) ? now : Math.Min(now, st.PendingSince), bx, by, P);
                    st.Pending = false;
                }
                st.Fs = fs; st.Ts = ts;
                if (double.IsNaN(st.PeriodStart)) st.PeriodStart = now;

                double rate = TopRate(p, st, now);
                var (x, y) = Global(pos);
                var v = MoveGuardRule.Judge(st.Track, now, x, y, MoveGuardRule.BASE_RUN * rate, P);
                st.Reports++;
                if (v.Ratio > st.Worst)
                {
                    st.Worst = v.Ratio;
                    st.WorstText = $"{v.Dist:F1} m against {v.Allowed:F1} m allowed over {v.Span:F1} s at {pos.ToLOCString()}";
                }
                if (!v.Flag)
                {
                    MoveGuardRule.Accept(st.Track, now, x, y, P);
                    if (now - st.PeriodStart >= SUMMARY_SECONDS) Summary(p, st, "15 min");
                    return true;
                }

                st.Flags++;
                bool enforce = mode == MoveGuardRule.ENFORCE;
                string line = $"moved {v.Dist:F1} m from where it was {v.Span:F1} s earlier; {v.Allowed:F1} m allowed at run rate {rate:F2} "
                    + $"({v.Ratio:F2} of the limit), at {pos.ToLOCString()}";
                if (enforce) Offences.Add(p, "moveguard", line + $"; snapped back to {p.Location.ToLOCString()}");
                else if (st.Flags <= LOG_FIRST || st.Flags % LOG_EVERY == 0)
                    Mod.Log.Warn($"[RevivalGuard] moveguard (log only): {p.Name} (0x{p.Guid.Full:X8}, flag {st.Flags}) {line}");
                // one incident, not a chain: the track starts again where the player now is (enforce: where the server keeps him)
                if (enforce) { var (sx, sy) = Global(p.Location); MoveGuardRule.Reset(st.Track, now, sx, sy, P); }
                else MoveGuardRule.Reset(st.Track, now, x, y, P);
                return !enforce;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] moveguard: {e.Message}"); return true; }
        }

        static void Summary(Player p, State st, string why)
        {
            if (st.Reports > 0)
                Mod.Log.Info($"[RevivalGuard] moveguard summary ({why}, {(Mode == MoveGuardRule.ENFORCE ? "enforce" : Mode == MoveGuardRule.LOG ? "log only" : "off")}): "
                    + $"{p.Name} (0x{p.Guid.Full:X8}) {st.Reports} reports, {st.Flags} flagged, worst {st.Worst:F2} of the limit"
                    + (st.WorstText != null ? $" ({st.WorstText})" : "") + $", top run rate {st.Rate:F2}");
            st.Reports = 0; st.Flags = 0; st.Worst = 0; st.WorstText = null; st.PeriodStart = Clock(DateTime.UtcNow);
        }

        internal static void LoggingOut(Player p)
        {
            if (p != null && s_State.TryGetValue(p, out var st) && st.Reports > 0) Summary(p, st, "logout");
        }
    }

    /// <summary>The client leaves portal space: its first report is measured from the destination as of now, not from when
    /// the teleport began (a long portal-space wait would otherwise count as running time).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnTeleportComplete))]
    static class MoveGuard_TeleportDone
    {
        static void Postfix(Player __instance)
        {
            try { MoveGuard.Restart(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] moveguard teleport: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.LogOut_Inner))]
    static class MoveGuard_LogOut
    {
        static void Prefix(Player __instance)
        {
            try { MoveGuard.LoggingOut(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] moveguard logout: {e.Message}"); }
        }
    }
}
