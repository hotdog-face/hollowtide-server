using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ALLEGIANCES THAT WELCOME NEW PLAYERS (owner 2026-11-17, docs/findings/growth-review-fable-2026-11-17.md lever 4:
    /// browser players who swore allegiance stayed at about 9x the rate; 2026-11-18 "Combine both" drafts). Ours, not
    /// retail. Player allegiances opt in; never a bot's (the Borg, the Hollow Company) or staff's. Swearing stays retail's:
    /// two players, the Swear button.
    ///
    ///   /recruit                       the allegiances on the list: size, who is on now, usual hours, their line, where
    ///                                  to meet, who to ask (/recruiting is the same command)
    ///   /recruit join &lt;n&gt;              a player with no patron or vassals asks number n: its contact, monarch and
    ///                                  officers who are on are told now; nobody on, they are told at their next login
    ///   /recruit on &lt;line&gt; | off        the monarch or a Seneschal+ officer, level 20+, of an allegiance with a vassal
    ///   /recruit pitch|meet|contact|hours &lt;text&gt;   the line, where to meet, the member asked first (level 20+), and
    ///                                  the hours in their own words ("hours auto": measured, RecruitRule.Hours)
    ///   /recruit status                your listing and the requests waiting
    ///   @recruit remove &lt;n&gt;            staff (Sentinel+): take a listing down
    ///
    /// The offer: a few seconds after a player with no patron reaches level 10 (the end of the starter path,
    /// starter-path.json until_level), the list, if it has anyone on it. All of it is behind rg_recruit (default OFF:
    /// inert until the owner's yes; admin panel Server Settings, Setting "rg_recruit", on; datafix
    /// 2026-11-18-recruit-on.sql keeps it on across restarts). State: Recruiting/state.json beside the dll. Rules and
    /// every player line: RecruitRule.cs (nettest RecruitCase).
    /// </summary>
    public static class Recruiting
    {
        internal const string P_ON = "rg_recruit";
        const double SAMPLE_MIN = 10, OFFER_DELAY_S = 6, LOGIN_DELAY_S = 15;

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }

        public sealed class Request { public uint Guid { get; set; } public string Name { get; set; } public int Level { get; set; } public DateTime At { get; set; } }
        public sealed class Entry
        {
            public uint Monarch { get; set; }
            public string Pitch { get; set; }
            public string Meet { get; set; }
            public string HoursText { get; set; }
            public uint Contact { get; set; }
            public string By { get; set; }
            public DateTime Since { get; set; }
            public int[] Samples { get; set; } = new int[24];
            public long[] Online { get; set; } = new long[24];
            public List<Request> Requests { get; set; } = new List<Request>();
        }

        static readonly object s_Lock = new object(), s_SaveLock = new object();
        static Dictionary<uint, Entry> s_Entries = new Dictionary<uint, Entry>();
        static readonly Dictionary<uint, DateTime> s_Joined = new Dictionary<uint, DateTime>();
        static readonly Dictionary<(uint, uint), DateTime> s_JoinedTo = new Dictionary<(uint, uint), DateTime>();
        static DateTime s_NextSample = DateTime.MinValue;
        static string s_File;

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard Recruiting: /recruit, the list of player allegiances that welcome new players, and its offer at level 10 (false: /recruit says it is not open, nothing is offered)");
            var dir = ModFolder.Sub("Recruiting");
            s_File = dir == null ? null : Path.Combine(dir, "state.json");
            Load();
            CommandManager.TryAddCommand(Handle, "recruit", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Allegiances that welcome new players: the list, or ask one to take you. Monarchs and officers: on <line> | off | meet | contact | hours | status.",
                "[join <n> | on <line> | off | pitch <text> | meet <place> | contact <name> | hours <text>|auto | status]");
            CommandManager.TryAddCommand(Handle, "recruiting", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The same as /recruit: allegiances that welcome new players.", "[join <n>]");
            Mod.Log.Info($"[RevivalGuard] Recruiting: {(On ? "ON" : "off")} ({P_ON}), {s_Entries.Count} listed; /recruit");
        }

        // ------------------------------------------------------------------ state
        static void Load()
        {
            try
            {
                if (s_File == null || !File.Exists(s_File)) return;
                var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(s_File)) ?? new List<Entry>();
                lock (s_Lock)
                {
                    s_Entries = new Dictionary<uint, Entry>();
                    foreach (var e in list)
                    {
                        if (e.Samples == null || e.Samples.Length != 24) e.Samples = new int[24];
                        if (e.Online == null || e.Online.Length != 24) e.Online = new long[24];
                        e.Requests ??= new List<Request>();
                        s_Entries[e.Monarch] = e;
                    }
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting: state not read ({e.Message}); starting empty"); }
        }

        static void Save()
        {
            if (s_File == null) return;
            string json;
            lock (s_Lock) json = JsonSerializer.Serialize(s_Entries.Values.ToList());
            var file = s_File;
            Task.Run(() =>
            {
                try { lock (s_SaveLock) { File.WriteAllText(file + ".tmp", json); File.Move(file + ".tmp", file, true); } }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting: state not saved ({e.Message})"); }
            });
        }

        // ------------------------------------------------------------------ who is who

        /// <summary>A bot (a Borg or a Hollow Company merc: both on hcmerc accounts, BorgRule) or a staff character: never lists, is never
        /// listed, told or offered.</summary>
        internal static bool BotOrStaff(IPlayer p)
        {
            if (p == null) return true;
            var acct = p.Account;
            string name = acct?.AccountName;
            if (Mercenaries.IsReservedName(name) || Mercenaries.IsMercAccount(name)) return true;
            if ((acct?.AccessLevel ?? 0) > (uint)AccessLevel.Player) return true;
            return p is Player online && online.Session != null && online.Session.AccessLevel > AccessLevel.Player;
        }

        static bool HasVassals(Player p) => p.AllegianceNode != null && p.AllegianceNode.HasVassals;

        /// <summary>NewcomerNotes: how many allegiances /recruit would show this player now, or -1 when /recruit is off
        /// or the player is not free to join one.</summary>
        internal static int OpenListingsFor(Player p)
        {
            if (!On || p == null || !Seeker(p)) return -1;
            return Listings().Count;
        }

        static bool Seeker(Player p) => RecruitRule.Seeker(p.PatronId != null, HasVassals(p), BotOrStaff(p));

        static RecruitRule.Toggle CanManage(Player p, out Allegiance a)
        {
            a = p.Allegiance;
            bool has = a != null && a.Monarch != null && a.TotalMembers >= 2;
            bool monarch = has && a.Monarch.PlayerGuid == p.Guid;
            bool bot = BotOrStaff(p) || (has && BotOrStaff(a.Monarch.Player));
            return RecruitRule.CanToggle(has, monarch, p.AllegianceOfficerRank, p.Level ?? 1, bot);
        }

        /// <summary>The members of an allegiance who are told of a request now (online, level 20+, players).</summary>
        static List<uint> ContactsOf(Allegiance a, uint contact)
        {
            var members = new List<RecruitRule.Member>();
            foreach (var on in a?.OnlinePlayers ?? new List<Player>())
            {
                if (on == null) continue;
                members.Add(new RecruitRule.Member
                {
                    Guid = on.Guid.Full, Online = true, IsMonarch = a.Monarch?.PlayerGuid == on.Guid, BotOrStaff = BotOrStaff(on),
                    OfficerRank = on.AllegianceOfficerRank ?? 0, Level = on.Level ?? 1,
                });
            }
            return RecruitRule.Contacts(members, contact);
        }

        sealed class Live { public Entry E; public Allegiance A; public RecruitRule.Listing L; public IPlayer Contact; }

        /// <summary>The listings that still stand, numbered as shown: who is on now first, then the biggest.</summary>
        static List<Live> Listings()
        {
            var res = new List<Live>();
            List<Entry> entries;
            lock (s_Lock) entries = s_Entries.Values.ToList();
            foreach (var e in entries)
            {
                var m = PlayerManager.FindByGuid(e.Monarch);
                if (m == null || m.IsDeleted || m.IsPendingDeletion || BotOrStaff(m)) continue;
                var a = AllegianceManager.GetAllegiance(m);
                if (a == null || a.Monarch == null || a.Monarch.PlayerGuid.Full != e.Monarch || a.TotalMembers < 2) continue;
                var c = e.Contact != 0 && a.Members.ContainsKey(new ACE.Entity.ObjectGuid(e.Contact)) ? PlayerManager.FindByGuid(e.Contact) : null;
                if (c == null || BotOrStaff(c)) c = m;
                string hours;
                lock (s_Lock) hours = e.HoursText ?? RecruitRule.Hours(e.Samples, e.Online);
                res.Add(new Live
                {
                    E = e, A = a, Contact = c,
                    L = new RecruitRule.Listing
                    {
                        Name = Mansions.AllegianceLabel(a, m), Monarch = m.Name, Members = a.TotalMembers,
                        OnlineNow = a.OnlinePlayers?.Count ?? 0, Hours = hours, Pitch = e.Pitch, Meet = e.Meet,
                        Contact = c.Name, ContactOnline = PlayerManager.GetOnlinePlayer(c.Guid) != null,
                    },
                });
            }
            return res.OrderByDescending(x => x.L.OnlineNow > 0).ThenByDescending(x => x.L.Members).ThenBy(x => x.L.Name).ToList();
        }

        static void Say(Player p, string text) => p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void ShowList(Player p, List<Live> list, string head)
        {
            if (list.Count == 0) { Say(p, RecruitRule.LIST_EMPTY); return; }
            Say(p, head);
            for (int i = 0; i < list.Count; i++) foreach (var line in RecruitRule.Entry(i + 1, list[i].L)) Say(p, line);
            Say(p, RecruitRule.LIST_FOOT);
        }

        // ------------------------------------------------------------------ the command
        static void Handle(Session session, params string[] p)
        {
            var me = session?.Player;
            if (me == null) return;
            string verb = p != null && p.Length > 0 ? p[0].ToLowerInvariant() : "";
            string rest = p != null && p.Length > 1 ? string.Join(" ", p.Skip(1)) : "";
            try
            {
                if (verb == "remove" && session.AccessLevel >= AccessLevel.Sentinel) { Remove(me, rest); return; }
                if (!On) { Say(me, RecruitRule.NOT_OPEN); return; }
                switch (verb)
                {
                    case "": case "list": ShowList(me, Listings(), RecruitRule.LIST_HEAD); return;
                    case "join": case "ask": Join(me, rest); return;
                    case "on": case "off": case "pitch": case "meet": case "contact": case "hours": case "status":
                        Manage(me, verb, rest); return;
                }
                Say(me, RecruitRule.USAGE);
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] Recruiting: " + e); Say(me, "That did not work; try again later."); }
        }

        static void Manage(Player me, string verb, string rest)
        {
            var why = CanManage(me, out var a);
            if (why != RecruitRule.Toggle.Ok) { Say(me, RecruitRule.Refused(why)); return; }
            uint key = a.Monarch.PlayerGuid.Full;
            Entry e;
            lock (s_Lock) s_Entries.TryGetValue(key, out e);
            string label = Mansions.AllegianceLabel(a, a.Monarch.Player);
            switch (verb)
            {
                case "status":
                {
                    if (e == null) { Say(me, RecruitRule.NotListed(label)); return; }
                    var mine = Listings().FirstOrDefault(x => x.E == e);
                    if (mine != null) foreach (var line in RecruitRule.Entry(1, mine.L)) Say(me, line);
                    List<Request> reqs; lock (s_Lock) reqs = e.Requests.ToList();
                    Say(me, RecruitRule.Waiting(reqs.Select(r => $"{r.Name} (level {r.Level}, {RecruitRule.Ago(DateTime.UtcNow - r.At)})").ToList()));
                    return;
                }
                case "off":
                    if (e == null) { Say(me, RecruitRule.NotListed(label)); return; }
                    lock (s_Lock) s_Entries.Remove(key);
                    Save();
                    Mod.Log.Info($"[RevivalGuard] Recruiting: {me.Name} took {label} off the list");
                    Say(me, RecruitRule.Unlisted(label));
                    return;
                case "on":
                {
                    string pitch = rest.Length > 0 ? RecruitRule.Clean(rest, RecruitRule.PITCH_MAX) : e?.Pitch;
                    if (pitch == null) { Say(me, rest.Length > 0 ? RecruitRule.PitchTooLong() : RecruitRule.PITCH_NEEDED); return; }
                    bool fresh = e == null;
                    lock (s_Lock)
                    {
                        if (e == null) { e = new Entry { Monarch = key, Contact = me.Guid.Full, Since = DateTime.UtcNow }; s_Entries[key] = e; }
                        e.Pitch = pitch; e.By = me.Name;
                    }
                    Save();
                    Mod.Log.Info($"[RevivalGuard] Recruiting: {me.Name} listed {label} ({(fresh ? "new" : "line changed")})");
                    Telemetry.Emit("recruit_on", me, $",\"members\":{a.TotalMembers}");
                    Say(me, RecruitRule.Listed(label, e.Meet == null));
                    return;
                }
            }
            if (e == null) { Say(me, RecruitRule.NotListedYet(label)); return; }
            switch (verb)
            {
                case "pitch":
                {
                    var t = RecruitRule.Clean(rest, RecruitRule.PITCH_MAX);
                    if (t == null) { Say(me, RecruitRule.PitchTooLong()); return; }
                    lock (s_Lock) e.Pitch = t;
                    Say(me, RecruitRule.PITCH_SET);
                    break;
                }
                case "meet":
                {
                    var t = RecruitRule.Clean(rest, RecruitRule.MEET_MAX);
                    if (t == null) { Say(me, RecruitRule.MeetBad()); return; }
                    lock (s_Lock) e.Meet = t;
                    Say(me, RecruitRule.MeetSet(t));
                    break;
                }
                case "contact":
                {
                    var who = rest.Trim().Length == 0 ? null : PlayerManager.FindByName(rest.Trim());
                    if (who == null || !a.Members.ContainsKey(who.Guid) || BotOrStaff(who) || (who.Level ?? 1) < RecruitRule.RECRUITER_LEVEL)
                    { Say(me, RecruitRule.CONTACT_BAD); return; }
                    lock (s_Lock) e.Contact = who.Guid.Full;
                    Say(me, RecruitRule.ContactSet(who.Name));
                    break;
                }
                case "hours":
                {
                    if (rest.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { lock (s_Lock) e.HoursText = null; Say(me, RecruitRule.HOURS_AUTO); break; }
                    var t = RecruitRule.Clean(rest, RecruitRule.HOURS_MAX);
                    if (t == null) { Say(me, RecruitRule.HoursBad()); return; }
                    lock (s_Lock) e.HoursText = t;
                    Say(me, RecruitRule.HoursSet(t));
                    break;
                }
            }
            Save();
        }

        static void Remove(Player me, string rest)
        {
            var list = Listings();
            if (!int.TryParse(rest.Trim(), out int n) || n < 1 || n > list.Count) { Say(me, "Usage: @recruit remove <number from /recruit>"); return; }
            var x = list[n - 1];
            lock (s_Lock) s_Entries.Remove(x.E.Monarch);
            Save();
            Mod.Log.Info($"[RevivalGuard] Recruiting: staff {me.Name} removed {x.L.Name}");
            Say(me, $"Removed {x.L.Name} from the list.");
        }

        static void Join(Player me, string rest)
        {
            var list = Listings();
            if (!int.TryParse(rest.Trim(), out int n) || n < 1 || n > list.Count) { Say(me, RecruitRule.JoinUsage(list.Count)); return; }
            if (BotOrStaff(me)) { Say(me, RecruitRule.Refused(RecruitRule.Toggle.NotPlayer)); return; }
            if (!Seeker(me)) { Say(me, RecruitRule.ALREADY_SWORN); return; }
            var x = list[n - 1];
            var now = DateTime.UtcNow;
            lock (s_Lock)
            {
                if (s_Joined.TryGetValue(me.Guid.Full, out var last) && (now - last).TotalSeconds < RecruitRule.JOIN_GAP_S) { Say(me, RecruitRule.WAIT_A_MINUTE); return; }
                if (s_JoinedTo.TryGetValue((me.Guid.Full, x.E.Monarch), out var to) && (now - to).TotalMinutes < RecruitRule.JOIN_SAME_MIN) { Say(me, RecruitRule.AlreadyAsked(x.L.Name)); return; }
                s_Joined[me.Guid.Full] = now; s_JoinedTo[(me.Guid.Full, x.E.Monarch)] = now;
            }
            var told = ContactsOf(x.A, x.Contact.Guid.Full).Where(g => g != me.Guid.Full).ToList();
            string line = RecruitRule.ToRecruiter(me.Name, me.Level ?? 1, x.L.Name, null);
            foreach (var g in told) Say(PlayerManager.GetOnlinePlayer(g), line);
            if (told.Count == 0)
            {
                lock (s_Lock)
                {
                    x.E.Requests.RemoveAll(r => r.Guid == me.Guid.Full || (now - r.At).TotalDays > RecruitRule.REQUEST_DAYS);
                    x.E.Requests.Add(new Request { Guid = me.Guid.Full, Name = me.Name, Level = me.Level ?? 1, At = now });
                    if (x.E.Requests.Count > RecruitRule.MAX_REQUESTS) x.E.Requests.RemoveRange(0, x.E.Requests.Count - RecruitRule.MAX_REQUESTS);
                }
                Save();
            }
            Say(me, RecruitRule.Asked(x.L.Name, told.Count, x.L.Contact, x.L.Meet, x.L.Hours));
            Telemetry.Emit("recruit_join", me, $",\"told\":{told.Count}");
            Mod.Log.Info($"[RevivalGuard] Recruiting: {me.Name} (level {me.Level}) asked to join {x.L.Name}; {told.Count} told now");
        }

        // ------------------------------------------------------------------ the offer at the end of the starter path

        /// <summary>World thread, a few seconds after the level-up that crosses the offer level.</summary>
        internal static void Offer(Player p)
        {
            if (!On || p?.Session == null || PlayerManager.GetOnlinePlayer(p.Guid) != p || !Seeker(p)) return;
            var list = Listings();
            if (list.Count == 0) return;   // nothing to offer: say nothing
            ShowList(p, list, RecruitRule.OFFER_HEAD);
            Telemetry.Emit("recruit_offer", p, $",\"n\":{list.Count}");
            Mod.Log.Info($"[RevivalGuard] Recruiting: offered {list.Count} allegiances to {p.Name} at level {p.Level}");
        }

        [HarmonyPatch(typeof(Player), "CheckForLevelup")]
        static class OfferOnLevel
        {
            static void Prefix(Player __instance, out int __state) => __state = __instance.Level ?? 1;
            static void Postfix(Player __instance, int __state)
            {
                try
                {
                    if (!On || !RecruitRule.OfferNow(__state, __instance.Level ?? 1, RecruitRule.OFFER_LEVEL)) return;
                    var p = __instance;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(OFFER_DELAY_S);   // after the level-up lines, not buried in them
                    chain.AddAction(p, () => { try { Offer(p); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting offer: {e.Message}"); } });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting level-up: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ requests waiting for a recruiter who was away
        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class RecruiterBack
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    if (!On) return;
                    var p = __instance;
                    bool any; lock (s_Lock) any = s_Entries.Values.Any(e => e.Requests.Count > 0);
                    if (!any) return;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(LOGIN_DELAY_S);   // after the login lines and the allegiance load
                    chain.AddAction(p, () => { try { TellWaiting(p); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting login: {e.Message}"); } });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting login: {e.Message}"); }
            }
        }

        static void TellWaiting(Player p)
        {
            if (PlayerManager.GetOnlinePlayer(p.Guid) != p || BotOrStaff(p) || p.Allegiance?.Monarch == null) return;
            uint key = p.Allegiance.Monarch.PlayerGuid.Full;
            Entry e; lock (s_Lock) s_Entries.TryGetValue(key, out e);
            if (e == null || !ContactsOf(p.Allegiance, e.Contact).Contains(p.Guid.Full)) return;
            List<Request> reqs;
            lock (s_Lock) { reqs = e.Requests.ToList(); e.Requests.Clear(); }
            if (reqs.Count == 0) return;
            var now = DateTime.UtcNow;
            string label = Mansions.AllegianceLabel(p.Allegiance, p.Allegiance.Monarch.Player);
            foreach (var r in reqs.Where(r => (now - r.At).TotalDays <= RecruitRule.REQUEST_DAYS))
            {
                var who = PlayerManager.FindByGuid(r.Guid);
                if (who == null || who.PatronId != null) continue;   // gone, or sworn since
                Say(p, RecruitRule.ToRecruiter(r.Name, r.Level, label, RecruitRule.Ago(now - r.At)));
            }
            Save();
        }

        // ------------------------------------------------------------------ "usually on": who is on, each 10 minutes
        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        static class Sample
        {
            static void Postfix()
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (now < s_NextSample) return;
                    s_NextSample = now.AddMinutes(SAMPLE_MIN);
                    if (!On) return;
                    lock (s_Lock) if (s_Entries.Count == 0) return;
                    var counts = new Dictionary<uint, int>();
                    foreach (var p in PlayerManager.GetAllOnline())
                    {
                        uint key;
                        lock (s_Lock) key = s_Entries.ContainsKey(p.Guid.Full) ? p.Guid.Full : (p.MonarchId ?? 0);
                        if (key == 0) continue;
                        counts.TryGetValue(key, out int c); counts[key] = c + 1;
                    }
                    int hour = PkNight.PacificLocal(now).Hour;
                    lock (s_Lock)
                        foreach (var e in s_Entries.Values)
                        {
                            e.Samples[hour]++;
                            if (counts.TryGetValue(e.Monarch, out int c)) e.Online[hour] += c;
                        }
                    Save();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting sample: {e.Message}"); }
            }
        }
    }
}
