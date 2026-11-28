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
    /// <summary>Allegiances that welcome new players.</summary>
    public static partial class Recruiting
    {
        internal const string P_ON = "rg_recruit";
        const double SAMPLE_MIN = 10, OFFER_DELAY_S = 6, LOGIN_DELAY_S = 15;

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }

        public sealed class Request
        {
            public uint Guid { get; set; } public string Name { get; set; } public int Level { get; set; } public DateTime At { get; set; }
            // the finder's request (Recruiting.Finder.cs): an old file's requests load as Pending
            public AllegianceFinderRule.State State { get; set; } = AllegianceFinderRule.State.Pending;
            public int Rank { get; set; } = 1;
            public uint By { get; set; }
            public string ByName { get; set; }
            public DateTime? AnsweredAt { get; set; }
            /// <summary>Recruiters holding the Yes/No now (this login); not kept.</summary>
            [System.Text.Json.Serialization.JsonIgnore] public List<uint> Asked { get; } = new List<uint>();
        }
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
            // the finder's listing (AllegianceFinderRule.Check): an old file's listing reads as every level, PvE, ask
            public int LevelMin { get; set; } = AllegianceFinderRule.LEVEL_MIN;
            public int LevelMax { get; set; } = AllegianceFinderRule.LEVEL_MAX;
            public string Style { get; set; } = AllegianceFinderRule.PVE;
            public string Join { get; set; } = AllegianceFinderRule.ASK;
            public int AutoRank { get; set; }
            /// <summary>The lowest officer rank the monarch lets change the listing (1-3; 4 = the monarch alone).</summary>
            public int OfficerMin { get; set; } = RecruitRule.OFFICER_RANK;
            /// <summary>The allegiance's name when last listed or asked (for lines said when it is not loaded).</summary>
            public string Label { get; set; }
            public bool Listed { get; set; } = true;
        }

        static readonly object s_Lock = new object(), s_SaveLock = new object();
        static Dictionary<uint, Entry> s_Entries = new Dictionary<uint, Entry>();
        static DateTime s_NextSample = DateTime.MinValue;
        static string s_File;

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard Recruiting: /recruit, the list of player allegiances that welcome new players, and its offer at level 10 (false: /recruit says it is not open, nothing is offered)");
            var dir = ModFolder.Sub("Recruiting");
            s_File = dir == null ? null : Path.Combine(dir, "state.json");
            Load();
            RegisterFinder();
            CommandManager.TryAddCommand(Handle, "recruit", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Allegiances that welcome new players: the list, or ask one to take you. Monarchs and officers: on <line> | off | levels | style | joinby | officers | meet | contact | hours | status.",
                "[join <n> | ask 0x<monarch> | cancel | board | on <line> | off | pitch <text> | levels <lo-hi> | style pk|pve | joinby ask|auto <rank> | officers <1-4> | meet <place> | contact <name> | hours <text>|auto | status]");
            CommandManager.TryAddCommand(Handle, "recruiting", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The same as /recruit: allegiances that welcome new players.", "[join <n>]");
            Mod.Log.Info($"[RevivalGuard] Recruiting: {(On ? "ON" : "off")} ({P_ON}), {s_Entries.Values.Count(e => e.Listed)} listed; /recruit");
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

        /// <summary>NewcomerNotes: how many allegiances /recruit would show this player now</summary>
        internal static int OpenListingsFor(Player p)
        {
            if (!On || p == null || !Seeker(p)) return -1;
            return Listings().Count;
        }

        static bool Seeker(Player p) => RecruitRule.Seeker(p.PatronId != null, HasVassals(p), BotOrStaff(p));

        static RecruitRule.Toggle CanManage(Player p, out Allegiance a) => CanManage(p, out a, out _);

        /// <summary>The monarch, or an officer of the rank the monarch allows.</summary>
        static RecruitRule.Toggle CanManage(Player p, out Allegiance a, out Entry e)
        {
            a = p.Allegiance;
            e = null;
            bool has = a != null && a.Monarch != null && a.TotalMembers >= 2;
            if (has) lock (s_Lock) s_Entries.TryGetValue(a.Monarch.PlayerGuid.Full, out e);
            bool monarch = has && a.Monarch.PlayerGuid == p.Guid;
            bool bot = BotOrStaff(p) || (has && BotOrStaff(a.Monarch.Player));
            return AllegianceFinderRule.CanManage(has, monarch, p.AllegianceOfficerRank, p.Level ?? 1, bot, e?.OfficerMin ?? RecruitRule.OFFICER_RANK);
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
                if (!e.Listed) continue;
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
                if (verb == "board") { SendBoard(me); return; }   // our client's panel; says "on":0 while closed
                if (!On) { Say(me, RecruitRule.NOT_OPEN); return; }
                switch (verb)
                {
                    case "": case "list": ShowList(me, Listings(), RecruitRule.LIST_HEAD); return;
                    case "join": case "ask": Ask(me, rest); return;
                    case "cancel": Cancel(me); return;
                    case "where": case "swear": WhereVerb(me); return;   // swear: an older panel's Join now; no swear at a distance
                    case "on": case "off": case "pitch": case "meet": case "contact": case "hours": case "status":
                    case "levels": case "style": case "joinby": case "officers":
                        Manage(me, verb, rest); SendBoard(me); return;
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
                    if (e == null || !e.Listed) { Say(me, RecruitRule.NotListed(label)); return; }
                    var mine = Listings().FirstOrDefault(x => x.E == e);
                    if (mine != null) foreach (var line in RecruitRule.Entry(1, mine.L)) Say(me, line);
                    List<Request> reqs; lock (s_Lock) reqs = e.Requests.ToList();
                    Say(me, RecruitRule.Waiting(reqs.Select(r => $"{r.Name} (level {r.Level}, {RecruitRule.Ago(DateTime.UtcNow - r.At)})").ToList()));
                    return;
                }
                case "off":
                    if (e == null || !e.Listed) { Say(me, RecruitRule.NotListed(label)); return; }
                    lock (s_Lock) { e.Listed = false; e.Pitch = null; }   // requests stand: the directory still lists every allegiance
                    Save();
                    Mod.Log.Info($"[RevivalGuard] Recruiting: {me.Name} took {label} off the list");
                    Say(me, RecruitRule.Unlisted(label));
                    return;
                case "on":
                {
                    string pitch = rest.Length > 0 ? RecruitRule.Clean(rest, RecruitRule.PITCH_MAX) : e?.Pitch;
                    if (pitch == null) { Say(me, rest.Length > 0 ? RecruitRule.PitchTooLong() : RecruitRule.PITCH_NEEDED); return; }
                    if (NameGuard.IsLookalike(pitch)) { Say(me, AllegianceFinderRule.PITCH_REFUSED); return; }
                    bool fresh = e == null;
                    lock (s_Lock)
                    {
                        if (e == null) { e = new Entry { Monarch = key, Contact = me.Guid.Full, Since = DateTime.UtcNow }; s_Entries[key] = e; }
                        if (!e.Listed) { fresh = true; e.Listed = true; e.Contact = me.Guid.Full; e.Since = DateTime.UtcNow; }
                        e.Pitch = pitch; e.By = me.Name; e.Label = label;
                    }
                    Save();
                    Mod.Log.Info($"[RevivalGuard] Recruiting: {me.Name} listed {label} ({(fresh ? "new" : "line changed")})");
                    Telemetry.Emit("recruit_on", me, $",\"members\":{a.TotalMembers}");
                    Say(me, RecruitRule.Listed(label, e.Meet == null));
                    return;
                }
            }
            if (e == null || !e.Listed) { Say(me, RecruitRule.NotListedYet(label)); return; }
            switch (verb)
            {
                case "pitch":
                {
                    var t = RecruitRule.Clean(rest, RecruitRule.PITCH_MAX);
                    if (t == null) { Say(me, RecruitRule.PitchTooLong()); return; }
                    if (NameGuard.IsLookalike(t)) { Say(me, AllegianceFinderRule.PITCH_REFUSED); return; }
                    lock (s_Lock) e.Pitch = t;
                    Say(me, RecruitRule.PITCH_SET);
                    break;
                }
                case "levels":
                {
                    var lv = AllegianceFinderRule.ParseLevels(rest);
                    if (lv == null) { Say(me, AllegianceFinderRule.ProblemLine(AllegianceFinderRule.Problem.Levels)); return; }
                    lock (s_Lock) { e.LevelMin = lv.Value.lo; e.LevelMax = lv.Value.hi; }
                    Say(me, $"Levels welcomed: {lv.Value.lo} to {lv.Value.hi}.");
                    break;
                }
                case "style":
                {
                    var st = AllegianceFinderRule.ParseStyle(rest);
                    if (st == null) { Say(me, AllegianceFinderRule.ProblemLine(AllegianceFinderRule.Problem.Style)); return; }
                    lock (s_Lock) e.Style = st;
                    Say(me, st == AllegianceFinderRule.PK ? "Listed as a PK allegiance." : "Listed as a PvE allegiance.");
                    break;
                }
                case "joinby":
                {
                    var j = AllegianceFinderRule.ParseJoin(rest);
                    if (j == null) { Say(me, AllegianceFinderRule.ProblemLine(AllegianceFinderRule.Problem.Join)); return; }
                    lock (s_Lock) { e.Join = j.Value.mode; e.AutoRank = j.Value.rank; }
                    Say(me, j.Value.mode == AllegianceFinderRule.AUTO
                        ? $"Players below rank {j.Value.rank} who ask are accepted at once by whoever of you is on."
                        : "Each request asks your monarch and officers who are on.");
                    break;
                }
                case "officers":
                {
                    if (a.Monarch.PlayerGuid != me.Guid) { Say(me, "Only the monarch chooses which officers may change the listing."); return; }
                    if (!int.TryParse(rest.Trim(), out int min) || min < 1 || min > 4)
                    { Say(me, "Type /recruit officers and 1 (Speakers and up), 2 (Seneschals and up), 3 (Castellans) or 4 (only you)."); return; }
                    lock (s_Lock) e.OfficerMin = min;
                    Say(me, min == 4 ? "Only you can change the listing now." : $"Officers of rank {min} and up can change the listing.");
                    break;
                }
                case "meet":
                {
                    var t = RecruitRule.Clean(rest, RecruitRule.MEET_MAX);
                    if (t == null) { Say(me, RecruitRule.MeetBad()); return; }
                    if (NameGuard.IsLookalike(t)) { Say(me, AllegianceFinderRule.PITCH_REFUSED); return; }
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
            lock (s_Lock) { s_Entries.Remove(x.E.Monarch); foreach (var r in x.E.Requests) Close(r, AllegianceFinderRule.Event.ListingGone, x.E, DateTime.UtcNow); }
            Save();
            Mod.Log.Info($"[RevivalGuard] Recruiting: staff {me.Name} removed {x.L.Name}");
            Say(me, $"Removed {x.L.Name} from the list.");
        }

        // ------------------------------------------------------------------ the newcomer hint

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
                    chain.AddAction(p, () => { try { Hint(p); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting hint: {e.Message}"); } });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting level-up: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ at login: requests waiting, and the hint
        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class RecruiterBack
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    if (!On) return;
                    var p = __instance;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(LOGIN_DELAY_S);   // after the login lines and the allegiance load
                    chain.AddAction(p, () =>
                    {
                        try { FinderLogin(p); Hint(p); }
                        catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting login: {e.Message}"); }
                    });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Recruiting login: {e.Message}"); }
            }
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
                    Sweep();   // stale listings down, old requests expired
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
