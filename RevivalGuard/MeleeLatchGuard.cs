using System.Collections.Generic;
using System.Linq;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A MELEE ATTACK THAT NEVER SWINGS AND BLOCKS EVERY LATER ONE (ticket 261114-0019, Money Maker: "I stop attacking on
    /// the macro and I can't attack again until I tap space to jump"). ACE's HandleActionTargetedMeleeAttack sets
    /// MeleeTarget, then reaches the target through CreateMoveToChain2 (a FastTick move-to or turn-to with no timeout), and
    /// MoveToManager.BeginTurnToHeading does nothing while the body IsAnimating. A body whose motion queue stopped moving
    /// (MotionQueueWatchdog) never turns, so the callback never comes, MeleeTarget stays set on a live monster, and every
    /// later attack request returns at "already in melee loop?" with no AttackDone: the Assistant re-sends to nothing. A
    /// jump frees it (HandleActionJump clears the link animations and calls StopExistingMoveToChains2).
    /// MeleeLatchRule decides: latched (live target, no swing, move-to pending, body still) for melee.latch_seconds
    /// (default 4; a turn in reach takes about 1 s) and the guard finishes it: a queue that did not move during the latch
    /// is drained (MotionQueueWatchdog.Finish), the move-to is cancelled and its callback answered false, which is ACE's
    /// OnAttackDone (AttackDone to the client, MeleeTarget cleared), so the next request starts a fresh attack. One log
    /// line per latch. rg_melee_latch_mode: 0 off, 1 log only (default), 2 log and finish.
    /// NOT SEEN YET (shard2, 2026-11-14): with a stuck head forced into a PK body's queue (@stuckqueuetest hard), its
    /// melee move-to still reached the target and swung, as the client reports its own positions; eating or a stance
    /// change mid-swing, and a jump mid-swing, never stuck the queue either. So it ships logging: the line (and the
    /// ticket's server_busy "melee on" part) shows whether this latch is what the next "stops attacking" report hit.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class MeleeLatchGuard
    {
        internal const string P_MODE = "rg_melee_latch_mode";
        static double LIMIT => Tunables.D("melee.latch_seconds", 4.0);
        static double s_Next;

        sealed class Seen { public int Seq = -1; public double Since; public object Head; public uint Counter; public bool Logged; }
        static readonly Dictionary<uint, Seen> s_Seen = new Dictionary<uint, Seen>();

        internal static long Mode
        {
            get { try { return MeleeLatchRule.Mode(PropertyManager.GetLong(P_MODE, MeleeLatchRule.LOG).Item); } catch { return MeleeLatchRule.LOG; } }
        }

        internal static void Register()
        {
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_MODE)) l[P_MODE] = new ACE.Server.Managers.Property<long>(MeleeLatchRule.LOG,
                    "RevivalGuard MeleeLatchGuard: 0 off, 1 log a melee attack latched on a move-to that never ends, 2 log and finish it");
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] {P_MODE}: not registered with @modifylong ({e.Message}); read with its default"); }
        }

        static bool s_Registered;
        static bool Prepare()
        {
            if (!s_Registered)
            {
                s_Registered = true;
                ACE.Server.Command.CommandManager.TryAddCommand(StuckQueueTest, "stuckqueuetest", ACE.Entity.Enum.AccessLevel.Developer,
                    ACE.Server.Command.CommandHandlerFlag.RequiresWorld,
                    "Admin test aid (ours, not retail): your server body's motion queue gets the stuck head seen in ticket 261114-0019 (SlashMed/1 with no animation left to finish it), so the next melee move-to never starts. \"hard\": a head no later animation can finish; \"state\": show the melee and move-to state. A name picks another online player. Jump, the guard or the motion queue watchdog clears it.", "[hard|state] [player name]");
            }
            return true;
        }

        /// <summary>@stuckqueuetest: the live state of 261114-0019 on demand: the body's link animations gone (as
        /// HandleActionJump leaves them) and a SlashMed entry still pending at the head with nothing to finish it.</summary>
        static void StuckQueueTest(ACE.Server.Network.Session session, params string[] parameters)
        {
            var args = (parameters ?? new string[0]).ToList();
            string verb = args.Count > 0 && (args[0] == "hard" || args[0] == "state") ? args[0] : "";
            if (verb != "") args.RemoveAt(0);
            // a name picks another online player (admins are not PK, so not FastTick: test on a Player-level PK character)
            var p = args.Count > 0 ? PlayerManager.GetOnlinePlayer(string.Join(" ", args)) : session?.Player;
            void Say(string m) { if (session?.Player != null) session.Player.SendMessage(m); else System.Console.WriteLine(m); }
            var po = p?.PhysicsObj;
            var mtm = po?.PartArray?.MotionTableManager;
            if (mtm == null) { Say("[stuckqueuetest] no such player online"); return; }
            if (verb == "state")
            {
                Say($"[stuckqueuetest] {p.Name}: melee {p.MeleeTarget?.Name ?? "none"} seq {p.AttackSequence} swinging {p.Attacking}, moving to {p.IsPlayerMovingTo2}, "
                    + $"callback {p.MoveToParams?.Callback != null}, chain {p.IsPlayerMovingTo}, charging {p.IsMoving}, restart pending {MeleeRestart.Pending(p)}, velocity {po.Velocity.Length():0.00}, fast tick {p.FastTick}, latched {IsLatched(p)}; {BusyLedger.Describe(p)}");
                return;
            }
            // "hard": a head no later animation can finish (box 2026-11-14: a SlashMed/1 head was finished by the next link)
            bool hard = verb == "hard";
            po.RemoveLinkAnimations();
            // both halves: the table's entry (nothing left to finish it) and the interpreter's pending motion, which is what
            // IsAnimating reads (MotionInterp.add_to_queue / MotionDone)
            mtm.PendingAnimations.AddFirst(new ACE.Server.Physics.Animation.AnimNode((uint)ACE.Entity.Enum.MotionCommand.SlashMed, hard ? 1000u : 1u));
            po.get_minterp().add_to_queue(0, (uint)ACE.Entity.Enum.MotionCommand.SlashMed, ACE.Entity.Enum.WeenieError.None);
            var b = MotionQueueWatchdog.Snapshot(p);
            Say($"[stuckqueuetest] {p.Name}: body queue {b.count}, counter {b.counter}, animating {po.IsAnimating}, interp pending {po.get_minterp().PendingMotions.Count}");
            Mod.Log.Warn($"[RevivalGuard] stuckqueuetest: {p.Name} body queue {b.count} (SlashMed/{(hard ? 1000 : 1)} head), counter {b.counter}");
        }

        static void Postfix()
        {
            try { Run(); }
            catch (System.Exception e) { TickGuard.Report("MeleeLatchGuard", e); }
        }

        static bool IsLatched(Player p)
        {
            var po = p.PhysicsObj;
            if (po == null || p.Teleporting || p.IsDead) return false;
            var t = p.MeleeTarget;
            bool pending = p.IsPlayerMovingTo2 || p.MoveToParams?.Callback != null;
            bool moving = po.Velocity != System.Numerics.Vector3.Zero;
            return MeleeLatchRule.Latched(t != null && t.IsAlive, p.Attacking, pending, moving);
        }

        static void Run()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 0.5;
            long mode = Mode;
            if (mode == MeleeLatchRule.OFF) { s_Seen.Clear(); return; }
            var live = new HashSet<uint>();
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                if (p == null || !p.FastTick || p.MeleeTarget == null) continue;
                uint guid = p.Guid.Full;
                live.Add(guid);
                if (!s_Seen.TryGetValue(guid, out var s)) s_Seen[guid] = s = new Seen { Since = now };
                int seq = s.Seq; double since = s.Since;
                double age = MeleeLatchRule.Age(IsLatched(p), p.AttackSequence, ref seq, ref since, now);
                s.Seq = seq; s.Since = since;
                if (age == 0) { var b = MotionQueueWatchdog.Snapshot(p); s.Head = b.head; s.Counter = b.counter; s.Logged = false; continue; }
                if (!MeleeLatchRule.Stuck(age, LIMIT)) continue;
                if (mode == MeleeLatchRule.LOG)
                {
                    if (s.Logged) continue;
                    s.Logged = true;
                    var b = MotionQueueWatchdog.Snapshot(p);
                    Mod.Log.Warn($"[RevivalGuard] melee latch: {p.Name} (0x{p.Guid.Full:X8}) melee on {p.MeleeTarget?.Name} 0x{p.MeleeTarget?.Guid.Full:X8} "
                        + $"(seq {p.AttackSequence}) waiting {age:F1}s on its move-to (moving to {p.IsPlayerMovingTo2}, callback {p.MoveToParams?.Callback != null}), "
                        + $"no swing; body queue {b.count}, moved {!(ReferenceEquals(b.head, s.Head) && b.counter == s.Counter)} ({ServerFooting.Describe(p)}); log only");
                    BusyLedger.Note(p, $"melee latch: waiting {age:F0}s on a move-to, no swing (log only)");
                    continue;
                }
                s.Since = now;   // once per window
                int seq0 = p.AttackSequence; var head0 = s.Head; var counter0 = s.Counter;
                p.EnqueueAction(new ActionEventDelegate(() => Finish(p, seq0, head0, counter0, age)));
            }
            if (s_Seen.Count > live.Count)
                foreach (var g in s_Seen.Keys.Where(g => !live.Contains(g)).ToList()) s_Seen.Remove(g);
        }

        static void Finish(Player p, int seq, object head0, uint counter0, double age)
        {
            if (p.AttackSequence != seq || !IsLatched(p)) return;   // it swung or moved on meanwhile
            string target = $"{p.MeleeTarget?.Name} 0x{p.MeleeTarget?.Guid.Full:X8}";
            string pending = $"moving to {p.IsPlayerMovingTo2}, callback {p.MoveToParams?.Callback != null}";
            var b = MotionQueueWatchdog.Snapshot(p);
            string body = MeleeLatchRule.DrainQueue(b.count, ReferenceEquals(b.head, head0), b.counter == counter0)
                ? "body " + MotionQueueWatchdog.Finish(p) : $"body queue {b.count} (moving)";
            // ACE's own ways out: cancel_moveto (OnMoveComplete -> OnMoveComplete_MoveTo2 keeps the callback for the tick),
            // then the pending callback answered false, which for melee is OnAttackDone.
            p.PhysicsObj?.cancel_moveto();
            p.IsPlayerMovingTo2 = false;
            if (p.MoveToParams != null) p.CheckMoveToParams();
            if (p.MeleeTarget != null) p.OnAttackDone();
            Mod.Log.Warn($"[RevivalGuard] melee latch: {p.Name} (0x{p.Guid.Full:X8}) melee on {target} (seq {seq}) waited {age:F1}s on its move-to "
                + $"({pending}) with no swing; {body}; move-to cancelled, attack finished ({ServerFooting.Describe(p)})");
            BusyLedger.Note(p, $"melee latch: finished an attack stuck {age:F0}s on its move-to");
        }
    }
}
