using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevivalGuard
{
    /// <summary>The decorator's encounters, PURE</summary>
    public static class DecorEncounterRule
    {
        public const uint GEN_TEMPLATE = 1154, CHEST_TEMPLATE = 3969, KEY_TEMPLATE = 291;
        public const int MAX_COUNT = 12, MAX_PATROL = 16;
        public const float MAX_RADIUS = 40f, MAX_LEG = 60f;
        public const int EMOTE_HEARTBEAT = 5, EMOTE_QUEST_SUCCESS = 12;
        public const uint MOVE = 6, MOVE_HOME = 4, STAMP_QUEST = 22;
        /// <summary>Metres a second a patrol leg is timed at.</summary>
        public const float LEG_SPEED = 1.2f, LEG_PAUSE = 2f;

        public static int Count(int n) => Math.Clamp(n, 1, MAX_COUNT);
        /// <summary>Respawn: 10 s to a week.</summary>
        public static float Respawn(float s) => Math.Clamp(float.IsFinite(s) ? s : 300f, 10f, 7f * 86400f);
        public static float Radius(float m) => Math.Clamp(float.IsFinite(m) ? m : 0f, 0f, MAX_RADIUS);
        /// <summary>A chest's reset: ACE floors anything under 15 s to its own 120 (Chest.ChestResetInterval).</summary>
        public static float ChestReset(float s) => Math.Clamp(float.IsFinite(s) ? s : 600f, 15f, 7f * 86400f);
        public static int Tier(int t) => Math.Clamp(t, 1, 8);
        public static float LockoutHours(float h) => Math.Clamp(float.IsFinite(h) ? h : 0f, 0f, 168f);

        /// <summary>The generator's own regen tick: how often its queue is looked at.</summary>
        public static float RegenInterval(float respawn) => Math.Clamp(Respawn(respawn) / 4f, 5f, 60f);

        /// <summary>One at its own spot is placed exactly there (Specific); more scatter over the radius (Scatter).</summary>
        public static bool Specific(int count, float radius) => Count(count) == 1 && Radius(radius) < 0.5f;

        /// <summary>A treasure_death id per loot tier, each one retail's own chests use.</summary>
        public static uint TreasureFor(int tier) => Tier(tier) switch
        {
            1 => 459, 2 => 16, 3 => 462, 4 => 13, 5 => 460, 6 => 449, 7 => 1001, _ => 2001,
        };

        /// <summary>The names a published boss uses: its lockout quest and its lock code (short, unique per area and key).</summary>
        public static string Slug(string area, string key)
        {
            var s = new System.Text.StringBuilder("Decor_");
            foreach (char c in (area ?? "") + "_" + (key ?? "")) s.Append(char.IsLetterOrDigit(c) ? c : '_');
            return s.ToString();
        }

        public static string QuestName(string area, string bossKey) => Slug(area, bossKey) + "_Loot";
        public static string LockCode(string area, string chestKey) => Slug(area, chestKey).ToLowerInvariant() + "_key";

        public struct Act
        {
            public uint Type; public float Delay; public float X, Y, Z; public string Message;
        }

        public sealed class Set
        {
            public int Category; public float Probability; public string Quest; public List<Act> Acts = new List<Act>();
        }

        public static List<Set> Wander(float radius, int seed)
        {
            var sets = new List<Set>();
            float r = Math.Max(2f, Radius(radius) * 0.6f);
            double turn = (seed % 360) * Math.PI / 180.0;
            for (int i = 0; i < 4; i++)
            {
                double a = turn + i * Math.PI / 2.0;
                var s = new Set { Category = EMOTE_HEARTBEAT, Probability = 0.08f * (i + 1) };
                s.Acts.Add(new Act { Type = MOVE, Delay = 0f, X = Round((float)(Math.Cos(a) * r)), Y = Round((float)(Math.Sin(a) * r)) });
                sets.Add(s);
            }
            var home = new Set { Category = EMOTE_HEARTBEAT, Probability = 0.4f };
            home.Acts.Add(new Act { Type = MOVE_HOME, Delay = 0f });
            sets.Add(home);
            return sets;
        }

        /// <summary>Patrol: points as offsets from home, walked in order, then home</summary>
        public static Set Patrol(IList<(float x, float y, float z)> offsets)
        {
            if (offsets == null || offsets.Count == 0) return null;
            var s = new Set { Category = EMOTE_HEARTBEAT, Probability = 1f };
            float px = 0f, py = 0f, pz = 0f, wait = 0f;
            for (int i = 0; i < offsets.Count && i < MAX_PATROL; i++)
            {
                var o = offsets[i];
                s.Acts.Add(new Act { Type = MOVE, Delay = Round(wait), X = Round(o.x), Y = Round(o.y), Z = Round(o.z) });
                wait = LegTime(px, py, pz, o.x, o.y, o.z);
                px = o.x; py = o.y; pz = o.z;
            }
            s.Acts.Add(new Act { Type = MOVE_HOME, Delay = Round(wait) });
            // the chain holds the emote manager busy until home is reached
            s.Acts.Add(new Act { Type = MOVE_HOME, Delay = Round(LegTime(px, py, pz, 0f, 0f, 0f)) });
            return s;
        }

        public static float LegTime(float ax, float ay, float az, float bx, float by, float bz)
        {
            double d = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
            return (float)(Math.Min(d, MAX_LEG) / LEG_SPEED + LEG_PAUSE);
        }

        /// <summary>The chest's lockout: QuestSuccess for its quest stamps it.</summary>
        public static Set Lockout(string quest)
        {
            var s = new Set { Category = EMOTE_QUEST_SUCCESS, Probability = 1f, Quest = quest };
            s.Acts.Add(new Act { Type = STAMP_QUEST, Delay = 0f, Message = quest });
            return s;
        }

        /// <summary>A patrol leg longer than MAX_LEG or a point outside 1.5 x the landblock is refused (the panel says why).</summary>
        public static string WhyNotPatrol(IList<(float x, float y, float z)> offsets)
        {
            if (offsets == null || offsets.Count == 0) return "A patrol needs at least one point.";
            if (offsets.Count > MAX_PATROL) return $"A patrol has at most {MAX_PATROL} points.";
            float px = 0f, py = 0f;
            foreach (var o in offsets)
            {
                double d = Math.Sqrt((o.x - px) * (o.x - px) + (o.y - py) * (o.y - py));
                if (d > MAX_LEG) return $"A patrol step is {d:0} m: keep each under {MAX_LEG:0} m.";
                px = o.x; py = o.y;
            }
            return null;
        }

        static float Round(float v) => (float)Math.Round(v, 3);

        /// <summary>Seconds in words for the panel and the server's replies: 45 s, 5 min, 2 h, 1 d.</summary>
        public static string Span(float s)
        {
            if (s < 90f) return ((int)Math.Round(s)).ToString(CultureInfo.InvariantCulture) + " s";
            if (s < 5400f) return ((int)Math.Round(s / 60f)).ToString(CultureInfo.InvariantCulture) + " min";
            if (s < 172800f) return Math.Round(s / 3600f, 1).ToString(CultureInfo.InvariantCulture) + " h";
            return Math.Round(s / 86400f, 1).ToString(CultureInfo.InvariantCulture) + " d";
        }
    }
}
