using System;
using System.Collections.Generic;
using System.Numerics;

namespace RevivalGuard
{
    /// <summary>
    /// THE EVENT BOSS'S PURE HALF (EventBoss.cs; docs/proposals/bael-boss-redesign.md, owner 2026-11-22: "i want him to kill
    /// people but not everyone"). No ACE types, so tools/nettest compiles it by path (EventBossCase.cs). Ours, not retail.
    ///
    ///   health      base x min(cap, 1 + k (N - 1)), N = distinct NON-STAFF players who have hurt him (WorldBossScaling)
    ///   hit cap     no single hit takes more than HitCapFrac of his max health (a staff x250 hit cannot carry him)
    ///   rotation    one telegraphed signature attack every Interval seconds, dealt from a shuffled bag of the five so the
    ///               same one never comes twice running; Portal to Teth waits until the learning minutes are over
    ///   wind-up     chat line + PlayScript at the start, the hit WindUp seconds later (2-3 s; never under 2 s)
    ///   phases      75% Shadows (empower him if alive after 20 s), 50% a 10 s shield, 25% faster rotation and wind-ups,
    ///               14 minutes after the first hit a hard enrage (a lethal pulse every few seconds)
    ///   geometry    2D, AC world metres (x east, y north). Cone and ring are measured from his centre; knockback moves a
    ///               player straight away from him by a fixed distance (a short teleport; ACE has no monster knockback).
    /// </summary>
    public static class EventBossRule
    {
        public enum Attack { Wrath, Slam, Pulse, Teth, Weakness }

        // BASE_HEALTH: the brief said about 700,000; the box fight (docs/WORLD-BOSS-SYSTEM.md section 9) measured 23 a second per
        // level-200-like melee bot and estimates ~45 for a real 145-220 player, so 700k x 1.56 for 8 would take ~40 minutes.
        // 150,000 x 1.56 = 234,000 is ~11 minutes for 8 at 45 a second.
        public const double BASE_HEALTH = 150000, PER_FIGHTER = 0.08, SCALE_CAP = 3.0, HIT_CAP_FRAC = 0.005;
        public const double FIRST_ATTACK_S = 15, INTERVAL_S = 16, INTERVAL_LAST_S = 10, INTERVAL_ENRAGE_S = 4;
        public const double LEARN_S = 120, LEARN_DAMAGE = 0.5, ENRAGE_S = 14 * 60;
        public const double WRATH_FRAC = 1.5, SLAM_FRAC = 0.8, PULSE_FRAC = 0.7, ENRAGE_FRAC = 2.0;   // slam and pulse kill the already hurt (box test 11-22)
        public const float WRATH_RADIUS = 8f, SLAM_RANGE = 14f, SLAM_HALF_DEG = 30f, SLAM_KNOCK = 12f;
        public const float PULSE_RADIUS = 10f, PULSE_KNOCK = 10f, TETH_DODGE = 25f, ADJACENT = 3f, ENRAGE_RADIUS = 60f;
        public const double WEAK_MULT = 0.7, WEAK_S = 30, ADDS_GRACE_S = 20, SHIELD_S = 10;

        /// <summary>Seconds from the warning to the hit. The last phase is 20% faster, never under 2 s (still dodgeable).</summary>
        public static double WindUp(Attack a, bool lastPhase)
        {
            double s = a switch { Attack.Wrath => 3.0, Attack.Slam => 2.5, Attack.Pulse => 3.0, Attack.Teth => 3.0, _ => 2.0 };
            return lastPhase ? Math.Max(2.0, s * 0.8) : s;
        }

        /// <summary>Seconds from one attack's start to the next.</summary>
        public static double Interval(bool lastPhase, bool enraged) => enraged ? INTERVAL_ENRAGE_S : lastPhase ? INTERVAL_LAST_S : INTERVAL_S;

        /// <summary>0 above 75%, 1 at or below 75%, 2 at or below 50%, 3 at or below 25%.</summary>
        public static int Phase(double healthFrac) => healthFrac <= 0.25 ? 3 : healthFrac <= 0.5 ? 2 : healthFrac <= 0.75 ? 1 : 0;

        /// <summary>Signature damage is halved in the first two minutes (learn the tells), then full.</summary>
        public static double DamageScale(double sinceEngage) => sinceEngage < LEARN_S ? LEARN_DAMAGE : 1.0;

        public static bool Enraged(double sinceEngage) => sinceEngage >= ENRAGE_S;

        public static uint MaxFor(uint baseMax, uint currentMax, int fighters, double k = PER_FIGHTER, double cap = SCALE_CAP) =>
            WorldBossScaling.TargetMax(baseMax, currentMax, fighters, k, cap);

        public static uint HitCap(uint maxHealth, double frac = HIT_CAP_FRAC) => (uint)Math.Max(1, Math.Round(maxHealth * frac));

        /// <summary>What one hit may take after the weakness and the cap: (kept, given back).</summary>
        public static (uint kept, uint back) Clamp(uint amount, uint maxHealth, bool weakened, bool shielded, double frac = HIT_CAP_FRAC, double weakMult = WEAK_MULT)
        {
            if (shielded) return (0, amount);
            uint kept = amount;
            if (weakened) kept = (uint)Math.Round(kept * weakMult);
            kept = Math.Min(kept, HitCap(maxHealth, frac));
            return (kept, amount - kept);
        }

        /// <summary>How many players Hopeslayer's Wrath marks: 1 under 4 in reach, 2 under 7, else 3.</summary>
        public static int WrathMarks(int playersInReach) => playersInReach < 4 ? 1 : playersInReach < 7 ? 2 : 3;

        /// <summary>Each player in one burst takes this share of their own max health: stacking shares it.</summary>
        public static double WrathShare(int inBurst, double frac = WRATH_FRAC) => frac / Math.Max(1, inBurst);

        public static bool InRing(Vector2 boss, Vector2 p, float radius) => Vector2.DistanceSquared(boss, p) <= radius * radius;

        /// <summary>In front of him: within range and within halfDeg of the way he faces.</summary>
        public static bool InCone(Vector2 boss, Vector2 facing, Vector2 p, float range = SLAM_RANGE, float halfDeg = SLAM_HALF_DEG)
        {
            var d = p - boss;
            float len = d.Length();
            if (len > range) return false;
            if (len < 0.5f) return true;   // standing inside him: hit
            if (facing.LengthSquared() < 1e-6f) return false;
            float cos = Vector2.Dot(d / len, Vector2.Normalize(facing));
            return cos >= MathF.Cos(halfDeg * MathF.PI / 180f);
        }

        /// <summary>Where a knocked player lands: dist metres further along the line from him to them (fallback = the way
        /// to push when they stand on his centre, normally his facing).</summary>
        public static Vector2 KnockTo(Vector2 boss, Vector2 p, float dist, Vector2 fallback)
        {
            var d = p - boss;
            if (d.LengthSquared() < 1e-4f) d = fallback.LengthSquared() < 1e-6f ? Vector2.UnitY : fallback;
            return p + Vector2.Normalize(d) * dist;
        }

        /// <summary>The horizontal way a rotation faces (AC: local +Y is forward).</summary>
        public static Vector2 Facing(Quaternion q)
        {
            var f = Vector3.Transform(Vector3.UnitY, q);
            var v = new Vector2(f.X, f.Y);
            return v.LengthSquared() < 1e-6f ? Vector2.UnitY : Vector2.Normalize(v);
        }

        /// <summary>The order of the five: a shuffled bag, refilled when empty, never the same attack twice running;
        /// Teth is skipped (kept in the bag) while it is not allowed.</summary>
        public sealed class Rotation
        {
            readonly Random m_Rng;
            readonly List<Attack> m_Bag = new List<Attack>();
            public Attack? Last { get; private set; }
            public Rotation(int seed) { m_Rng = new Random(seed); }

            public Attack Next(bool allowTeth)
            {
                if (m_Bag.Count == 0) Refill();
                int i = m_Bag.FindIndex(a => a != Last && (allowTeth || a != Attack.Teth));
                if (i < 0) { Refill(); i = m_Bag.FindIndex(a => a != Last && (allowTeth || a != Attack.Teth)); }   // only a repeat or a barred Teth left: deal from a fresh bag behind it
                var pick = m_Bag[i];
                m_Bag.RemoveAt(i);
                Last = pick;
                return pick;
            }

            void Refill()
            {
                m_Bag.AddRange((Attack[])Enum.GetValues(typeof(Attack)));
                for (int i = m_Bag.Count - 1; i > 0; i--) { int j = m_Rng.Next(i + 1); (m_Bag[i], m_Bag[j]) = (m_Bag[j], m_Bag[i]); }
            }
        }

        /// <summary>One planned attack: when it is announced, when it lands.</summary>
        public struct Planned { public double At, Lands; public Attack What; }

        /// <summary>The timeline the server would run for a fight, given when each phase is reached (seconds after the
        /// first hit; NaN = never) and its end: the scheduling checked by the tests.</summary>
        public static List<Planned> Plan(int seed, double lastPhaseAt, double end)
        {
            var rot = new Rotation(seed);
            var list = new List<Planned>();
            double t = FIRST_ATTACK_S;
            while (t < end)
            {
                bool last = !double.IsNaN(lastPhaseAt) && t >= lastPhaseAt;
                bool enraged = Enraged(t);
                var a = enraged ? Attack.Pulse : rot.Next(t >= LEARN_S);
                double w = WindUp(a, last);
                list.Add(new Planned { At = t, Lands = t + w, What = a });
                // the next one starts after this one has landed, at least Interval after this start
                t = Math.Max(t + Interval(last, enraged), t + w + 1.0);
            }
            return list;
        }
    }
}
