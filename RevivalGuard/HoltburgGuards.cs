using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;
using static RevivalGuard.HoltburgGuardsRule;

namespace RevivalGuard
{
    /// <summary>
    /// HOLTBURG'S ROYAL GUARDS KEEP THE TOWN (owner 2026-11-12: "they do what guards do. talk like guards. patrol like guards.
    /// ... if it sees some it should fight them"). Ours, not retail (docs/DIVERGENCES.md; findings
    /// docs/findings/holtburg-guards-2026-11-12.md). Only the three Royal Guards (37518) placed in Holtburg (A9B4); the same
    /// weenie in other towns, Tirenia and every other NPC are untouched.
    ///
    ///   TALK    an ambient line now and then (HoltburgGuardLines.cs, picked by HoltburgGuardsRule) instead of retail's
    ///           heartbeat line; an answer to a Use (after retail's own reply) or to a greeting in local chat
    ///           (heard on Player.OnTalk, where ACE offers local chat to nearby creatures).
    ///   PATROL  a walking round of waypoints along Holtburg's roads, every leg checked offline against the retail physics
    ///           port's 1 m walk grid (findings doc), with pauses at posts; ACE's MoveTo(Position) walks it server-side.
    ///   DUTY    a hostile monster in town: the nearest one or two guards run to it (ACE's MoveTo), draw and swing with
    ///           ACE's own MeleeAttack (animation, timing); the blow is rolled here (FightStep/Strike: ACE's damage needs a
    ///           body-part table an NPC lacks) and lands through ACE's TakeDamage. A monster that turns on the guard swings
    ///           back the same way (Struck). The kill's experience and loot stay with the players who fought it (the guard's
    ///           damage is never recorded); a guard never dies and falls back to its post below 30 % health. ACE's own
    ///           monster tick is not used for a guard: it reads targets from physics lists that never hold an NPC, and
    ///           its visibility and foe checks are inlined (no hook can reach them).
    ///
    /// Switch (registered, read every think, so it turns off live):
    ///   @modifybool rg_holtburg_guards false    guards walk home, then stand and say retail's line as before
    ///   @modifylong rg_holtburg_guards_trace 1  log each guard's state and position every 2 s (tests)
    /// Everything that runs on the landblock thread is caught and reported through TickGuard.
    /// </summary>
    static class HoltburgGuards
    {
        internal const string P_ON = "rg_holtburg_guards", P_TRACE = "rg_holtburg_guards_trace";
        // whose fight it is (HoltburgGuardsRule.Decide): live tunables beside the switch, defaults in the rule
        internal const string P_NEWCOMER = "rg_holtburg_guards_newcomer_level", P_WINDOW = "rg_holtburg_guards_engage_window_s",
                              P_RESCUE = "rg_holtburg_guards_rescue_health";
        internal const string P_EMOTE = "rg_holtburg_guards_emote_minutes";   // gestures: minutes apart per guard, 0 = none at all
        internal const string P_SALUTE = "rg_holtburg_guards_salute_m";        // another guard this close (and in view): a salute
        internal const uint WCID = 37518;          // Royal Guard
        const uint LONG_SWORD = 351;               // ace_world: Long Sword, Heavy Weapons (MercKit's)
        const uint HUMAN_CMT = 0x30000000;         // the human combat maneuver table 2,512 human weenies use
        const float CENTRE_X = 96, CENTRE_Y = 92;  // posts face away from here

        // ---- the routes (A9B4-local metres; index 0 is the station, set from the guard's own spawn) ----
        // Made by A* on the outdoor layer's 1 m grid (tools/atlas/outdoor over the DATs) preferring road, then every straight
        // leg re-checked: each 0.25 m sample clear of anything not walkable by 1 m, rise under 0.5 m per metre.
        sealed class Route { public string Name; public (float x, float y, float z)[] P; public bool[] Post; }

        static readonly Dictionary<uint, Route> ROUTES = new Dictionary<uint, Route>
        {
            [0x7A9B46AF] = new Route { Name = "west", P = new[] { (9.9f, 114.1f, 67f), (38f, 119f, 66.08f), (51f, 142f, 66f), (88f, 145f, 66f), (50f, 144f, 66f) },
                                        Post = new[] { true, true, false, true, true } },
            [0x7A9B46AE] = new Route { Name = "south", P = new[] { (115.3f, 6.5f, 94f), (124f, 24f, 94f), (140f, 25f, 94f), (117f, 17f, 94f), (104f, 9f, 94f), (96f, 2f, 94f) },
                                        Post = new[] { true, true, true, false, true, true } },
            [0x7A9B46B0] = new Route { Name = "east", P = new[] { (176.7f, 106.4f, 65.3f), (148f, 122f, 66f), (118f, 145f, 66f), (134f, 133f, 66f), (138f, 136f, 66f), (164f, 115f, 66f) },
                                        Post = new[] { true, true, true, false, true, false } },
        };

        sealed class Guard
        {
            public Creature Obj;
            public Route Route;
            public Position Station;
            public readonly Mind Mind = new Mind();
            public readonly LineBag Bag = new LineBag();
            public double NextThink, NextAmbient, NextTrace, DutySince, NextScanLine, NextChase, NextSwing;
            public int Swings; public string Step = "";
            public double RestUntil;   // after a stand-down: its closing line first, then the next monster
            public Vector2 ChaseTo;
            public double MotionUntil, NextGesture, NextSalute;   // gestures: busy until, next idle one, next salute
            public Gesture LastGesture;
            public bool PendingWalk;
            public double SheathTime;                              // a walk order held back until a gesture ends
            public Creature Target;
            public bool Kitted, FellBack;
        }

        static readonly object s_Lock = new object();
        static readonly Dictionary<uint, Guard> s_Guards = new Dictionary<uint, Guard>();
        static volatile uint[] s_Duty = Array.Empty<uint>();             // guards in a fight (read from any thread)
        static readonly Dictionary<uint, double> s_Called = new Dictionary<uint, double>();   // monster -> call-out time
        static readonly Dictionary<uint, double> s_GreetedBy = new Dictionary<uint, double>(); // player -> last answer
        static readonly Dictionary<uint, double> s_GivenUp = new Dictionary<uint, double>();   // monster -> fight given up (left alone GIVE_UP_FOR)
        const double GIVE_UP_FOR = 300;
        static double s_NextHoldLog, s_LastLine = -1e9, s_LastDeparture = -1e9, s_LastVictory = -1e9, s_NextScan;
        static readonly Random s_Rng = new Random();
        static MethodInfo s_Wield;

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, true).Item; } catch { return false; } } }
        static long Trace { get { try { return PropertyManager.GetLong(P_TRACE, 0).Item; } catch { return 0; } } }
        internal static bool Tracing => Trace > 0;
        static int NewcomerLevel { get { try { return (int)PropertyManager.GetLong(P_NEWCOMER, NEWCOMER_LEVEL).Item; } catch { return NEWCOMER_LEVEL; } } }
        static float SaluteRange { get { try { return (float)PropertyManager.GetDouble(P_SALUTE, SALUTE_RANGE).Item; } catch { return SALUTE_RANGE; } } }
        static double EmoteMinutes { get { try { return PropertyManager.GetDouble(P_EMOTE, EMOTE_MINUTES).Item; } catch { return EMOTE_MINUTES; } } }
        static double Window { get { try { return PropertyManager.GetDouble(P_WINDOW, ENGAGE_WINDOW).Item; } catch { return ENGAGE_WINDOW; } } }
        static float RescueHealth { get { try { return (float)PropertyManager.GetDouble(P_RESCUE, RESCUE_HEALTH).Item; } catch { return RESCUE_HEALTH; } } }

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard HoltburgGuards: Holtburg's Royal Guards talk, patrol and fight monsters in town (false: retail's standing guards)");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_TRACE)) l[P_TRACE] = new Property<long>(0, "RevivalGuard HoltburgGuards: 1 logs each guard's state and place every 2 s");
                if (!l.ContainsKey(P_NEWCOMER)) l[P_NEWCOMER] = new Property<long>(NEWCOMER_LEVEL, "RevivalGuard HoltburgGuards: a player under this level fighting a monster keeps it to themselves (guards step in only if they are in real trouble)");
                var d = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                if (!d.ContainsKey(P_WINDOW)) d[P_WINDOW] = new Property<double>(ENGAGE_WINDOW, "RevivalGuard HoltburgGuards: seconds a player's hit or being the target counts as fighting a monster");
                if (!d.ContainsKey(P_SALUTE)) d[P_SALUTE] = new Property<double>(SALUTE_RANGE, "RevivalGuard HoltburgGuards: metres within which two standing guards salute each other");
                if (!d.ContainsKey(P_EMOTE)) d[P_EMOTE] = new Property<double>(EMOTE_MINUTES, "RevivalGuard HoltburgGuards: minutes between a guard's idle gestures (flex, stretch, look-around); 0 turns every gesture off (bow, salute, cheer too)");
                if (!d.ContainsKey(P_RESCUE)) d[P_RESCUE] = new Property<double>(RESCUE_HEALTH, "RevivalGuard HoltburgGuards: a newcomer below this share of health is in real trouble and the guards step in");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] HoltburgGuards: trace switch not registered ({e.Message})"); }
            s_Wield = AccessTools.Method(typeof(Creature), "TryWieldObjectWithBroadcasting", new[] { typeof(WorldObject), typeof(EquipMask) });
            int lines = 0; foreach (var p in HoltburgGuardLines.Pools.Values) lines += p.Length;
            Mod.Log.Info($"[RevivalGuard] HoltburgGuards: {ROUTES.Count} Royal Guards in Holtburg talk ({lines} lines), patrol and keep the town ({P_ON}{(s_Wield == null ? "; no wield method, guards fight bare-handed" : "")})");
        }

        // ---- who is a guard ----
        internal static bool Owns(WorldObject wo) => wo != null && wo.WeenieClassId == WCID && ROUTES.ContainsKey(wo.Guid.Full);

        static bool OnDuty(uint guid)
        {
            var d = s_Duty;
            for (int i = 0; i < d.Length; i++) if (d[i] == guid) return true;
            return false;
        }

        static void SetDutyList()
        {
            var l = new List<uint>();
            lock (s_Lock) foreach (var g in s_Guards.Values) if (g.Mind.State == PState.Duty) l.Add(g.Obj.Guid.Full);
            s_Duty = l.ToArray();
        }

        static Guard Get(Creature c)
        {
            lock (s_Lock)
            {
                if (s_Guards.TryGetValue(c.Guid.Full, out var g) && ReferenceEquals(g.Obj, c)) return g;
                // first sight, or the landblock was built again: a fresh guard at its station
                g = new Guard { Obj = c, Route = ROUTES[c.Guid.Full] };
                g.Station = new Position(c.Home ?? c.Location);
                double now = Timers.RunningTime;
                g.Mind.Until = now + 30 + s_Rng.NextDouble() * STATION_SPAN;
                g.NextAmbient = now + 60 + s_Rng.NextDouble() * 180;
                s_Guards[c.Guid.Full] = g;
                return g;
            }
        }

        // ---- geometry: everything in A9B4-local metres ----
        static Vector2 Local(Position p)
        {
            var gl = p.ToGlobal();
            return new Vector2(gl.X - 0xA9 * 192f, gl.Y - 0xB4 * 192f);
        }

        static Vector2 Wp(Guard g, int i) => i == 0 ? Local(g.Station) : new Vector2(g.Route.P[i].x, g.Route.P[i].y);

        static Position WpPos(Guard g, int i)
        {
            if (i == 0) return new Position(g.Station);
            var (x, y, z) = g.Route.P[i];
            var pos = new Position(0xA9B40000, x, y, z, 0, 0, 0, 1);
            var from = Wp(g, Math.Max(0, i - 1)); var dir = new Vector2(x, y) - from;
            if (dir.LengthSquared() > 0.01f) pos.Rotate(new Vector3(Vector2.Normalize(dir), 0));
            return pos;
        }

        static bool PlayerNear(Creature c, float range)
        {
            if (c.PhysicsObj == null || c.PhysicsObj.ObjMaint.GetKnownPlayersCount() == 0) return false;
            foreach (var p in c.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                if (p.Location != null && c.Location.DistanceTo(p.Location) <= range) return true;
            return false;
        }

        // ---- the think, once a second per guard, from its own Monster_Tick (landblock thread); in a fight the guard's
        // ---- swings and steps every tick (0.2 s). True: the guard is fighting, and ACE's own monster tick is skipped.
        internal static bool Tick(Creature c)
        {
            if (!Owns(c) || c.PhysicsObj == null || c.Location == null || c.IsDead) return false;
            var g = Get(c);
            double now = Timers.RunningTime;
            if (g.Mind.State == PState.Duty) FightStep(g, now);
            if (now < g.NextThink) return g.Mind.State == PState.Duty;
            g.NextThink = now + 1.0;
            bool on = On;

            if (!on && g.Mind.State == PState.Station && !g.Kitted && !g.FellBack) return false;   // retail: nothing to do

            if (g.Mind.State == PState.Duty) DutyThink(g, now, on);
            else
            {
                if (on && now >= s_NextScan) { s_NextScan = now + 1.5; Scan(g, now); }
                if (g.Mind.State != PState.Duty) PatrolThink(g, now, on);
            }
            if (on && g.Mind.State != PState.Duty) Ambient(g, now);
            if (on && g.Mind.State != PState.Duty) Gestures(g, now);
            if (g.FellBack && g.Mind.State != PState.Duty) Recover(g);
            if (!on && g.Mind.State == PState.Station && g.Kitted) Unkit(g);
            if (Trace > 0 && now >= g.NextTrace) { g.NextTrace = now + 2; TraceLine(g); }
            return g.Mind.State == PState.Duty;
        }

        static void PatrolThink(Guard g, double now, bool on)
        {
            var c = g.Obj;
            if (now < g.MotionUntil) return;   // a gesture plays out before any move order
            if (g.PendingWalk) { g.PendingWalk = false; g.Mind.ProgressAt = now; c.MoveTo(WpPos(g, g.Mind.Index), 0f); return; }
            int away = 0;
            lock (s_Lock) foreach (var o in s_Guards.Values) if (o != g && o.Mind.State != PState.Station) away++;
            bool mayLeave = !g.FellBack && MayLeave(away, now, s_LastDeparture);
            float dist = Vector2.Distance(Local(c.Location), Wp(g, g.Mind.Index));
            var before = g.Mind.State;
            var act = Step(g.Mind, now, dist, g.Route.Post, mayLeave, on, PlayerNear(c, 60), s_Rng.NextDouble());
            if (before == PState.Station && g.Mind.State == PState.Walking) s_LastDeparture = now;
            switch (act)
            {
                case PAct.WalkTo:
                    c.MoveTo(WpPos(g, g.Mind.Index), 0f);      // runRate 0: walk
                    break;
                case PAct.FaceOut:
                {
                    var here = Local(c.Location);
                    var dir = here - new Vector2(CENTRE_X, CENTRE_Y);
                    var face = new Position(c.Location);
                    if (dir.LengthSquared() > 0.01f) face.Rotate(new Vector3(Vector2.Normalize(dir), 0));
                    c.TurnTo(face);
                    break;
                }
                case PAct.FaceStation:
                    c.TurnTo(new Position(g.Station));
                    break;
                case PAct.Teleport:
                    if (c.Location.Landblock == g.Station.Landblock) c.FakeTeleport(new Position(g.Station));
                    Mod.Log.Info($"[RevivalGuard] HoltburgGuards: {g.Route.Name} guard stuck for good, sent back to its station");
                    break;
            }
            if (act == PAct.WalkTo && g.Mind.Retries > 0)
                Mod.Log.Info($"[RevivalGuard] HoltburgGuards: {g.Route.Name} guard made no progress to waypoint {g.Mind.Index} ({g.Mind.State}); trying again");
        }

        // ---- talk ----
        static string Line(Guard g, string pool)
        {
            if (!HoltburgGuardLines.Pools.TryGetValue(pool, out var lines) || lines.Length == 0) return null;
            int i;
            lock (s_Lock) i = g.Bag.Next(pool, lines.Length, s_Rng);
            return i < 0 ? null : lines[i];
        }

        internal static void Say(Creature c, string text)
        {
            if (string.IsNullOrEmpty(text) || c == null || c.IsDestroyed) return;
            c.EnqueueBroadcast(new GameMessageHearSpeech(text, c.Name, c.Guid.Full, ChatMessageType.Emote), WorldObject.LocalBroadcastRange);
            s_LastLine = Timers.RunningTime;
            if (Trace > 0 && ROUTES.TryGetValue(c.Guid.Full, out var r)) Mod.Log.Info($"[RevivalGuard] HoltburgGuards said: {r.Name} \"{text}\"");
        }

        static void SayLater(Creature c, double delay, Func<string> text)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(c, () => { try { Say(c, text()); } catch (Exception e) { TickGuard.Report("HoltburgGuards.Say", e); } });
            chain.EnqueueChain();
        }

        static string SeasonKey()
        {
            try { return Timers.CurrentInGameTime.Season.ToString().ToLowerInvariant(); } catch { return null; }
        }

        static Bucket NowBucket()
        {
            try { return TimeBucket(Timers.CurrentInGameTime.Hour); } catch { return Bucket.Day; }
        }

        static void Ambient(Guard g, double now)
        {
            var c = g.Obj;
            if (!MaySpeak(now, g.NextAmbient, s_LastLine, true, false)) return;
            if (!PlayerNear(c, EARSHOT)) { g.NextAmbient = now + 10; return; }   // look again soon, when someone comes by
            int players = c.PhysicsObj.ObjMaint.GetKnownPlayersCount();
            bool after = now - s_LastVictory < 600;
            bool quiet = now - s_LastVictory > 3600 && players <= 1;
            string pool = PickPool(NowBucket(), SeasonKey(), after, players >= 6, quiet, s_Rng.NextDouble());
            Say(c, Line(g, pool) ?? Line(g, "general"));
            g.NextAmbient = now + AMBIENT_GAP_MIN + s_Rng.NextDouble() * AMBIENT_GAP_SPAN;
        }

        // ---- gestures ----
        static MotionCommand Cmd(Gesture gs) => gs switch
        {
            Gesture.Akimbo => MotionCommand.Akimbo, Gesture.Stretch => MotionCommand.YawnStretch, Gesture.LookAround => MotionCommand.ScanHorizon,
            Gesture.WarmHands => MotionCommand.WarmHands, Gesture.Bow => MotionCommand.BowDeep, Gesture.Salute => MotionCommand.Salute,
            Gesture.Cheer => MotionCommand.Cheer, _ => MotionCommand.Ready,
        };

        static float Length(Creature c, MotionCommand cmd)
        {
            try
            {
                var mt = ACE.DatLoader.DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.MotionTable>(c.MotionTableId);
                float l = mt?.GetAnimationLength(MotionStance.NonCombat, cmd, MotionCommand.Ready) ?? 0;
                return l > 0 ? l : 2f;
            }
            catch { return 2f; }
        }

        /// <summary>
        /// One gesture, as ACE's own emote Motion step plays it: the action broadcast to everyone in range (ExecuteMotion),
        /// then back to Ready when its animation ends. The guard is busy (no move order, no other gesture) until then.
        /// </summary>
        static void Play(Guard g, Gesture gs, double delay, string why)
        {
            var c = g.Obj; var cmd = Cmd(gs);
            float len = Length(c, cmd);
            double now = Timers.RunningTime;
            g.MotionUntil = Math.Max(g.MotionUntil, now + delay + len + 0.4);
            var chain = new ActionChain();
            chain.AddDelaySeconds(Math.Max(0, delay));
            chain.AddAction(c, () =>
            {
                try
                {
                    bool moving = c.PhysicsObj?.IsMovingTo() ?? true;
                    if (c.IsDead || g.Mind.State == PState.Duty || moving || c.CurrentMotionState?.Stance != MotionStance.NonCombat)
                    {
                        if (Tracing) Mod.Log.Info($"[RevivalGuard] HoltburgGuards gesture skipped: {g.Route.Name} {gs} ({why}) state {g.Mind.State} moving {moving} stance {c.CurrentMotionState?.Stance}");
                        return;
                    }
                    c.ExecuteMotion(new Motion(MotionStance.NonCombat, cmd), true, WorldObject.LocalBroadcastRange);
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] HoltburgGuards gesture: {g.Route.Name} {gs} ({why}) {cmd} {len:0.00} s, state {g.Mind.State} moving {moving}");
                    var back = new ActionChain();
                    back.AddDelaySeconds(len);
                    back.AddAction(c, () =>
                    {
                        try
                        {
                            if (!c.IsDead && c.CurrentMotionState?.MotionState.ForwardCommand == cmd)
                                c.ExecuteMotion(new Motion(MotionStance.NonCombat, MotionCommand.Ready), true, WorldObject.LocalBroadcastRange);
                        }
                        catch (Exception e) { TickGuard.Report("HoltburgGuards.Gesture", e); }
                    });
                    back.EnqueueChain();
                }
                catch (Exception e) { TickGuard.Report("HoltburgGuards.Gesture", e); }
            });
            chain.EnqueueChain();
        }

        static readonly Dictionary<Gesture, string> GESTURE_LINES = new Dictionary<Gesture, string>
        {
            [Gesture.Stretch] = "gesture.stretch", [Gesture.LookAround] = "gesture.look", [Gesture.WarmHands] = "gesture.warm",
        };

        /// <summary>Idle gestures on the guard's own timer, and a salute when another guard is in view.</summary>
        static void Gestures(Guard g, double now)
        {
            double minutes = EmoteMinutes;
            if (minutes <= 0) return;
            var c = g.Obj;
            bool moving = c.PhysicsObj.IsMovingTo();
            if (g.NextGesture == 0) g.NextGesture = NextIdle(now, minutes, s_Rng.NextDouble());
            if (g.NextGesture > now + minutes * 60 * 1.25) g.NextGesture = NextIdle(now, minutes, s_Rng.NextDouble());   // the tunable was lowered
            if (!MayGesture(g.Mind.State, moving, true, now, g.MotionUntil)) return;

            // another guard in view, both standing: a salute, answered
            if (now >= g.NextSalute)
            {
                float range = SaluteRange;
                Guard other = null;
                lock (s_Lock)
                    foreach (var o in s_Guards.Values)
                        if (o != g && o.Obj?.Location != null && o.Mind.State != PState.Duty && Vector2.Distance(Local(o.Obj.Location), Local(c.Location)) <= range) { other = o; break; }
                if (other != null && PlayerNear(c, 40))
                {
                    double turn = c.Rotate(other.Obj);
                    Play(g, For(GestureFor.MetGuard), turn + 0.1, "met " + other.Route.Name + " guard");
                    g.NextSalute = other.NextSalute = now + SALUTE_GAP;
                    if (MayGesture(other.Mind.State, other.Obj.PhysicsObj.IsMovingTo(), true, now, other.MotionUntil))
                    {
                        double t2 = other.Obj.Rotate(c);
                        Play(other, For(GestureFor.MetGuard), turn + 1.2 + t2, "answering " + g.Route.Name + " guard");
                    }
                    return;
                }
            }

            if (now < g.NextGesture) return;
            if (!PlayerNear(c, 40)) { g.NextGesture = now + 15; return; }   // nobody to see it: look again soon
            var gs = PickIdle(NowBucket(), SeasonKey(), g.LastGesture, s_Rng.NextDouble());
            g.LastGesture = gs;
            Play(g, gs, 0, "idle");
            g.NextGesture = NextIdle(now, minutes, s_Rng.NextDouble());
            // now and then a word with it, when the talk pacing allows
            if (GESTURE_LINES.TryGetValue(gs, out var pool) && s_Rng.NextDouble() < 0.35 && MaySpeak(now, 0, s_LastLine, true, false))
                SayLater(c, 0.8, () => Line(g, pool));
        }

        /// <summary>A Use: retail's own Use emote has run (prefix let it); a reply in character follows it.</summary>
        internal static bool OnUse(Creature c, WorldObject user)
        {
            if (!On || !(user is Player p)) return true;
            var g = Get(c);
            if (g.Mind.State == PState.Duty)
            {
                Say(c, Line(g, "greet.fight"));
                return false;   // no turning to chat in the middle of a fight
            }
            if (Answered(p, 30)) return true;
            // retail's own Use emote turns the guard to the player; the bow follows, then the greeting
            if (EmoteMinutes > 0 && MayGesture(g.Mind.State, c.PhysicsObj?.IsMovingTo() ?? true, true, Timers.RunningTime, g.MotionUntil))
                Play(g, Gesture.Bow, 1.0, "use");
            SayLater(c, 2.5, () => Line(g, "greet." + Key(NowBucket())));
            return true;
        }

        /// <summary>A player spoke in local chat: the nearest guard within 12 m answers a greeting.</summary>
        internal static void Heard(Player p, string message)
        {
            if (p?.Location == null || s_Guards.Count == 0) return;
            uint bx = p.Location.LandblockId.LandblockX, by = p.Location.LandblockId.LandblockY;
            if (bx < 0xA8 || bx > 0xAA || by < 0xB3 || by > 0xB5) return;   // nowhere near Holtburg
            if (!IsGreeting(message)) return;
            Creature best = null; float bd = 12f;
            lock (s_Lock)
                foreach (var g in s_Guards.Values)
                {
                    if (g.Obj?.Location == null || g.Obj.IsDestroyed) continue;
                    float d = g.Obj.Location.DistanceTo(p.Location);
                    if (d <= bd) { bd = d; best = g.Obj; }
                }
            if (best != null) OnHeard(best, p, message);
        }

        internal static void OnHeard(Creature c, Player p, string message)
        {
            if (!On || p == null || !IsGreeting(message)) return;
            if (c.Location == null || p.Location == null || c.Location.DistanceTo(p.Location) > 12f) return;
            var g = Get(c);
            if (Answered(p, 45)) return;   // the first guard to hear it answers; the others let it be
            string pool = g.Mind.State == PState.Duty ? "greet.fight" : "greet." + Key(NowBucket());
            double say = 1.2;
            if (EmoteMinutes > 0 && MayGesture(g.Mind.State, c.PhysicsObj?.IsMovingTo() ?? true, true, Timers.RunningTime, g.MotionUntil))
            {
                double turn = c.Rotate(p);   // face the player first
                Play(g, Gesture.Bow, turn + 0.1, "greeting");
                say = turn + 0.6;
            }
            SayLater(c, say, () => Line(g, pool));
        }

        static bool Answered(Player p, double within)
        {
            double now = Timers.RunningTime;
            lock (s_Lock)
            {
                if (s_GreetedBy.TryGetValue(p.Guid.Full, out var t) && now - t < within) return true;
                s_GreetedBy[p.Guid.Full] = now;
                if (s_GreetedBy.Count > 200) s_GreetedBy.Clear();
            }
            return false;
        }

        // ---- duty ----
        static Candidate Facts(Creature m, Vector2 guardAt)
        {
            var at = Local(m.Location);
            return new Candidate
            {
                IsPlayer = m is Player,
                IsPet = m is Pet,
                HasOwner = m.PetOwner != null,
                IsMonster = m.IsMonster,
                Attackable = m.Attackable,
                IsDead = m.IsDead,
                IsGuard = Owns(m),
                Peaceful = (m.Tolerance & (Tolerance.NoAttack | Tolerance.Retaliate)) != 0,
                InTown = InTown(at.X, at.Y),
                Dist = Vector2.Distance(at, guardAt),
            };
        }

        /// <summary>Every 1.5 s (whichever guard thinks first): monsters in town on the guards' landblock and its
        /// neighbours (one landblock group, one thread), each given its nearest free guards.</summary>
        static void Scan(Guard first, double now)
        {
            var lb = first.Obj.CurrentLandblock;
            if (lb == null) return;
            List<Guard> free;
            lock (s_Lock)
            {
                free = new List<Guard>();
                foreach (var g in s_Guards.Values)
                    if (g.Mind.State != PState.Duty && !g.FellBack && now >= g.RestUntil && g.Obj.CurrentLandblock != null && !g.Obj.IsDestroyed) free.Add(g);
            }
            if (free.Count == 0) return;
            ScanBlock(lb, free, now);
            foreach (var adj in lb.Adjacents) if (adj != null) ScanBlock(adj, free, now);
        }

        static void ScanBlock(Landblock lb, List<Guard> free, double now)
        {
            // a copy, not GetWorldObjectsForPhysicsHandling: that one runs the landblock's pending adds and removes, which
            // must not happen inside its own creature tick loop (this runs from a guard's Monster_Tick)
            foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
            {
                if (free.Count == 0) return;
                if (!(wo is Creature m) || wo is Player || m.Location == null || !m.IsMonster) continue;
                lock (s_Lock) if (s_GivenUp.TryGetValue(m.Guid.Full, out var gu)) { if (now - gu < GIVE_UP_FOR) continue; s_GivenUp.Remove(m.Guid.Full); }
                int engaged = 0;
                foreach (var d in s_Duty) { var dg = GuardBy(d); if (dg?.Target == m) engaged++; }
                if (engaged >= MAX_PER_MONSTER) continue;
                bool anyHostile = false;
                foreach (var g in free) if (Eligible(Facts(m, Local(g.Obj.Location))) == Verdict.Hostile) { anyHostile = true; break; }
                if (!anyHostile) continue;
                var whose = Whose(m, out var whyNot);
                if (whose == Call.HoldNewcomer || whose == Call.HoldPlayerFight)
                {
                    if (Tracing && now >= s_NextHoldLog) { s_NextHoldLog = now + 5; Mod.Log.Info($"[RevivalGuard] HoltburgGuards: holding off {m.Name} 0x{m.Guid.Full:X8}: {whyNot}"); }
                    continue;
                }
                while (engaged < MAX_PER_MONSTER && free.Count > 0)
                {
                    Guard best = null; float bestD = float.MaxValue;
                    foreach (var g in free)
                    {
                        var f = Facts(m, Local(g.Obj.Location));
                        if (Eligible(f) != Verdict.Hostile) continue;
                        if (f.Dist < bestD) { bestD = f.Dist; best = g; }
                    }
                    if (best == null) break;
                    free.Remove(best);
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] HoltburgGuards: may fight {m.Name} 0x{m.Guid.Full:X8}: {whyNot}");
                    Engage(best, m, engaged == 0, now, whose == Call.Rescue);
                    engaged++;
                }
            }
        }

        /// <summary>The player behind a creature: itself, or a combat pet's owner.</summary>
        static Player Behind(WorldObject wo)
        {
            if (wo is Player p) return p;
            if (wo is Creature cr && cr.PetOwner != null) return PlayerManager.GetOnlinePlayer(cr.PetOwner.Value);
            return null;
        }

        /// <summary>HoltburgGuardsRule.Decide for one monster: who has fought it lately (its damage log, its target), and
        /// whether it has swung at a player, a pet or a guard within the window.</summary>
        static Call Whose(Creature m, out string why)
        {
            double window = Window;
            var list = new List<Engager>(2);
            var nowUtc = DateTime.UtcNow;
            var log = m.DamageHistory?.Log;
            if (log != null)
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    var e = log[i];
                    double since = (nowUtc - e.Time).TotalSeconds;
                    if (since > window) break;
                    if (e.Amount >= 0) continue;   // heals
                    var p = Behind(PlayerManager.GetOnlinePlayer(e.Attacker.Full) ?? (WorldObject)m.CurrentLandblock?.GetObject(e.Attacker));
                    if (p != null) list.Add(new Engager { Level = p.Level ?? 1, Since = since, Health = p.Health.Current / (float)Math.Max(1u, p.Health.MaxValue) });
                }
            var target = Behind(m.AttackTarget);
            double rt = Timers.RunningTime;
            if (target != null)
            {
                var e = new Engager { Level = target.Level ?? 1, Since = 0, IsTarget = true, Health = target.Health.Current / (float)Math.Max(1u, target.Health.MaxValue) };
                list.Add(e);
                lock (s_Lock) { if (s_Targeted.Count > 200) s_Targeted.Clear(); s_Targeted[m.Guid.Full] = (e, rt); }
            }
            else
            {
                // ACE drops and re-picks a monster's target now and then (FindNextTarget every few seconds): "was its target
                // within the window" is remembered here, so a newcomer's fight is not lost in that gap
                (Engager e, double t) seen;
                bool had;
                lock (s_Lock) had = s_Targeted.TryGetValue(m.Guid.Full, out seen);
                if (had && rt - seen.t <= window) { var e = seen.e; e.IsTarget = false; e.Since = rt - seen.t; list.Add(e); }
            }
            bool attacking = m.AttackTarget != null && (target != null || Owns(m.AttackTarget)) && Timers.RunningTime - m.PrevAttackTime <= window;
            var verdict = Decide(attacking, list, NewcomerLevel, window, RescueHealth);
            why = $"{verdict} (attacking {attacking}, fighters {list.Count}{(list.Count > 0 ? $", lowest level {MinLevel(list)}" : "")})";
            return verdict;
        }

        static int MinLevel(List<Engager> l) { int m = int.MaxValue; foreach (var e in l) m = Math.Min(m, e.Level); return m; }

        static Guard GuardBy(uint guid) { lock (s_Lock) return s_Guards.TryGetValue(guid, out var g) ? g : null; }

        static void Kit(Guard g)
        {
            var c = g.Obj;
            if (g.Kitted) return;
            g.Kitted = true;
            if (c.CombatTableDID == null) c.CombatTableDID = HUMAN_CMT;
            if (c.CombatTable == null) c.GetCombatTable();   // ACE loads it in GetNextAttackType, which a guard never calls
            // A level 40 Royal Guard, as a fighter: weapon and defense about 200, health about 300.
            var w = c.GetCreatureSkill(Skill.HeavyWeapons);
            if (w.AdvancementClass < SkillAdvancementClass.Trained) { w.AdvancementClass = SkillAdvancementClass.Trained; w.InitLevel = 120; }
            var d = c.GetCreatureSkill(Skill.MeleeDefense);
            if (d.AdvancementClass < SkillAdvancementClass.Trained) { d.AdvancementClass = SkillAdvancementClass.Trained; d.InitLevel = 120; }
            if (c.Health.StartingValue < 250) { c.Health.StartingValue = 250; c.SetMaxVitals(); }
            if (s_Wield != null && c.GetEquippedMeleeWeapon() == null)
            {
                var sword = WorldObjectFactory.CreateNewWorldObject(LONG_SWORD);
                if (sword != null && !(bool)s_Wield.Invoke(c, new object[] { sword, EquipMask.MeleeWeapon })) sword.Destroy();
            }
        }

        static void Unkit(Guard g)
        {
            var c = g.Obj;
            g.Kitted = false;
            var sword = c.GetEquippedMeleeWeapon();
            if (sword != null && sword.WeenieClassId == LONG_SWORD && c.TryUnwieldObjectWithBroadcasting(sword.Guid, out var item, out _))
                item?.Destroy();
        }

        static void Engage(Guard g, Creature m, bool first, double now, bool rescue = false)
        {
            var c = g.Obj;
            Kit(g);
            if (c.PhysicsObj.IsMovingTo()) c.CancelMoveTo();
            g.Mind.State = PState.Duty;
            g.Target = m;
            g.DutySince = now;
            c.AttackTarget = m;
            c.MonsterState = Creature.State.Awake;   // not Idle: a monster's swing then never "wakes" the guard (AlertFriendly)
            // ACE lands creature-on-creature melee only for pets, factions or foe types (Monster_Melee): a faction value,
            // even None, opens that gate both ways for the fight (the monster's swing reads the guard's, the guard's its own)
            c.Faction1Bits = FactionBits.None;
            g.NextChase = 0; g.NextSwing = now;
            SetDutyList();
            bool callNow;
            lock (s_Lock)
            {
                callNow = !s_Called.TryGetValue(m.Guid.Full, out var t) || now - t > 30;
                if (callNow) s_Called[m.Guid.Full] = now;
                if (s_Called.Count > 100) s_Called.Clear();
            }
            if (callNow) Say(c, Line(g, rescue ? "duty.rescue" : "duty.callout"));
            else if (s_Rng.NextDouble() < 0.6) SayLater(c, 2.0, () => Line(g, "duty.answer"));
            Mod.Log.Info($"[RevivalGuard] HoltburgGuards: {g.Route.Name} guard engages {m.Name} 0x{m.Guid.Full:X8} at {Local(m.Location).X:0},{Local(m.Location).Y:0} ({Vector2.Distance(Local(c.Location), Local(m.Location)):0} m)");
        }

        static void DutyThink(Guard g, double now, bool on)
        {
            var c = g.Obj; var m = g.Target;
            string why = null, pool = null;
            if (!on) why = "switched off";
            else if (m == null || m.IsDead || m.IsDestroyed || m.Location == null) { why = "monster dead"; pool = "duty.closing"; }
            else
            {
                var at = Local(m.Location); var me = Local(c.Location);
                if (!InTown(at.X, at.Y, BOUNDS_SLACK)) { why = "monster left town"; pool = "duty.abandon"; lock (s_Lock) s_GivenUp[m.Guid.Full] = now; }
                else if (!InTown(me.X, me.Y, BOUNDS_SLACK)) { why = "guard at the town's edge"; pool = "duty.abandon"; lock (s_Lock) s_GivenUp[m.Guid.Full] = now; }
                else if (Whose(m, out var whose) == Call.HoldNewcomer) { why = "a newcomer's fight: " + whose; pool = "duty.watch"; }
                else if (now - g.DutySince > DUTY_TIMEOUT) { why = "fight too long"; pool = "duty.toolong"; lock (s_Lock) s_GivenUp[m.Guid.Full] = now; }
                else if (c.Health.Current < c.Health.MaxValue * FALLBACK_HEALTH)
                {
                    why = "hurt"; pool = "duty.fallback"; g.FellBack = true;
                    lock (s_Lock) s_GivenUp[m.Guid.Full] = now;   // too strong for the guards: left alone for a while
                }
                else if (c.AttackTarget != m) c.AttackTarget = m;   // keep it on its own monster (never anyone else)
            }
            if (why == null) return;
            Disengage(g, now);
            Mod.Log.Info($"[RevivalGuard] HoltburgGuards: {g.Route.Name} guard stands down ({why}) after {now - g.DutySince:0} s, health {c.Health.Current}/{c.Health.MaxValue}");
            if (pool == null) return;
            bool speak = true;
            if (pool == "duty.closing")
            {
                s_LastVictory = now;
                lock (s_Lock)   // one closing line per monster
                {
                    var key = m?.Guid.Full ?? 0;
                    if (s_Called.TryGetValue(key, out var t) && t < 0) speak = false; else s_Called[key] = -1;
                }
            }
            g.RestUntil = now + 3;
            if (speak) SayLater(c, pool == "duty.fallback" ? 0.2 : 1.0, () => Line(g, pool));
            if (speak && pool == "duty.closing" && EmoteMinutes > 0 && c.PhysicsObj.IsMovingTo())
            {
                c.CancelMoveTo();            // stand, cheer, then walk home
                g.PendingWalk = true;
                Play(g, Gesture.Cheer, Math.Max(1.2, g.SheathTime), "victory");
            }
        }

        /// <summary>
        /// One fight tick (0.2 s): close on the monster at a run (ACE's MoveTo, the same server walk as the patrol), then in
        /// reach draw (combat stance), face it and swing with ACE's own MeleeAttack (its animation, hit roll, damage,
        /// splatter). ACE's monster tick is not used for the guard: it reads targets from physics lists that never hold an
        /// NPC. A monster that has turned on the guard (ACE's MonsterOnAttackMonster wakes it onto its attacker) swings
        /// back the same way when in reach; one fighting a player is left to fight the player.
        /// </summary>
        static void FightStep(Guard g, double now)
        {
            var c = g.Obj; var m = g.Target;
            if (m == null || m.IsDead || m.IsDestroyed || m.PhysicsObj == null || m.Location == null) return;
            c.AttackTarget = m;
            if (!c.IsMeleeRange())
            {
                // a new run order only when the last one ended or the monster has moved off from where it was sent:
                // each MoveTo starts with a turn, so ordering it every tick never gets the guard going
                g.Step = "chase";
                var at = Local(m.Location);
                bool moving = c.PhysicsObj.IsMovingTo();
                if (now >= g.NextChase && (!moving || Vector2.Distance(at, g.ChaseTo) > 3f))
                {
                    c.MoveTo(new Position(m.Location), Math.Max(1f, c.GetRunRate()));
                    g.ChaseTo = at;
                    g.NextChase = now + 2.0;
                }
                return;
            }
            if (c.IsAnimating) { g.Step = "anim"; return; }
            if (c.PhysicsObj.IsMovingTo()) c.CancelMoveTo();
            if (c.CurrentMotionState.Stance == MotionStance.NonCombat)
            {
                g.Step = "stance";
                g.NextSwing = now + c.SetCombatMode(CombatMode.Melee);
                return;
            }
            if (!c.IsFacing(m)) { g.Step = "turn"; g.NextSwing = Math.Max(g.NextSwing, now + c.Rotate(m)); return; }
            if (now >= g.NextSwing && Timers.RunningTime >= c.NextAttackTime)
            {
                // ACE's swing: animation, timing, the monster turned onto the guard. Its damage needs a body-part table
                // (DamageEvent.GetBaseDamage), which an NPC weenie has none of, so the blow itself is the guard's strike.
                float t = c.MeleeAttack();
                g.Swings++; g.Step = $"swing {t:0.0}";
                g.NextSwing = now + Math.Max(1.0, t);
                if (t > 0) Strike(c, m, t * 0.45f);
            }
            // the monster's answer
            if (m.AttackTarget == c && m.IsMeleeRange() && Timers.RunningTime >= m.NextAttackTime && !m.IsAnimating)
            {
                if (m.CombatTable == null) m.GetCombatTable();
                if (m.CombatTable == null) return;
                if (m.CurrentMotionState.Stance == MotionStance.NonCombat) { m.DoAttackStance(); return; }
                if (!m.IsFacing(c)) { m.Rotate(c); return; }
                float mt = m.MeleeAttack();
                if (mt > 0) Struck(m, c, mt * 0.45f);
            }
        }

        /// <summary>
        /// The guard's blow, landed mid-swing: hit or miss by ACE's own skill contest (SkillCheck, attack ~200 against the
        /// monster's Melee Defense), STRIKE_MIN..STRIKE_MAX slashing (a Long Sword in a strong level 40 hand), one in ten a
        /// critical for half again, through ACE's Creature.TakeDamage (health, death, corpse) with ACE's splatter.
        /// </summary>
        const int STRIKE_MIN = 8, STRIKE_MAX = 16;

        static void Strike(Creature c, Creature m, float delay)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(c, () =>
            {
                try
                {
                    if (m.IsDead || m.IsDestroyed || c.IsDead || c.AttackTarget != m || !c.IsMeleeRange()) return;
                    uint attack = c.GetCreatureSkill(Skill.HeavyWeapons).Current;
                    uint defense = m.GetCreatureSkill(Skill.MeleeDefense).Current;
                    bool hit = ThreadSafeRandom.Next(0.0f, 1.0f) < SkillCheck.GetSkillChance(attack, defense);
                    if (!hit) { if (Tracing) Mod.Log.Info($"[RevivalGuard] HoltburgGuards strike: miss ({attack} vs {defense})"); return; }
                    float dmg = ThreadSafeRandom.Next(STRIKE_MIN, STRIKE_MAX);
                    bool crit = ThreadSafeRandom.Next(0.0f, 1.0f) < 0.1f;
                    if (crit) dmg *= 1.5f;
                    c.EmitSplatter(m, dmg);
                    if (Tracing) lock (s_Lock) { if (s_Fought.Count > 500) s_Fought.Clear(); s_Fought.Add(m.Guid.Full); }
                    m.TakeDamage(c, DamageType.Slash, dmg, crit);
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] HoltburgGuards strike: {m.Name} {dmg:0}{(crit ? " critical" : "")} ({attack} vs {defense}), left {m.Health.Current}");
                }
                catch (Exception e) { TickGuard.Report("HoltburgGuards.Strike", e); }
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// A monster's blow on a guard. ACE rolls every hit on a creature against its body-part table and the guard's
        /// weenie has none, so every blow would read as evaded: the blow is rolled here instead. Hit: the monster's best
        /// melee skill against the guard's Melee Defense (SkillCheck); damage: level/4 to level/2, plus 2, slashing;
        /// through ACE's Creature.TakeDamage (the guard's own hook holds it above 0).
        /// </summary>
        static readonly Skill[] MELEE = { Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.TwoHandedCombat, Skill.UnarmedCombat };

        static void Struck(Creature m, Creature c, float delay)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(m, () =>
            {
                try
                {
                    if (m.IsDead || m.IsDestroyed || c.IsDead || m.AttackTarget != c || !m.IsMeleeRange()) return;
                    uint attack = 0;
                    foreach (var sk in MELEE) attack = Math.Max(attack, m.GetCreatureSkill(sk).Current);
                    uint defense = c.GetCreatureSkill(Skill.MeleeDefense).Current;
                    if (ThreadSafeRandom.Next(0.0f, 1.0f) >= SkillCheck.GetSkillChance(attack, defense))
                    {
                        if (Tracing) Mod.Log.Info($"[RevivalGuard] HoltburgGuards struck: {m.Name} misses ({attack} vs {defense})");
                        return;
                    }
                    int lvl = Math.Max(1, m.Level ?? 1);
                    float dmg = ThreadSafeRandom.Next(lvl / 4, Math.Max(lvl / 4, lvl / 2)) + 2;
                    m.EmitSplatter(c, dmg);
                    c.TakeDamage(m, DamageType.Slash, dmg);
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] HoltburgGuards struck: {m.Name} hits {dmg:0} ({attack} vs {defense}), guard at {c.Health.Current}");
                }
                catch (Exception e) { TickGuard.Report("HoltburgGuards.Struck", e); }
            });
            chain.EnqueueChain();
        }

        static void Disengage(Guard g, double now)
        {
            var c = g.Obj;
            g.Target = null;
            c.AttackTarget = null;
            g.Mind.State = PState.Returning;   // first: nothing else reads this guard as on duty
            SetDutyList();
            c.Faction1Bits = null;
            if (c.PhysicsObj.IsMovingTo()) c.CancelMoveTo();
            g.SheathTime = 0;
            if (c.CurrentMotionState.Stance != MotionStance.NonCombat) g.SheathTime = c.SetCombatMode(CombatMode.NonCombat);
            c.MonsterState = Creature.State.Idle;
            // walk home by the route, from the nearest waypoint
            var here = Local(c.Location); int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < g.Route.P.Length; i++) { var d = Vector2.Distance(here, Wp(g, i)); if (d < bd) { bd = d; best = i; } }
            ReturnFrom(g.Mind, best, now);
            c.MoveTo(WpPos(g, best), 0f);
        }

        static void Recover(Guard g)
        {
            var c = g.Obj;
            // back at its post: two per cent a second until whole
            if (g.Mind.State != PState.Station) return;
            uint add = Math.Max(1u, c.Health.MaxValue / 50);
            if (c.Health.Current + add >= c.Health.MaxValue) { c.SetMaxVitals(); g.FellBack = false; }
            else c.UpdateVitalDelta(c.Health, (int)add);
        }

        static void TraceLine(Guard g)
        {
            var c = g.Obj; var at = Local(c.Location);
            Mod.Log.Info($"[RevivalGuard] HoltburgGuards trace: {g.Route.Name} {g.Mind.State} wp{g.Mind.Index} at {at.X:0.0},{at.Y:0.0},{c.Location.PositionZ:0.0} cell {c.Location.Cell:X8} hp {c.Health.Current}/{c.Health.MaxValue}"
                + (g.Target != null ? $" target {g.Target.Name} hp {g.Target.Health.Current} d {c.GetDistanceToTarget():0.0} {c.CurrentMotionState.Stance} anim {c.IsAnimating} facing {c.IsFacing(g.Target)} swings {g.Swings} ({g.Step})" : ""));
        }

        // ---- the hooks' questions (any thread; cheap when no guard is fighting) ----
        internal static bool FightPair(WorldObject a, WorldObject b)
        {
            if (s_Duty.Length == 0 || a == null || b == null || a is Player || b is Player) return false;
            if (OnDuty(a.Guid.Full) && a is Creature ga && ga.AttackTarget == b) return true;
            if (OnDuty(b.Guid.Full) && a is Creature ma && ma.AttackTarget == b) return true;
            return false;
        }

        // the feature's own cost on the guards' landblock thread (their ticks: think, scan, fight), logged with the trace
        static long s_CostTicks, s_CostCalls;
        static double s_CostSince = -1;
        internal static void Cost(long ticks)
        {
            s_CostTicks += ticks; s_CostCalls++;
            double now = Timers.RunningTime;
            if (s_CostSince < 0) { s_CostSince = now; return; }
            if (now - s_CostSince < 60) return;
            if (Tracing)
                Mod.Log.Info($"[RevivalGuard] HoltburgGuards cost: {s_CostTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms over {now - s_CostSince:0} s in {s_CostCalls} guard ticks ({s_CostTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, s_CostCalls):0.000} ms each)");
            s_CostTicks = 0; s_CostCalls = 0; s_CostSince = now;
        }

        static readonly HashSet<uint> s_Fought = new HashSet<uint>();
        static readonly Dictionary<uint, (Engager e, double t)> s_Targeted = new Dictionary<uint, (Engager, double)>();   // monster -> its last player target
        internal static bool Fought(Creature m) { lock (s_Lock) return s_Fought.Contains(m.Guid.Full); }

        internal static bool IsDutyGuard(WorldObject wo) => s_Duty.Length > 0 && wo != null && OnDuty(wo.Guid.Full);
    }

    // ================= hooks =================

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class HoltburgGuardsTick
    {
        static bool Prefix(Creature __instance, double currentUnixTime)
        {
            if (__instance.WeenieClassId != HoltburgGuards.WCID) return true;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (!HoltburgGuards.Tick(__instance)) return true;
                __instance.NextMonsterTickTime = currentUnixTime + 0.2;   // the landblock's tick queue needs it moved on
                return false;
            }
            catch (Exception e) { TickGuard.Report("HoltburgGuards", e); return true; }
            finally { HoltburgGuards.Cost(System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
        }
    }

    /// <summary>A guard picks no target of its own; a monster fighting a guard is not pulled off it.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.FindNextTarget))]
    static class HoltburgGuardsNext
    {
        static bool Prefix(Creature __instance, ref bool __result)
        {
            if (HoltburgGuards.Owns(__instance)) { __result = false; return false; }
            // a monster fighting a guard keeps fighting it (ACE would look only for players, find none and walk home)
            var t = __instance.AttackTarget;
            if (t != null && HoltburgGuards.IsDutyGuard(t) && HoltburgGuards.FightPair(t, __instance)) { __result = true; return false; }
            return true;
        }
    }

    /// <summary>Trace only: each guard swing's roll, and each swing at a guard, in the log.</summary>
    [HarmonyPatch(typeof(DamageEvent), nameof(DamageEvent.CalculateDamage))]
    static class HoltburgGuardsRoll
    {
        static void Postfix(Creature attacker, Creature defender, DamageEvent __result)
        {
            try
            {
                if (__result == null || !(HoltburgGuards.Owns(attacker) || HoltburgGuards.Owns(defender)) || !HoltburgGuards.Tracing) return;
                Mod.Log.Info($"[RevivalGuard] HoltburgGuards swing: {attacker.Name} -> {defender.Name}: evade chance {__result.EvasionChance:0.00} {(__result.Evaded ? "evaded" : $"{__result.Damage:0.0} {__result.DamageType}")}");
            }
            catch { }
        }
    }

    /// <summary>The Royal Guard weenie has no body-part table: ACE's lookup would log an ERROR the first time a monster's
    /// swing reaches a guard. Answered here, silently, with ACE's own "no table" (null: the swing reads as evaded; the
    /// real blow is rolled by HoltburgGuards.Struck).</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.GetBodyParts))]
    static class HoltburgGuardsNoBodyTable
    {
        static bool Prefix(uint wcid, ref BodyPartTable __result)
        {
            if (wcid != HoltburgGuards.WCID) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>Last line of defence: a guard's swing at anything but a monster is dropped.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.MeleeAttack))]
    static class HoltburgGuardsSwing
    {
        static bool Prefix(Creature __instance, ref float __result)
        {
            if (!HoltburgGuards.Owns(__instance)) return true;
            var t = __instance.AttackTarget;
            if (t is Creature tc && !(t is Player) && !(t is Pet) && tc.PetOwner == null && tc.IsMonster && !HoltburgGuards.Owns(t)) return true;
            __instance.AttackTarget = null;
            __result = 0f;
            return false;
        }
    }

    /// <summary>A guard's damage is not written into the monster's damage history: experience (shared by damage), the
    /// kill message and the corpse's owner go to the players who fought it, in full.</summary>
    [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
    static class HoltburgGuardsCredit
    {
        static bool Prefix(WorldObject attacker) => !HoltburgGuards.IsDutyGuard(attacker) && !HoltburgGuards.Owns(attacker);
    }

    /// <summary>Trace only: who the experience of a monster a guard fought goes to (ACE shares it by recorded damage).</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class HoltburgGuardsCreditTrace
    {
        static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
        {
            try
            {
                if (__instance is Player || HoltburgGuards.Owns(__instance) || !HoltburgGuards.Tracing || !HoltburgGuards.Fought(__instance)) return;
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in __instance.DamageHistory.TotalDamage) parts.Add($"{kv.Value.Name} {kv.Value.TotalDamage:0}");
                Mod.Log.Info($"[RevivalGuard] HoltburgGuards credit: {__instance.Name} dies; recorded damage [{string.Join(", ", parts)}] of {__instance.DamageHistory.TotalHealth:0}; last damager {lastDamager?.Name ?? "none"}; xp {__instance.XpOverride ?? 0}");
            }
            catch { }
        }
    }

    /// <summary>A guard never dies: a blow is held to leave it one point, and a death that gets past that is undone.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(bool) })]
    static class HoltburgGuardsHurt
    {
        static void Prefix(Creature __instance, ref float amount)
        {
            if (!HoltburgGuards.Owns(__instance)) return;
            float room = Math.Max(0, (float)__instance.Health.Current - 1);
            if (amount > room) amount = room;
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class HoltburgGuardsNoDeath
    {
        static bool Prefix(Creature __instance, DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit, ref DeathMessage __result)
        {
            if (!HoltburgGuards.Owns(__instance)) return true;
            try { __instance.UpdateVital(__instance.Health, Math.Max(1u, __instance.Health.MaxValue / 4)); __result = __instance.GetDeathMessage(lastDamager, damageType, criticalHit); }
            catch (Exception e) { TickGuard.Report("HoltburgGuards.OnDeath", e); }
            return false;
        }
    }

    [HarmonyPatch(typeof(Creature), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
    static class HoltburgGuardsNoDie
    {
        static bool Prefix(Creature __instance)
        {
            if (!HoltburgGuards.Owns(__instance)) return true;
            try { if (__instance.Health.Current == 0) __instance.UpdateVital(__instance.Health, Math.Max(1u, __instance.Health.MaxValue / 4)); }
            catch (Exception e) { TickGuard.Report("HoltburgGuards.Die", e); }
            return false;
        }
    }

    /// <summary>Retail's heartbeat line ("All's quiet on this front!") is in the guards' own pool while the switch is on.</summary>
    /// (EmoteManager.HeartBeat is AggressiveInlining, so a patch there never runs: the heartbeat set is refused where it is
    /// looked up.)
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.GetEmoteSet), new[] { typeof(EmoteCategory), typeof(string), typeof(VendorType?), typeof(uint?), typeof(bool) })]
    static class HoltburgGuardsQuiet
    {
        static bool Prefix(EmoteManager __instance, EmoteCategory category, ref ACE.Entity.Models.PropertiesEmote __result)
        {
            if (category != EmoteCategory.HeartBeat) return true;
            try
            {
                if (!HoltburgGuards.Owns(__instance.WorldObject) || !HoltburgGuards.On) return true;
                __result = null;
                return false;
            }
            catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.ActOnUse))]
    static class HoltburgGuardsUse
    {
        static bool Prefix(Creature __instance, WorldObject worldObject)
        {
            if (!HoltburgGuards.Owns(__instance)) return true;
            try { return HoltburgGuards.OnUse(__instance, worldObject); }
            catch (Exception e) { TickGuard.Report("HoltburgGuards.Use", e); return true; }
        }
    }

    /// <summary>Local chat: ACE hands it to nearby creatures through EmoteManager.OnHearChat, a one-line method the JIT
    /// inlines (a patch there never ran on the box), so the hook is on the speaker's own Player.OnTalk.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnTalk), new[] { typeof(string) })]
    static class HoltburgGuardsHear
    {
        static void Postfix(Player __instance, string message)
        {
            try { HoltburgGuards.Heard(__instance, message); }
            catch (Exception e) { TickGuard.Report("HoltburgGuards.Hear", e); }
        }
    }
}
