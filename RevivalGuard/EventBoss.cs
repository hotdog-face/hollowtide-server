using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using R = RevivalGuard.EventBossRule;

namespace RevivalGuard
{
    /// <summary>The event boss: Bael'Zharon, the Unleashed.</summary>
    static class EventBoss
    {
        internal const uint WCID = 900207, SHADOW = 900201;
        const uint PORTAL_TO_TETH = 2046;
        const float HEAR = 60f, REACH = 40f;

        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            ("eventboss_base_health", R.BASE_HEALTH, "EventBoss: Bael'Zharon the Unleashed's health for one fighter"),
            ("eventboss_per_fighter", R.PER_FIGHTER, "EventBoss: health added per extra non-staff fighter (fraction of base)"),
            ("eventboss_scale_cap", R.SCALE_CAP, "EventBoss: most the health can grow to (times base)"),
            ("eventboss_hit_cap", R.HIT_CAP_FRAC, "EventBoss: most one hit may take (fraction of his max health)"),
            ("eventboss_interval_s", R.INTERVAL_S, "EventBoss: seconds between signature attacks"),
            ("eventboss_interval_last_s", R.INTERVAL_LAST_S, "EventBoss: seconds between signature attacks under 25%"),
            ("eventboss_wrath_frac", R.WRATH_FRAC, "EventBoss: Hopeslayer's Wrath, max-health fraction shared by those in the burst"),
            ("eventboss_slam_frac", R.SLAM_FRAC, "EventBoss: Shadowfall Slam damage (fraction of max health)"),
            ("eventboss_pulse_frac", R.PULSE_FRAC, "EventBoss: Demon's Pulse damage (fraction of max health)"),
            ("eventboss_enrage_s", R.ENRAGE_S, "EventBoss: seconds from the first hit to the hard enrage"),
        };
        static double P(int i) => PropertyManager.GetDouble(s_Doubles[i].key, s_Doubles[i].def).Item;
        static double BaseHealth => P(0); static double PerFighter => P(1); static double ScaleCap => P(2); static double HitCapFrac => P(3);
        static double IntervalS => P(4); static double IntervalLastS => P(5); static double WrathFrac => P(6); static double SlamFrac => P(7);
        static double PulseFrac => P(8); static double EnrageS => P(9);

        sealed class St
        {
            public double Engaged, NextAttack, BusyUntil, ShieldUntil, AddsCheckAt, LastNear, Spawned, NextLog;
            public uint BaseMax;
            public int Phase, Deaths, Attacks, Hits, Dodges, Knocks;
            public bool Enraged;
            public readonly HashSet<uint> Fighters = new HashSet<uint>();
            public readonly Dictionary<uint, double> Weak = new Dictionary<uint, double>();
            public readonly List<(double due, Action act)> Pending = new List<(double, Action)>();
            public readonly List<Creature> Adds = new List<Creature>();
            public readonly List<string> DeathNames = new List<string>();
            public R.Rotation Rot = new R.Rotation(Environment.TickCount);
        }
        static readonly ConditionalWeakTable<Creature, St> s_St = new ConditionalWeakTable<Creature, St>();
        static readonly List<WeakReference<Creature>> s_Live = new List<WeakReference<Creature>>();
        static readonly object s_Lock = new object();
        static int s_Registered, s_Errors;

        internal static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var d = MonsterAi.Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventBoss: tunables not registered with @modifydouble ({e.Message}); defaults apply"); }
            CommandManager.TryAddCommand(Cmd, "eventboss", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "The event boss, Bael'Zharon the Unleashed (RevivalGuard): spawn one in front of you, or test one nearby.",
                "[spawn | status | despawn | hp <percent> | attack <wrath|slam|pulse|teth|weak>]");
            Mod.Log.Info($"[RevivalGuard] EventBoss: @eventboss (weenie {WCID}; health scaling, hit cap, five telegraphed attacks, phases, enrage)");
        }

        static bool Staff(Player p) => p?.Session != null && p.Session.AccessLevel >= AccessLevel.Sentinel;
        static Player Hand(WorldObject attacker) =>
            attacker as Player ?? attacker?.ProjectileSource as Player ?? (attacker as CombatPet)?.P_PetOwner;
        static double Now => Time.GetUnixTime();

        // ------------------------------------------------------------------ the command
        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            if (me == null) return;
            void Reply(string t) => me.Session?.Network?.EnqueueSend(new GameMessageSystemChat("[EventBoss] " + t, ChatMessageType.Broadcast));
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "spawn";
            if (verb == "spawn") { Reply(Spawn(me)); return; }
            var c = Nearest(me);
            if (c == null) { Reply("no event boss within 200 m."); return; }
            var st = s_St.GetOrCreateValue(c);
            switch (verb)
            {
                case "status":
                    Reply($"{c.Name} 0x{c.Guid.Full:X8}: {c.Health.Current:N0}/{c.Health.MaxValue:N0} ({c.Health.Percent:P0}), phase {st.Phase}, "
                        + $"{st.Fighters.Count} fighter(s), {(st.Engaged > 0 ? $"{(Now - st.Engaged) / 60:0.0} min in" : "not engaged")}, "
                        + $"{st.Attacks} attacks, {st.Hits} hits, {st.Dodges} dodged, {st.Knocks} knocked back, {st.Deaths} death(s){(st.Enraged ? ", ENRAGED" : "")}.");
                    return;
                case "despawn":
                    Despawn(c, st, "despawned by " + me.Name);
                    Reply($"{c.Name} removed.");
                    return;
                case "hp":
                    if (args.Length < 2 || !double.TryParse(args[1], out var pct)) { Reply("@eventboss hp <percent>"); return; }
                    c.UpdateVital(c.Health, (uint)Math.Clamp(c.Health.MaxValue * pct / 100.0, 1, c.Health.MaxValue));
                    Reply($"health set to {c.Health.Current:N0} ({c.Health.Percent:P0}).");
                    return;
                case "attack":
                    var which = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    R.Attack a = which.StartsWith("wr") ? R.Attack.Wrath : which.StartsWith("sl") ? R.Attack.Slam : which.StartsWith("pu") ? R.Attack.Pulse
                               : which.StartsWith("te") ? R.Attack.Teth : which.StartsWith("we") ? R.Attack.Weakness : (R.Attack)(-1);
                    if ((int)a < 0) { Reply("@eventboss attack <wrath|slam|pulse|teth|weak>"); return; }
                    var chain = new ActionChain();
                    chain.AddAction(c, () => { if (st.Engaged == 0) st.Engaged = Now; Start(c, st, a, Now); });
                    chain.EnqueueChain();
                    Reply($"{a} queued.");
                    return;
            }
            Reply("@eventboss [spawn | status | despawn | hp <percent> | attack <name>]");
        }

        internal static string Spawn(Player me)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(WCID);
            if (wo == null) return $"weenie {WCID} is not in this world: the datafix 2026-11-26-eventboss-1-bael-unleashed.sql has not been applied here.";
            if (!(wo is Creature c)) { wo.Destroy(); return $"weenie {WCID} is not a creature."; }
            c.Location = me.Location.InFrontOf(8f, true);
            c.Location.LandblockId = new ACE.Entity.LandblockId(c.Location.GetCell());
            c.TimeToRot = -1;
            // his health is the tunable, not the weenie's (so the size can change live without SQL)
            uint want = (uint)Math.Clamp(BaseHealth, 1000, 50_000_000);
            if (c.Health.MaxValue != want) c.Health.StartingValue = (uint)Math.Max(1, (long)c.Health.StartingValue + want - c.Health.MaxValue);
            c.Health.Current = c.Health.MaxValue;
            var st = s_St.GetOrCreateValue(c);
            st.BaseMax = c.Health.MaxValue; st.Spawned = st.LastNear = Now;
            if (!c.EnterWorld()) { c.Destroy(); return $"{c.Name} could not be placed here."; }
            lock (s_Lock) { s_Live.RemoveAll(w => !w.TryGetTarget(out var x) || x.IsDestroyed); s_Live.Add(new WeakReference<Creature>(c)); }
            Near(c, HEAR).ToList().ForEach(p => Tell(p, $"The air tears open. {c.Name} steps through, and the light around him dies."));
            PlayerManager.BroadcastToAuditChannel(me, $"{me.Name} has spawned the event boss {c.Name} (0x{c.Guid.Full:X8}) at {c.Location.ToLOCString()}.");
            Mod.Log.Info($"[RevivalGuard] EventBoss: {me.Name} spawned {c.Name} 0x{c.Guid.Full:X8} at {c.Location.ToLOCString()}, {c.Health.MaxValue:N0} health");
            return $"{c.Name} (0x{c.Guid.Full:X8}) is here, {c.Health.MaxValue:N0} health. He stays until killed (@eventboss despawn removes him).";
        }

        static Creature Nearest(Player me)
        {
            lock (s_Lock)
                return s_Live.Select(w => w.TryGetTarget(out var x) ? x : null)
                    .Where(x => x != null && !x.IsDestroyed && !x.IsDead && x.Location != null && x.Location.DistanceTo(me.Location) < 200)
                    .OrderBy(x => x.Location.DistanceTo(me.Location)).FirstOrDefault();
        }

        static void Despawn(Creature c, St st, string why)
        {
            var chain = new ActionChain();
            chain.AddAction(c, () =>
            {
                foreach (var a in st.Adds) try { if (!a.IsDestroyed && !a.IsDead) a.Destroy(); } catch { }
                st.Adds.Clear();
                if (!c.IsDestroyed) c.Destroy();
            });
            chain.EnqueueChain();
            Mod.Log.Info($"[RevivalGuard] EventBoss: {c.Name} 0x{c.Guid.Full:X8} {why}; {Summary(st)}");
        }

        static string Summary(St st) =>
            $"{(st.Engaged > 0 ? (Now - st.Engaged) / 60 : 0):0.0} min engaged, {st.Fighters.Count} fighter(s), {st.Attacks} attacks, {st.Hits} hits, "
            + $"{st.Dodges} dodged, {st.Knocks} knocked back, {st.Deaths} death(s){(st.DeathNames.Count > 0 ? " (" + string.Join(", ", st.DeathNames) + ")" : "")}";

        // ------------------------------------------------------------------ every hit (DamageHistory.Add prefix)
        internal static void OnDamage(Creature c, WorldObject attacker, ref uint amount)
        {
            if (c.IsDead || amount == 0) return;
            var st = s_St.GetOrCreateValue(c);
            double now = Now;
            if (st.BaseMax == 0) st.BaseMax = c.Health.MaxValue;
            if (attacker == c) { if (now < st.ShieldUntil) { c.UpdateVitalDelta(c.Health, (int)amount); amount = 0; } return; }   // a combined DoT tick
            if (st.Engaged == 0) { st.Engaged = now; st.NextAttack = now + R.FIRST_ATTACK_S; Mod.Log.Info($"[RevivalGuard] EventBoss: {c.Name} 0x{c.Guid.Full:X8} engaged"); }
            var p = Hand(attacker);
            if (p != null && !Staff(p))
            {
                bool added;
                lock (st.Fighters) added = st.Fighters.Add(p.Guid.Full);
                if (added)
                {
                    uint oldMax = c.Health.MaxValue, newMax = R.MaxFor(st.BaseMax, oldMax, st.Fighters.Count, PerFighter, ScaleCap);
                    if (newMax > oldMax)
                    {
                        var (max, cur) = WorldBossScaling.Rescale(oldMax, c.Health.Current, newMax);
                        c.Health.StartingValue += max - oldMax;
                        c.UpdateVital(c.Health, cur);
                        Mod.Log.Info($"[RevivalGuard] EventBoss: fighter {st.Fighters.Count} ({p.Name}); max health {oldMax:N0} -> {c.Health.MaxValue:N0}");
                    }
                }
            }
            bool weak = p != null && st.Weak.ContainsKey(p.Guid.Full);
            var (kept, back) = R.Clamp(amount, c.Health.MaxValue, weak, now < st.ShieldUntil, HitCapFrac);
            if (back > 0) c.UpdateVitalDelta(c.Health, (int)back);
            amount = kept;
        }

        // ------------------------------------------------------------------ the tick (Monster_Tick postfix, his landblock thread)
        internal static void Tick(Creature c)
        {
            if (c.IsDead || c.Location == null) return;
            var st = s_St.GetOrCreateValue(c);
            double now = Now;
            if (st.BaseMax == 0)
            {
                // one that ACE saved and brought back with his landblock: ours again, so @eventboss finds him and the 20-minute sweep can take him
                st.BaseMax = c.Health.MaxValue; st.LastNear = now;
                lock (s_Lock) if (!s_Live.Any(w => w.TryGetTarget(out var x) && x == c)) s_Live.Add(new WeakReference<Creature>(c));
            }
            for (int i = st.Pending.Count - 1; i >= 0; i--)
                if (now >= st.Pending[i].due) { var a = st.Pending[i].act; st.Pending.RemoveAt(i); try { a(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventBoss: pending action: {e.Message}"); } }

            var near = Near(c, 120f).ToList();
            if (near.Count > 0) st.LastNear = now;
            else if (now - st.LastNear > 20 * 60) { Despawn(c, st, "removed (nobody within 120 m for 20 minutes)"); return; }

            // the weakness: cured by any other player within 3 m, else it runs out
            if (st.Weak.Count > 0)
                foreach (var g in st.Weak.Keys.ToList())
                {
                    var w = near.FirstOrDefault(x => x.Guid.Full == g);
                    bool cured = w != null && near.Any(o => o != w && !o.IsDead && o.Location.DistanceTo(w.Location) <= R.ADJACENT);
                    if (w == null || w.IsDead || cured || now >= st.Weak[g])
                    {
                        st.Weak.Remove(g);
                        if (w != null && !w.IsDead)
                        {
                            Script(w, PlayScript.EnchantUpWhite);
                            Tell(w, cured ? "An ally stands with you, and Bael'Zharon's curse breaks." : "Bael'Zharon's curse fades.");
                            if (cured) st.Dodges++;
                        }
                    }
                }

            if (st.Engaged == 0) return;
            double since = now - st.Engaged;
            if (now >= st.NextLog) { st.NextLog = now + 60; Mod.Log.Info($"[RevivalGuard] EventBoss: t={since:0}s {c.Health.Current:N0}/{c.Health.MaxValue:N0} ({c.Health.Percent:P0}), {near.Count(p => !p.IsDead && p.Location.DistanceTo(c.Location) <= HEAR)} player(s) near; {Summary(st)}"); }
            int phase = R.Phase(c.Health.Current / (double)Math.Max(1, c.Health.MaxValue));
            while (st.Phase < phase) { st.Phase++; PhaseStart(c, st, st.Phase, now); }
            if (st.AddsCheckAt > 0 && now >= st.AddsCheckAt)
            {
                st.AddsCheckAt = 0;
                int alive = st.Adds.Count(a => !a.IsDead && !a.IsDestroyed);
                if (alive > 0)
                {
                    c.SetProperty(PropertyInt.DamageRating, (c.GetProperty(PropertyInt.DamageRating) ?? 0) + 10 * alive);
                    Shout(c, $"{alive} Shadow{(alive == 1 ? "" : "s")} pour{(alive == 1 ? "s" : "")} into Bael'Zharon. His blows grow heavier.");
                    Script(c, PlayScript.EnchantUpRed);
                }
                else Shout(c, "The Shadows are broken before they can feed him.");
            }
            if (!st.Enraged && since >= EnrageS)
            {
                st.Enraged = true;
                Shout(c, "Bael'Zharon has lost all patience. The dark swallows everything near him!");
                Script(c, PlayScript.BlackMadness);
                Mod.Log.Info($"[RevivalGuard] EventBoss: {c.Name} 0x{c.Guid.Full:X8} ENRAGED at {since / 60:0.0} min; {Summary(st)}");
                st.NextAttack = Math.Min(st.NextAttack, now + 1);
            }
            if (now < st.NextAttack || now < st.BusyUntil) return;
            bool last = st.Phase >= 3;
            var what = st.Enraged ? R.Attack.Pulse : st.Rot.Next(since >= R.LEARN_S);
            Start(c, st, what, now);
            double w2 = R.WindUp(what, last);
            st.NextAttack = Math.Max(now + (st.Enraged ? R.INTERVAL_ENRAGE_S : last ? IntervalLastS : IntervalS), now + w2 + 1.0);
        }

        static void PhaseStart(Creature c, St st, int phase, double now)
        {
            Mod.Log.Info($"[RevivalGuard] EventBoss: {c.Name} 0x{c.Guid.Full:X8} phase {phase} at {c.Health.Percent:P0}, {(now - st.Engaged) / 60:0.0} min in");
            Script(c, PlayScript.BaelZharonSmite);
            if (phase == 1)
            {
                Shout(c, "Bael'Zharon calls his Shadows! Kill them within 20 seconds, or they feed him.");
                for (int i = 0; i < 3; i++)
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(SHADOW);
                    if (!(wo is Creature add)) { wo?.Destroy(); break; }
                    double ang = i * 2 * Math.PI / 3;
                    var off = new Vector3((float)Math.Cos(ang) * 6f, (float)Math.Sin(ang) * 6f, 0f);
                    add.Location = Offset(c.Location, off) ?? c.Location.InFrontOf(4f, true);
                    add.Location.LandblockId = new ACE.Entity.LandblockId(add.Location.GetCell());
                    add.TimeToRot = -1;
                    if (add.EnterWorld()) st.Adds.Add(add); else add.Destroy();
                }
                st.AddsCheckAt = now + R.ADDS_GRACE_S;
            }
            else if (phase == 2)
            {
                st.ShieldUntil = now + R.SHIELD_S;
                Shout(c, "Bael'Zharon wraps himself in living shadow. Nothing can harm him for 10 seconds!");
                Script(c, PlayScript.ShieldUpGrey);
                st.Pending.Add((now + R.SHIELD_S, () => { if (!c.IsDead) { Shout(c, "The shadow around Bael'Zharon tears away. Strike now!"); Script(c, PlayScript.ShieldDownGrey); } }));
            }
            else if (phase == 3)
                Shout(c, "Bael'Zharon roars, and his fury quickens!");
        }

        // ------------------------------------------------------------------ the five
        static void Start(Creature c, St st, R.Attack what, double now)
        {
            bool last = st.Phase >= 3;
            double wind = R.WindUp(what, last);
            double scale = R.DamageScale(now - st.Engaged);
            var reach = Near(c, REACH).Where(p => !p.IsDead).ToList();
            var targets = reach.Where(p => !Staff(p)).ToList();
            st.Attacks++;
            switch (what)
            {
                case R.Attack.Wrath:
                {
                    if (targets.Count == 0) { st.Attacks--; return; }
                    var marks = targets.OrderBy(_ => Guid.NewGuid()).Take(R.WrathMarks(targets.Count)).ToList();
                    string names = string.Join(" and ", marks.Select(m => m.Name));
                    foreach (var p in Near(c, HEAR))
                        Tell(p, marks.Contains(p)
                            ? $"Bael'Zharon marks YOU with Hopeslayer's Wrath! It bursts in {wind:0} seconds: get others to stand with you to share it, and keep away from other marks."
                            : $"Bael'Zharon marks {names} with Hopeslayer's Wrath! It bursts in {wind:0} seconds: stack on a mark to share it.");
                    foreach (var m in marks) { Script(m, PlayScript.AetheriaSurgeDestruction); BossWarning.WarnAlways(m); }
                    Log(c, st, $"Wrath telegraphed on {names}, lands in {wind:0.0} s");
                    st.Pending.Add((now + wind, () =>
                    {
                        foreach (var m in marks)
                        {
                            if (m.IsDead || m.Location == null) continue;
                            var inBurst = Near(c, HEAR).Where(o => !o.IsDead && o.Location.DistanceTo(m.Location) <= R.WRATH_RADIUS).ToList();
                            double share = R.WrathShare(inBurst.Count, WrathFrac) * scale;
                            foreach (var o in inBurst) Hit(c, st, o, share, $"Hopeslayer's Wrath bursts over {(o == m ? "you" : m.Name)}, shared {inBurst.Count} ways");
                            Log(c, st, $"Wrath burst on {m.Name}: {inBurst.Count} in it, {share:P0} each");
                        }
                    }));
                    break;
                }
                case R.Attack.Slam:
                {
                    var facing = R.Facing(c.Location.Rotation);
                    var focus = c.AttackTarget as Player ?? targets.FirstOrDefault();
                    if (focus != null && focus.Location != null)
                    {
                        // he turns to his target first and the cone is locked to that facing: what players see when he raises his fists
                        var off = c.Location.GetOffset(focus.Location);
                        if (off.X * off.X + off.Y * off.Y > 0.01f) facing = Vector2.Normalize(new Vector2(off.X, off.Y));
                        c.TurnTo(focus);
                    }
                    Hold(c, st, now, wind);
                    Shout(c, $"Bael'Zharon raises his fists{(focus != null ? " toward " + focus.Name : "")}: Shadowfall Slam! Get beside or behind him!");
                    Script(c, PlayScript.BaelZharonSmite);
                    foreach (var p in reach) if (R.InCone(V2(c, c), facing, V2(c, p), R.SLAM_RANGE + 4f)) { Script(p, PlayScript.EnchantDownRed); BossWarning.WarnAlways(p); }
                    Log(c, st, $"Slam telegraphed (facing {facing.X:0.00},{facing.Y:0.00}), lands in {wind:0.0} s");
                    st.Pending.Add((now + wind, () =>
                    {
                        int n = 0;
                        foreach (var p in Near(c, HEAR).Where(o => !o.IsDead).ToList())
                        {
                            if (!R.InCone(V2(c, c), facing, V2(c, p))) continue;
                            n++;
                            if (Hit(c, st, p, SlamFrac * scale, "Shadowfall Slam crushes you")) Knock(c, st, p, R.SLAM_KNOCK, facing);
                        }
                        Log(c, st, $"Slam landed: {n} in the cone");
                    }));
                    break;
                }
                case R.Attack.Pulse:
                {
                    bool enr = st.Enraged;
                    float radius = enr ? R.ENRAGE_RADIUS : R.PULSE_RADIUS;
                    Hold(c, st, now, wind);
                    Shout(c, enr ? "The dark around Bael'Zharon gathers to swallow everything!"
                                 : $"Bael'Zharon draws the dark inward: Demon's Pulse! Get more than {R.PULSE_RADIUS:0} paces from him!");
                    Script(c, PlayScript.BaelZharonSmite);
                    foreach (var p in reach) if (R.InRing(V2(c, c), V2(c, p), radius + 3f)) { Script(p, PlayScript.EnchantDownPurple); BossWarning.WarnAlways(p); }
                    Log(c, st, $"Pulse telegraphed (radius {radius:0}), lands in {wind:0.0} s");
                    st.Pending.Add((now + wind, () =>
                    {
                        int n = 0;
                        var facing = R.Facing(c.Location.Rotation);
                        foreach (var p in Near(c, HEAR).Where(o => !o.IsDead).ToList())
                        {
                            if (!R.InRing(V2(c, c), V2(c, p), radius)) continue;
                            n++;
                            if (Hit(c, st, p, (enr ? R.ENRAGE_FRAC : PulseFrac) * scale, enr ? "The dark swallows you" : "Demon's Pulse tears through you") && !enr)
                                Knock(c, st, p, R.PULSE_KNOCK, facing);
                        }
                        Log(c, st, $"Pulse landed: {n} inside {radius:0} m");
                    }));
                    break;
                }
                case R.Attack.Teth:
                {
                    var top = TopDamager(c, targets);
                    if (top == null) { st.Attacks--; return; }
                    foreach (var p in Near(c, HEAR))
                        Tell(p, p == top ? $"Bael'Zharon fixes his gaze on YOU. The way to Teth opens beneath your feet: get {R.TETH_DODGE:0} paces away from him, now!"
                                         : $"Bael'Zharon fixes his gaze on {top.Name}. The way to Teth opens!");
                    Script(top, PlayScript.PortalStorm); BossWarning.WarnAlways(top);
                    Log(c, st, $"Teth telegraphed on {top.Name}, lands in {wind:0.0} s");
                    st.Pending.Add((now + wind, () =>
                    {
                        if (top.IsDead || top.Location == null) return;
                        if (top.Location.DistanceTo(c.Location) >= R.TETH_DODGE || top.CurrentLandblock == null)
                        {
                            st.Dodges++;
                            Tell(top, "You slip out of Bael'Zharon's gaze. The way to Teth closes behind you.");
                            Log(c, st, $"Teth dodged by {top.Name} ({top.Location.DistanceTo(c.Location):0} m)");
                            return;
                        }
                        var spell = new Spell(PORTAL_TO_TETH);
                        if (!spell.NotFound) c.TryCastSpell_WithRedirects(spell, top, c, tryResist: false);
                        st.Hits++;
                        Tell(top, "Bael'Zharon seizes you and hurls you to Teth!");
                        Log(c, st, $"Teth sent {top.Name}");
                    }));
                    break;
                }
                case R.Attack.Weakness:
                {
                    var t = targets.Where(p => !st.Weak.ContainsKey(p.Guid.Full)).OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                    if (t == null) { st.Attacks--; return; }
                    foreach (var p in Near(c, HEAR))
                        Tell(p, p == t ? "Bael'Zharon points at YOU: a curse of weakness gathers. An ally must stand right beside you to lift it."
                                       : $"Bael'Zharon curses {t.Name} with weakness. Stand right beside them to lift it.");
                    Script(t, PlayScript.DirtyFightingAttackDebuff);
                    Log(c, st, $"Weakness telegraphed on {t.Name}, lands in {wind:0.0} s");
                    st.Pending.Add((now + wind, () =>
                    {
                        if (t.IsDead) return;
                        st.Weak[t.Guid.Full] = Now + R.WEAK_S;
                        st.Hits++;
                        Script(t, PlayScript.SkillDownBlack);
                        Tell(t, $"Weakness takes you: your blows against Bael'Zharon do {1 - R.WEAK_MULT:P0} less until an ally stands beside you.");
                        Log(c, st, $"Weakness on {t.Name}");
                    }));
                    break;
                }
            }
        }

        /// <summary>He stands still for the wind-up (so the telegraph reads), then carries on.</summary>
        static void Hold(Creature c, St st, double now, double wind)
        {
            if (c.IsMoving) c.CancelMoveTo();
            c.EmoteManager.IsBusy = true;
            st.BusyUntil = now + wind + 0.6;
            st.Pending.Add((now + wind + 0.6, () => { if (!c.IsDestroyed) c.EmoteManager.IsBusy = false; }));
        }

        static Player TopDamager(Creature c, List<Player> targets)
        {
            if (c.DamageHistory == null) return targets.FirstOrDefault();
            var tot = new Dictionary<uint, float>();
            foreach (var info in c.DamageHistory.TotalDamage.Values.ToList())
            {
                if (info == null) continue;
                var p = info.TryGetPetOwnerOrAttacker() as Player;
                if (p == null) continue;
                tot[p.Guid.Full] = (tot.TryGetValue(p.Guid.Full, out var v) ? v : 0) + info.TotalDamage;
            }
            return targets.OrderByDescending(p => tot.TryGetValue(p.Guid.Full, out var v) ? v : 0).FirstOrDefault();
        }

        /// <summary>A share of the player's own max health, straight to health (no resists: the dodge is the defence).</summary>
        static bool Hit(Creature c, St st, Player p, double frac, string what)
        {
            if (p.IsDead) return false;
            int dealt = p.TakeDamage(c, DamageType.Nether, (float)(frac * p.Health.MaxValue), BodyPart.Chest);
            st.Hits++;
            Script(p, PlayScript.HealthDownVoid);
            p.SendMessage($"{what} for {dealt} points of damage!", ChatMessageType.Combat);
            return !p.IsDead;
        }

        /// <summary>Knockback: a short teleport straight away from him, outdoors only.</summary>
        static void Knock(Creature c, St st, Player p, float dist, Vector2 facing)
        {
            if (p.IsDead || p.Location == null || c.Location == null || p.Location.Indoors || c.Location.Indoors) return;
            var off = c.Location.GetOffset(p.Location);
            var land = R.KnockTo(Vector2.Zero, new Vector2(off.X, off.Y), dist, facing) - new Vector2(off.X, off.Y);   // the move, metres
            var to = Offset(p.Location, new Vector3(land.X, land.Y, 0f));
            if (to == null) return;
            st.Knocks++;
            var who = p;
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.3);
            chain.AddAction(who, () => { if (!who.IsDead) who.Teleport(to); });
            chain.EnqueueChain();
        }

        /// <summary>An outdoor position moved by a world offset</summary>
        static ACE.Entity.Position Offset(ACE.Entity.Position from, Vector3 by)
        {
            if (from == null || from.Indoors) return null;
            var q = from.Rotation;
            var to = new ACE.Entity.Position(from.LandblockId.Raw & 0xFFFF0000, from.PositionX + by.X, from.PositionY + by.Y, from.PositionZ + by.Z,
                q.X, q.Y, q.Z, q.W);
            try { to.AdjustMapCoords(); } catch { to.PositionZ = from.PositionZ + 0.5f; }
            to.PositionZ += 0.1f;
            return to;
        }

        static Vector2 V2(Creature boss, WorldObject o)
        {
            if (o == boss) return Vector2.Zero;
            var off = boss.Location.GetOffset(o.Location);
            return new Vector2(off.X, off.Y);
        }

        // ------------------------------------------------------------------ death
        internal static void OnDeath(Creature c, DamageHistoryInfo last)
        {
            var st = s_St.GetOrCreateValue(c);
            var hands = new HashSet<uint>();
            if (c.DamageHistory != null)
                foreach (var info in c.DamageHistory.TotalDamage.Values.ToList())
                {
                    if (info == null || info.TotalDamage <= 0) continue;
                    var p = info.TryGetPetOwnerOrAttacker() as Player;
                    if (p != null && !Staff(p)) hands.Add(p.Guid.Full);
                    else if (p == null && info.IsPlayer) hands.Add(info.Guid.Full);   // logged out since: still theirs
                }
            foreach (var g in hands) WorldBossSchedule.Grant(g, WorldBossSchedule.RollBael(), "your share of Bael'Zharon's hoard");
            foreach (var a in st.Adds) try { if (!a.IsDestroyed && !a.IsDead) a.Destroy(); } catch { }
            st.Adds.Clear();
            Shout(c, hands.Count == 0 ? "Bael'Zharon, the Unleashed, falls back into the dark."
                : $"Bael'Zharon, the Unleashed, is defeated! {hands.Count} {(hands.Count == 1 ? "champion shares" : "champions share")} the spoils.");
            Mod.Log.Info($"[RevivalGuard] EventBoss: {c.Name} 0x{c.Guid.Full:X8} DIED; rewards to {hands.Count} non-staff player(s); {Summary(st)}");
        }

        internal static void OnPlayerDeath(Player p)
        {
            List<Creature> live;
            lock (s_Lock) live = s_Live.Select(w => w.TryGetTarget(out var x) ? x : null).Where(x => x != null && !x.IsDead && !x.IsDestroyed && x.Location != null).ToList();
            foreach (var c in live)
            {
                if (p.Location == null || p.Location.DistanceTo(c.Location) > HEAR) continue;
                var st = s_St.GetOrCreateValue(c);
                if (st.Engaged == 0) continue;
                st.Deaths++; st.DeathNames.Add(p.Name);
                Mod.Log.Info($"[RevivalGuard] EventBoss: {p.Name} died at {(Now - st.Engaged) / 60:0.0} min (death {st.Deaths})");
                return;
            }
        }

        // ------------------------------------------------------------------ helpers
        static IEnumerable<Player> Near(Creature c, float r) =>
            PlayerManager.GetAllOnline().Where(p => p?.Location != null && c.Location != null && p.Location.DistanceTo(c.Location) <= r);

        static void Tell(Player p, string text) => p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        static void Shout(Creature c, string text) { foreach (var p in Near(c, HEAR)) Tell(p, text); }
        static void Script(WorldObject o, PlayScript s) => o?.EnqueueBroadcast(new GameMessageScript(o.Guid, s));
        static void Log(Creature c, St st, string what) =>
            Mod.Log.Info($"[RevivalGuard] EventBoss: t={(st.Engaged > 0 ? Now - st.Engaged : 0):0.0}s {what}");

        internal static void ReportTickError(Exception e)
        {
            if (Interlocked.Increment(ref s_Errors) <= 5 || s_Errors % 1000 == 0) Mod.Log.Error($"[RevivalGuard] EventBoss tick threw (error {s_Errors})", e);
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class EventBossTick
    {
        static bool Prepare() { EventBoss.Register(); return true; }
        static void Postfix(Creature __instance)
        {
            if (__instance.WeenieClassId != EventBoss.WCID) return;
            try { EventBoss.Tick(__instance); }
            catch (Exception e) { EventBoss.ReportTickError(e); }
        }
    }

    [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
    static class EventBossHit
    {
        static void Prefix(DamageHistory __instance, WorldObject attacker, ref uint amount)
        {
            var c = __instance?.Creature;
            if (c == null || c.WeenieClassId != EventBoss.WCID) return;
            try { EventBoss.OnDamage(c, attacker, ref amount); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventBoss hit: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class EventBossDeath
    {
        // a PREFIX: OnDeath clears the damage history on its way out (ForgeWardenDeath's note)
        static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
        {
            if (__instance == null || __instance.WeenieClassId != EventBoss.WCID) return;
            try { EventBoss.OnDeath(__instance, lastDamager); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventBoss death: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class EventBossPlayerDeath
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null || m.DeclaringType != typeof(Player)) { Mod.Log.Warn("[RevivalGuard] EventBoss: Player.OnDeath override not found; deaths near him are not counted"); return null; }
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Player __instance)
        {
            try { EventBoss.OnPlayerDeath(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EventBoss player death: {e.Message}"); }
        }
    }
}
