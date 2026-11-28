using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using static RevivalGuard.TownLifeRule;
using R = RevivalGuard.DinerRule;

namespace RevivalGuard
{
    static partial class DinerPlaces
    {
        /// <summary>A place in the diner: full cell id, landblock metres, and a facing (degrees, 0 east, 90 north) or none.</summary>
        public readonly struct P
        {
            public readonly uint Cell; public readonly float X, Y, Z, Deg; public readonly bool Faces;
            public P(uint cell, float x, float y, float z) { Cell = cell; X = x; Y = y; Z = z; Deg = 0; Faces = false; }
            public P(uint cell, float x, float y, float z, float deg) { Cell = cell; X = x; Y = y; Z = z; Deg = deg; Faces = true; }

            /// <summary>An ACE position; forward is +y in the object's frame, so heading deg turns it by deg - 90.</summary>
            public Position Pos(float? deg = null, bool upsideDown = false)
            {
                float d = deg ?? (Faces ? Deg : 90f);
                double t = (d - 90) * Math.PI / 180;
                if (upsideDown) return new Position(Cell, X, Y, Z, 1, 0, 0, 0);
                return new Position(Cell, X, Y, Z, 0, 0, (float)Math.Sin(t / 2), (float)Math.Cos(t / 2));
            }
        }
    }

    /// <summary>The pancake house diner.</summary>
    static class Diner
    {
        internal const string P_ON = "rg_diner", P_LIFE = "rg_diner_life", P_SPEECH = "rg_diner_speech", P_BUFF = "rg_diner_buff",
            P_CONTEST = "rg_diner_contest", P_TIPS = "rg_diner_tips", P_COFFEE = "rg_diner_coffee", P_SIZZLE = "rg_diner_sizzle",
            P_GATE = "rg_diner_gate", P_TRACE = "rg_diner_trace";
        const string DAY_FILE = "diner-day.json";
        const float HEAR = 20f;

        static bool B(string k) { try { return PropertyManager.GetBool(k, true).Item; } catch { return true; } }
        internal static bool On => B(P_ON);
        static bool Life => On && B(P_LIFE);
        static bool Speech => On && B(P_SPEECH);
        static bool Tracing { get { try { return PropertyManager.GetLong(P_TRACE, 0).Item > 0; } catch { return false; } } }
        static double Now => Timers.RunningTime;
        static double Wall => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        static readonly object s_Lock = new object();
        static readonly Random s_Rng = Random.Shared;
        static readonly Budget s_Budget = new Budget(new Limits
        {
            LineGap = 60, NpcGap = 240, ExchangeGap = 600, GreetGap = 3600, AgainGap = 60, ArrivalGap = 1800, Screen = 6, TalkRange = 12, HearRange = HEAR,
        });

        // ---------------------------------------------------------------- registration
        static bool s_Registered;
        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard Diner: the Pancake House diner's staff, buff, contest and tips (false: a plain shop and retail Gladys; the gate stays)");
            PkCommon.RegisterBool(P_LIFE, true, "RevivalGuard Diner: staff walk, gesture and work; regulars sit in their booths by the hour; stools up at night");
            PkCommon.RegisterBool(P_SPEECH, true, "RevivalGuard Diner: the staff may speak unprompted (through the diner's speech budget)");
            PkCommon.RegisterBool(P_BUFF, true, "RevivalGuard Diner: a meal eaten at a booth or the counter gives Regeneration I and Rejuvenation I for 30 minutes");
            PkCommon.RegisterBool(P_CONTEST, true, "RevivalGuard Diner: the Pancake Challenge (a minute of contest pancakes, /top pancakes, the day's title)");
            PkCommon.RegisterBool(P_TIPS, true, "RevivalGuard Diner: pyreals given to Gladys are taken as a tip (a big one brings a free coffee)");
            PkCommon.RegisterBool(P_COFFEE, true, "RevivalGuard Diner: Diner Coffee clears a hangover like Ulgrim's cure");
            PkCommon.RegisterBool(P_SIZZLE, true, "RevivalGuard Diner: the griddle sizzles now and then while someone is near");
            PkCommon.RegisterBool(P_GATE, true, "RevivalGuard Diner: Gladys sends a player downstairs only with IHOPflag during the Spring Festival (false: retail, anyone who pays)");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_TRACE)) l[P_TRACE] = new Property<long>(0, "RevivalGuard Diner: 1 logs every staff step, line and contest");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Diner: {P_TRACE} not registered ({e.Message})"); }
            CommandManager.TryAddCommand(Cmd, "diner", AccessLevel.Developer, CommandHandlerFlag.None,
                "The Pancake House diner: status, force the hours (tests), start a contest for yourself, re-seat regulars and stools.",
                "[status | hours rush|midday|evening|night|auto | contest | reset]");
            LoadDay();
            Mod.Log.Info($"[RevivalGuard] Diner: {DinerPlaces.Tables.Length} booths, {DinerPlaces.Rounds.Length} rounds for Gladys ({P_ON}, {P_LIFE}, {P_GATE})");
        }

        // ---------------------------------------------------------------- the hour
        static string s_ForcedHours;
        static R.Hours HoursNow()
        {
            var f = s_ForcedHours;
            if (f != null && Enum.TryParse<R.Hours>(f, true, out var h)) return h;
            try { return R.HoursOf((int)Timers.CurrentInGameTime.Hour); } catch { return R.Hours.Midday; }
        }

        static TimeZoneInfo s_Tz;
        static int Today()
        {
            try { s_Tz ??= TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch { s_Tz = TimeZoneInfo.Utc; }
            return R.DayNumber(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, s_Tz));
        }

        // ---------------------------------------------------------------- staff
        enum Role { Gladys, Hostess, Cook, Clerk, Regular }

        sealed class Staff
        {
            public Creature Obj; public Role Role; public string Key; public Position Spawn;   // where it stood when first seen (its post)
            public double NextThink, NextLine, NextGesture, MotionUntil, UsedAt, NextBell, NextSizzle;
            public int LastGesture = -1;
            // Gladys's walk: Round -1 = at her post
            public int Round = -1, Index = -1, LastRound = -1, ForceRound = -1;
            public bool Back, Paused, Held;
            public double Until, ProgressAt; public float Best = float.MaxValue; public int Retries;
        }

        static readonly Dictionary<uint, Staff> s_Staff = new Dictionary<uint, Staff>();
        static readonly Dictionary<uint, Creature> s_Regulars = new Dictionary<uint, Creature>();   // wcid -> the one seated now
        static readonly List<WorldObject> s_Stools = new List<WorldObject>();
        static bool? s_StoolsUp;
        static double s_NextDirector;
        static Staff s_Gladys;

        static readonly MotionCommand[] G_HOSTESS = { MotionCommand.Nod, MotionCommand.TapFoot, MotionCommand.Wave };
        static readonly MotionCommand[] G_COOK = { MotionCommand.WarmHands, MotionCommand.Pickup, MotionCommand.Nod, MotionCommand.WarmHands };
        static readonly MotionCommand[] G_CLERK = { MotionCommand.Nod, MotionCommand.Shrug, MotionCommand.Point };
        static readonly MotionCommand[] G_GLADYS = { MotionCommand.Nod, MotionCommand.BowDeep };

        internal static bool Mine(Creature c)
        {
            if (c?.Location == null || c.Location.Landblock != DinerPlaces.LB) return false;
            uint w = c.WeenieClassId;
            return w == R.GLADYS || w == DinerPlaces.HOSTESS || w == DinerPlaces.COOK || w == DinerPlaces.CLERK || Array.IndexOf(DinerPlaces.REGULARS, w) >= 0;
        }

        static Staff Get(Creature c)
        {
            lock (s_Lock)
            {
                if (s_Staff.TryGetValue(c.Guid.Full, out var s) && ReferenceEquals(s.Obj, c)) return s;
                uint w = c.WeenieClassId;
                var role = w == R.GLADYS ? Role.Gladys : w == DinerPlaces.HOSTESS ? Role.Hostess : w == DinerPlaces.COOK ? Role.Cook
                         : w == DinerPlaces.CLERK ? Role.Clerk : Role.Regular;
                double now = Now;
                s = new Staff
                {
                    Obj = c, Role = role, Spawn = new Position(c.Location), Key = role == Role.Regular ? "regular" + (Array.IndexOf(DinerPlaces.REGULARS, w) + 1) : role.ToString().ToLowerInvariant(),
                    NextLine = now + 30 + s_Rng.NextDouble() * 90, NextGesture = now + 20 + s_Rng.NextDouble() * 60,
                    NextBell = now + 60 + s_Rng.NextDouble() * 120, NextSizzle = now + 15 + s_Rng.NextDouble() * 30,
                    Until = now + 20 + s_Rng.NextDouble() * 30,
                };
                s_Staff[c.Guid.Full] = s;
                if (role == Role.Gladys) s_Gladys = s;
                return s;
            }
        }

        static List<Listener> Listeners(WorldObject c, float range, out Player nearest)
        {
            var l = new List<Listener>(); nearest = null; float best = float.MaxValue;
            if (c?.PhysicsObj == null || c.Location == null || c.PhysicsObj.ObjMaint.GetKnownPlayersCount() == 0) return l;
            foreach (var p in c.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
            {
                if (p?.Location == null || p.Teleporting) continue;
                float d = c.Location.DistanceTo(p.Location);
                if (d > range) continue;
                l.Add(new Listener(p.Guid.Full, d));
                if (d < best) { best = d; nearest = p; }
            }
            return l;
        }

        /// <summary>The once-a-second think of one diner NPC (its Monster_Tick).</summary>
        internal static void Tick(Creature c)
        {
            var s = Get(c);
            double now = Now;
            if (now < s.NextThink) return;
            s.NextThink = now + 1.0;
            if (s.Role == Role.Gladys && now >= s_NextDirector) { s_NextDirector = now + 5; Director(c, now); }
            var ls = Listeners(c, HEAR, out var nearest);
            if (!Life)
            {
                // switched off: Gladys finishes the walk back to her post, then everyone stands as in retail
                if (s.Role == Role.Gladys && s.Round >= 0) { if (!s.Back) GoHome(s, now, "switched off"); Walk(s, now, ls, true); }
                return;
            }
            switch (s.Role)
            {
                case Role.Gladys: Walk(s, now, ls, false); break;
                case Role.Cook: Cook(s, now, ls); break;
                case Role.Hostess: Hostess(s, now, ls); break;
            }
            if (ls.Count > 0) Gesture(s, now, ls);
            if (ls.Count > 0 && Speech) Ambient(s, now, ls);
        }

        // ---- Gladys's rounds of the tables
        static float D2(Position a, DinerPlaces.P b) => MathF.Sqrt((a.PositionX - b.X) * (a.PositionX - b.X) + (a.PositionY - b.Y) * (a.PositionY - b.Y));

        static DinerPlaces.P Target(Staff s) => s.Index < 0 ? DinerPlaces.GladysPost : DinerPlaces.Rounds[s.Round].Path[s.Index];

        static void MoveTo(Staff s, DinerPlaces.P p, double now)
        {
            var c = s.Obj;
            var from = c.Location;
            float dx = p.X - from.PositionX, dy = p.Y - from.PositionY;
            float deg = MathF.Atan2(dy, dx) * 180 / MathF.PI;
            c.MoveTo(p.Pos(deg), 0f);
            s.ProgressAt = now; s.Best = float.MaxValue;
            if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner: Gladys to {p.X:0.0},{p.Y:0.0} (round {(s.Round >= 0 ? DinerPlaces.Rounds[s.Round].Name : "post")} #{s.Index}{(s.Back ? " back" : "")})");
        }

        static void GoHome(Staff s, double now, string why)
        {
            if (s.Round < 0) return;
            // back the way she came: the point before the one she was at or walking to (-1 is her post)
            s.Back = true; s.Paused = false; s.Index = Math.Max(-1, s.Index - 1);
            MoveTo(s, Target(s), now);
            if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner: Gladys heads back ({why})");
        }

        static void Walk(Staff s, double now, List<Listener> ls, bool summon)
        {
            var c = s.Obj;
            bool busy = c.EmoteManager?.IsBusy ?? false;
            bool hold = now - s.UsedAt < 45 || busy && now - s.UsedAt < 300;
            if (hold)
            {
                if (!s.Held && s.Round >= 0 && (c.PhysicsObj?.IsMovingTo() ?? false)) { try { c.CancelMoveTo(); } catch { } }
                s.Held = true;
                return;
            }
            if (s.Held) { s.Held = false; if (s.Round >= 0) { if (!s.Back) GoHome(s, now, "business over"); else MoveTo(s, Target(s), now); return; } }
            if (now < s.MotionUntil) return;
            if (s.Round < 0)
            {
                if (summon || now < s.Until && s.ForceRound < 0) return;
                bool anyone = ls.Count > 0 || c.PhysicsObj?.ObjMaint.GetKnownPlayersCount() > 0;
                if (!anyone) { s.Until = now + 30; return; }
                int k = s.ForceRound >= 0 ? s.ForceRound : PickRound(s);
                s.ForceRound = -1;
                if (k < 0) { s.Until = now + R.PostWait(HoursNow(), s_Rng.NextDouble()); return; }
                s.Round = k; s.LastRound = k; s.Index = 0; s.Back = false; s.Paused = false; s.Retries = 0;
                MoveTo(s, Target(s), now);
                return;
            }
            var path = DinerPlaces.Rounds[s.Round].Path;
            if (s.Paused)
            {
                if (now < s.Until && !summon) return;
                s.Paused = false; s.Back = true; s.Index--;
                MoveTo(s, Target(s), now);
                return;
            }
            var t = Target(s);
            float dist = D2(c.Location, t);
            bool moving = c.PhysicsObj?.IsMovingTo() ?? false;
            if (dist <= 1.2f && !moving || dist <= 0.6f)
            {
                if (!s.Back && s.Index < path.Length - 1) { s.Index++; MoveTo(s, Target(s), now); return; }
                if (!s.Back)
                {
                    // at the table: a pause, a word for whoever sits there, a nod
                    s.Paused = true; s.Until = now + 6 + s_Rng.NextDouble() * 6;
                    AtTable(s, now, ls);
                    return;
                }
                if (s.Index >= 0) { s.Index--; MoveTo(s, Target(s), now); return; }
                // home
                s.Round = -1; s.Back = false; s.Until = now + R.PostWait(HoursNow(), s_Rng.NextDouble());
                { var face = new Position(c.Location); face.Rotation = s.Spawn.Rotation; c.TurnTo(Ahead(face)); }
                return;
            }
            if (dist < s.Best - 0.3f) { s.Best = dist; s.ProgressAt = now; return; }
            if (now - s.ProgressAt < 8) return;
            if (s.Retries++ < 2) { MoveTo(s, t, now); return; }
            // stuck for good: home at once (no one near to see it) or after a while anyway
            bool seen = ls.Any(l => l.Dist < 15);
            if (!seen || now - s.ProgressAt > 25)
            {
                if (c.Location.Landblock == s.Spawn.Landblock) c.FakeTeleport(new Position(s.Spawn));
                s.Round = -1; s.Back = false; s.Retries = 0; s.Until = now + R.PostWait(HoursNow(), s_Rng.NextDouble());
                Mod.Log.Info($"[RevivalGuard] Diner: Gladys stuck at {c.Location.PositionX:0.0},{c.Location.PositionY:0.0} on her way to {t.X:0.0},{t.Y:0.0}: sent back to her post");
            }
        }

        static int PickRound(Staff s)
        {
            var rounds = DinerPlaces.Rounds;
            var fit = new List<int>();
            for (int i = 0; i < rounds.Length; i++)
            {
                if (i == s.LastRound) continue;
                string n = rounds[i].Name;
                if (n == "tsuyoshi" && !RegularIn(DinerPlaces.REGULARS[0])) continue;
                if (n == "mei" && !RegularIn(DinerPlaces.REGULARS[1])) continue;
                if (n == "south-row" && !RegularIn(DinerPlaces.REGULARS[2]) && s_Rng.NextDouble() < 0.5) continue;
                fit.Add(i);
            }
            return fit.Count == 0 ? -1 : fit[s_Rng.Next(fit.Count)];
        }

        static bool RegularIn(uint wcid) { lock (s_Lock) return s_Regulars.TryGetValue(wcid, out var r) && r != null && !r.IsDestroyed; }

        static void AtTable(Staff s, double now, List<Listener> ls)
        {
            string name = DinerPlaces.Rounds[s.Round].Name;
            if (name == "counter")
            {
                if (Speech && Budgeted(s, ls, now)) Say(s, Pick(R.GLADYS_COUNTER), "counter");
                return;
            }
            Play(s, MotionCommand.Nod, 0.2, "table");
            if (!Speech) return;
            // the line is for a player sitting at this table when there is one, else (rarely) the room
            var stop = DinerPlaces.Rounds[s.Round].Path[^1];
            bool customer = false;
            foreach (var p in s.Obj.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                if (p?.Location != null && D2(p.Location, stop) < 3.5f) { customer = true; break; }
            if ((customer || s_Rng.NextDouble() < 0.3) && Budgeted(s, ls, now)) Say(s, Pick(R.GLADYS_TABLE), "table");
        }

        static Position Ahead(Position p)
        {
            var q = p.Rotation; var fwd = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0, 1, 0), q);
            var a = new Position(p); a.PositionX += fwd.X; a.PositionY += fwd.Y;
            return a;
        }

        // ---- the fry cook: the griddle, the sizzle, the bell
        static void Cook(Staff s, double now, List<Listener> ls)
        {
            if (ls.Count == 0) return;
            var c = s.Obj;
            if (now >= s.NextSizzle)
            {
                s.NextSizzle = now + 90 + s_Rng.NextDouble() * 90;
                if (B(P_SIZZLE) && On) c.EnqueueBroadcast(new GameMessageSound(c.Guid, Sound.Fizzle, 0.35f), HEAR);
            }
            if (now >= s.NextBell && now >= s.MotionUntil)
            {
                var h = HoursNow();
                s.NextBell = now + (h == R.Hours.Rush ? 180 : h == R.Hours.Night ? 900 : 360) * (1 + 0.6 * s_Rng.NextDouble());
                c.EnqueueBroadcast(new GameMessageSound(c.Guid, Sound.UI_Bell, 0.8f), HEAR);
                if (Speech && Budgeted(s, ls, now)) Say(s, Pick(R.COOK_BELL), "bell");
                var g = s_Gladys;
                if (g != null && !g.Obj.IsDestroyed && g.Round < 0) g.ForceRound = Array.FindIndex(DinerPlaces.Rounds, r => r.Name == "counter");
            }
        }

        // ---- the hostess: a greeting for whoever comes in
        static void Hostess(Staff s, double now, List<Listener> ls)
        {
            if (!Speech) return;
            var c = s.Obj;
            foreach (var l in ls)
            {
                if (l.Dist > 9) continue;
                bool ok;
                lock (s_Lock) ok = s_Budget.TryGreet(c.Guid.Full, l.Id, now, arrival: true);
                if (!ok) continue;
                var p = PlayerManager.GetOnlinePlayer(l.Id);
                if (p == null) continue;
                lock (s_Lock) s_Budget.PromptedLine(ls, now);
                double turn = 0;
                try { turn = c.Rotate(p); } catch { }
                Play(s, MotionCommand.Wave, turn + 0.1, "greet");
                var hrs = HoursNow();
                var line = hrs == R.Hours.Night ? Pick(R.HOSTESS_NIGHT) : hrs == R.Hours.Rush && s_Rng.NextDouble() < 0.5 ? Pick(R.HOSTESS_RUSH) : Pick(R.HOSTESS_GREET);
                SayLater(s, turn + 0.4, line, "greet");
                break;
            }
        }

        // ---- unprompted lines (the budget) and gestures
        static bool Budgeted(Staff s, List<Listener> ls, double now)
        {
            lock (s_Lock) return s_Budget.TryAmbient(s.Obj.Guid.Full, ls, now);
        }

        static void Ambient(Staff s, double now, List<Listener> ls)
        {
            if (now < s.NextLine) return;
            s.NextLine = NextLine(now, s_Budget.L.NpcGap, s_Rng.NextDouble());
            if (s.Role == Role.Clerk && s_Rng.NextDouble() < 0.5 && Budgeted(s, ls, now)) { Say(s, Pick(R.CLERK_IDLE), "idle"); return; }
            if (s.Role != Role.Regular && s.Role != Role.Gladys) return;
            // an exchange: one of the regulars starts it, Gladys or another regular answers (both must be in the room)
            double nowEx = now;
            foreach (var ex in R.EXCHANGES.OrderBy(_ => s_Rng.Next()))
            {
                if (ex[0].Who != s.Key) continue;
                var other = Find(ex[1].Who);
                if (other == null) continue;
                bool ok;
                lock (s_Lock) ok = s_Budget.TryExchange(s.Obj.Guid.Full, other.Obj.Guid.Full, ls, nowEx, 9);
                if (!ok) return;
                Say(s, ex[0].Line, "exchange");
                SayLater(other, 3.5, ex[1].Line, "exchange");
                return;
            }
        }

        static Staff Find(string key)
        {
            lock (s_Lock)
                foreach (var s in s_Staff.Values)
                    if (s.Key == key && s.Obj != null && !s.Obj.IsDestroyed && s.Obj.Location?.Landblock == DinerPlaces.LB) return s;
            return null;
        }

        static void Gesture(Staff s, double now, List<Listener> ls)
        {
            if (now < s.NextGesture || s.Obj.EmoteManager?.IsBusy == true) return;
            s.NextGesture = NextGesture(now, s.Role == Role.Cook ? 1.5 : 3, s_Rng.NextDouble());
            if (s.Role == Role.Regular) { Eat(s); return; }
            var g = s.Role switch { Role.Cook => G_COOK, Role.Hostess => G_HOSTESS, Role.Clerk => G_CLERK, _ => G_GLADYS };
            if (s.Role == Role.Gladys && s.Round >= 0) return;
            if (ls.All(l => l.Dist > 30)) return;
            int k = PickGesture(g.Length, s.LastGesture, s_Rng.NextDouble());
            if (k < 0) return;
            s.LastGesture = k;
            Play(s, g[k], 0, "idle");
        }

        /// <summary>A regular at the booth: a bite (MimeEat) and back to sitting.</summary>
        static void Eat(Staff s)
        {
            var c = s.Obj;
            float len = Length(c, MotionCommand.MimeEat);
            if (len <= 0) return;
            try
            {
                c.ExecuteMotion(new Motion(MotionStance.NonCombat, MotionCommand.MimeEat), true, WorldObject.LocalBroadcastRange);
                var back = new ActionChain();
                back.AddDelaySeconds(len);
                back.AddAction(c, () => { try { if (!c.IsDestroyed) Sit(c); } catch (Exception e) { TickGuard.Report("Diner.Eat", e); } });
                back.EnqueueChain();
            }
            catch (Exception e) { TickGuard.Report("Diner.Eat", e); }
        }

        static float Length(Creature c, MotionCommand cmd)
        {
            try
            {
                var mt = ACE.DatLoader.DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.MotionTable>(c.MotionTableId);
                return mt?.GetAnimationLength(MotionStance.NonCombat, cmd, MotionCommand.Ready) ?? 0;
            }
            catch { return 0; }
        }

        static bool Play(Staff s, MotionCommand cmd, double delay, string why)
        {
            var c = s.Obj;
            float len = Length(c, cmd);
            if (len <= 0) return false;
            s.MotionUntil = Math.Max(s.MotionUntil, Now + delay + len + 0.4);
            var chain = new ActionChain();
            chain.AddDelaySeconds(Math.Max(0, delay));
            chain.AddAction(c, () =>
            {
                try
                {
                    if (c.IsDestroyed || (c.PhysicsObj?.IsMovingTo() ?? true)) return;
                    c.ExecuteMotion(new Motion(MotionStance.NonCombat, cmd), true, WorldObject.LocalBroadcastRange);
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner gesture: {s.Key} {cmd} ({why})");
                    var back = new ActionChain();
                    back.AddDelaySeconds(len);
                    back.AddAction(c, () =>
                    {
                        try { if (!c.IsDestroyed && c.CurrentMotionState?.MotionState.ForwardCommand == cmd) c.ExecuteMotion(new Motion(MotionStance.NonCombat, MotionCommand.Ready), true, WorldObject.LocalBroadcastRange); }
                        catch (Exception e) { TickGuard.Report("Diner.Gesture", e); }
                    });
                    back.EnqueueChain();
                }
                catch (Exception e) { TickGuard.Report("Diner.Gesture", e); }
            });
            chain.EnqueueChain();
            return true;
        }

        static string Pick(string[] a) => a[s_Rng.Next(a.Length)];

        static void Say(Staff s, string text, string kind)
        {
            var c = s.Obj;
            if (string.IsNullOrEmpty(text) || c == null || c.IsDestroyed) return;
            c.EnqueueBroadcast(new GameMessageHearSpeech(text, c.Name, c.Guid.Full, ChatMessageType.Speech), HEAR);
            if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner said ({kind}): {s.Key} \"{text}\"");
        }

        static void SayLater(Staff s, double delay, string text, string kind)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(s.Obj, () => { try { Say(s, text, kind); } catch (Exception e) { TickGuard.Report("Diner.Say", e); } });
            chain.EnqueueChain();
        }

        /// <summary>A line to one player from an NPC, as a retail Tell (an answer: not counted against the budget).</summary>
        static void Tell(WorldObject npc, Player p, string text)
        {
            if (npc == null || p?.Session == null) return;
            p.Session.Network.EnqueueSend(new GameEventTell(npc, text, p, ChatMessageType.Tell));
        }

        /// <summary>Retail heartbeat chatter of Gladys ("Do you need your coffee refreshed, hon?") goes through the budget.</summary>
        internal static bool TryAmbient(WorldObject npc)
        {
            if (!Speech) return !On;    // with the diner off, retail's own chatter as it was
            var ls = Listeners(npc, HEAR, out _);
            lock (s_Lock) return s_Budget.TryAmbient(npc.Guid.Full, ls, Now);
        }

        // ---------------------------------------------------------------- the director: regulars by the hour, the stools
        static ACE.Server.Entity.Landblock s_SweptLb;

        internal static bool Transient(WorldObject wo) =>
            wo != null && (wo.WeenieClassId == DinerPlaces.STOOL || Array.IndexOf(DinerPlaces.REGULARS, wo.WeenieClassId) >= 0);

        static void Director(Creature gladys, double now)
        {
            var lb = gladys.CurrentLandblock;
            if (lb == null) return;
            if (!ReferenceEquals(s_SweptLb, lb))
            {
                // a fresh load of the block: the hour seats its own
                s_SweptLb = lb;
                lock (s_Lock) { s_Regulars.Clear(); s_Stools.Clear(); }
                s_StoolsUp = null;
            }
            // anything of ours in the block we did not seat goes
            HashSet<uint> mine;
            lock (s_Lock) mine = new HashSet<uint>(s_Regulars.Values.Where(r => r != null).Select(r => r.Guid.Full).Concat(s_Stools.Where(o => o != null).Select(o => o.Guid.Full)));
            int gone = 0;
            foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                if (Transient(wo) && !wo.IsDestroyed && !mine.Contains(wo.Guid.Full)) { try { wo.Destroy(); gone++; } catch { } }
            if (gone > 0) Mod.Log.Info($"[RevivalGuard] Diner: cleared {gone} regulars and stools the diner did not seat");
            var h = HoursNow();
            int want = Life ? R.RegularsIn(h) : 0;
            for (int i = 0; i < DinerPlaces.REGULARS.Length; i++)
            {
                uint w = DinerPlaces.REGULARS[i];
                Creature have;
                lock (s_Lock) s_Regulars.TryGetValue(w, out have);
                bool present = have != null && !have.IsDestroyed && have.CurrentLandblock == lb;
                if (i < want && !present) Seat(w);
                else if (i >= want && present)
                {
                    lock (s_Lock) s_Regulars.Remove(w);
                    have.Destroy();
                    if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner: {have.Name} goes home ({h})");
                }
            }
            bool up = Life && R.StoolsUp(h);
            bool stoolsThere;
            lock (s_Lock) stoolsThere = s_Stools.Count > 0 && s_Stools.All(o => o != null && !o.IsDestroyed && o.CurrentLandblock == lb);
            if (s_StoolsUp != up || !stoolsThere) Stools(up);
        }

        static void Seat(uint wcid)
        {
            if (!DinerPlaces.Seats.TryGetValue(wcid, out var seat)) return;
            if (!(WorldObjectFactory.CreateNewWorldObject(wcid) is Creature c)) { Mod.Log.Warn($"[RevivalGuard] Diner: regular {wcid} is not in the world database (datafix not applied?)"); return; }
            c.Location = seat.Pos();
            if (!c.EnterWorld()) { Mod.Log.Warn($"[RevivalGuard] Diner: {c.Name} could not sit at {seat.X:0.0},{seat.Y:0.0}"); return; }
            // feet at the bench's front edge
            float dx = c.Location.PositionX - seat.X, dy = c.Location.PositionY - seat.Y;
            if (dx * dx + dy * dy > 0.0025f && c.PhysicsObj != null)
            {
                c.PhysicsObj.set_current_pos(new ACE.Server.Physics.Common.Position(seat.Pos()));
                c.SyncLocation();
                c.SendUpdatePosition(true);
                if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner: {c.Name} slid {Math.Sqrt(dx * dx + dy * dy):0.00} m by placement; back on the bench");
            }
            lock (s_Lock) s_Regulars[wcid] = c;
            var chain = new ActionChain();
            chain.AddDelaySeconds(1.5);
            chain.AddAction(c, () => { try { if (!c.IsDestroyed) Sit(c); } catch (Exception e) { TickGuard.Report("Diner.Sit", e); } });
            chain.EnqueueChain();
            if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner: {c.Name} sits down at {seat.X:0.0},{seat.Y:0.0}");
        }

        static void Sit(Creature c) => c.ExecuteMotion(new Motion(MotionStance.NonCombat, MotionCommand.SitState), true, WorldObject.LocalBroadcastRange);

        static void Stools(bool up)
        {
            List<WorldObject> old;
            lock (s_Lock) { old = new List<WorldObject>(s_Stools); s_Stools.Clear(); }
            foreach (var o in old) try { if (o != null && !o.IsDestroyed) o.Destroy(); } catch { }
            var at = up ? DinerPlaces.StoolsUp : DinerPlaces.Stools;
            int made = 0;
            foreach (var p in at)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(DinerPlaces.STOOL);
                if (wo == null) break;
                wo.Location = p.Pos(up ? null : 90f, upsideDown: up);
                if (wo.EnterWorld()) { lock (s_Lock) s_Stools.Add(wo); made++; }
            }
            s_StoolsUp = up;
            if (Tracing || made < at.Length) Mod.Log.Info($"[RevivalGuard] Diner: {made} of {at.Length} stools {(up ? "up on the counter" : "at the counter")}");
        }

        // ---------------------------------------------------------------- eating: the buff, the special, the contest
        static bool InDiner(Player p) => p?.Location != null && p.Location.Landblock == DinerPlaces.LB;

        static readonly (float x, float y)[] s_TablesXY = DinerPlaces.Tables.Select(t => (t.X, t.Y)).ToArray();
        static readonly (float x, float y)[] s_StoolsXY = DinerPlaces.Stools.Select(t => (t.X, t.Y)).ToArray();

        static bool Seated(Player p) => InDiner(p) && R.Seated(p.Location.PositionX, p.Location.PositionY, s_TablesXY, s_StoolsXY,
            DinerPlaces.HALL_X0, DinerPlaces.HALL_Y0, DinerPlaces.HALL_X1, DinerPlaces.HALL_Y1);

        static readonly Dictionary<uint, int> s_SpecialDay = new Dictionary<uint, int>();    // player -> the day they had the free coffee

        /// <summary>Food.ApplyConsumable has eaten it.</summary>
        internal static void Ate(Player p, uint wcid)
        {
            if (p == null || p.IsDead || !On) return;
            if (wcid == R.CONTEST_PANCAKE) { Bite(p); return; }
            if (wcid == R.CHALLENGE) { StartContest(p, false); return; }
            if (!R.IsMeal(wcid)) return;
            if (!Seated(p)) { if (InDiner(p) && B(P_BUFF)) PkCommon.Say(p, R.NOT_SEATED_LINE); return; }
            if (B(P_BUFF))
            {
                bool a = Hold(p, R.REGEN_I, R.FED_S), b = Hold(p, R.REJUV_I, R.FED_S);
                if (a || b) PkCommon.Say(p, R.FED_LINE, ChatMessageType.Magic);
            }
            int today = Today();
            if (wcid == R.Special(today))
            {
                bool first;
                lock (s_Lock) { first = !s_SpecialDay.TryGetValue(p.Guid.Full, out var d) || d != today; if (first) s_SpecialDay[p.Guid.Full] = today; }
                if (first) GiveCoffee(p, "Gladys sets down a coffee for you. \"It comes with the special, hon.\"");
            }
        }

        static void GiveCoffee(Player p, string line)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(R.COFFEE);
            if (wo == null) return;
            if (p.TryCreateInInventoryWithNetworking(wo)) PkCommon.Say(p, line);
            else wo.Destroy();
        }

        /// <summary>A self spell held for secs (Drinking's Hold): the same spell again only refreshes it.</summary>
        static bool Hold(Player p, uint id, double secs)
        {
            try
            {
                var spell = new Spell(id);
                if (spell.NotFound) return false;
                var r = p.EnchantmentManager.Add(spell, p, null);
                if (r?.Enchantment == null) return false;
                r.Enchantment.Duration = secs;
                p.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(p.Session, new ACE.Server.Network.Structure.Enchantment(p, r.Enchantment)));
                p.HandleSpellHooks(spell);
                return true;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Diner: hold {id}: {e.Message}"); return false; }
        }

        // ---- the Pancake Challenge
        sealed class Contest { public double Until; public int Eaten; public int Token; }
        static readonly Dictionary<uint, Contest> s_Contests = new Dictionary<uint, Contest>();
        static int s_Token;
        static (int day, uint id, string name, int count) s_DayBest;

        static void StartContest(Player p, bool admin)
        {
            if (!B(P_CONTEST)) { if (!admin) PkCommon.Say(p, "The Pancake Challenge is closed today. That was a very nice plate of pancakes, though."); return; }
            if (!InDiner(p)) { PkCommon.Say(p, "The Pancake Challenge only counts inside the Tanada House of Pancakes. That was just a nice plate of pancakes."); return; }
            Contest c;
            lock (s_Lock)
            {
                if (s_Contests.TryGetValue(p.Guid.Full, out var cur) && Now < cur.Until) { PkCommon.Say(p, "You are already in the middle of the challenge. Eat!"); return; }
                c = new Contest { Until = Now + R.CONTEST_S, Token = ++s_Token };
                s_Contests[p.Guid.Full] = c;
            }
            p.TryConsumeFromInventoryWithNetworking(R.CONTEST_PANCAKE);    // leftovers from an earlier try
            var stack = WorldObjectFactory.CreateNewWorldObject(R.CONTEST_PANCAKE);
            if (stack == null) { PkCommon.Say(p, "The kitchen is out of contest pancakes (the datafix is missing)."); return; }
            stack.SetStackSize(R.CONTEST_STACK);
            if (!p.TryCreateInInventoryWithNetworking(stack)) { stack.Destroy(); PkCommon.Say(p, "Your pack is too full for the contest pancakes."); lock (s_Lock) s_Contests.Remove(p.Guid.Full); return; }
            PkCommon.Say(p, R.StartLine);
            int token = c.Token;
            After(p, 30, () => { if (Running(p, token)) PkCommon.Say(p, R.HalfLine); });
            After(p, 50, () => { if (Running(p, token)) PkCommon.Say(p, R.LastLine); });
            After(p, R.CONTEST_S, () => EndContest(p, token));
            if (Tracing) Mod.Log.Info($"[RevivalGuard] Diner: {p.Name} starts the Pancake Challenge");
        }

        static bool Running(Player p, int token) { lock (s_Lock) return s_Contests.TryGetValue(p.Guid.Full, out var c) && c.Token == token; }

        static void Bite(Player p)
        {
            lock (s_Lock)
                if (s_Contests.TryGetValue(p.Guid.Full, out var c) && Now <= c.Until + 0.5) c.Eaten++;
        }

        static void After(Player p, double secs, Action a)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(secs);
            chain.AddAction(p, () => { try { a(); } catch (Exception e) { TickGuard.Report("Diner.Contest", e); } });
            chain.EnqueueChain();
        }

        static void EndContest(Player p, int token)
        {
            Contest c;
            lock (s_Lock)
            {
                if (!s_Contests.TryGetValue(p.Guid.Full, out c) || c.Token != token) return;
                s_Contests.Remove(p.Guid.Full);
            }
            if (p.Session != null) p.TryConsumeFromInventoryWithNetworking(R.CONTEST_PANCAKE);
            int best = p.QuestManager.GetCurrentSolves(R.BEST_FLAG);
            PkCommon.Say(p, R.ResultLine(c.Eaten, best));
            if (c.Eaten > best) p.QuestManager.SetQuestCompletions(R.BEST_FLAG, c.Eaten);
            int today = Today();
            bool champion;
            lock (s_Lock)
            {
                champion = R.NewDayBest(s_DayBest.day, today, s_DayBest.count, c.Eaten);
                if (champion) s_DayBest = (today, p.Guid.Full, p.Name, c.Eaten);
            }
            if (champion)
            {
                SaveDay();
                if (Enum.TryParse<CharacterTitle>(R.TITLE, out var title)) p.AddTitle(title);
                var cook = Find("cook");
                string line = R.ChampionLine(p.Name, c.Eaten);
                if (cook != null) cook.Obj.EnqueueBroadcast(new GameMessageHearSpeech(line, cook.Obj.Name, cook.Obj.Guid.Full, ChatMessageType.Speech), HEAR * 1.5f);
                else PkCommon.Say(p, line);
            }
            Mod.Log.Info($"[RevivalGuard] Diner: {p.Name} ate {c.Eaten} pancakes in a minute (best {Math.Max(best, c.Eaten)}{(champion ? ", the day's best" : "")})");
        }

        static string DayPath => Path.Combine(ModFolder.Path(), DAY_FILE);

        static void LoadDay()
        {
            try
            {
                if (!File.Exists(DayPath)) return;
                var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(DayPath));
                s_DayBest = (d["day"].GetInt32(), d["id"].GetUInt32(), d["name"].GetString(), d["count"].GetInt32());
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Diner: {DAY_FILE} not read ({e.Message})"); }
        }

        static void SaveDay()
        {
            var b = s_DayBest;
            var text = JsonSerializer.Serialize(new { day = b.day, id = b.id, name = b.name, count = b.count });
            System.Threading.Tasks.Task.Run(() =>
            {
                try { File.WriteAllText(DayPath + ".tmp", text); File.Move(DayPath + ".tmp", DayPath, true); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Diner: {DAY_FILE} not saved ({e.Message})"); }
            });
        }

        // ---------------------------------------------------------------- Use: Gladys's way downstairs, the special
        /// <summary>WorldObject.OnActivate, before: false stops retail's Use (Gladys's pay-and-go-downstairs).</summary>
        internal static bool MayUse(WorldObject target, Player p)
        {
            if (target.WeenieClassId != R.GLADYS || target.Location?.Landblock != DinerPlaces.LB) return true;
            lock (s_Lock) if (s_Staff.TryGetValue(target.Guid.Full, out var s)) s.UsedAt = Now;
            if (!B(P_GATE)) return true;
            bool flag = p.QuestManager.HasQuest(R.FLAG);
            bool spring = false;
            try { spring = EventManager.IsEventStarted(R.SPRING_EVENT, null, null); } catch { }
            if (R.DownstairsOpen(flag, spring)) return true;
            if (target is Creature c) { try { c.Rotate(p); } catch { } }
            Tell(target, p, flag ? R.KITCHEN_OFF_SEASON : R.KITCHEN_CLOSED);
            return false;
        }

        /// <summary>WorldObject.OnActivate, after: Hana and Bo Lin name the special; any Use holds a walking NPC.</summary>
        internal static void Used(WorldObject target, Player p)
        {
            if (target?.Location?.Landblock != DinerPlaces.LB || !On) return;
            lock (s_Lock) if (s_Staff.TryGetValue(target.Guid.Full, out var s)) s.UsedAt = Now;
            uint w = target.WeenieClassId;
            if (w == DinerPlaces.HOSTESS || w == DinerPlaces.CLERK)
            {
                var chain = new ActionChain();
                chain.AddDelaySeconds(w == DinerPlaces.HOSTESS ? 3.0 : 0.6);
                chain.AddAction(target, () => { try { Tell(target, p, R.SpecialLine(Today())); } catch (Exception e) { TickGuard.Report("Diner.Use", e); } });
                chain.EnqueueChain();
            }
        }

        // ---------------------------------------------------------------- tips
        static readonly Dictionary<uint, double> s_Refill = new Dictionary<uint, double>();

        /// <summary>Player.GiveObjectToNPC, before: pyreals given to Gladys are a tip, taken whole.</summary>
        internal static bool Give(Player p, WorldObject target, WorldObject item, int amount)
        {
            if (!On || !B(P_TIPS) || target?.WeenieClassId != R.GLADYS || item?.WeenieClassId != R.PYREAL || target.Location?.Landblock != DinerPlaces.LB) return true;
            if (amount <= 0 || amount > (item.StackSize ?? 1)) return true;
            int offered = amount;
            amount = R.TipTaken(offered);
            if (!p.TryConsumeFromInventoryWithNetworking(item, amount)) return true;
            p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You give {target.Name} {amount:N0} pyreal{(amount == 1 ? "" : "s")}.", ChatMessageType.Broadcast));
            target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.ReceiveItem));
            lock (s_Lock) if (s_Staff.TryGetValue(target.Guid.Full, out var s)) s.UsedAt = Now;
            var tier = R.TipOf(amount);
            bool refill = false;
            if (tier == R.Tip.Big)
                lock (s_Lock)
                {
                    double now = Now;
                    if (!s_Refill.TryGetValue(p.Guid.Full, out var at) || now - at >= R.REFILL_GAP_S) { s_Refill[p.Guid.Full] = now; refill = true; }
                }
            if (target is Creature c) { try { c.Rotate(p); } catch { } }
            if (offered > amount) Tell(target, p, R.TOO_MUCH);
            Tell(target, p, R.TipLine(tier, refill));
            if (refill) GiveCoffee(p, "Gladys pours you a fresh Diner Coffee.");
            Mod.Log.Info($"[RevivalGuard] Diner: {p.Name} tipped Gladys {amount} pyreals{(refill ? " (free refill)" : "")}");
            return false;
        }

        // ---------------------------------------------------------------- admin
        static void Cmd(Session session, params string[] a)
        {
            void Out(string s) { if (session != null) session.Network.EnqueueSend(new GameMessageSystemChat(s, ChatMessageType.Broadcast)); else Mod.Log.Info("[RevivalGuard] @diner: " + s); }
            string verb = a != null && a.Length > 0 ? a[0].ToLowerInvariant() : "status";
            var p = session?.Player;
            switch (verb)
            {
                case "hours":
                    string h = a.Length > 1 ? a[1].ToLowerInvariant() : "auto";
                    s_ForcedHours = h == "auto" ? null : h;
                    Out($"Diner hours: {(s_ForcedHours ?? "auto")} (now {HoursNow()}); the director applies it within 5 s.");
                    return;
                case "contest":
                    if (p != null) StartContest(p, true);
                    return;
                case "reset":
                    lock (s_Lock) { foreach (var r in s_Regulars.Values) try { r?.Destroy(); } catch { } s_Regulars.Clear(); }
                    s_StoolsUp = null; s_NextDirector = 0;
                    Out("Diner: regulars and stools are seated again within 5 s.");
                    return;
            }
            int today = Today();
            int regs; lock (s_Lock) regs = s_Regulars.Values.Count(r => r != null && !r.IsDestroyed);
            Out($"Diner: {(On ? "on" : "OFF")} (life {B(P_LIFE)}, speech {B(P_SPEECH)}, buff {B(P_BUFF)}, contest {B(P_CONTEST)}, tips {B(P_TIPS)}, coffee {B(P_COFFEE)}, gate {B(P_GATE)}); "
                + $"hours {HoursNow()}{(s_ForcedHours != null ? " (forced)" : "")}, {regs} regulars seated, stools {(s_StoolsUp == true ? "up" : "down")}; "
                + $"special today: {R.MealName(R.Special(today))}; day's best: {(s_DayBest.day == today ? $"{s_DayBest.name} {s_DayBest.count}" : "none yet")}; "
                + $"Gladys {(s_Gladys == null ? "not seen" : s_Gladys.Round >= 0 ? "on round " + DinerPlaces.Rounds[s_Gladys.Round].Name : "at her post")}; "
                + $"speech budget: {s_Budget.Unprompted} lines, {s_Budget.Exchanges} exchanges, {s_Budget.Dropped} dropped"
                + (p != null && InDiner(p) ? $"; you are {(Seated(p) ? "" : "not ")}at a booth" : ""));
        }
    }

    // ================= hooks =================

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class DinerTick
    {
        static bool Prepare() { Diner.Register(); return true; }
        static void Prefix(Creature __instance)
        {
            if (!Diner.Mine(__instance)) return;
            try { Diner.Tick(__instance); }
            catch (Exception e) { TickGuard.Report("Diner", e); }
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsDynamicThatShouldPersistToShard))]
    static class DinerNoSave
    {
        static void Postfix(WorldObject __instance, ref bool __result)
        {
            if (__result && Diner.Transient(__instance)) __result = false;
        }
    }

    /// <summary>A diner meal went down.</summary>
    [HarmonyPatch(typeof(Food), nameof(Food.ApplyConsumable))]
    static class DinerAte
    {
        static void Prefix(Food __instance, Player player, out uint __state)
        {
            __state = 0;
            try
            {
                uint w = __instance.WeenieClassId;
                if (player != null && !player.IsDead && DinerRule.IsDinerFood(w) && player.FindObject(__instance.Guid.Full, Player.SearchLocations.MyInventory) != null) __state = w;
            }
            catch { }
        }

        static void Postfix(Player player, uint __state)
        {
            if (__state == 0) return;
            try { Diner.Ate(player, __state); }
            catch (Exception e) { TickGuard.Report("Diner.Ate", e); }
        }
    }

    /// <summary>Gladys's retail Use (pay, go downstairs) only for a flagged player in the spring; Hana and Bo Lin name the special.</summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.OnActivate), new[] { typeof(WorldObject) })]
    static class DinerUse
    {
        static bool Prefix(WorldObject __instance, WorldObject activator)
        {
            try { return !(activator is Player p) || Diner.MayUse(__instance, p); }
            catch (Exception e) { TickGuard.Report("Diner.Use", e); return true; }
        }

        static void Postfix(WorldObject __instance, WorldObject activator)
        {
            try { if (activator is Player p) Diner.Used(__instance, p); }
            catch (Exception e) { TickGuard.Report("Diner.Use", e); }
        }
    }

    /// <summary>Pyreals handed to Gladys: a tip, taken whole (retail's Give would take one coin, and she has no Give emote).</summary>
    [HarmonyPatch]
    static class DinerTip
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(Player), "GiveObjectToNPC",
            new[] { typeof(WorldObject), typeof(WorldObject), typeof(Container), typeof(Container), typeof(bool), typeof(int) });
        static bool Prepare()
        {
            if (TargetMethod() != null) return true;
            Mod.Log.Warn("[RevivalGuard] Diner: Player.GiveObjectToNPC not found; tips for Gladys are off");
            return false;
        }
        static bool Prefix(Player __instance, WorldObject target, WorldObject item, int amount)
        {
            try { return Diner.Give(__instance, target, item, amount); }
            catch (Exception e) { TickGuard.Report("Diner.Tip", e); return true; }
        }
    }

    /// <summary>Gladys's retail heartbeat chatter goes through the diner's speech budget.</summary>
    [HarmonyPatch(typeof(ACE.Server.WorldObjects.Managers.EmoteManager), nameof(ACE.Server.WorldObjects.Managers.EmoteManager.GetEmoteSet),
        new[] { typeof(EmoteCategory), typeof(string), typeof(VendorType?), typeof(uint?), typeof(bool) })]
    static class DinerHeartbeat
    {
        static void Postfix(ACE.Server.WorldObjects.Managers.EmoteManager __instance, EmoteCategory category, ref ACE.Entity.Models.PropertiesEmote __result)
        {
            if (category != EmoteCategory.HeartBeat || __result == null) return;
            try
            {
                var wo = __instance.WorldObject;
                if (wo?.WeenieClassId != DinerRule.GLADYS || wo.Location?.Landblock != DinerPlaces.LB) return;
                bool speaks = false;
                foreach (var a in __result.PropertiesEmoteAction)
                    if (a.Type == (uint)EmoteType.Say || a.Type == (uint)EmoteType.Tell || a.Type == (uint)EmoteType.LocalBroadcast) { speaks = true; break; }
                if (speaks && !Diner.TryAmbient(wo)) __result = null;
            }
            catch (Exception e) { TickGuard.Report("Diner.Heartbeat", e); }
        }
    }
}
