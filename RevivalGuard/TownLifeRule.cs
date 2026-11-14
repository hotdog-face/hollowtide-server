using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of TownLife (no ACE types; tools/nettest compiles it by path, TownLifeCase.cs): the moment of the day,
    /// THE SPEECH BUDGET (owner 2026-11-14: "i dont want holtberg to be flooded with random npc talking"), which pools a line
    /// comes from, greetings, rumours by level, keywords, and the routine state machine (rounds out and back along checked
    /// legs, a stay through the evening, back to the post for business).
    /// </summary>
    public static class TownLifeRule
    {
        // ---- the moment (Dereth's 16 hours: 5-12 Dawnsong .. Warmtide-and-Half, 13-15 Evensong .. Gloaming) ----
        public enum Moment { Day, Evening, Night }

        public static Moment MomentOf(int derethHour)
        {
            if (derethHour >= 5 && derethHour <= 12) return Moment.Day;
            if (derethHour >= 13 && derethHour <= 15) return Moment.Evening;
            return Moment.Night;
        }

        public static string Key(Moment m) => m == Moment.Day ? "day" : m == Moment.Evening ? "evening" : "night";

        public static bool TryMoment(string s, out Moment m)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "day": m = Moment.Day; return true;
                case "evening": m = Moment.Evening; return true;
                case "night": m = Moment.Night; return true;
            }
            m = Moment.Day; return false;
        }

        // =====================================================================================================
        // THE SPEECH BUDGET. Most of town life is silent (routines, walking, gestures); speech is rare:
        //   - unprompted lines: at most one per LineGap per listening player, from the whole town (guards and Azimar too);
        //     each NPC at most once per NpcGap, and only with a player within TalkRange;
        //   - a two-NPC exchange counts as one line and is rarer still (ExchangeGap per listener);
        //   - prompted lines (a Use, a greeting or keyword aimed at the NPC, a greeting by name on arrival) do not count,
        //     but a greeting comes once per player per NPC per GreetGap, an arrival greeting once per ArrivalGap per player;
        //   - an event (raid, PK death, level-up) is answered by one NPC at most;
        //   - one line on a player's screen at a time: an unprompted line finding a listener's screen busy is dropped, never queued.
        // =====================================================================================================
        public sealed class Limits
        {
            public double LineGap = 60, NpcGap = 300, ExchangeGap = 600, GreetGap = 3600, ArrivalGap = 300, Screen = 6, EventWindow = 120;
            public float TalkRange = 10, HearRange = 20;
        }

        public struct Listener
        {
            public uint Id; public float Dist;
            public Listener(uint id, float dist) { Id = id; Dist = dist; }
        }

        public enum No { Ok, NoListener, NpcGap, PlayerGap, ExchangeGap, Screen, Greeted, Arrival }

        public sealed class Budget
        {
            public readonly Limits L;
            readonly Dictionary<uint, double> m_Line = new Dictionary<uint, double>();       // player -> last unprompted line heard
            readonly Dictionary<uint, double> m_Exchange = new Dictionary<uint, double>();   // player -> last exchange heard
            readonly Dictionary<uint, double> m_Screen = new Dictionary<uint, double>();     // player -> a town line on screen until
            readonly Dictionary<uint, double> m_Npc = new Dictionary<uint, double>();        // npc -> last unprompted line
            readonly Dictionary<uint, double> m_Arrival = new Dictionary<uint, double>();    // player -> last arrival greeting
            readonly Dictionary<(uint npc, uint player), double> m_Greeted = new Dictionary<(uint, uint), double>();
            readonly Dictionary<string, double> m_Events = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public int Unprompted, Exchanges, Prompted, Dropped;   // counters (trace, @townlife status)

            public Budget(Limits l = null) { L = l ?? new Limits(); }

            static double Get(Dictionary<uint, double> d, uint k) => d.TryGetValue(k, out var t) ? t : double.NegativeInfinity;

            public bool ScreenFree(uint player, double now) => now >= Get(m_Screen, player);

            /// <summary>May this NPC say an unprompted line to these listeners (players within HearRange) now?</summary>
            public No CheckAmbient(uint npc, IList<Listener> ls, double now)
            {
                bool close = false;
                if (ls != null) foreach (var l in ls) if (l.Dist <= L.TalkRange) { close = true; break; }
                if (!close) return No.NoListener;
                if (now - Get(m_Npc, npc) < L.NpcGap) return No.NpcGap;
                foreach (var l in ls)
                {
                    if (now - Get(m_Line, l.Id) < L.LineGap) return No.PlayerGap;
                    if (!ScreenFree(l.Id, now)) return No.Screen;
                }
                return No.Ok;
            }

            /// <summary>Check and, when allowed, count it at once (the line is said now): true = speak.</summary>
            public bool TryAmbient(uint npc, IList<Listener> ls, double now, double seconds = -1)
            {
                var r = CheckAmbient(npc, ls, now);
                if (r != No.Ok) { if (r == No.Screen || r == No.PlayerGap) Dropped++; return false; }
                m_Npc[npc] = now;
                foreach (var l in ls) { m_Line[l.Id] = now; m_Screen[l.Id] = now + (seconds < 0 ? L.Screen : seconds); }
                Unprompted++;
                return true;
            }

            /// <summary>A two-NPC exchange: counts as one unprompted line for both speakers and every listener, and needs
            /// ExchangeGap since the listener's last one. <paramref name="seconds"/> is how long it holds the screen.</summary>
            public No CheckExchange(uint a, uint b, IList<Listener> ls, double now)
            {
                var r = CheckAmbient(a, ls, now);
                if (r != No.Ok) return r;
                if (now - Get(m_Npc, b) < L.NpcGap) return No.NpcGap;
                foreach (var l in ls) if (now - Get(m_Exchange, l.Id) < L.ExchangeGap) return No.ExchangeGap;
                return No.Ok;
            }

            public bool TryExchange(uint a, uint b, IList<Listener> ls, double now, double seconds)
            {
                if (CheckExchange(a, b, ls, now) != No.Ok) return false;
                m_Npc[a] = now; m_Npc[b] = now;
                foreach (var l in ls) { m_Line[l.Id] = now + seconds; m_Exchange[l.Id] = now; m_Screen[l.Id] = now + seconds; }
                Unprompted++; Exchanges++;
                return true;
            }

            /// <summary>A prompted line (an answer to the player): not counted against the budget; it takes the screen.</summary>
            public void PromptedLine(IList<Listener> ls, double now, double seconds = -1)
            {
                if (ls != null) foreach (var l in ls) m_Screen[l.Id] = Math.Max(Get(m_Screen, l.Id), now + (seconds < 0 ? L.Screen : seconds));
                Prompted++;
            }

            /// <summary>A greeting by this NPC to this player: once per GreetGap; an arrival greeting (the NPC speaks first) also
            /// at most once per ArrivalGap per player across the town, and only on a free screen.</summary>
            public No CheckGreet(uint npc, uint player, double now, bool arrival)
            {
                if (m_Greeted.TryGetValue((npc, player), out var t) && now - t < L.GreetGap) return No.Greeted;
                if (arrival && now - Get(m_Arrival, player) < L.ArrivalGap) return No.Arrival;
                if (arrival && !ScreenFree(player, now)) return No.Screen;
                return No.Ok;
            }

            public bool TryGreet(uint npc, uint player, double now, bool arrival)
            {
                if (CheckGreet(npc, player, now, arrival) != No.Ok) return false;
                m_Greeted[(npc, player)] = now;
                if (arrival) m_Arrival[player] = now;
                return true;
            }

            /// <summary>An event (a raid, a PK death, a level-up) is answered by one NPC: the first to claim it within the window.</summary>
            public bool ClaimEvent(string key, double now)
            {
                if (m_Events.TryGetValue(key, out var t) && now - t < L.EventWindow) return false;
                m_Events[key] = now;
                return true;
            }

            /// <summary>Forget what no rule can need any more (called now and then).</summary>
            public void Prune(double now)
            {
                double keep = Math.Max(Math.Max(L.LineGap, L.ExchangeGap), Math.Max(L.NpcGap, L.ArrivalGap)) + 60;
                Trim(m_Line, now, keep); Trim(m_Exchange, now, keep); Trim(m_Screen, now, 60); Trim(m_Npc, now, keep); Trim(m_Arrival, now, keep);
                var old = new List<(uint, uint)>();
                foreach (var kv in m_Greeted) if (now - kv.Value > L.GreetGap) old.Add(kv.Key);
                foreach (var k in old) m_Greeted.Remove(k);
                var oe = new List<string>();
                foreach (var kv in m_Events) if (now - kv.Value > L.EventWindow) oe.Add(kv.Key);
                foreach (var k in oe) m_Events.Remove(k);
            }

            static void Trim(Dictionary<uint, double> d, double now, double keep)
            {
                var old = new List<uint>();
                foreach (var kv in d) if (now - kv.Value > keep) old.Add(kv.Key);
                foreach (var k in old) d.Remove(k);
            }
        }

        // =====================================================================================================
        // WHICH POOL A LINE COMES FROM. Pool names (TownLife.json "lines"): npc.KEY[.moment|.event.E|.fest.F], role.ROLE[...],
        // town[.moment], event.E, fest.F, news. The caller takes the first pool in the list that has lines.
        // =====================================================================================================
        public sealed class Ctx
        {
            public string Npc, Role, Event, Festival;
            public Moment Moment;
            public bool News;
        }

        public static List<string> AmbientPools(Ctx c, double r)
        {
            var l = new List<string>();
            string m = Key(c.Moment);
            if (!string.IsNullOrEmpty(c.Event))
            {
                l.Add($"npc.{c.Npc}.event.{c.Event}"); l.Add($"role.{c.Role}.event.{c.Event}"); l.Add("event." + c.Event);
                return l;
            }
            if (!string.IsNullOrEmpty(c.Festival) && r < 0.25)
            {
                l.Add($"npc.{c.Npc}.fest.{c.Festival}"); l.Add($"role.{c.Role}.fest.{c.Festival}"); l.Add("fest." + c.Festival);
            }
            else if (c.News && r >= 0.25 && r < 0.33) { l.Add($"role.{c.Role}.news"); l.Add("news"); }   // (r 0.25..0.33: about one line in twelve)
            else if (r < 0.7) { l.Add($"npc.{c.Npc}.{m}"); l.Add($"npc.{c.Npc}"); }
            l.Add($"role.{c.Role}.{m}"); l.Add($"role.{c.Role}"); l.Add("town." + m); l.Add("town");
            return l;
        }

        public static string Band(int level) => level <= 15 ? "low" : level <= 50 ? "mid" : level <= 125 ? "high" : "legend";

        /// <summary>The greeting pools in the order tried. <paramref name="visits"/> counts this visit.</summary>
        public static List<string> GreetPools(string npc, int visits, double daysAway, int level, double diedAgo, bool hasTitle, Moment m, double r)
        {
            var l = new List<string>();
            if (diedAgo >= 0 && diedAgo < 900) l.Add("greet.died");
            else if (visits <= 1) l.Add("greet.first");
            else if (daysAway >= 3) l.Add("greet.away");
            else if (r < 0.3) l.Add("greet.level." + Band(level));
            else if (r < 0.45 && hasTitle) l.Add("greet.title");
            else l.Add("greet.return");
            l.Add($"npc.{npc}.greet"); l.Add("greet." + Key(m)); l.Add("greet.return");
            return l;
        }

        /// <summary>Fill the tokens a line may carry: {name} {visits} (an ordinal) {level} {title} {chronicle}.</summary>
        public static string Fill(string line, string name, int visits, int level, string title, string chronicle)
        {
            if (line == null) return null;
            return line.Replace("{name}", name ?? "traveller").Replace("{visits}", Ordinal(Math.Max(1, visits)))
                       .Replace("{level}", level.ToString()).Replace("{title}", title ?? "").Replace("{chronicle}", chronicle ?? "");
        }

        public static bool NeedsToken(string line, string token) => line != null && line.IndexOf("{" + token + "}", StringComparison.Ordinal) >= 0;

        public static string Ordinal(int n)
        {
            var s = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return n + s;
        }

        /// <summary>"KingOfTheHill" -> "King Of The Hill" (ACE's CharacterTitle names).</summary>
        public static string Humanize(string enumName)
        {
            if (string.IsNullOrEmpty(enumName)) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < enumName.Length; i++)
            {
                char ch = enumName[i];
                if (i > 0 && char.IsUpper(ch) && !char.IsUpper(enumName[i - 1])) sb.Append(' ');
                sb.Append(ch == '_' ? ' ' : ch);
            }
            return sb.ToString().Trim();
        }

        // ---- rumours: the bands that hold a level (TownLife.json "rumours": {min, max, lines}) ----
        public static List<int> RumourBands(int level, IList<(int min, int max)> bands)
        {
            var l = new List<int>();
            for (int i = 0; i < bands.Count; i++) if (level >= bands[i].min && level <= bands[i].max) l.Add(i);
            return l;
        }

        // ---- what a player said: a greeting (HoltburgGuardsRule.IsGreeting's words) or a keyword ----
        static readonly char[] SEP = { ' ', ',', '.', '!', '?', ';', ':', '\'', '"', '-' };

        public static string[] Words(string msg) =>
            string.IsNullOrWhiteSpace(msg) ? Array.Empty<string>() : msg.ToLowerInvariant().Split(SEP, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>The first keyword group whose word the message holds (a whole word, or a phrase with a space), else null.</summary>
        public static string Keyword(string msg, IEnumerable<KeyValuePair<string, string[]>> groups)
        {
            if (string.IsNullOrWhiteSpace(msg) || msg.Length > 120 || groups == null) return null;
            var m = msg.ToLowerInvariant();
            var words = Words(msg);
            foreach (var g in groups)
                foreach (var w in g.Value)
                {
                    if (string.IsNullOrEmpty(w)) continue;
                    if (w.IndexOf(' ') >= 0 ? m.Contains(w) : Array.IndexOf(words, w) >= 0) return g.Key;
                }
            return null;
        }

        /// <summary>Is the message aimed at this NPC by name (any word of its name of four letters or more, e.g. "Wilomine")?</summary>
        public static bool Names(string msg, string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return false;
            var words = Words(msg);
            foreach (var part in npcName.ToLowerInvariant().Split(new[] { ' ', ',', '\'' }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 4 && part != "the" && Array.IndexOf(words, part) >= 0) return true;
            return false;
        }

        // =====================================================================================================
        // ROUTINES. A round is out and back along its points (post, p0 .. pn, back the same way), every leg checked offline
        // (tools/townlife/check_legs.py). A point may be a STAY: the NPC stays there for the rest of the moment it arrived in
        // (patrons drifting in for the evening, townsfolk at their door for the night). Business (a player near, a Use, an
        // emote chain, an alarm) sends it back to its post and keeps it there.
        // =====================================================================================================
        public enum RState { Post, Walking, Paused, Returning }
        public enum RAct { None, WalkTo, FacePost, Stop, Teleport }

        public sealed class RoundDef
        {
            public string Name;
            public Moment[] When = { Moment.Day, Moment.Evening };
            public int Count;          // points
            public bool[] Stay;        // per point
            public double PauseMin = 8, PauseSpan = 14;
            public bool Run;
            public double Weight = 1;
            public bool Has(Moment m) { foreach (var w in When) if (w == m) return true; return false; }
            public bool AnyStay { get { if (Stay != null) foreach (var s in Stay) if (s) return true; return false; } }
        }

        public sealed class Routine
        {
            public RState State = RState.Post;
            public int Round = -1, Index = -1;     // Index -1 = the post
            public double Until, ProgressAt, StuckSince;
            public float Best = float.MaxValue;
            public int Retries;
            public Moment StayMoment; public bool Staying;
            public bool Held;                      // stopped where it stood for business
            public string Last = "";               // the last round walked (not twice running when another fits)
        }

        public const float ARRIVE = 0.8f;
        public const double POST_MIN = 90, POST_SPAN = 150;     // 1.5 to 4 minutes at the post between rounds
        public const double STUCK = 8, STUCK_FOR_GOOD = 25;

        static RAct Target(Routine m, RState s, int index, double now)
        {
            m.State = s; m.Index = index; m.ProgressAt = now; m.Best = float.MaxValue; m.Retries = 0; m.StuckSince = 0;
            return RAct.WalkTo;
        }

        /// <summary>The round to walk now, or -1: a stay round for this moment first (it has somewhere to be), else one that
        /// fits the moment, by weight, not the one just walked when another fits.</summary>
        public static int PickRound(IList<RoundDef> rounds, Moment mo, string last, double r)
        {
            if (rounds == null || rounds.Count == 0) return -1;
            for (int i = 0; i < rounds.Count; i++) if (rounds[i].Has(mo) && rounds[i].AnyStay) return i;
            var fit = new List<int>(); double total = 0;
            for (int i = 0; i < rounds.Count; i++) if (rounds[i].Has(mo) && !rounds[i].AnyStay) fit.Add(i);
            if (fit.Count > 1) fit.RemoveAll(i => rounds[i].Name == last);
            foreach (var i in fit) total += Math.Max(0.01, rounds[i].Weight);
            double x = r * total;
            foreach (var i in fit) { x -= Math.Max(0.01, rounds[i].Weight); if (x < 0) return i; }
            return fit.Count > 0 ? fit[fit.Count - 1] : -1;
        }

        /// <summary>
        /// One think (about a second apart). <paramref name="dist"/>: metres to the current target (the post when Index is -1).
        /// <paramref name="hold"/>: someone is dealing with it where it stands (a Use, its emotes running, a vendor's customer):
        /// it stops and waits, and walks back to its post once that is over. <paramref name="summon"/>: back to the post now and
        /// stay there (a player waiting at its post, an alarm, the switch off). <paramref name="playerNear"/>: someone could see a
        /// teleport home. <paramref name="r"/> a roll in [0, 1).
        /// </summary>
        public static RAct Step(Routine m, double now, Moment mo, float dist, IList<RoundDef> rounds, bool hold, bool summon, bool playerNear, double r)
        {
            RoundDef rd = m.Round >= 0 && rounds != null && m.Round < rounds.Count ? rounds[m.Round] : null;
            if (hold)
            {
                if (m.State == RState.Post) return RAct.None;
                bool moving = !m.Held && (m.State == RState.Walking || m.State == RState.Returning);
                m.Held = true;
                return moving ? RAct.Stop : RAct.None;
            }
            if (m.Held)
            {
                m.Held = false;
                if (m.State != RState.Post) return Target(m, RState.Returning, m.State == RState.Returning ? m.Index : m.Index - 1, now);
            }
            if (rd == null && m.State != RState.Post && m.State != RState.Returning) return Target(m, RState.Returning, -1, now);
            switch (m.State)
            {
                case RState.Post:
                    if (summon || now < m.Until) return RAct.None;
                    int k = PickRound(rounds, mo, m.Last, r);
                    if (k < 0 || rounds[k].Count == 0) { m.Until = now + POST_MIN + r * POST_SPAN; return RAct.None; }
                    // a stay round already walked in this moment is not walked again (back at the post for business, it stays)
                    if (rounds[k].AnyStay && m.Staying && m.StayMoment == mo) { m.Until = now + POST_MIN; return RAct.None; }
                    m.Round = k; m.Last = rounds[k].Name; m.Staying = false;
                    return Target(m, RState.Walking, 0, now);

                case RState.Paused:
                    if (summon) return Back(m, now);
                    if (m.Staying) return mo != m.StayMoment ? Back(m, now) : RAct.None;
                    if (now < m.Until) return RAct.None;
                    if (m.Index < rd.Count - 1) return Target(m, RState.Walking, m.Index + 1, now);
                    return Back(m, now);

                case RState.Walking:
                case RState.Returning:
                    if (m.State == RState.Walking && summon) return Back(m, now);
                    if (dist <= ARRIVE)
                    {
                        if (m.State == RState.Returning)
                        {
                            if (m.Index < 0)
                            {
                                m.State = RState.Post; m.Round = -1;
                                m.Until = now + POST_MIN + r * POST_SPAN;
                                return RAct.FacePost;
                            }
                            return Target(m, RState.Returning, m.Index - 1, now);
                        }
                        // walking: at a point
                        if (rd.Stay != null && m.Index < rd.Stay.Length && rd.Stay[m.Index])
                        {
                            m.State = RState.Paused; m.Staying = true; m.StayMoment = mo; m.Until = double.MaxValue;
                            return RAct.Stop;
                        }
                        m.State = RState.Paused; m.Until = now + rd.PauseMin + r * rd.PauseSpan;
                        return RAct.Stop;
                    }
                    if (dist < m.Best - 0.3f) { m.Best = dist; m.ProgressAt = now; m.StuckSince = 0; }
                    if (now - m.ProgressAt < STUCK) return RAct.None;
                    if (m.State == RState.Walking)
                    {
                        if (m.Retries++ < 1) { m.ProgressAt = now; return RAct.WalkTo; }
                        return Back(m, now);
                    }
                    if (m.StuckSince == 0) m.StuckSince = m.ProgressAt;
                    if (!playerNear && now - m.StuckSince >= STUCK * 2 || now - m.StuckSince >= STUCK_FOR_GOOD)
                    {
                        m.State = RState.Post; m.Index = -1; m.Until = now + POST_MIN + r * POST_SPAN;
                        return RAct.Teleport;
                    }
                    m.ProgressAt = now;
                    return RAct.WalkTo;
            }
            return RAct.None;
        }

        /// <summary>Back toward the post the way it came: from a point (Paused) the one before it; mid-leg (Walking) the point
        /// it set out from (index - 1 either way; -1 is the post).</summary>
        static RAct Back(Routine m, double now) => Target(m, RState.Returning, Math.Max(-1, m.Index - 1), now);

        /// <summary>Away from its post (walking, paused or on its way back).</summary>
        public static bool Away(Routine m) => m.State != RState.Post;

        // ---- gestures: an NPC's own list (TownLife.json), never the same twice running, only standing with someone to see ----
        public static bool MayGesture(RState s, bool moving, bool playerNear, bool business, double now, double busyUntil)
            => (s == RState.Post || s == RState.Paused) && !moving && playerNear && !business && now >= busyUntil;

        public static int PickGesture(int count, int last, double r)
        {
            if (count <= 0) return -1;
            if (count == 1) return 0;
            int k = Math.Min(count - 2, (int)(r * (count - 1)));
            return k >= last && last >= 0 ? k + 1 : k;
        }

        public static double NextGesture(double now, double minutes, double r)
            => minutes <= 0 ? double.MaxValue : now + minutes * 60 * (0.75 + 0.5 * r);

        /// <summary>An NPC's own chance at an unprompted line comes round every NpcGap give or take (the budget decides).</summary>
        public static double NextLine(double now, double npcGap, double r) => now + npcGap * (1.0 + 0.6 * r);

        /// <summary>A visit: back in town after this long away (seconds) counts again.</summary>
        public const double VISIT_GAP = 7200;
        public static bool NewVisit(double lastSeen, double now) => double.IsNegativeInfinity(lastSeen) || lastSeen <= 0 || now - lastSeen >= VISIT_GAP;
    }
}
