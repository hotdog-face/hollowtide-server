using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE ARRIVAL A CLIENT NEVER CONFIRMS ("stuck in portal space", pink bubbles).
    ///
    /// ACE sets Teleporting on every teleport, portal and login, and only the client's LoginComplete ends it
    /// (OnTeleportComplete). A client that stays connected but never sends it is held hidden and unable to act;
    /// TeleportWatchdog completes it after rg_tele_arrive_seconds (default 120 s, the PK-safe wait of ticket 261103-0036).
    /// This adds what the watchdog cannot see (rules in TeleportArrivalRule, tested in tools/nettest):
    ///   (a) rg_tele_move_arrive: the client walking the destination is proof it left portal space, so the arrival
    ///       completes then, not two minutes later. Gated on the body having moved (rg_tele_move_min_cm), the teleport
    ///       sequence and the landblock, because our client relays held keys inside the tunnel.
    ///   (c) rg_tele_latch: the arrival side effects run once per teleport, whichever of the client, the movement
    ///       arm or the watchdog gets there first, and however many re-check chains were started.
    ///   (d) rg_tele_diag: a WARN at 10 s and 30 s for a pending teleport saying whether packets are being lost, the
    ///       client has stalled, or it is alive and acking but never sent LoginComplete; and a line when a player
    ///       abandons a pending teleport by logging out.
    /// </summary>
    public static class TeleportArrival
    {
        internal const string P_MOVE = "rg_tele_move_arrive", P_LATCH = "rg_tele_latch", P_DIAG = "rg_tele_diag";
        internal const string P_GRACE = "rg_tele_move_grace_seconds", P_MIN_CM = "rg_tele_move_min_cm", P_WAIT = "rg_tele_arrive_seconds";
        const long DEF_GRACE = 3, DEF_MIN_CM = 100, DEF_WAIT = 120;
        const double WARN_FIRST = 10, WARN_SECOND = 30, QUIET = 5;

        // Read on the world thread every half second (ProcessPacket runs per packet on the network thread).
        static volatile bool s_Move = true, s_Latch = true, s_Diag = true;
        static double s_Grace = DEF_GRACE, s_MinMeters = DEF_MIN_CM / 100.0, s_Wait = DEF_WAIT;
        static int s_Registered;

        /// <summary>(b) TeleportWatchdog's wait for a client that never said its portal space ended.</summary>
        internal static double ClientWaitSeconds(double floor) => TeleportArrivalRule.ClientWait(s_Wait, floor);

        /// <summary>Per pending teleport: its start stamp, the traffic baseline and what has been logged.</summary>
        sealed class Pending
        {
            public double Start, ReportAt, AckAdvancedAt;
            public int NakBase, Warned;
            public ushort ReportSeq;
            public bool HasReport, Requested;
        }
        static readonly ConcurrentDictionary<uint, Pending> s_Pending = new ConcurrentDictionary<uint, Pending>();
        /// <summary>(c) guid -> the start stamp of the teleport that last arrived.</summary>
        static readonly ConcurrentDictionary<uint, double> s_Arrived = new ConcurrentDictionary<uint, double>();

        /// <summary>Why the next logout is happening, when RevivalGuard forces it (world thread, read in LogOut_Inner).</summary>
        [ThreadStatic] internal static string t_ForceWhy;

        internal static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            ExpansionMotions.RegisterProperty(P_MOVE, true, "RevivalGuard TeleportArrival: a client walking the destination completes a teleport whose LoginComplete never came");
            ExpansionMotions.RegisterProperty(P_LATCH, true, "RevivalGuard TeleportArrival: a teleport's arrival side effects run once, whoever completes it");
            ExpansionMotions.RegisterProperty(P_DIAG, true, "RevivalGuard TeleportArrival: WARN at 10 s and 30 s for a pending teleport (lost packets / client stall / no LoginComplete)");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                void Add(string k, long d, string desc) { if (!l.ContainsKey(k)) l[k] = new Property<long>(d, desc); }
                Add(P_GRACE, DEF_GRACE, "RevivalGuard TeleportArrival: seconds after a teleport starts before movement can complete it");
                Add(P_MIN_CM, DEF_MIN_CM, "RevivalGuard TeleportArrival: centimetres the client must have moved from the arrival point (proves it left portal space)");
                Add(P_WAIT, DEF_WAIT, "RevivalGuard TeleportWatchdog: seconds a client that never sent LoginComplete is left before the server completes the arrival (min 20)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TeleportArrival: long properties not registered ({e.Message}); defaults used"); }
        }

        static void Refresh()
        {
            try
            {
                s_Move = PropertyManager.GetBool(P_MOVE, true).Item;
                s_Latch = PropertyManager.GetBool(P_LATCH, true).Item;
                s_Diag = PropertyManager.GetBool(P_DIAG, true).Item;
                s_Grace = Math.Max(0, PropertyManager.GetLong(P_GRACE, DEF_GRACE).Item);
                s_MinMeters = Math.Max(0, PropertyManager.GetLong(P_MIN_CM, DEF_MIN_CM).Item) / 100.0;
                s_Wait = PropertyManager.GetLong(P_WAIT, DEF_WAIT).Item;
            }
            catch { }
        }

        static string Who(Player p) => $"{p.Name} (0x{p.Guid.Full:X8})";

        static string Kind(Player p, double start)
            => p.LoginTimestamp == start ? "login" : p.LastPortalTeleportTimestamp == start ? "portal" : "teleport";

        static ushort ServerTeleSeq(Player p)
        {
            var b = p.Sequences.GetCurrentSequence(SequenceType.ObjectTeleport);
            return b != null && b.Length >= 2 ? BitConverter.ToUInt16(b, 0) : (ushort)0;
        }

        static Pending Track(Player p, double start)
        {
            var guid = p.Guid.Full;
            var e = s_Pending.GetOrAdd(guid, _ => new Pending());
            if (e.Start != start)
            {
                var net = NetStats.For(p.Session?.Network);
                e.Start = start; e.Warned = 0; e.HasReport = false; e.Requested = false;
                e.NakBase = net?.Naks ?? 0;
                e.AckAdvancedAt = start;
            }
            return e;
        }

        /// <summary>Completes the arrival on the client's behalf: what GameActionLoginComplete would have done.</summary>
        internal static void Complete(Player p, string source, string detail)
        {
            double start = p.LastTeleportStartTimestamp ?? 0;
            if (start > 0) Track(p, start).Requested = true;
            Mod.Log.Warn($"[RevivalGuard] teleport arrival: {Who(p)} completed by {source} "
                + $"{ACE.Common.Time.GetUnixTime() - start:F1}s into its {Kind(p, start)} without the client's LoginComplete; {detail}");
            p.OnTeleportComplete();
            if (!p.FirstEnterWorldDone)
            {
                p.FirstEnterWorldDone = true;
                p.SendPropertyUpdatesAndOverrides();
            }
        }

        /// <summary>The arrival was already asked for (the client's LoginComplete or one of ours); only ACE's landblock
        /// re-check chain can still be holding it.</summary>
        internal static bool Requested(Player p, double start)
            => s_Pending.TryGetValue(p.Guid.Full, out var e) && e.Start == start && e.Requested;

        /// <summary>A position report from the client while it is Teleporting (MoveToState or AutonomousPosition).</summary>
        internal static void OnClientReport(Player p, Position pos, ushort teleSeq)
        {
            if (p == null || pos == null || !p.Teleporting || p.IsLoggingOut || p.PKLogout) return;
            double start = p.LastTeleportStartTimestamp ?? 0;
            if (start <= 0) return;
            var now = ACE.Common.Time.GetUnixTime();
            var e = Track(p, start);
            e.ReportAt = now; e.ReportSeq = teleSeq; e.HasReport = true;
            if (!s_Move) return;

            bool clientSaidDone = TeleportWatchdog.s_ClientArrived.TryGetValue(p.Guid.Full, out var at) && at >= start;
            var rs = p.LastMoveToState?.RawMotionState;
            bool movement = rs != null && TeleportArrivalRule.IsMovement((uint)rs.ForwardCommand, (uint)rs.SidestepCommand);
            var dest = p.Location;
            bool sameLb = dest != null && pos.Landblock == dest.Landblock;
            bool seqOk = teleSeq == ServerTeleSeq(p);
            double moved = sameLb ? dest.DistanceTo(pos) : 0;
            if (!TeleportArrivalRule.MoveArrives(true, clientSaidDone || e.Requested, now - start, s_Grace, movement, sameLb, seqOk, moved, s_MinMeters))
                return;
            Complete(p, "the client's own movement", $"it walked {moved:F1} m from the arrival point {dest} (teleport seq {teleSeq})");
        }

        // ------------------------------------------------------------------------------------------- (c) the latch
        /// <summary>Prefix of OnTeleportComplete: skip a second arrival for a teleport that already arrived. Returns the
        /// start stamp to latch when this call will arrive (not re-queue for an unloaded landblock), else 0.</summary>
        internal static bool LatchPrefix(Player p, out double latch)
        {
            latch = 0;
            if (p == null) return true;
            double start = p.LastTeleportStartTimestamp ?? 0;
            s_Arrived.TryGetValue(p.Guid.Full, out var done);
            if (TeleportArrivalRule.AlreadyArrived(s_Latch, done, start, p.Teleporting))
            {
                Mod.Log.Info($"[RevivalGuard] teleport arrival: {Who(p)} second completion of the same {Kind(p, start)} skipped "
                    + $"(arrival already ran; a late LoginComplete or a second re-check chain)");
                return false;
            }
            var lb = p.CurrentLandblock;
            if (lb == null || lb.CreateWorldObjectsCompleted) latch = start;   // ACE re-queues itself otherwise
            return true;
        }

        internal static void LatchPostfix(Player p, double latch)
        {
            if (p != null && latch > 0) s_Arrived[p.Guid.Full] = latch;
        }

        // ------------------------------------------------------------------------------------------- (d) diagnostics
        internal static void Tick()
        {
            Refresh();
            var now = ACE.Common.Time.GetUnixTime();
            var online = PlayerManager.GetAllOnline();
            foreach (var p in online)
            {
                if (p == null || !p.Teleporting || p.IsLoggingOut || p.PKLogout) continue;
                double start = p.LastTeleportStartTimestamp ?? 0;
                if (start <= 0) continue;
                var e = Track(p, start);
                if (!s_Diag) continue;
                int due = TeleportArrivalRule.WarnDue(now - start, e.Warned, WARN_FIRST, WARN_SECOND);
                if (due == 0) continue;
                e.Warned = due;
                Mod.Log.Warn($"[RevivalGuard] teleport pending: {Describe(p, e, now)}");
            }
            // forget players who logged off
            if (s_Pending.Count > 0 || s_Arrived.Count > 0)
            {
                var ids = online.Where(p => p != null).Select(p => p.Guid.Full).ToHashSet();
                foreach (var g in s_Pending.Keys) if (!ids.Contains(g)) s_Pending.TryRemove(g, out _);
                foreach (var g in s_Arrived.Keys) if (!ids.Contains(g)) s_Arrived.TryRemove(g, out _);
            }
        }

        /// <summary>One line of fields that tell a lost-packet, a stalled client and a silent-but-alive client apart.</summary>
        static string Describe(Player p, Pending e, double now)
        {
            double start = e.Start, age = now - start;
            bool clientSaidDone = TeleportWatchdog.s_ClientArrived.TryGetValue(p.Guid.Full, out var at) && at >= start;
            var net = p.Session?.Network;
            var st = NetStats.For(net);
            double sinceHeard = -1;
            try
            {
                if (net != null)
                    sinceHeard = (DateTime.UtcNow - new DateTime(net.TimeoutTick, DateTimeKind.Utc)).TotalSeconds + ACE.Server.Network.Managers.NetworkManager.DefaultSessionTimeout;
            }
            catch { }
            int naks = st == null ? 0 : st.Naks - e.NakBase;
            long sent = 0, unacked = 0; double sinceAck = age;
            try { sent = net?.ConnectionData.PacketSequence.CurrentValue ?? 0; } catch { }
            if (st != null)
            {
                unacked = Math.Max(0, sent - st.LastAck);
                double advanced = st.AckAdvancedAt;
                sinceAck = now - Math.Max(advanced, start);
            }
            string verdict = clientSaidDone ? "landblock-wait"
                : TeleportArrivalRule.Verdict(sinceHeard < 0 ? 0 : sinceHeard, naks, unacked, sinceAck, QUIET);
            var lb = p.CurrentLandblock;
            string lbState = lb == null ? "none" : lb.CreateWorldObjectsCompleted ? "loaded" : "loading";
            string report = e.HasReport ? $"{now - e.ReportAt:F1}s ago seq={e.ReportSeq}" : "none";
            return $"{Who(p)} {Kind(p, start)} {age:F0}s verdict={verdict} heard={sinceHeard:F1}s resendAsks={naks} "
                + $"unacked={unacked} ackAdvanced={sinceAck:F1}s clientReport={report} serverTeleSeq={ServerTeleSeq(p)} "
                + $"loginComplete={(clientSaidDone ? "yes" : "no")} landblock={lbState} dest={p.Location}";
        }

        internal static void OnLogout(Player p, bool dropped)
        {
            if (p == null || !p.Teleporting) return;
            double start = p.LastTeleportStartTimestamp ?? 0;
            if (start <= 0) return;
            var now = ACE.Common.Time.GetUnixTime();
            var e = Track(p, start);
            string why = t_ForceWhy ?? (dropped ? "the connection dropped" : "logged out");
            Mod.Log.Info($"[RevivalGuard] teleport abandoned ({why}, still pending): {Describe(p, e, now)}");
        }

        /// <summary>Per-connection traffic the diagnostics read: resend requests and acks (network thread writes).</summary>
        internal sealed class NetStats
        {
            static readonly ConditionalWeakTable<NetworkSession, NetStats> s_Table = new ConditionalWeakTable<NetworkSession, NetStats>();
            public int Naks;
            public long LastAck;
            public double AckAdvancedAt;

            internal static NetStats For(NetworkSession n) => n == null ? null : s_Table.GetValue(n, _ => new NetStats());

            internal static void Seen(NetworkSession n, ClientPacket packet)
            {
                if (!s_Diag || n == null || packet == null) return;
                var flags = packet.Header.Flags;
                bool nak = (flags & PacketHeaderFlags.RequestRetransmit) != 0, ack = (flags & PacketHeaderFlags.AckSequence) != 0;
                if (!nak && !ack) return;
                var s = For(n);
                if (nak) Interlocked.Increment(ref s.Naks);
                if (ack)
                {
                    long a = packet.HeaderOptional.AckSequence;
                    if (a > Interlocked.Read(ref s.LastAck)) { Interlocked.Exchange(ref s.LastAck, a); s.AckAdvancedAt = ACE.Common.Time.GetUnixTime(); }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnTeleportComplete))]
    static class TeleportArrivalLatch
    {
        static bool Prepare() { TeleportArrival.Register(); return true; }

        static bool Prefix(Player __instance, out double __state)
        {
            __state = 0;
            try { return TeleportArrival.LatchPrefix(__instance, out __state); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TeleportArrivalLatch: {e.Message}"); return true; }
        }

        static void Postfix(Player __instance, double __state)
        {
            try { TeleportArrival.LatchPostfix(__instance, __state); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TeleportArrivalLatch post: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameActionMoveToState), nameof(GameActionMoveToState.Handle))]
    static class TeleportArrivalMove
    {
        static void Postfix(Session session)
        {
            try
            {
                var p = session?.Player;
                var m = p?.CurrentMoveToState;
                if (p != null && m != null && p.Teleporting) TeleportArrival.OnClientReport(p, m.Position, m.TeleportSequence);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TeleportArrivalMove: {e.Message}"); }
        }
    }

    /// <summary>ACE ignores AutonomousPosition while Teleporting; a held key sends no new MoveToState, so this peeks the
    /// position (and puts the stream back) to see the client walking.</summary>
    [HarmonyPatch(typeof(GameActionAutonomousPosition), nameof(GameActionAutonomousPosition.Handle))]
    static class TeleportArrivalAutoPos
    {
        static void Prefix(ClientMessage message, Session session)
        {
            var p = session?.Player;
            if (p == null || !p.Teleporting || message?.Payload == null) return;
            var s = message.Payload.BaseStream;
            long at = s.Position;
            Position pos = null; ushort tele = 0;
            try
            {
                pos = new Position(message.Payload);
                message.Payload.ReadUInt16(); message.Payload.ReadUInt16();
                tele = message.Payload.ReadUInt16();
            }
            catch { pos = null; }
            finally { s.Position = at; }
            try { if (pos != null) TeleportArrival.OnClientReport(p, pos, tele); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TeleportArrivalAutoPos: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class TeleportArrivalWatch
    {
        static double s_Next;

        static void Postfix()
        {
            var t = ACE.Common.Time.GetUnixTime();
            if (t < s_Next) return;
            s_Next = t + 0.5;
            try { TeleportArrival.Tick(); }
            catch (Exception e) { TickGuard.Report("TeleportArrival", e); }
        }
    }

    [HarmonyPatch(typeof(NetworkSession), nameof(NetworkSession.ProcessPacket))]
    static class TeleportArrivalNet
    {
        static void Prefix(NetworkSession __instance, ClientPacket packet)
        {
            try { TeleportArrival.NetStats.Seen(__instance, packet); } catch { }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.LogOut_Inner))]
    static class TeleportArrivalLogout
    {
        static void Prefix(Player __instance, bool clientSessionTerminatedAbruptly)
        {
            try { TeleportArrival.OnLogout(__instance, clientSessionTerminatedAbruptly); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TeleportArrivalLogout: {e.Message}"); }
        }
    }
}
