using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Microsoft.EntityFrameworkCore;
using R = RevivalGuard.NewcomerWelcomeRule;

namespace RevivalGuard
{
    /// <summary>
    /// A NEW PLAYER IS NOTICED (owner 2026-11-18: new browser players leave in their first ten minutes, and regulars help
    /// newcomers when they notice them). When a brand-new player's first character enters the world for the first time and
    /// is still there SETTLE_S later, one short line on General names them ("Welcome Bob to Dereth! Say hi in General."),
    /// and the Discord bridge posts the same line to #in-game. Character name only.
    ///   * new = the character's first login (ACE's TotalLogins 1, FirstEnterWorldDone unset) on an account made in the
    ///     last day with no other character (deleted ones count), and no other account from the same address with a
    ///     character that has been in the world (a second tab's guest, a lost-browser guest, a player's second account).
    ///     Local, shared-relay and unknown addresses cannot tell, so only the account is judged there;
    ///   * never staff, hcmerc* (Hollow Company, Borg), the dev and test fleet's accounts or the owner's (NewcomerWelcomeRule.NotNew);
    ///   * at most one line per GAP_S: arrivals in between share the next line ("Welcome Bob and Alice ...");
    ///     whoever has left by then is dropped.
    ///   @modifybool rg_newcomer_welcome true       on (default off)
    ///   @modifylong rg_newcomer_welcome_style 2    wording 1-3 (NewcomerWelcomeRule.Line)
    /// </summary>
    static class NewcomerWelcome
    {
        internal const string P_ON = "rg_newcomer_welcome", P_STYLE = "rg_newcomer_welcome_style";
        static readonly TimeSpan SAME_ADDRESS_MEMORY = TimeSpan.FromHours(24);

        static readonly object s_Lock = new object();
        static readonly List<(uint guid, string name)> s_Pending = new List<(uint, string)>();
        static double s_LastPost = double.NaN;
        static bool s_FlushScheduled;
        // addresses whose newcomer was judged this run: a second tab's guest arrives before the first one's TotalLogins is saved
        static readonly Dictionary<string, DateTime> s_SeenAddr = new Dictionary<string, DateTime>();

        static bool On => PropertyManager.GetBool(P_ON, false).Item;
        static double Now => ACE.Common.Time.GetUnixTime();

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard NewcomerWelcome: a brand-new player's first character is welcomed on General (and Discord #in-game), at most one line per 10 minutes");
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_STYLE)) l[P_STYLE] = new Property<long>(1, "RevivalGuard NewcomerWelcome: the welcome line's wording, 1-3");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {P_STYLE}: not registered ({e.Message}); read with its default 1"); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                var p = __instance;
                try
                {
                    if (p == null || !On) return;
                    var acct = p.Account;
                    string why = R.NotNew(acct?.AccountName, acct?.AccessLevel ?? 0, p.Name, p.Character?.TotalLogins ?? 0, p.FirstEnterWorldDone);
                    if (why != null) return;   // every ordinary login lands here: no log
                    var session = p.Session;
                    uint guid = p.Guid.Full, accountId = acct.AccountId;
                    string name = p.Name;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(R.SETTLE_S);
                    chain.AddAction(p, () =>
                    {
                        // the same login still in the world: the database checks go off the world thread
                        if (session == null || p.Session != session || !On) return;
                        Task.Run(() => Judge(guid, accountId, name));
                    });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerWelcome enter {p?.Name}: {e.Message}"); }
            }
        }

        static void Judge(uint guid, uint accountId, string name)
        {
            try
            {
                Account account;
                List<(uint id, byte[] create, byte[] last)> accounts;
                using (var ctx = new AuthDbContext())
                {
                    account = ctx.Account.AsNoTracking().FirstOrDefault(a => a.AccountId == accountId);
                    accounts = ctx.Account.AsNoTracking().Where(a => a.AccountId != accountId)
                        .Select(a => new { a.AccountId, a.CreateIP, a.LastLoginIP }).ToList()
                        .Select(a => (a.AccountId, a.CreateIP, a.LastLoginIP)).ToList();
                }
                if (account == null) return;
                double ageH = (DateTime.UtcNow - account.CreateTime).TotalHours;

                var addr = Address(account.CreateIP);
                bool canTell = addr != null && !Gateway.IsUnknown(addr) && !StaffLoginGate.IsLocal(addr) && !StaffLoginGate.IsSharedRelay(addr);
                string key = canTell ? addr.ToString() : null;
                var sameAddr = !canTell ? new List<uint>()
                    : accounts.Where(a => Same(a.create, addr) || Same(a.last, addr)).Select(a => a.id).ToList();

                int others, known = canTell ? 0 : -1;
                using (var ctx = new ShardDbContext())
                {
                    others = ctx.Character.AsNoTracking().Count(c => c.AccountId == accountId && c.Id != guid);
                    if (canTell && sameAddr.Count > 0)
                        known = ctx.Character.AsNoTracking().Count(c => sameAddr.Contains(c.AccountId) && (c.TotalLogins > 0 || c.LastLoginTimestamp > 0));
                }
                if (canTell)
                    lock (s_Lock)
                    {
                        foreach (var k in s_SeenAddr.Where(kv => DateTime.UtcNow - kv.Value > SAME_ADDRESS_MEMORY).Select(kv => kv.Key).ToList()) s_SeenAddr.Remove(k);
                        if (s_SeenAddr.ContainsKey(key)) known++;
                        s_SeenAddr[key] = DateTime.UtcNow;
                    }
                // an online player of another account on the same address (its rows may not be saved yet)
                if (canTell)
                    foreach (var o in PlayerManager.GetAllOnline())
                        if (o?.Account != null && o.Account.AccountId != accountId && (Same(o.Account.CreateIP, addr) || Same(o.Account.LastLoginIP, addr))) known++;

                string why = R.NotNewAccount(ageH, others, known);
                if (why != null) { Mod.Log.Info($"[RevivalGuard] NewcomerWelcome: {name} first login, not welcomed ({why})"); return; }
                Add(guid, name);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerWelcome judge {name}: {e.GetBaseException().Message}"); }
        }

        static IPAddress Address(byte[] b)
        {
            if (b == null || (b.Length != 4 && b.Length != 16)) return null;
            var a = new IPAddress(b);
            return a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
        }

        static bool Same(byte[] b, IPAddress addr) => b != null && addr != null && Address(b)?.Equals(addr) == true;

        static void Add(uint guid, string name)
        {
            lock (s_Lock)
            {
                if (!s_Pending.Any(x => x.guid == guid)) s_Pending.Add((guid, name));
                if (s_FlushScheduled) { Mod.Log.Info($"[RevivalGuard] NewcomerWelcome: {name} is new; waits for the next line"); return; }
                Mod.Log.Info($"[RevivalGuard] NewcomerWelcome: {name} is new; welcomed in {Math.Max(0, R.NextAt(s_LastPost, Now) - Now):F0} s");
                Schedule();
            }
        }

        /// <summary>Under s_Lock: one flush at the next time the rate limit allows.</summary>
        static void Schedule()
        {
            s_FlushScheduled = true;
            double wait = Math.Max(0, R.NextAt(s_LastPost, Now) - Now);
            Task.Delay(TimeSpan.FromSeconds(wait)).ContinueWith(_ => WorldManager.EnqueueAction(new ActionEventDelegate(Flush)));
        }

        /// <summary>World thread: one line for everyone waiting who is still in the world. Whoever arrives meanwhile
        /// waits for the next flush, GAP_S after this line.</summary>
        static void Flush()
        {
            List<(uint guid, string name)> batch;
            lock (s_Lock) { batch = s_Pending.ToList(); s_Pending.Clear(); }
            try
            {
                if (!On || batch.Count == 0) return;
                var here = batch.Where(x => PlayerManager.GetOnlinePlayer(x.guid) != null).Select(x => x.name).ToList();
                long style = 1;
                try { style = PropertyManager.GetLong(P_STYLE, 1).Item; } catch { }
                string line = R.Line(here, style);
                if (line == null) { Mod.Log.Info($"[RevivalGuard] NewcomerWelcome: {string.Join(", ", batch.Select(x => x.name))} left before the line"); return; }
                if (PropertyManager.GetBool("chat_disable_general").Item) return;
                var msg = new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_EVENT_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME,
                                                     TurbineChatChannel.General, R.SENDER, line, 0, ChatType.General);
                foreach (var p in PlayerManager.GetAllOnline())
                {
                    if (!p.GetCharacterOption(CharacterOption.ListenToGeneralChat) || Mercenaries.IsMerc(p)) continue;
                    p.Session?.Network.EnqueueSend(msg);
                }
                lock (s_Lock) s_LastPost = Now;
                ChatLog.Write("General", R.SENDER, line);
                bool discord = DiscordBridge.PostGeneral(R.Discord(line));
                Mod.Log.Info($"[RevivalGuard] NewcomerWelcome: said \"{line}\"{(discord ? " (and to Discord)" : " (no Discord bridge)")}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NewcomerWelcome flush: {e.Message}"); }
            finally
            {
                lock (s_Lock) { if (s_Pending.Count > 0) Schedule(); else s_FlushScheduled = false; }
            }
        }
    }
}
