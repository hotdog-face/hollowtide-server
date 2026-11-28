using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using S = RevivalGuard.AllegianceFinderRule.State;

namespace RevivalGuard
{
    /// <summary>The allegiance directory.</summary>
    public static partial class Recruiting
    {
        const int DIR_CACHE_S = 60, DIR_MAX = 120;

        internal sealed class Dir
        {
            public uint Monarch; public IPlayer M; public Allegiance A; public string Name;
            public int Members, Active, Online, Avg; public double SeenHours; public List<string> Names;
            /// <summary>Distinct accounts, and those with a member on this week.</summary>
            public int Accounts, ActiveAccounts;
        }

        static List<Dir> s_Dir = new List<Dir>();
        static DateTime s_DirAt = DateTime.MinValue;

        /// <summary>The allegiance object if ACE has it loaded (no database read), else null.</summary>
        static Allegiance Loaded(uint monarch)
        {
            try { return AllegianceManager.Players.TryGetValue(new ObjectGuid(monarch), out var n) ? n?.Allegiance : null; }
            catch { return null; }
        }

        /// <summary>Every player allegiance now (cached a minute), from the player list: no database reads.</summary>
        internal static List<Dir> Directory()
        {
            var now = DateTime.UtcNow;
            lock (s_Lock) if ((now - s_DirAt).TotalSeconds < DIR_CACHE_S) return s_Dir;
            var groups = new Dictionary<uint, List<IPlayer>>();
            foreach (var p in PlayerManager.GetAllPlayers())
            {
                // no IsDeleted here: on an offline player it is a database read each; a deleted character leaves PlayerManager and its allegiance
                if (p == null || p.MonarchId == null) continue;
                uint m = p.MonarchId.Value;
                if (!groups.TryGetValue(m, out var l)) groups[m] = l = new List<IPlayer>();
                if (p.Guid.Full != m) l.Add(p);
            }
            var res = new List<Dir>();
            foreach (var kv in groups)
            {
                var m = PlayerManager.FindByGuid(kv.Key);
                if (m == null || kv.Value.Count == 0 || BotOrStaff(m)) continue;
                bool gone; try { gone = m.IsDeleted || m.IsPendingDeletion; } catch { gone = false; }   // one read per allegiance
                if (gone) continue;
                var all = new List<IPlayer>(kv.Value) { m };
                int active = 0, online = 0; long sum = 0; DateTime? last = null;
                foreach (var ip in all)
                {
                    bool on = PlayerManager.GetOnlinePlayer(ip.Guid) != null;
                    var login = Unix(ip.GetProperty(PropertyFloat.LoginTimestamp));
                    if (on) online++;
                    if (AllegianceFinderRule.ActiveThisWeek(login, on, now)) active++;
                    if (on) last = now; else if (login != null && (last == null || login > last)) last = login;
                    sum += ip.Level ?? 1;
                }
                var a = Loaded(kv.Key);
                var (accounts, activeAccounts) = SmallAllegianceBonus.Count(all, now);
                res.Add(new Dir
                {
                    Accounts = accounts, ActiveAccounts = activeAccounts,
                    Monarch = kv.Key, M = m, A = a, Name = Mansions.AllegianceLabel(a, m), Members = all.Count, Active = active,
                    Online = online, Avg = (int)Math.Round((double)sum / all.Count), SeenHours = last == null ? -1 : (now - last.Value).TotalHours,
                    Names = all.Select(x => x.Name).ToList(),
                });
            }
            lock (s_Lock) { s_Dir = res; s_DirAt = now; }
            return res;
        }

        static void DirtyDirectory() { lock (s_Lock) s_DirAt = DateTime.MinValue; }

        /// <summary>The members ACE would let take this asker, asked in AllegianceBalanceRule's order (the listing's contact first).</summary>
        static List<Player> PromptedFor(Allegiance a, Entry e, Player asker, int askerLevel, uint askerGuid)
        {
            var members = new List<AllegianceBalanceRule.Member>();
            foreach (var on in a?.OnlinePlayers ?? new List<Player>())
            {
                if (on == null) continue;
                members.Add(new AllegianceBalanceRule.Member
                {
                    Guid = on.Guid.Full, Online = true, BotOrStaff = BotOrStaff(on),
                    IgnoresRequests = on.GetCharacterOption(CharacterOption.IgnoreAllegianceRequests),
                    IsMonarch = a.Monarch?.PlayerGuid == on.Guid, IsContact = e != null && e.Contact == on.Guid.Full,
                    Level = on.Level ?? 1, Vassals = on.AllegianceNode?.TotalVassals ?? 0, OfficerRank = on.AllegianceOfficerRank ?? 0,
                });
            }
            bool locked = false;
            try { locked = a?.IsLocked ?? false; } catch { }
            return AllegianceBalanceRule.Prompted(members, askerGuid, askerLevel, locked)
                .Select(g => PlayerManager.GetOnlinePlayer(g)).Where(p => p != null).ToList();
        }

        // meeting to swear

        /// <summary>The place words for a player now ("at 42.1N, 33.6E near Holtburg", "in &lt;dungeon&gt;"), or null.</summary>
        static string PlaceOf(Player p)
        {
            if (p == null) return null;
            return DeathLog.WhereOf(p) ?? (WhereOf(p) is string w ? "at " + w : null);
        }

        /// <summary>The patron's map spot for the panel's pin: outdoors or in a surface building, not a dungeon.</summary>
        static bool MapSpot(Player p, out float ns, out float ew)
        {
            ns = ew = 0;
            try
            {
                var loc = p?.Location;
                if (loc == null || loc.Cell == 0 || (p.CurrentLandblock?.IsDungeon ?? false)) return false;
                float gx = ((loc.Cell >> 24) & 0xFFu) * 192f + loc.PositionX, gy = ((loc.Cell >> 16) & 0xFFu) * 192f + loc.PositionY;
                DeathLogRule.Coords(gx, gy, out ns, out ew);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Tell both sides of an accepted request who and where (the accept, a login, "@recruit where").</summary>
        static void TellMeet(Request r, Player only = null)
        {
            if (r == null || r.State != S.Accepted) return;
            var asker = PlayerManager.GetOnlinePlayer(r.Guid);
            var patron = PlayerManager.GetOnlinePlayer(r.By);
            if (asker != null && (only == null || only == asker)) Say(asker, AllegianceFinderRule.AcceptedLine(r.ByName ?? patron?.Name ?? "Your patron", PlaceOf(patron)));
            if (patron != null && (only == null || only == patron)) Say(patron, AllegianceFinderRule.MeetReminderToPatron(r.Name, PlaceOf(asker)));
        }

        /// <summary>"@recruit where" (and the old "@recruit swear"): where to meet, for either side of an accepted request.</summary>
        static void WhereVerb(Player me)
        {
            var r = OpenRequestOf(me.Guid.Full, out _);
            if (r != null && r.State == S.Accepted) { TellMeet(r, me); SendBoard(me); return; }
            List<Request> mine;
            lock (s_Lock) mine = s_Entries.Values.SelectMany(e => e.Requests).Where(x => x.State == S.Accepted && x.By == me.Guid.Full).ToList();
            if (mine.Count == 0) { Say(me, "You have no accepted request."); SendBoard(me); return; }
            foreach (var x in mine) TellMeet(x, me);
        }

        /// <summary>At a recruiter's login: the requests they accepted, still waiting for the in-person swear.</summary>
        static void MeetWaitingFor(Player recruiter)
        {
            List<Request> mine;
            lock (s_Lock) mine = s_Entries.Values.SelectMany(e => e.Requests).Where(r => r.State == S.Accepted && r.By == recruiter.Guid.Full).ToList();
            foreach (var r in mine) TellMeet(r);
        }

        // ------------------------------------------------------------------ the reader's own allegiance, for the panel

        /// <summary>"me": the reader's allegiance as our panel's My Allegiance and Actions views need it</summary>
        static void AppendMe(StringBuilder sb, Player p)
        {
            var a = p.Allegiance;
            if (a == null || p.AllegianceNode == null) { sb.Append(",\"me\":null"); return; }
            int perm = 0;
            try { perm = (int)p.AllegiancePermissionLevel; } catch { }
            bool locked = false;
            try { locked = a.IsLocked; } catch { }
            var (pu, lum) = SmallAllegianceBonus.For(a);   // by accounts; none for a bot's or staff's
            sb.Append(",\"me\":{\"perm\":").Append(perm).Append(",\"name\":").Append(Q(a.AllegianceName))
              .Append(",\"label\":").Append(Q(RecruitRule.Cap(Mansions.AllegianceLabel(a, a.Monarch?.Player))))
              .Append(",\"motd\":").Append(Q(a.AllegianceMotd)).Append(",\"motdBy\":").Append(Q(a.AllegianceMotdSetBy))
              .Append(",\"locked\":").Append(locked ? 1 : 0).Append(",\"members\":").Append(a.TotalMembers)
              .Append(",\"on\":").Append(a.OnlinePlayers?.Count ?? 0).Append(",\"patron\":").Append(p.PatronId != null ? 1 : 0)
              .Append(",\"pu\":").Append(AllegianceBalanceRule.Percent(pu)).Append(",\"lum\":").Append(AllegianceBalanceRule.Percent(lum))
              .Append(",\"chain\":[");
            var up = new List<AllegianceNode>();
            for (var n = p.AllegianceNode; n != null && up.Count < 64; n = n.Patron) up.Add(n);
            up.Reverse();
            int k = 0;
            foreach (var n in up)
            {
                var ip = n.Player;
                if (ip == null) continue;
                bool on = PlayerManager.GetOnlinePlayer(n.PlayerGuid) != null;
                sb.Append(k++ > 0 ? "," : "").Append("{\"n\":").Append(Q(ip.Name)).Append(",\"l\":").Append(ip.Level ?? 1)
                  .Append(",\"r\":").Append(n.Rank).Append(",\"v\":").Append(n.TotalVassals).Append(",\"on\":").Append(on ? 1 : 0).Append('}');
            }
            sb.Append("]}");
        }
    }
}
