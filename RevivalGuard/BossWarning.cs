using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// BOSS WARNINGS BEFORE BIG HITS. Ours, not retail. Owner, 2026-09-29: "do the boss shadowfall
    /// warning before big hits" (the approved boss_shadowfall effect), and of the Unbound Queen, "her
    /// attacks dont feel like they do much ... something to be afraid of".
    ///
    /// Warn() raises boss_shadowfall over a body on every nearby client that reads the mod channel
    /// ("vfx", AcBossWarning on the client). The Unbound Queen
    /// gets a telegraphed Brood Slam of her own: she stops, the shadow falls on the
    /// player she has chosen, and SLAM_DELAY later the spot where they stood is crushed, with
    /// knockback (Knockback.cs), for anyone still inside SLAM_RADIUS. Moving out avoids it.
    ///
    /// Switch `rg_boss_telegraphs`, default OFF (tools/gpubox-ace/live-switch.sh). First-pass tuning.
    /// </summary>
    public static class BossWarning
    {
        public const string P_ON = "rg_boss_telegraphs";
        public const uint QUEEN = 900803;
        const string EFFECT = "boss_shadowfall";
        const float HEAR = 60f;

        const double SLAM_EVERY = 16, SLAM_FIRST = 8, SLAM_DELAY = 1.6;
        const float SLAM_REACH = 18f, SLAM_RADIUS = 4.5f, SLAM_FRAC = 0.30f;

        sealed class St { public double Next; public double BusyUntil; public readonly List<(double due, Action act)> Pending = new List<(double, Action)>(); }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Creature, St> s_St = new System.Runtime.CompilerServices.ConditionalWeakTable<Creature, St>();
        static bool s_Registered;

        public static bool On => PropertyManager.GetBool(P_ON, false).Item;

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard BossWarning: boss_shadowfall before big boss hits, and the Unbound Queen's telegraphed Brood Slam (ours, not retail)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BossWarning: property default not registered: {e.Message}"); }
            Mod.Log.Info($"[RevivalGuard] BossWarning: {(On ? "ON" : "off")} ({P_ON})");
        }

        /// <summary>Raise the warning over a body for every nearby client that can draw it.</summary>
        public static void Warn(WorldObject on)
        {
            if (on?.Location == null || !On) return;
            string payload = "{\"k\":\"" + EFFECT + "\",\"g\":" + on.Guid.Full + "}";
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (p?.Session == null || p.Location == null || !ModChannel.Capable(p.Session)) continue;
                if (p.Location.Landblock != on.Location.Landblock || p.Location.DistanceTo(on.Location) > HEAR) continue;
                ModChannel.Send(p.Session, "vfx", payload);
            }
        }

        // ------------------------------------------------------------------ the Queen's Brood Slam

        internal static void QueenTick(Creature q)
        {
            if (!On || q.IsDead || q.Location == null) return;
            var st = s_St.GetOrCreateValue(q);
            double now = Time.GetUnixTime();
            for (int i = st.Pending.Count - 1; i >= 0; i--)
                if (now >= st.Pending[i].due) { var a = st.Pending[i].act; st.Pending.RemoveAt(i); a(); }
            var target = q.AttackTarget as Player;
            if (target == null || target.IsDead || target.Location == null) { st.Next = Math.Max(st.Next, now + SLAM_FIRST); return; }
            if (st.Next == 0) { st.Next = now + SLAM_FIRST; return; }
            if (now < st.Next || now < st.BusyUntil) return;
            if (target.Location.Landblock != q.Location.Landblock || q.Location.DistanceTo(target.Location) > SLAM_REACH) return;
            st.Next = now + SLAM_EVERY;
            StartSlam(q, st, target, now);
        }

        static void StartSlam(Creature q, St st, Player target, double now)
        {
            if (q.IsMoving) q.CancelMoveTo();
            q.TurnTo(target);
            q.EmoteManager.IsBusy = true;
            st.BusyUntil = now + SLAM_DELAY + 0.8;
            var at = target.Location.Pos;   // the spot, fixed now: stepping out of it is the dodge
            Warn(target);
            foreach (var p in Near(q))
                p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                    $"The Unbound Queen rears over {(p == target ? "you" : target.Name)}. Move!", ChatMessageType.Broadcast));
            st.Pending.Add((now + SLAM_DELAY, () => Slam(q, at)));
            st.Pending.Add((now + SLAM_DELAY + 0.8, () => { if (!q.IsDestroyed) q.EmoteManager.IsBusy = false; }));
        }

        static void Slam(Creature q, Vector3 at)
        {
            if (q.IsDead || q.Location == null) return;
            int hit = 0;
            foreach (var p in Near(q))
            {
                if (p.IsDead) continue;
                var d = p.Location.Pos - at; d.Z = 0;
                if (d.Length() > SLAM_RADIUS) continue;
                int dealt = p.TakeDamage(q, DamageType.Bludgeon, SLAM_FRAC * p.Health.MaxValue, BodyPart.Chest);
                p.SendMessage($"The Unbound Queen's slam crushes you for {dealt} points of damage!", ChatMessageType.Combat);
                Knockback.PushFrom(p, at, 8f, 3.5f);
                hit++;
            }
            Mod.Log.Info($"[RevivalGuard] Unbound Queen: brood slam, {hit} hit");
        }

        static IEnumerable<Player> Near(Creature q) =>
            PlayerManager.GetAllOnline().Where(p => p.Location != null && p.CurrentLandblock == q.CurrentLandblock && p.Location.DistanceTo(q.Location) <= HEAR);

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class BossWarningQueenTick
    {
        static int s_Errors;
        static bool Prepare() { BossWarning.Register(); return true; }
        static void Postfix(Creature __instance)
        {
            if (__instance.WeenieClassId != BossWarning.QUEEN) return;
            try { BossWarning.QueenTick(__instance); }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref s_Errors) <= 5 || s_Errors % 1000 == 0)
                    Mod.Log.Error($"[RevivalGuard] Queen tick threw (error {s_Errors})", e);
            }
        }
    }
}
