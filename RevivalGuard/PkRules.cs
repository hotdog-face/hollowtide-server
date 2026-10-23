using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevivalGuard
{
    /// <summary>
    /// THE PK LADDER'S RULES, PURE C# (no ACE types) so NetTest runs them (tools/nettest/PkRulesCase.cs): the
    /// honour check on plain facts, the scoring of one kill into a ledger, and the re-judge of a whole season.
    /// PkCommon.Judge and PkRenown.Score feed these from the live Player objects.
    ///
    /// Owner 2026-10-22: the newcomer line is level 15 (was 30), and the Hollow Company mercenaries are "players
    /// only": a kill of or by one never earns renown or infamy and is no kill and no death on the ladder. The
    /// season so far is judged again under those rules (Rejudge), once, the first time the ladder is used after
    /// the deploy (RULES).
    /// </summary>
    internal static partial class PkCommon
    {
        internal const long DEFAULT_MIN_LEVEL = 15;
        internal const double DEFAULT_RATIO = 0.6, DEFAULT_REPEAT_HOURS = 24;
        internal const string WHY_MERC = "a mercenary";

        internal enum Verdict { Honourable, NotCounted, Infamous }

        internal sealed class Judgement
        {
            public Verdict Verdict;
            public string Why;          // for the killer and the log, when not honourable
            public double Weight = 1;   // honourable kills: repeat and gank reductions, 0..1
            public int Attackers = 1;
        }

        /// <summary>What the honour check needs to know about one kill.</summary>
        internal struct Facts
        {
            public bool NoKiller { get; set; }
            public bool SameAccount { get; set; }
            public bool SameAddress { get; set; }
            public bool Allies { get; set; }
            public bool KillerMerc { get; set; }
            public bool VictimMerc { get; set; }
            public int KillerLevel { get; set; }
            public int VictimLevel { get; set; }
        }

        internal static Judgement JudgeFacts(Facts f, int attackers, int priorKills, long minLevel, double ratio)
        {
            var j = new Judgement { Attackers = Math.Max(1, attackers) };
            if (f.NoKiller) { j.Verdict = Verdict.NotCounted; j.Why = "no killer"; return j; }
            // PLAYERS ONLY (owner 2026-10-22): the mercenaries are disclosed bots, so neither side of their fights counts.
            if (f.KillerMerc || f.VictimMerc) { j.Verdict = Verdict.NotCounted; j.Why = WHY_MERC; return j; }
            if (f.SameAccount) { j.Verdict = Verdict.NotCounted; j.Why = "two characters of one account"; return j; }
            if (f.SameAddress) { j.Verdict = Verdict.NotCounted; j.Why = "two characters on one connection"; return j; }
            if (f.Allies) { j.Verdict = Verdict.NotCounted; j.Why = "an ally"; return j; }

            int kl = Math.Max(1, f.KillerLevel), vl = Math.Max(1, f.VictimLevel);
            if (vl < minLevel)
            { j.Verdict = Verdict.Infamous; j.Why = $"a newcomer (level {vl})"; return j; }
            if (vl < kl * ratio)
            { j.Verdict = Verdict.Infamous; j.Why = $"someone far beneath you (level {vl} to your {kl})"; return j; }
            if (priorKills >= 3)
            { j.Verdict = Verdict.NotCounted; j.Why = $"killed by you {priorKills} times already today"; return j; }

            j.Verdict = Verdict.Honourable;
            j.Weight = 1.0 / (1 + priorKills);
            // A GANK SQUAD SHARES ONE KILL: two on one is a fair fight's worth, more than two
            // divides it (the 2006 dev chat: "fewer battles that are more epic", not ten on one).
            if (j.Attackers > 2) j.Weight *= 2.0 / j.Attackers;
            return j;
        }

        internal static bool IsRepeat(string why) => why != null && why.StartsWith("killed by you", StringComparison.Ordinal);
    }

    internal static partial class PkRenown
    {
        internal const int WANTED_AT = 5, KILL_LOG_KEEP = 2000;
        internal const long INFAMY_FADE = 86400;
        /// <summary>The ledger's rules version. 2 = newcomer line 15 and mercenaries never counted (2026-10-22).
        /// A ledger saved under an older version is judged again once (EnsureRejudged).</summary>
        internal const int RULES = 2;

        // ACE.Entity.Enum.CharacterTitle values (checked against the enum at startup, PkRenown.CheckTitleIds).
        internal const uint T_BLOODLETTER = 26, T_DUELIST = 46, T_KINGSLAYER = 81, T_WARLORD = 153, T_BOUNTY_HUNTER = 185,
            T_GLADIATOR = 215, T_CHAMPION = 244, T_MURDERER = 303;

        internal sealed class Rec
        {
            public string Name { get; set; }
            public long Renown { get; set; }
            public int Kills { get; set; }
            public int Deaths { get; set; }
            public int Streak { get; set; }
            public int Best { get; set; }
            public int Infamy { get; set; }
            public long InfamyAt { get; set; }
            public List<uint> Titles { get; set; } = new List<uint>();        // granted this season
            public List<uint> PendingTitles { get; set; } = new List<uint>(); // earned while offline
        }

        internal sealed class KillEntry
        {
            public long At { get; set; }
            public uint Killer { get; set; }
            public uint Victim { get; set; }
            public bool Counts { get; set; }    // counts toward the repeat rule
            public int Points { get; set; }
            public string Why { get; set; }
            public int KL { get; set; }         // killer's level at the kill (0 = not recorded: kills before 2026-10-22)
            public int VL { get; set; }         // victim's level at the kill
        }

        internal sealed class State
        {
            public string Season { get; set; }
            public long SeasonStarted { get; set; }
            public int Rules { get; set; }
            public long RejudgedAt { get; set; }
            public Dictionary<uint, Rec> Chars { get; set; } = new Dictionary<uint, Rec>();
            public List<KillEntry> Kills { get; set; } = new List<KillEntry>();
            /// <summary>Characters taken off the ladder by @pkladder remove, and when: a re-judge skips their kills
            /// up to then. A guid in Kills with no record at all was removed before this was kept.</summary>
            public Dictionary<uint, long> Removed { get; set; } = new Dictionary<uint, long>();
        }

        /// <summary>One kill as Apply sees it.</summary>
        internal sealed class KillIn
        {
            public uint Killer, Victim;
            public string KillerName, VictimName, KillerOf = "";
            public int KillerLevel, VictimLevel;     // 0 = unknown
            public long At;
            // A re-judged kill whose levels were never recorded keeps the points it was given, moved by the change
            // in its bonuses (a victim's streak that was built on mercenaries is gone).
            public int? FixedPoints;
            public double OldBonus;
            public bool ExactPoints;                 // the old season replayed: its stored points, as they were
        }

        internal sealed class Outcome
        {
            public int Points;
            public double Bonus;
            public List<string> KillerLines = new List<string>();
            public List<string> World = new List<string>();
            public List<(uint guid, uint title)> Titles = new List<(uint, uint)>();
            public KillEntry Entry;
        }

        internal static int InfamyOf(Rec r, long now)
        {
            if (r.Infamy <= 0) return 0;
            long faded = Math.Max(0, (now - r.InfamyAt) / INFAMY_FADE);
            return (int)Math.Max(0, r.Infamy - faded);
        }

        /// <summary>The ladder's leader (guid), or 0.</summary>
        internal static uint LeaderOf(State s) =>
            s.Chars.Where(kv => kv.Value.Renown > 0).OrderByDescending(kv => kv.Value.Renown).ThenByDescending(kv => kv.Value.Kills)
                .Select(kv => kv.Key).FirstOrDefault();

        static Rec GetRec(State s, uint g, string name)
        {
            if (!s.Chars.TryGetValue(g, out var r)) s.Chars[g] = r = new Rec();
            if (name != null) r.Name = name;
            return r;
        }

        internal static int PriorKillsIn(State s, uint killer, uint victim, long now, double repeatHours)
        {
            long since = now - (long)(repeatHours * 3600);
            return s.Kills.Count(k => k.Counts && k.Killer == killer && k.Victim == victim && k.At >= since && k.At <= now);
        }

        /// <summary>Score one judged kill into the ledger: renown, streaks, infamy, titles, and the kill log entry.
        /// The caller holds the ledger lock, and says the lines and grants the titles afterwards.</summary>
        internal static Outcome Apply(State s, KillIn k, PkCommon.Judgement j)
        {
            var o = new Outcome();
            long now = k.At;
            bool merc = j.Verdict == PkCommon.Verdict.NotCounted && j.Why == PkCommon.WHY_MERC;
            if (merc)
            {
                // No record is made for either side: a mercenary is never on the ladder, and a player's streak,
                // deaths and infamy are untouched by one.
                o.KillerLines.Add($"That kill earns no renown: {j.Why}.");
                o.Entry = new KillEntry { At = now, Killer = k.Killer, Victim = k.Victim, Why = j.Why, KL = k.KillerLevel, VL = k.VictimLevel };
                s.Kills.Add(o.Entry);
                Trim(s);
                return o;
            }
            var kr = GetRec(s, k.Killer, k.KillerName); var vr = GetRec(s, k.Victim, k.VictimName);
            bool wasLeader = LeaderOf(s) == k.Victim;
            int victimStreak = vr.Streak;
            int victimInfamy = InfamyOf(vr, now);
            // a counted death ends the victim's streak, whatever the verdict (an ally's kill does not)
            if (j.Verdict != PkCommon.Verdict.NotCounted || PkCommon.IsRepeat(j.Why))
            {
                vr.Deaths++;
                vr.Streak = 0;
                if (victimStreak >= WANTED_AT)
                    o.World.Add($"{kr.Name} has ended {vr.Name}'s killing streak of {victimStreak}.");
            }

            switch (j.Verdict)
            {
                case PkCommon.Verdict.Honourable:
                {
                    double bonus = 0;
                    var extra = new List<string>();
                    if (victimStreak >= 3) { bonus += 2 * victimStreak; extra.Add($"ended a streak of {victimStreak}"); }
                    if (victimStreak >= WANTED_AT) { bonus += 25; extra.Add("a Wanted player killer"); }
                    if (victimInfamy >= 3) { bonus += 10; extra.Add("justice on a Murderer"); }
                    o.Bonus = bonus;
                    if (k.ExactPoints && k.FixedPoints.HasValue) o.Points = k.FixedPoints.Value;
                    else if (k.FixedPoints.HasValue && (k.KillerLevel <= 0 || k.VictimLevel <= 0))
                        o.Points = Math.Max(1, (int)Math.Round(k.FixedPoints.Value + (bonus - k.OldBonus) * j.Weight));
                    else
                    {
                        double ratio = k.KillerLevel > 0 && k.VictimLevel > 0 ? Math.Clamp((double)k.VictimLevel / k.KillerLevel, 0.5, 2.0) : 1.0;
                        o.Points = Math.Max(1, (int)Math.Round((10 * ratio + bonus) * j.Weight));
                    }
                    int pts = o.Points;
                    kr.Renown += pts; kr.Kills++; kr.Streak++;
                    kr.Best = Math.Max(kr.Best, kr.Streak);
                    string why = j.Weight < 1 ? (j.Attackers > 2 ? $", shared among {j.Attackers}" : ", less for a victim you killed today") : "";
                    o.KillerLines.Add($"+{pts} renown for {vr.Name}{(extra.Count > 0 ? " (" + string.Join(", ", extra) + ")" : "")}{why}. Renown {kr.Renown}, streak {kr.Streak}.");
                    if (kr.Streak == WANTED_AT)
                        o.World.Add($"{kr.Name}{k.KillerOf} has killed {WANTED_AT} player killers without falling and is now Wanted.");
                    else if (kr.Streak > WANTED_AT && kr.Streak % 5 == 0)
                        o.World.Add($"{kr.Name}'s killing streak reaches {kr.Streak}.");
                    foreach (var (at, t) in new[] { (100L, T_DUELIST), (500L, T_BLOODLETTER), (1500L, T_GLADIATOR) })
                        if (kr.Renown >= at && !kr.Titles.Contains(t)) { kr.Titles.Add(t); o.Titles.Add((k.Killer, t)); }
                    if (wasLeader && !kr.Titles.Contains(T_KINGSLAYER))
                    { kr.Titles.Add(T_KINGSLAYER); o.Titles.Add((k.Killer, T_KINGSLAYER)); }
                    break;
                }
                case PkCommon.Verdict.Infamous:
                {
                    int inf = InfamyOf(kr, now) + 1;
                    kr.Infamy = inf; kr.InfamyAt = now;
                    kr.Streak = 0;
                    o.KillerLines.Add($"No renown for killing {j.Why}. Your infamy is {inf}; it fades a point a day.");
                    if (inf >= 3 && !kr.Titles.Contains(T_MURDERER))
                    {
                        kr.Titles.Add(T_MURDERER); o.Titles.Add((k.Killer, T_MURDERER));
                        o.World.Add($"{kr.Name} preys on the weak and is branded a Murderer. Killing them is justice.");
                    }
                    break;
                }
                default:
                    o.KillerLines.Add($"That kill earns no renown: {j.Why}.");
                    break;
            }

            o.Entry = new KillEntry
            {
                At = now, Killer = k.Killer, Victim = k.Victim, Points = o.Points, Why = j.Why, KL = k.KillerLevel, VL = k.VictimLevel,
                Counts = j.Verdict == PkCommon.Verdict.Honourable || PkCommon.IsRepeat(j.Why),
            };
            s.Kills.Add(o.Entry);
            Trim(s);
            return o;
        }

        static void Trim(State s)
        {
            if (s.Kills.Count > KILL_LOG_KEEP) s.Kills.RemoveRange(0, s.Kills.Count - KILL_LOG_KEEP);
        }

        // ------------------------------------------------------------------ the re-judge

        /// <summary>What the re-judge may ask the world: is this character a mercenary (by account), and its level
        /// today (the fallback for a kill whose killer level was never recorded). Either may answer null.</summary>
        internal interface IWorld
        {
            bool? IsMerc(uint guid);
            int? LevelNow(uint guid);
        }

        internal sealed class RejudgeResult
        {
            public State Fresh;
            public int Kills, Changed, Skipped, Mercs, LevelsFromToday;
            public List<string> Lines = new List<string>();   // one per change worth telling staff
            public List<(uint guid, string name, uint title)> Granted = new List<(uint, string, uint)>();
            public List<(uint guid, string name, uint title)> Taken = new List<(uint, string, uint)>();
        }

        static readonly Regex NEWCOMER = new Regex(@"^a newcomer \(level (\d+)\)$");
        static readonly Regex BENEATH = new Regex(@"^someone far beneath you \(level (\d+) to your (\d+)\)$");
        static readonly HashSet<string> KEEP_WHY = new HashSet<string>
            { "no killer", "two characters of one account", "two characters on one connection", "an ally" };
        static readonly uint[] LADDER_TITLES = { T_DUELIST, T_BLOODLETTER, T_GLADIATOR, T_KINGSLAYER, T_MURDERER };

        /// <summary>
        /// THE SEASON JUDGED AGAIN under today's rules, from the kill log. Each kill's levels come from what was
        /// recorded with it (KL/VL, kept since 2026-10-22), else from its stored reason ("a newcomer (level 20)" has
        /// the victim's; "far beneath" both), else the killer's level today (LevelsFromToday). A kill that was
        /// honourable and has no levels stays honourable at its old points, moved only by changed bonuses. Kills
        /// refused for the account, the connection or an ally stay refused (the address is not stored), and the
        /// kills of characters taken off the ladder stay off it.
        /// TITLES: the ladder's titles are what the new judging earns. A title the old ledger had and the new
        /// judging does not give is KEPT, unless the old judging only gave it because of mercenary kills (the old
        /// season replayed with and without them); bounty and season titles are always kept.
        /// </summary>
        internal static RejudgeResult Rejudge(State old, IWorld world, long minLevel, double ratio, double repeatHours, long now)
        {
            var res = new RejudgeResult();
            var mercCache = new Dictionary<uint, bool>();
            bool IsMerc(uint g)
            {
                if (!mercCache.TryGetValue(g, out var m)) mercCache[g] = m = world?.IsMerc(g) ?? false;
                return m;
            }
            string NameOf(uint g) => old.Chars.TryGetValue(g, out var r) ? r.Name : g.ToString();
            bool Dropped(KillEntry k) =>
                !old.Chars.ContainsKey(k.Killer) || !old.Chars.ContainsKey(k.Victim)
                || (old.Removed != null && ((old.Removed.TryGetValue(k.Killer, out var a) && k.At <= a) || (old.Removed.TryGetValue(k.Victim, out var b) && k.At <= b)));

            var kills = old.Kills.OrderBy(k => k.At).ToList();

            // The old judging replayed (stored verdicts and points), with and without the mercenary kills: which
            // titles only the mercenaries gave, and each old honourable kill's bonus.
            var oldBonus = new Dictionary<KillEntry, double>();
            var withMercs = Replay(kills, k => !Dropped(k), oldBonus);
            var noMercs = Replay(kills, k => !Dropped(k) && !IsMerc(k.Killer) && !IsMerc(k.Victim), null);

            var fresh = new State
            {
                Season = old.Season, SeasonStarted = old.SeasonStarted, Rules = RULES, RejudgedAt = now,
                Removed = new Dictionary<uint, long>(old.Removed ?? new Dictionary<uint, long>()),
            };
            foreach (var k in kills)
            {
                if (Dropped(k)) { fresh.Kills.Add(k); res.Skipped++; continue; }
                res.Kills++;
                bool km = IsMerc(k.Killer), vm = IsMerc(k.Victim);
                int kl = k.KL, vl = k.VL;
                var f = new PkCommon.Facts { KillerMerc = km, VictimMerc = vm };
                var oldVerdict = OldVerdict(k);
                PkCommon.Judgement j;
                int prior = PriorKillsIn(fresh, k.Killer, k.Victim, k.At, repeatHours);
                var input = new KillIn { Killer = k.Killer, Victim = k.Victim, KillerName = NameOf(k.Killer), VictimName = NameOf(k.Victim), At = k.At };
                if (km || vm) { res.Mercs++; j = PkCommon.JudgeFacts(f, 1, prior, minLevel, ratio); }
                else if (k.Why != null && KEEP_WHY.Contains(k.Why))
                    j = new PkCommon.Judgement { Verdict = PkCommon.Verdict.NotCounted, Why = k.Why };
                else
                {
                    if (vl <= 0 || kl <= 0)
                    {
                        var m = k.Why == null ? null : BENEATH.Match(k.Why);
                        if (m != null && m.Success) { if (vl <= 0) vl = int.Parse(m.Groups[1].Value); if (kl <= 0) kl = int.Parse(m.Groups[2].Value); }
                        else if (k.Why != null && (m = NEWCOMER.Match(k.Why)).Success)
                        {
                            if (vl <= 0) vl = int.Parse(m.Groups[1].Value);
                            // the killer's level only matters for a victim over the newcomer line
                            if (kl <= 0 && vl >= minLevel) { kl = world?.LevelNow(k.Killer) ?? 0; if (kl > 0) res.LevelsFromToday++; }
                        }
                    }
                    if (vl > 0 && (kl > 0 || vl < minLevel))
                    {
                        f.KillerLevel = kl; f.VictimLevel = vl;
                        j = PkCommon.JudgeFacts(f, 1, prior, minLevel, ratio);
                        input.KillerLevel = kl; input.VictimLevel = vl;
                    }
                    else if (oldVerdict == PkCommon.Verdict.Honourable || PkCommon.IsRepeat(k.Why))
                    {
                        // levels never recorded: an honourable kill is honourable still (today's rules only add
                        // refusals for mercenaries), but the repeat rule is counted again
                        if (prior >= 3) j = new PkCommon.Judgement { Verdict = PkCommon.Verdict.NotCounted, Why = $"killed by you {prior} times already today" };
                        else j = new PkCommon.Judgement { Verdict = PkCommon.Verdict.Honourable, Weight = 1.0 / (1 + prior) };
                        input.FixedPoints = k.Points > 0 ? k.Points : 10;
                        input.OldBonus = oldBonus.TryGetValue(k, out var ob) ? ob : 0;
                    }
                    else j = new PkCommon.Judgement { Verdict = oldVerdict, Why = k.Why };   // nothing to judge it by
                }
                var o = Apply(fresh, input, j);
                if (o.Entry.Why != k.Why || o.Entry.Points != k.Points || o.Entry.Counts != k.Counts)
                {
                    res.Changed++;
                    res.Lines.Add($"{Stamp(k.At)} {input.KillerName} killed {input.VictimName}: {Describe(k.Why, k.Points)} -> {Describe(o.Entry.Why, o.Entry.Points)}");
                }
            }

            // Records with no kill left: keep what is not the kill log's (bounty and season titles, titles owed at login).
            foreach (var kv in old.Chars)
            {
                if (IsMerc(kv.Key)) continue;
                var r = kv.Value;
                fresh.Chars.TryGetValue(kv.Key, out var n);
                foreach (var t in r.Titles)
                {
                    if (n != null && n.Titles.Contains(t)) continue;
                    bool ladder = LADDER_TITLES.Contains(t);
                    bool mercOnly = ladder && Has(withMercs, kv.Key, t) && !Has(noMercs, kv.Key, t);
                    if (mercOnly) { res.Taken.Add((kv.Key, r.Name, t)); continue; }
                    if (n == null) fresh.Chars[kv.Key] = n = new Rec { Name = r.Name };
                    n.Titles.Add(t);
                }
                if (r.PendingTitles.Count > 0)
                {
                    if (n == null) fresh.Chars[kv.Key] = n = new Rec { Name = r.Name };
                    foreach (var t in r.PendingTitles) if (!n.PendingTitles.Contains(t)) n.PendingTitles.Add(t);
                }
                if (n != null)
                    foreach (var t in n.Titles.Where(t => !r.Titles.Contains(t)).ToList())
                    {
                        res.Granted.Add((kv.Key, r.Name, t));
                        if (!n.PendingTitles.Contains(t)) n.PendingTitles.Add(t);   // handed over at the next login (or now, if online)
                    }
            }
            // a character first seen in the kill log with no old record cannot happen (Apply always made both)
            fresh.Chars = fresh.Chars.Where(kv => !IsEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
            res.Fresh = fresh;
            return res;
        }

        static bool IsEmpty(Rec r) =>
            r.Renown == 0 && r.Kills == 0 && r.Deaths == 0 && r.Infamy == 0 && r.Best == 0 && r.Titles.Count == 0 && r.PendingTitles.Count == 0;

        static bool Has(State s, uint g, uint t) => s.Chars.TryGetValue(g, out var r) && r.Titles.Contains(t);

        static PkCommon.Verdict OldVerdict(KillEntry k)
        {
            if (k.Why == null) return k.Counts || k.Points > 0 ? PkCommon.Verdict.Honourable : PkCommon.Verdict.NotCounted;
            if (k.Why.StartsWith("a newcomer", StringComparison.Ordinal) || k.Why.StartsWith("someone far beneath", StringComparison.Ordinal))
                return PkCommon.Verdict.Infamous;
            return PkCommon.Verdict.NotCounted;
        }

        /// <summary>The old season as it was judged: stored verdicts, stored points.</summary>
        static State Replay(List<KillEntry> kills, Func<KillEntry, bool> use, Dictionary<KillEntry, double> bonuses)
        {
            var s = new State();
            foreach (var k in kills)
            {
                if (!use(k)) continue;
                var v = OldVerdict(k);
                var j = new PkCommon.Judgement { Verdict = v, Why = k.Why };
                var o = Apply(s, new KillIn { Killer = k.Killer, Victim = k.Victim, At = k.At, FixedPoints = k.Points, ExactPoints = true }, j);
                if (bonuses != null && v == PkCommon.Verdict.Honourable) bonuses[k] = o.Bonus;
            }
            return s;
        }

        static string Describe(string why, int points) => why == null ? $"+{points} renown" : why;

        static string Stamp(long at) => DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime.ToString("MM-dd HH:mm");

        internal static string TitleWord(uint t) => t switch
        {
            T_DUELIST => "Duelist", T_BLOODLETTER => "Bloodletter", T_GLADIATOR => "Dereth's Gladiator", T_KINGSLAYER => "Kingslayer",
            T_MURDERER => "Murderer", T_WARLORD => "Warlord of Dereth", T_CHAMPION => "Gladiator Champion", T_BOUNTY_HUNTER => "Bounty Hunter",
            _ => "title " + t,
        };
    }
}
