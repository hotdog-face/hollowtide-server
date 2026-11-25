using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;
using S = RevivalGuard.AllegianceFinderRule.State;
using E = RevivalGuard.AllegianceFinderRule.Event;

namespace RevivalGuard
{
    /// <summary>
    /// FIND AN ALLEGIANCE (owner 2026-11-23: "the allegance panel in our ui might be a spot for players to find an
    /// allegiance to join. players who join an ali are more retailed according to our data"; growth review 2026-11-17:
    /// 22 of 29 stayers swore, 56 of 662 others). The listing of Recruiting.cs, grown into a board our client shows on
    /// the Allegiance panel's "Find an allegiance" view, with a request the recruiters answer Yes or No.
    ///
    ///   A listing adds: a level range, PK or PvE, play times (the old hours), and how to join: ask (a Yes/No to the
    ///   monarch and online officers), or auto-accept an asker whose own allegiance rank is under X. The monarch picks
    ///   which officers may change it (/recruit officers 1-4). Live facts: members, active this week, average level,
    ///   the allegiance's mansion or villa. A listing whose monarch has been away STALE_DAYS comes down by itself.
    ///
    ///   A request: one open per player; asks are spaced and capped (AllegianceFinderRule.CanAsk). The first recruiter
    ///   to answer decides; nobody on, it waits PENDING_HOURS and the next recruiter to log in is asked. On Yes the
    ///   asker is told who and where, and swears to THAT recruiter in person with retail's Swear (owner 2026-11-25: the
    ///   kneel plays, they see each other; ACE's walk-to, distance, vassal cap and locks all apply); since the recruiter
    ///   already said Yes to this asker, ACE's second Yes/No to the patron is skipped for that one swear. Not sworn within
    ///   ACCEPTED_HOURS, the request closes.
    ///
    ///   Client: "@recruit board" answers on the mod channel ("allegfind"); "@recruit ask 0x&lt;monarch&gt;", "@recruit
    ///   cancel"; the recruiter verbs as Recruiting's. Staff: "@allegiancelist [remove &lt;n|monarch&gt;]".
    ///   Newcomer hint: once per character (quest stamp), first week, level 10+, no patron, when anything is listed.
    /// All behind rg_recruit (default off) as the rest of Recruiting.
    /// </summary>
    public static partial class Recruiting
    {
        const int PUSH_READERS_S = 180, FACTS_CACHE_S = 60, HALL_CACHE_S = 600, MAX_VASSALS = 11;
        /// <summary>The accepted swear skips the patron's second Yes/No only this close (ACE's swear walk-to ends at 2 m).</summary>
        const float SIDE_BY_SIDE_M = 5f;

        static readonly Dictionary<uint, List<DateTime>> s_Asks = new Dictionary<uint, List<DateTime>>();
        static readonly Dictionary<(uint, uint), DateTime> s_Declined = new Dictionary<(uint, uint), DateTime>();
        static readonly Dictionary<uint, (S state, string label, DateTime at)> s_Outcome = new Dictionary<uint, (S, string, DateTime)>();
        static readonly Dictionary<uint, DateTime> s_Readers = new Dictionary<uint, DateTime>();
        sealed class Facts { public int Active, Avg; public DateTime At; }
        static readonly Dictionary<uint, Facts> s_Facts = new Dictionary<uint, Facts>();
        static readonly Dictionary<uint, (string text, DateTime at)> s_Hall = new Dictionary<uint, (string, DateTime)>();

        static void RegisterFinder()
        {
            CommandManager.TryAddCommand(StaffList, "allegiancelist", AccessLevel.Sentinel, CommandHandlerFlag.RequiresWorld,
                "Staff: the allegiance finder's listings, or take one down; dir lists every allegiance in the directory.", "[dir | remove <number | monarch name>]");
            CommandManager.TryAddCommand(ConsoleDir, "allegiancedir", AccessLevel.Admin, CommandHandlerFlag.None,
                "Staff and console: every allegiance in the directory, with its members.", "");
        }

        static DateTime? Unix(double? t) => t == null || t <= 0 ? (DateTime?)null : DateTimeOffset.FromUnixTimeMilliseconds((long)(t.Value * 1000)).UtcDateTime;

        static Request OpenRequestOf(uint asker, out Entry where)
        {
            lock (s_Lock)
                foreach (var e in s_Entries.Values)
                    foreach (var r in e.Requests)
                        if (r.Guid == asker && AllegianceFinderRule.Open(r.State)) { where = e; return r; }
            where = null;
            return null;
        }

        /// <summary>The allegiance rank of a character: its node's, or 1 with no allegiance (no vassals).</summary>
        static int RankOf(Player p) => (int)(p?.AllegianceNode?.Rank ?? 1);

        static bool HasRoom(Player p) => (p?.AllegianceNode?.TotalVassals ?? 0) < MAX_VASSALS;

        static string WhereOf(Player p) { try { return p?.Location == null ? null : Mansions.Where(p.Location); } catch { return null; } }

        // ------------------------------------------------------------------ live facts

        static Facts FactsOf(Allegiance a, uint key)
        {
            var now = DateTime.UtcNow;
            lock (s_Lock) if (s_Facts.TryGetValue(key, out var f) && (now - f.At).TotalSeconds < FACTS_CACHE_S) return f;
            int active = 0, n = 0; long sum = 0;
            foreach (var node in a.Members.Values.ToList())
            {
                var ip = node?.Player;
                if (ip == null) continue;
                bool online = PlayerManager.GetOnlinePlayer(ip.Guid) != null;
                if (AllegianceFinderRule.ActiveThisWeek(Unix(ip.GetProperty(PropertyFloat.LoginTimestamp)), online, now)) active++;
                sum += ip.Level ?? 1; n++;
            }
            var res = new Facts { Active = active, Avg = n == 0 ? 0 : (int)Math.Round((double)sum / n), At = now };
            lock (s_Lock) s_Facts[key] = res;
            return res;
        }

        /// <summary>"Mansion at 12.3N, 45.6E" when the monarch holds a mansion or villa (the allegiance hall), else null.</summary>
        static string HallOf(Allegiance a, uint key)
        {
            var now = DateTime.UtcNow;
            lock (s_Lock) if (s_Hall.TryGetValue(key, out var h) && (now - h.at).TotalSeconds < HALL_CACHE_S) return h.text;
            string text = null;
            try
            {
                if (a.Monarch?.Player?.HouseInstance != null)
                {
                    var house = a.GetHouse();
                    if (house != null && (house.HouseType == HouseType.Mansion || house.HouseType == HouseType.Villa))
                        text = (house.HouseType == HouseType.Mansion ? "Mansion" : "Villa") + " at " + Mansions.Where(house.Location);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting hall: {e.Message}"); }
            lock (s_Lock) s_Hall[key] = (text, now);
            return text;
        }

        // ------------------------------------------------------------------ upkeep

        /// <summary>Stale listings come down; requests age (pending and accepted past their hours expire).</summary>
        static void Sweep()
        {
            var now = DateTime.UtcNow;
            bool changed = false;
            List<Entry> entries;
            lock (s_Lock) entries = s_Entries.Values.ToList();
            foreach (var e in entries)
            {
                var m = PlayerManager.FindByGuid(e.Monarch);
                bool online = m != null && PlayerManager.GetOnlinePlayer(m.Guid) != null;
                if (e.Listed && m == null || e.Listed && AllegianceFinderRule.Stale(Unix(m.GetProperty(PropertyFloat.LoginTimestamp)), online, now))
                {
                    lock (s_Lock) { s_Entries.Remove(e.Monarch); foreach (var r in e.Requests) Close(r, E.ListingGone, e, now); }
                    Mod.Log.Info($"[RevivalGuard] Recruiting: {m?.Name ?? e.Monarch.ToString("X8")}'s listing came down (monarch away {AllegianceFinderRule.STALE_DAYS}+ days)");
                    changed = true;
                    continue;
                }
                lock (s_Lock)
                    foreach (var r in e.Requests.ToList())
                    {
                        var aged = AllegianceFinderRule.Aged(r.State, r.At, r.AnsweredAt, now);
                        if (aged != r.State) { Close(r, E.Timeout, e, now); changed = true; }
                    }
            }
            lock (s_Lock)
            {
                foreach (var e in s_Entries.Values) e.Requests.RemoveAll(r => !AllegianceFinderRule.Open(r.State));
                foreach (var k in s_Entries.Where(kv => !kv.Value.Listed && kv.Value.Requests.Count == 0).Select(kv => kv.Key).ToList())
                { s_Entries.Remove(k); changed = true; }
            }
            if (changed) Save();
        }

        /// <summary>Under s_Lock: a request leaves the open states; the asker's outcome is kept for their board.</summary>
        static void Close(Request r, E ev, Entry e, DateTime now)
        {
            var next = AllegianceFinderRule.Next(r.State, ev);
            if (next == r.State) return;
            bool wasAccepted = r.State == S.Accepted;
            r.State = next;
            string label = e?.Label ?? "that allegiance";
            s_Outcome[r.Guid] = (next, label, now);
            if (next == S.Declined) s_Declined[(r.Guid, e?.Monarch ?? 0)] = now;
            var asker = PlayerManager.GetOnlinePlayer(r.Guid);
            if (asker != null)
            {
                if (next == S.Declined) Say(asker, AllegianceFinderRule.DeclinedLine(label));
                else if (next == S.Expired) Say(asker, wasAccepted && r.ByName != null ? AllegianceFinderRule.AcceptedExpiredLine(r.ByName) : AllegianceFinderRule.ExpiredLine(label));
            }
        }

        // ------------------------------------------------------------------ asking

        static void Ask(Player me, string arg)
        {
            uint key = ParseGuid(arg);
            var list = Listings();
            var x = list.FirstOrDefault(l => l.E.Monarch == key);
            if (x == null && int.TryParse(arg.Trim(), out int n) && n >= 1 && n <= list.Count) x = list[n - 1];
            if (x == null)
            {
                // not listed: any allegiance of the directory (owner 2026-11-25, Request to join), its entry made to hold the request
                var d = key == 0 ? null : Directory().FirstOrDefault(v => v.Monarch == key);
                if (d != null)
                {
                    Entry ue;
                    lock (s_Lock)
                        if (!s_Entries.TryGetValue(key, out ue)) s_Entries[key] = ue = new Entry { Monarch = key, Listed = false, Since = DateTime.UtcNow, Label = d.Name };
                    var a = d.A ?? AllegianceManager.GetAllegiance(d.M);
                    if (a != null)
                        x = new Live
                        {
                            E = ue, A = a, Contact = d.M,
                            L = new RecruitRule.Listing { Name = d.Name, Monarch = d.M.Name, Members = d.Members, OnlineNow = d.Online },
                        };
                }
            }
            if (x == null) { Say(me, RecruitRule.JoinUsage(list.Count)); SendBoard(me); return; }
            var now = DateTime.UtcNow;
            AllegianceFinderRule.AskRefusal why;
            lock (s_Lock)
            {
                s_Asks.TryGetValue(me.Guid.Full, out var asks);
                asks?.RemoveAll(t => (now - t).TotalHours >= 24);
                s_Declined.TryGetValue((me.Guid.Full, x.E.Monarch), out var no);
                why = AllegianceFinderRule.CanAsk(me.PatronId != null, BotOrStaff(me), OpenRequestOf(me.Guid.Full, out _) != null,
                    asks?.Count > 0 ? asks.Max() : (DateTime?)null, asks?.Count ?? 0, no == default ? (DateTime?)null : no, now);
                if (x.A.Members.ContainsKey(me.Guid) || me.MonarchId == x.E.Monarch) why = AllegianceFinderRule.AskRefusal.Sworn;
            }
            if (why != AllegianceFinderRule.AskRefusal.None) { Say(me, AllegianceFinderRule.Refused(why, x.L.Name)); SendBoard(me); return; }

            var req = new Request { Guid = me.Guid.Full, Name = me.Name, Level = me.Level ?? 1, At = now, State = S.Pending, Rank = RankOf(me) };
            lock (s_Lock)
            {
                if (!s_Asks.TryGetValue(me.Guid.Full, out var asks)) s_Asks[me.Guid.Full] = asks = new List<DateTime>();
                asks.Add(now);
                x.E.Label = x.L.Name;
                x.E.Requests.RemoveAll(r => r.Guid == me.Guid.Full);
                x.E.Requests.Add(req);
                if (x.E.Requests.Count > RecruitRule.MAX_REQUESTS) x.E.Requests.RemoveAt(0);
                s_Outcome.Remove(me.Guid.Full);
            }
            int told = Offer(x.E, x.A, req, x.E.Contact);
            if (req.State == S.Pending) Say(me, told > 0 ? AllegianceFinderRule.SentNow(x.L.Name, told) : AllegianceFinderRule.SentLater(x.L.Name));
            Save();
            Telemetry.Emit("allegfind_ask", me, $",\"told\":{told},\"auto\":{(req.State == S.Accepted ? 1 : 0)}");
            Mod.Log.Info($"[RevivalGuard] Recruiting: {me.Name} (L{me.Level}, rank {req.Rank}) asked {x.L.Name}; {told} asked now, {req.State}");
            Push(x.E, me.Guid.Full);
        }

        /// <summary>Put a pending request to the recruiters who are on: auto-accept by the first with room when the listing
        /// takes this asker's rank, else a Yes/No to each (once per login). Returns how many were asked.</summary>
        static int Offer(Entry e, Allegiance a, Request r, uint contact)
        {
            if (r.State != S.Pending || a == null) return 0;
            var recruiters = PromptedFor(a, e, null, r.Level, r.Guid);
            if (recruiters.Count == 0) return 0;
            if (e.Listed && AllegianceFinderRule.AutoAccepts(e.Join, e.AutoRank, r.Rank))
            {
                Accept(e, r, recruiters[0], true);
                return 1;
            }
            int n = 0;
            foreach (var rec in recruiters)
            {
                lock (s_Lock) if (r.Asked.Contains(rec.Guid.Full)) { n++; continue; }
                var q = new FinderConfirmation(rec.Guid, r.Guid);
                if (!rec.ConfirmationManager.EnqueueSend(q, AllegianceFinderRule.Question(r.Name, r.Level, e.Label ?? "your allegiance"))) continue;
                lock (s_Lock) r.Asked.Add(rec.Guid.Full);
                n++;
            }
            return n;
        }

        static void Accept(Entry e, Request r, Player by, bool auto)
        {
            var now = DateTime.UtcNow;
            lock (s_Lock)
            {
                if (AllegianceFinderRule.Next(r.State, E.Yes) != S.Accepted) return;
                r.State = S.Accepted; r.By = by.Guid.Full; r.ByName = by.Name; r.AnsweredAt = now;
            }
            Save();
            var asker = PlayerManager.GetOnlinePlayer(r.Guid);
            if (asker != null) Say(asker, AllegianceFinderRule.AcceptedLine(by.Name, PlaceOf(by)));
            Say(by, auto ? AllegianceFinderRule.AutoToRecruiter(r.Name, e.Label ?? "your allegiance", PlaceOf(asker))
                         : AllegianceFinderRule.AcceptedToRecruiter(r.Name, PlaceOf(asker)));
            Telemetry.Emit("allegfind_yes", by, $",\"auto\":{(auto ? 1 : 0)}");
            Mod.Log.Info($"[RevivalGuard] Recruiting: {by.Name} {(auto ? "auto-" : "")}accepted {r.Name} into {e.Label}");
            Push(e, r.Guid);
        }

        /// <summary>A recruiter's answer (or its timeout), from the Yes/No.</summary>
        internal static void Answered(Player rec, uint askerGuid, bool yes, bool timeout)
        {
            if (rec == null) return;
            Request r; Entry e;
            r = OpenRequestOf(askerGuid, out e);
            if (timeout) { if (r != null) lock (s_Lock) r.Asked.Remove(rec.Guid.Full); return; }   // still waiting: asked again at a login
            if (r == null || r.State != S.Pending)
            {
                if (r != null && r.ByName != null) Say(rec, AllegianceFinderRule.AnsweredByOther(r.Name, r.ByName));
                return;
            }
            if (yes)
            {
                if (!HasRoom(rec)) { Say(rec, "You already have the most vassals you can take."); return; }
                Accept(e, r, rec, false);
                return;
            }
            lock (s_Lock) { r.ByName = rec.Name; Close(r, E.No, e, DateTime.UtcNow); e.Requests.Remove(r); }
            Save();
            Telemetry.Emit("allegfind_no", rec, "");
            Mod.Log.Info($"[RevivalGuard] Recruiting: {rec.Name} declined {r.Name} for {e.Label}");
            Push(e, askerGuid);
        }

        static void Cancel(Player me)
        {
            var r = OpenRequestOf(me.Guid.Full, out var e);
            if (r == null) { Say(me, "You have no request open."); SendBoard(me); return; }
            lock (s_Lock) { Close(r, E.Withdraw, e, DateTime.UtcNow); e.Requests.Remove(r); }
            Save();
            Say(me, AllegianceFinderRule.WITHDRAWN);
            Push(e, me.Guid.Full);
        }

        /// <summary>At login: a recruiter is asked about requests waiting for them; an asker hears where theirs stands.</summary>
        static void FinderLogin(Player p)
        {
            if (PlayerManager.GetOnlinePlayer(p.Guid) != p) return;
            Sweep();
            var mine = OpenRequestOf(p.Guid.Full, out var me);
            if (mine != null && mine.State == S.Accepted)
            {
                TellMeet(mine, p);
            }
            else
            {
                (S state, string label, DateTime at) o; bool has;
                lock (s_Lock) has = s_Outcome.TryGetValue(p.Guid.Full, out o);
                if (has && o.state == S.Expired) Say(p, AllegianceFinderRule.ExpiredLine(o.label));
            }
            MeetWaitingFor(p);
            var a = p.Allegiance;
            if (a?.Monarch == null || BotOrStaff(p)) return;
            Entry e; lock (s_Lock) s_Entries.TryGetValue(a.Monarch.PlayerGuid.Full, out e);
            if (e == null) return;
            List<Request> waiting;
            lock (s_Lock) { waiting = e.Requests.Where(r => r.State == S.Pending).ToList(); foreach (var r in waiting) r.Asked.Remove(p.Guid.Full); }
            foreach (var r in waiting)
            {
                var who = PlayerManager.FindByGuid(r.Guid);
                if (who == null || who.PatronId != null) { lock (s_Lock) { Close(r, E.Withdraw, e, DateTime.UtcNow); e.Requests.Remove(r); } continue; }
                if (!PromptedFor(a, e, null, r.Level, r.Guid).Contains(p)) continue;   // this member can't take them
                if (e.Listed && AllegianceFinderRule.AutoAccepts(e.Join, e.AutoRank, r.Rank)) { Accept(e, r, p, true); continue; }
                var q = new FinderConfirmation(p.Guid, r.Guid);
                if (p.ConfirmationManager.EnqueueSend(q, AllegianceFinderRule.Question(r.Name, r.Level, e.Label ?? "your allegiance")
                                                       .Replace(" Accept?", $" (asked {RecruitRule.Ago(DateTime.UtcNow - r.At)}) Accept?")))
                    lock (s_Lock) r.Asked.Add(p.Guid.Full);
                break;   // one question at a time: ACE holds one Yes/No per kind; the rest are asked at the next login or ask
            }
            Save();
        }

        // ------------------------------------------------------------------ the swear

        /// <summary>The asker swears in person (retail's Swear: ACE's walk-to, then this) to the recruiter who said Yes:
        /// that Yes stands for the patron's, so ACE's second Yes/No is skipped for this one swear, and only side by side
        /// (SIDE_BY_SIDE_M; ACE's walk-to ends within 2 m). The vassal cap, locks, ignore-requests: all still ACE's.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.SwearAllegiance))]
        static class SwearAccepted
        {
            static void Prefix(Player __instance, uint targetGuid, bool success, ref bool confirmed)
            {
                try
                {
                    if (confirmed || !success || !On) return;
                    var r = OpenRequestOf(__instance.Guid.Full, out _);
                    if (r == null || r.State != S.Accepted || r.By != targetGuid) return;
                    var patron = PlayerManager.GetOnlinePlayer(targetGuid);
                    if (patron != null && __instance.Location != null && patron.Location != null
                        && __instance.Location.DistanceTo(patron.Location) <= SIDE_BY_SIDE_M) confirmed = true;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting swear: {e.Message}"); }
            }

            static void Postfix(Player __instance, uint targetGuid)
            {
                try
                {
                    if (__instance?.PatronId == null) return;
                    var r = OpenRequestOf(__instance.Guid.Full, out var e);
                    if (r == null) return;
                    bool ours = r.State == S.Accepted && r.By == targetGuid && __instance.PatronId == targetGuid;
                    lock (s_Lock) { Close(r, ours ? E.Swore : E.Withdraw, e, DateTime.UtcNow); e.Requests.Remove(r); }
                    Save();
                    if (ours) Telemetry.Emit("allegfind_sworn", __instance, "");
                    DirtyDirectory();
                    Mod.Log.Info($"[RevivalGuard] Recruiting: {__instance.Name} swore to {(ours ? r.ByName : "someone else")}; request closed");
                    Push(e, __instance.Guid.Full);
                }
                catch (Exception ex) { Mod.Log.Warn($"[RevivalGuard] Recruiting swear: {ex.Message}"); }
            }
        }

        // ------------------------------------------------------------------ the newcomer hint

        static void Hint(Player p)
        {
            if (!On || p?.Session == null || PlayerManager.GetOnlinePlayer(p.Guid) != p) return;
            double ageDays = p.CreationTimestamp is int c && c > 0 ? (DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(c).UtcDateTime).TotalDays : -1;
            bool hinted = p.QuestManager.HasQuest(AllegianceFinderRule.HINT_FLAG);
            if (hinted || p.PatronId != null) return;
            int listings = Directory().Count(x => x.Monarch != (p.MonarchId ?? 0));
            if (!AllegianceFinderRule.HintDue(p.Level ?? 1, ageDays, false, false, BotOrStaff(p), listings)) return;
            p.QuestManager.Stamp(AllegianceFinderRule.HINT_FLAG);
            bool panel = ModChannel.Capable(p.Session);
            if (panel) p.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(p.Session, AllegianceFinderRule.HINT));
            Say(p, panel ? AllegianceFinderRule.HINT : AllegianceFinderRule.HINT_CHAT);
            Telemetry.Emit("allegfind_hint", p, $",\"n\":{listings}");
            Mod.Log.Info($"[RevivalGuard] Recruiting: allegiance hint to {p.Name} (L{p.Level}, {ageDays:0.0} days old)");
        }

        // ------------------------------------------------------------------ the board our client draws

        static void SendBoard(Player p)
        {
            if (p?.Session == null || !ModChannel.Capable(p.Session)) return;
            lock (s_Lock) s_Readers[p.Guid.Full] = DateTime.UtcNow;
            ModChannel.Send(p.Session, "allegfind", BoardJson(p));
        }

        /// <summary>The changed board to the asker, the listing's recruiters on now, and anyone who read it lately.</summary>
        static void Push(Entry e, uint asker)
        {
            var now = DateTime.UtcNow;
            var to = new HashSet<uint> { asker };
            List<uint> readers;
            lock (s_Lock) { foreach (var k in s_Readers.Where(kv => (now - kv.Value).TotalSeconds > PUSH_READERS_S).Select(kv => kv.Key).ToList()) s_Readers.Remove(k); readers = s_Readers.Keys.ToList(); }
            foreach (var g in readers) to.Add(g);
            var m = e == null ? null : PlayerManager.FindByGuid(e.Monarch);
            var a = m == null ? null : AllegianceManager.GetAllegiance(m);
            foreach (var on in a?.OnlinePlayers ?? new List<Player>()) if (on != null) to.Add(on.Guid.Full);
            foreach (var g in to) SendBoard(PlayerManager.GetOnlinePlayer(g));
        }

        static string BoardJson(Player p)
        {
            var now = DateTime.UtcNow;
            int lvl = p.Level ?? 1;
            // the directory: every player allegiance (owner 2026-11-25); a listing adds its line and settings
            var dir = On ? Directory() : new List<Dir>();
            var listed = On ? Listings().ToDictionary(x => x.E.Monarch) : new Dictionary<uint, Live>();
            var byKey = dir.ToDictionary(d => d.Monarch);
            var sorted = AllegianceBalanceRule.Sort(dir.Select(d => new AllegianceBalanceRule.Row
            {
                Monarch = d.Monarch, Name = d.Name, Members = d.Members, Active = d.Active, Online = d.Online,
                Fit = listed.TryGetValue(d.Monarch, out var x0) ? AllegianceFinderRule.Fit(lvl, x0.E.LevelMin, x0.E.LevelMax) : 1.0,
            }));
            uint mine = p.MonarchId ?? (p.AllegianceNode != null && p.AllegianceNode.IsMonarch && p.AllegianceNode.HasVassals ? p.Guid.Full : 0u);
            uint sug = AllegianceBalanceRule.Suggested(sorted, mine);
            var sb = new StringBuilder("{\"v\":2,\"on\":").Append(On ? 1 : 0)
                .Append(",\"seek\":").Append(p.PatronId == null && !BotOrStaff(p) ? 1 : 0).Append(",\"lvl\":").Append(lvl)
                .Append(",\"bonus\":").Append(SmallAllegianceBonus.On ? 1 : 0).Append(",\"total\":").Append(sorted.Count).Append(",\"rows\":[");
            int n = 0;
            foreach (var row in sorted.Take(DIR_MAX))
            {
                var d = byKey[row.Monarch];
                listed.TryGetValue(row.Monarch, out var x);
                if (n++ > 0) sb.Append(',');
                string hall = d.A != null ? HallOf(d.A, d.Monarch) : null;
                var (pu, _) = SmallAllegianceBonus.For(d.Members);
                sb.Append("{\"m\":").Append(d.Monarch).Append(",\"n\":").Append(Q(RecruitRule.Cap(d.Name))).Append(",\"mo\":").Append(Q(d.M.Name))
                  .Append(",\"lst\":").Append(x != null ? 1 : 0).Append(",\"mem\":").Append(d.Members).Append(",\"act\":").Append(d.Active)
                  .Append(",\"on\":").Append(d.Online).Append(",\"avg\":").Append(d.Avg).Append(",\"seen\":").Append((int)Math.Round(d.SeenHours))
                  .Append(",\"hall\":").Append(Q(hall)).Append(",\"b\":").Append(AllegianceBalanceRule.Percent(pu))
                  .Append(",\"sug\":").Append(d.Monarch == sug ? 1 : 0).Append(",\"in\":").Append(d.Monarch == mine ? 1 : 0);
                if (x != null)
                {
                    double fit = AllegianceFinderRule.Fit(lvl, x.E.LevelMin, x.E.LevelMax);
                    sb.Append(",\"p\":").Append(Q(x.E.Pitch)).Append(",\"lo\":").Append(x.E.LevelMin).Append(",\"hi\":").Append(x.E.LevelMax)
                      .Append(",\"st\":").Append(Q(x.E.Style)).Append(",\"j\":").Append(Q(x.E.Join)).Append(",\"r\":").Append(x.E.AutoRank)
                      .Append(",\"h\":").Append(Q(x.L.Hours)).Append(",\"meet\":").Append(Q(x.E.Meet)).Append(",\"fit\":").Append(fit >= 1 ? 2 : fit >= 0.5 ? 1 : 0);
                }
                sb.Append('}');
            }
            sb.Append(']');
            AppendMe(sb, p);

            var req = OpenRequestOf(p.Guid.Full, out var re);
            if (req != null)
            {
                var by = req.State == S.Accepted ? PlayerManager.GetOnlinePlayer(req.By) : null;
                bool spot = MapSpot(by, out float bns, out float bew);
                long left = req.State == S.Accepted ? Math.Max(0, (long)((req.AnsweredAt ?? req.At).AddHours(AllegianceFinderRule.ACCEPTED_HOURS) - now).TotalSeconds) : 0;
                sb.Append(",\"req\":{\"m\":").Append(re.Monarch).Append(",\"n\":").Append(Q(RecruitRule.Cap(re.Label ?? ""))).Append(",\"s\":")
                  .Append(Q(req.State.ToString().ToLowerInvariant())).Append(",\"by\":").Append(Q(req.ByName)).Append(",\"g\":").Append(req.By)
                  .Append(",\"w\":").Append(Q(by != null ? PlaceOf(by) : null)).Append(",\"byon\":").Append(by != null ? 1 : 0)
                  .Append(",\"left\":").Append(left);
                if (spot) sb.Append(",\"ns\":").Append(bns.ToString("0.0", CultureInfo.InvariantCulture)).Append(",\"ew\":").Append(bew.ToString("0.0", CultureInfo.InvariantCulture));
                sb
                  .Append(",\"ago\":").Append((long)(now - req.At).TotalSeconds).Append('}');
            }
            else
            {
                (S state, string label, DateTime at) o; bool has;
                lock (s_Lock) has = s_Outcome.TryGetValue(p.Guid.Full, out o);
                if (has && (now - o.at).TotalHours < 24)
                    sb.Append(",\"last\":{\"s\":").Append(Q(o.state.ToString().ToLowerInvariant())).Append(",\"n\":").Append(Q(RecruitRule.Cap(o.label))).Append('}');
            }

            // a recruiter's own listing, and the requests waiting on it
            var why = CanManage(p, out var a, out var e);
            sb.Append(",\"manage\":").Append(why == RecruitRule.Toggle.Ok ? 1 : 0);
            if (why == RecruitRule.Toggle.Ok)
            {
                sb.Append(",\"mine\":{\"listed\":").Append(e != null && e.Listed ? 1 : 0).Append(",\"monarch\":").Append(a.Monarch.PlayerGuid == p.Guid ? 1 : 0);
                if (e != null && e.Listed)
                {
                    List<Request> waiting; lock (s_Lock) waiting = e.Requests.Where(r => AllegianceFinderRule.Open(r.State)).ToList();
                    sb.Append(",\"p\":").Append(Q(e.Pitch)).Append(",\"lo\":").Append(e.LevelMin).Append(",\"hi\":").Append(e.LevelMax)
                      .Append(",\"st\":").Append(Q(e.Style)).Append(",\"j\":").Append(Q(e.Join)).Append(",\"r\":").Append(e.AutoRank)
                      .Append(",\"h\":").Append(Q(e.HoursText)).Append(",\"off\":").Append(e.OfficerMin).Append(",\"wait\":[");
                    int k = 0;
                    foreach (var r in waiting)
                        sb.Append(k++ > 0 ? "," : "").Append("{\"n\":").Append(Q(r.Name)).Append(",\"l\":").Append(r.Level)
                          .Append(",\"s\":").Append(Q(r.State.ToString().ToLowerInvariant())).Append(",\"by\":").Append(Q(r.ByName)).Append('}');
                    sb.Append(']');
                }
                sb.Append('}');
            }
            return sb.Append('}').ToString();
        }

        static uint ParseGuid(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(s.Substring(2), NumberStyles.HexNumber, null, out uint g)) return g;
            return 0;
        }

        static string Q(string v)
        {
            if (v == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in v)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ------------------------------------------------------------------ staff

        /// <summary>@allegiancelist dir and the console's allegiancedir: every allegiance in the directory with its members.</summary>
        static IEnumerable<string> DirLines()
        {
            DirtyDirectory();
            var dir = Directory();
            yield return SmallAllegianceBonus.Paid();
            yield return $"{dir.Count} allegiances in the directory:";
            foreach (var d in dir.OrderByDescending(x => x.Members))
                yield return $"{RecruitRule.Cap(d.Name)}: {d.Members} ({d.Online} on, {d.Active} this week, bonus +{AllegianceBalanceRule.Percent(SmallAllegianceBonus.For(d.Members).passup)}%): {string.Join(", ", d.Names.Take(20))}";
        }

        static void ConsoleDir(Session session, params string[] p)
        {
            foreach (var l in DirLines()) { if (session?.Player != null) Say(session.Player, l); else Mod.Log.Info("[RevivalGuard] allegiancedir: " + l); }
        }

        static void StaffList(Session session, params string[] p)
        {
            var me = session?.Player;
            if (me == null) return;
            if (p != null && p.Length >= 1 && p[0].Equals("dir", StringComparison.OrdinalIgnoreCase)) { foreach (var l in DirLines()) Say(me, l); return; }
            var list = Listings();
            if (p != null && p.Length >= 2 && p[0].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                string arg = string.Join(" ", p.Skip(1)).Trim();
                Live x = int.TryParse(arg, out int n) && n >= 1 && n <= list.Count ? list[n - 1]
                       : list.FirstOrDefault(l => l.L.Monarch.Equals(arg, StringComparison.OrdinalIgnoreCase));
                if (x == null) { Say(me, "Usage: @allegiancelist remove <number from @allegiancelist | monarch name>"); return; }
                lock (s_Lock) { s_Entries.Remove(x.E.Monarch); foreach (var r in x.E.Requests) Close(r, E.ListingGone, x.E, DateTime.UtcNow); }
                Save();
                Mod.Log.Info($"[RevivalGuard] Recruiting: staff {me.Name} removed {x.L.Name} (pitch \"{x.E.Pitch}\")");
                Say(me, $"Removed {x.L.Name} from the list.");
                return;
            }
            if (list.Count == 0) { Say(me, "No allegiance is listed."); return; }
            for (int i = 0; i < list.Count; i++)
                Say(me, $"{i + 1}. {RecruitRule.Cap(list[i].L.Name)} (monarch {list[i].L.Monarch}, by {list[i].E.By}): \"{list[i].E.Pitch}\"");
            Say(me, "@allegiancelist remove <number> takes one down.");
        }
    }

    /// <summary>A recruiter's Yes/No for an allegiance finder request (Recruiting.Offer).</summary>
    sealed class FinderConfirmation : Confirmation
    {
        readonly uint m_Asker;
        public FinderConfirmation(ObjectGuid recruiter, uint asker) : base(recruiter, ConfirmationType.Yes_No) { m_Asker = asker; }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            try { Recruiting.Answered(Player, m_Asker, response && !timeout, timeout); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting answer: {e.Message}"); }
        }
    }
}
