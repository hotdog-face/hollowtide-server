using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of HoltburgGuards (no ACE types; tools/nettest compiles it by path, HeldKeys.cs HoltburgGuards):
    /// time-of-day buckets, which pool a line comes from, lines without repeats, who a guard may fight, the town's bounds,
    /// greetings heard in chat, and the patrol state machine. Coordinates are metres local to landblock A9B4.
    /// </summary>
    public static class HoltburgGuardsRule
    {
        // ---- time of day (Dereth's 16 hours: 1 Darktide ... 9 Midsong ... 13 Evensong, 15 Gloaming) ----
        public enum Bucket { Dawn, Day, Dusk, Night }

        public static Bucket TimeBucket(int derethHour)
        {
            if (derethHour >= 5 && derethHour <= 6) return Bucket.Dawn;       // Dawnsong
            if (derethHour >= 7 && derethHour <= 12) return Bucket.Day;       // Morntide .. Warmtide-and-Half
            if (derethHour >= 13 && derethHour <= 14) return Bucket.Dusk;     // Evensong
            return Bucket.Night;                                               // Gloaming, Darktide, Foredawn
        }

        public static string Key(Bucket b) => b switch { Bucket.Dawn => "dawn", Bucket.Day => "day", Bucket.Dusk => "dusk", _ => "night" };

        /// <summary>The pool an ambient line comes from. <paramref name="r"/> is a roll in [0, 1).</summary>
        public static string PickPool(Bucket b, string season, bool afterFight, bool busy, bool quiet, double r)
        {
            if (afterFight) { if (r < 0.5) return "event.after"; r = (r - 0.5) / 0.5; }
            else if (busy) { if (r < 0.25) return "event.busy"; r = (r - 0.25) / 0.75; }
            else if (quiet) { if (r < 0.25) return "event.quiet"; r = (r - 0.25) / 0.75; }
            if (r < 0.4) return "time." + Key(b);
            if (r < 0.52 && !string.IsNullOrEmpty(season)) return "season." + season;
            return "general";
        }

        /// <summary>
        /// Lines without repeats: each pool is dealt from a shuffled deck, so a guard says every line of a pool once before
        /// any line of it again, and a new deck never starts with the line that ended the last one.
        /// </summary>
        public sealed class LineBag
        {
            readonly Dictionary<string, Queue<int>> m_Decks = new Dictionary<string, Queue<int>>();
            readonly Dictionary<string, int> m_Last = new Dictionary<string, int>();

            public int Next(string pool, int count, Random rng)
            {
                if (count <= 0) return -1;
                if (!m_Decks.TryGetValue(pool, out var deck) || deck.Count == 0)
                {
                    var order = new int[count];
                    for (int i = 0; i < count; i++) order[i] = i;
                    for (int i = count - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
                    if (count > 1 && m_Last.TryGetValue(pool, out int last) && order[0] == last) (order[0], order[1]) = (order[1], order[0]);
                    m_Decks[pool] = deck = new Queue<int>(order);
                }
                int pick = deck.Dequeue();
                m_Last[pool] = pick;
                return pick;
            }
        }

        // ---- ambient pacing ----
        public const double AMBIENT_GAP_MIN = 240, AMBIENT_GAP_SPAN = 240;   // one guard: a line every 4 to 8 minutes at most
        public const double ANY_GUARD_GAP = 45;                               // two guards never speak within 45 s of each other
        public const float EARSHOT = 20f;                                     // a player this close, or no ambient line

        public static bool MaySpeak(double now, double guardNext, double lastAnyGuard, bool playerInEarshot, bool onDuty)
            => !onDuty && playerInEarshot && now >= guardNext && now - lastAnyGuard >= ANY_GUARD_GAP;

        // ---- greetings heard in local chat ----
        static readonly string[] GREET = { "hail", "hello", "hi", "hey", "greetings", "greeting", "morning", "evening", "afternoon",
                                           "howdy", "salutations", "morrow", "hallo", "ho" };
        static readonly string[] GREET2 = { "good day", "well met", "good morrow", "good morning", "good evening", "good night" };

        public static bool IsGreeting(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg) || msg.Length > 60) return false;
            var m = msg.ToLowerInvariant();
            foreach (var p in GREET2) if (m.Contains(p)) return true;
            var words = m.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '\'', '"', '-' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var w in words) if (Array.IndexOf(GREET, w) >= 0) return true;
            return false;
        }

        // ---- the town and who may be fought ----
        /// <summary>Holtburg: the A9B4 block, every building in it, and a little road beyond each edge.</summary>
        public const float X0 = -12, Y0 = -12, X1 = 204, Y1 = 180;
        public const float NOTICE = 60f;           // a guard notices a monster this close (and in town)
        public const float BOUNDS_SLACK = 12f;     // a chase is given up this far outside the town

        public static bool InTown(float x, float y, float margin = 0) => x >= X0 - margin && x <= X1 + margin && y >= Y0 - margin && y <= Y1 + margin;

        public struct Candidate
        {
            public bool IsPlayer, IsPet, HasOwner, IsMonster, Attackable, IsDead, IsGuard, Peaceful, InTown;
            public float Dist;
        }

        public enum Verdict { Hostile, Player, Pet, NotMonster, Peaceful, Dead, Guard, OutOfTown, TooFar }

        /// <summary>
        /// Only a hostile monster in town: never a player (PK or not, a Borg, a mercenary or a bot: all are players), a pet or
        /// summon of anyone, an NPC, a creature that does not attack players on its own (Tolerance NoAttack / Retaliate: town
        /// critters, training targets), another guard, or anything outside the town.
        /// </summary>
        public static Verdict Eligible(Candidate c)
        {
            if (c.IsPlayer) return Verdict.Player;
            if (c.IsPet || c.HasOwner) return Verdict.Pet;
            if (c.IsGuard) return Verdict.Guard;
            if (!c.IsMonster || !c.Attackable) return Verdict.NotMonster;
            if (c.IsDead) return Verdict.Dead;
            if (c.Peaceful) return Verdict.Peaceful;
            if (!c.InTown) return Verdict.OutOfTown;
            if (c.Dist > NOTICE) return Verdict.TooFar;
            return Verdict.Hostile;
        }

        // ---- whose fight is it (owner 2026-11-12: newcomers fight drudges near town; guards must not take their kills) ----
        public const int NEWCOMER_LEVEL = 15;       // a player under this level is a newcomer
        public const double ENGAGE_WINDOW = 10;     // s: a player who hit the monster this recently, or is its target, is fighting it
        public const float RESCUE_HEALTH = 0.25f;   // a newcomer below this share of health is in real trouble

        /// <summary>A player fighting the monster: one who damaged it within the window (Since = seconds ago), or is its
        /// current target (IsTarget). A combat pet's damage or a monster fighting a pet counts as its owner.</summary>
        public struct Engager { public int Level; public double Since; public float Health; public bool IsTarget; }

        public enum Call { Engage, Rescue, HoldNewcomer, HoldPlayerFight }

        /// <summary>
        /// May a guard fight this hostile monster in town (and keep fighting it)?
        ///   a newcomer is fighting it              never (break off at once), unless that newcomer is in real trouble (Rescue)
        ///   it is attacking someone (it swung at a player or a guard within the window)   yes
        ///   no player has fought it within the window                                     yes
        ///   otherwise (a player is fighting it and it has not swung yet)                  not yet
        /// Whoever fought it keeps the full credit either way: a guard's damage is never recorded.
        /// </summary>
        public static Call Decide(bool attacking, IList<Engager> engagers, int newcomerLevel = NEWCOMER_LEVEL,
                                    double window = ENGAGE_WINDOW, float rescueHealth = RESCUE_HEALTH)
        {
            bool fought = false, newcomer = false, trouble = false;
            if (engagers != null)
                foreach (var e in engagers)
                {
                    if (!e.IsTarget && e.Since > window) continue;
                    fought = true;
                    if (e.Level < newcomerLevel) { newcomer = true; if (e.Health < rescueHealth) trouble = true; }
                }
            if (newcomer) return trouble ? Call.Rescue : Call.HoldNewcomer;
            if (attacking || !fought) return Call.Engage;
            return Call.HoldPlayerFight;
        }

        public const double DUTY_TIMEOUT = 120;     // a fight longer than this is given up
        public const float FALLBACK_HEALTH = 0.3f;  // below this share of health a guard falls back to its post
        public const int MAX_PER_MONSTER = 2;       // guards that engage one monster

        // ---- patrol ----
        public enum PState { Station, Walking, Paused, Returning, Duty }
        public enum PAct { None, WalkTo, FaceOut, FaceStation, Teleport }

        public sealed class Mind
        {
            public PState State = PState.Station;
            public int Index;                    // the waypoint walked to (0 = the station)
            public double Until;                 // Station / Paused: when to move on
            public double ProgressAt;            // last time the distance to the waypoint fell
            public float Best = float.MaxValue;  // the closest it has been to that waypoint
            public int Retries;
            public double StuckSince;            // Returning: first time with no progress
        }

        public const float ARRIVE = 1.6f;                        // metres from a waypoint that count as there
        public const double STATION_MIN = 150, STATION_SPAN = 180;   // 2.5 to 5.5 minutes at the station between rounds
        public const double PAUSE_MIN = 10, PAUSE_SPAN = 30;     // 10 to 40 s at each post
        public const double STUCK = 8;                           // no progress this long: stuck
        public const double STUCK_FOR_GOOD = 90;                 // returning, no progress this long even after retries: teleport home
        public const int MAX_AWAY = 2;                           // guards off their station at once
        public const double DEPART_GAP = 45;                     // between two guards leaving

        public static PAct Go(Mind m, PState s, int index, double now)
        {
            m.State = s; m.Index = index; m.ProgressAt = now; m.Best = float.MaxValue; m.Retries = 0; m.StuckSince = 0;
            return PAct.WalkTo;
        }

        /// <summary>
        /// One think (about a second apart). <paramref name="dist"/> is the distance to waypoint m.Index, <paramref name="posts"/>
        /// says which waypoints are posts (index 0 is the station), <paramref name="mayLeave"/> the stagger rule, <paramref name="on"/>
        /// the switch, <paramref name="playerNear"/> whether a player could see a teleport, <paramref name="r"/> a roll in [0, 1).
        /// Duty is decided outside; after it, ReturnFrom.
        /// </summary>
        public static PAct Step(Mind m, double now, float dist, bool[] posts, bool mayLeave, bool on, bool playerNear, double r)
        {
            int n = posts.Length;
            switch (m.State)
            {
                case PState.Station:
                    if (on && mayLeave && now >= m.Until && n > 1) return Go(m, PState.Walking, 1, now);
                    return PAct.None;

                case PState.Duty:
                    return PAct.None;

                case PState.Paused:
                    if (!on) return Go(m, PState.Returning, m.Index, now);
                    if (now < m.Until) return PAct.None;
                    return Go(m, PState.Walking, (m.Index + 1) % n, now);

                case PState.Walking:
                case PState.Returning:
                    if (!on && m.State == PState.Walking) { m.State = PState.Returning; m.ProgressAt = now; m.Best = dist; }
                    if (dist <= ARRIVE)
                    {
                        if (m.Index == 0)
                        {
                            m.State = PState.Station; m.Until = now + STATION_MIN + r * STATION_SPAN;
                            return PAct.FaceStation;
                        }
                        if (m.State == PState.Returning) return Go(m, PState.Returning, m.Index - 1, now);
                        if (posts[m.Index]) { m.State = PState.Paused; m.Until = now + PAUSE_MIN + r * PAUSE_SPAN; return PAct.FaceOut; }
                        return Go(m, PState.Walking, (m.Index + 1) % n, now);
                    }
                    if (dist < m.Best - 0.5f) { m.Best = dist; m.ProgressAt = now; m.StuckSince = 0; }
                    if (now - m.ProgressAt < STUCK) return PAct.None;
                    // stuck
                    if (m.State == PState.Walking)
                    {
                        if (m.Retries++ < 1) { m.ProgressAt = now; return PAct.WalkTo; }
                        // give the round up: back the way it came
                        int back = Math.Max(0, m.Index - 1);
                        var a = Go(m, PState.Returning, back, now);
                        return a;
                    }
                    if (m.StuckSince == 0) m.StuckSince = m.ProgressAt;
                    if (!playerNear && now - m.StuckSince >= STUCK * 2 || now - m.StuckSince >= STUCK_FOR_GOOD)
                    {
                        m.State = PState.Station; m.Index = 0; m.Until = now + STATION_MIN + r * STATION_SPAN;
                        return PAct.Teleport;
                    }
                    m.ProgressAt = now;   // try the same waypoint again
                    return PAct.WalkTo;
            }
            return PAct.None;
        }

        /// <summary>After a fight: walk back by the route, from the waypoint nearest where the guard stands.</summary>
        public static PAct ReturnFrom(Mind m, int nearest, double now) => Go(m, PState.Returning, nearest, now);

        /// <summary>The stagger: a guard may leave its station if fewer than MAX_AWAY are out and none left lately.</summary>
        public static bool MayLeave(int away, double now, double lastDeparture) => away < MAX_AWAY && now - lastDeparture >= DEPART_GAP;
    }
}
