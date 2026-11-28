using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.Managers;
using ACE.Server.Network.GameMessages.Messages;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Forgot password.</summary>
    [HarmonyPatch]
    static class PasswordRecovery
    {
        internal const uint REQ_Q = 0x51525448, REQ_A = 0x41525448;     // "HTRQ" / "HTRA"
        internal const uint POLL_Q = 0x50525448, POLL_A = 0x42525448;   // "HTRP" / "HTRB"
        internal const string REPLY = "[PwReq] ";
        const int CLAIM = 16, MAX_NAME = 50, MAX_NOTE = 300, REQ_PER_DAY = 3;
        // Bounds for the unauthenticated path.
        const int GLOBAL_PER_MINUTE = 10, MAX_OPEN = 200;
        static DateTime s_BudgetStart, s_LastPrune, s_LastDropWarn;
        static int s_BudgetUsed, s_Dropped;
        static readonly TimeSpan POLL_GAP = TimeSpan.FromSeconds(5), TEMP_KEEP = TimeSpan.FromHours(48), OPEN_KEEP = TimeSpan.FromDays(14);
        // A temporary password is one of these words and TEMP_DIGITS digits ("pine4827"): easy to read aloud and type.
        // No word has an l or an o and no digit is 0 or 1, so nothing in it has a look-alike. 301 words x 8^4 = 1.2 million.
        const string TEMP_DIGIT_CHARS = "23456789";
        const int TEMP_DIGITS = 4, NEW_MIN = 6;
        const string EXAMPLE = "MyNewPassword55";   // the hint's example: refused as a real password
        static readonly string[] TEMP_WORDS = (
            "amber arch badge badger bake baker banana band bank banner barber barge barn basin basket bath batter bead beak beam bean beef bench bike birch bird "
          + "braid brass brave breeze brick bridge brush bucket bunk bunny burger butter cabin cactus cake camera camp camper canary candy canvas cape card carp "
          + "carpet cart cave cavern cedar chain chair charm chart chase cheese chef cherry chess chest chime chip cider cinder cinema circus crab crane crate "
          + "cream creek crumb crust cube dairy daisy dance dancer dart dawn deck denim desert desk diary diner dinner dipper dish drawer dress drift drum duck "
          + "dune dusk earth ember engine fabric fairy farm farmer faucet fawn feast fence fern ferry finch finger fish frame fresh fridge fruit fudge gadget "
          + "gander garden garnet gate gear giant gift ginger grain grape grass grater gravy green guitar hammer hamper hanger harp hatch hawk heart heater hedge "
          + "herd hermit hike hinge hive hunter insect jacket jaguar jasper jeans jeep jigsaw judge juice jump kayak kick king kite kitten magnet mantis march "
          + "marker market marsh mask mesa mint mist mitten muffin music napkin nectar nest nutmeg paint panda pantry paper parade park pasta pastry patch path "
          + "peach peanut pepper picnic pine pink pirate pizza prize puffin pump puppy quartz rabbit radish raft rafter raisin rake ranch ranger raven reef rink "
          + "river ruby sand scarf seed shark shed sheep ship shirt shrimp shrub skate sketch snack snake spark spice spider spring spur square squid stag stage "
          + "stamp star statue steam stem stick straw stream street sugar summer sunny sunset swan swim swing tank tavern teacup team tent thread thumb ticket "
          + "tiger timber track train tray tree trek truck trunk tuna tundra turkey turnip tusk twig twin vest vine wand wasp water wave wheat wick wing winter "
          + "wish wizard yard yarn zebra zigzag zipper").Split(' ');
        const string STRONG_CHARS = "abcdefghjkmnpqrstuvwxyz23456789";
        static Timer s_ExpireTimer;

        internal sealed class Req
        {
            public int Id { get; set; }
            public string Account { get; set; } = "";       // as typed
            public string Note { get; set; } = "";
            public string Address { get; set; } = "";
            public DateTime At { get; set; }
            public string ClaimHash { get; set; } = "";
            public string Status { get; set; } = "open";
            public string TempPw { get; set; }
            public string DecidedBy { get; set; }
            public DateTime? DecidedAt { get; set; }
            public DateTime? Collected { get; set; }
        }

        static readonly object s_Lock = new object();
        static List<Req> s_Reqs;
        static string s_File;
        static readonly Dictionary<string, Queue<DateTime>> s_ReqsBy = new Dictionary<string, Queue<DateTime>>();
        static readonly Dictionary<string, DateTime> s_PollBy = new Dictionary<string, DateTime>();
        static readonly Dictionary<uint, int> s_Viewing = new Dictionary<uint, int>();   // staff account -> request id

        internal static void Register()
        {
            CommandManager.TryAddCommand(Staff, "pwreq", AccessLevel.Admin, CommandHandlerFlag.None,
                "Forgot-password requests: next shows the oldest open one; approve / decline act on the one shown.", "next|approve|decline|list");
            RegisterPasswd();
            lock (s_Lock) Load();
            // Requests are otherwise only looked at when a packet or a staff command comes in.
            s_ExpireTimer = new Timer(_ => { try { lock (s_Lock) Load(); } catch (Exception e) { Mod.Log.Warn("[RevivalGuard] PasswordRecovery expire: " + e.Message); } },
                null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
        }

        /// <summary>ACE's own passwd, plus the short form for an account still on its temporary password.</summary>
        static void RegisterPasswd() =>
            CommandManager.TryAddCommand(Passwd, "passwd", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Change your account password.", "oldpassword newpassword");

        // At a server start the mods load first and ACE then lists its own commands over ours.
        [HarmonyPatch(typeof(CommandManager), nameof(CommandManager.Initialize))]
        static class AfterAceCommands
        {
            static void Postfix() { try { RegisterPasswd(); } catch (Exception e) { Mod.Log.Warn("[RevivalGuard] PasswordRecovery passwd: " + e.Message); } }
        }

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method("ACE.Server.Network.Managers.NetworkManager:ProcessPacket");

        static bool Prefix(ConnectionListener connectionListener, ClientPacket packet, IPEndPoint endPoint)
        {
            bool ours = false;
            try
            {
                if (packet == null || endPoint == null) return true;
                var h = packet.Header;
                if ((h.Sequence != REQ_Q && h.Sequence != POLL_Q) || h.Id != 0xFFFF || (uint)h.Flags != 0) return true;
                var d = packet.Data;
                if (d == null || d.Length != h.Size || h.Size < 4 + CLAIM || h.Size > 4 + CLAIM + 1 + MAX_NAME + MAX_NOTE) return true;
                long pos = d.Position;
                d.Position = 0;
                var buf = new byte[d.Length];
                int got = d.Read(buf, 0, buf.Length);
                d.Position = pos;
                if (got != buf.Length || BitConverter.ToUInt32(buf, 0) != h.Sequence) return true;
                ours = true;

                var real = Gateway.RealAddressForLimits(endPoint);
                string key = real.ToString();
                bool local = StaffLoginGate.IsLocal(real);
                string claimHash = Convert.ToHexString(SHA256.HashData(buf.AsSpan(4, CLAIM)));
                byte[] reply = h.Sequence == REQ_Q ? Request(buf, key, local, claimHash) : Poll(key, local, claimHash);
                if (reply != null) connectionListener?.Socket?.SendTo(reply, endPoint);
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn("[RevivalGuard] PasswordRecovery: " + e.Message);
                return !ours;
            }
        }

        static byte[] Request(byte[] buf, string key, bool local, string claimHash)
        {
            var reply = new byte[5];
            BitConverter.GetBytes(REQ_A).CopyTo(reply, 0);
            reply[4] = 1;
            int at = 4 + CLAIM;
            if (buf.Length < at + 1) return reply;
            int n = buf[at++];
            if (n < 1 || n > MAX_NAME || buf.Length < at + n) return reply;
            string name = Printable(Encoding.ASCII.GetString(buf, at, n)).Trim();
            string note = Printable(Encoding.UTF8.GetString(buf, at + n, buf.Length - at - n)).Trim();
            if (name.Length == 0) return reply;
            var now = DateTime.UtcNow;
            lock (s_Lock)
            {
                PruneHourly(now);
                Queue<DateTime> q = null;
                if (!local && s_ReqsBy.TryGetValue(key, out q))
                {
                    while (q.Count > 0 && now - q.Peek() > TimeSpan.FromDays(1)) q.Dequeue();
                    if (q.Count >= REQ_PER_DAY) { Dropped(now, $"{key} passed {REQ_PER_DAY} requests a day"); return reply; }
                }
                Load();
                if (s_Reqs.Any(r => r.ClaimHash == claimHash)) return reply;   // a resend of the same request
                if (!local)
                {
                    if (s_Reqs.Count(r => r.Status == "open") >= MAX_OPEN) { Dropped(now, $"{MAX_OPEN} requests already open"); return reply; }
                    if (now - s_BudgetStart >= TimeSpan.FromMinutes(1)) { s_BudgetStart = now; s_BudgetUsed = 0; }
                    if (s_BudgetUsed >= GLOBAL_PER_MINUTE) { Dropped(now, $"{GLOBAL_PER_MINUTE} outside requests this minute already"); return reply; }
                    s_BudgetUsed++;
                    if (q == null) s_ReqsBy[key] = q = new Queue<DateTime>();
                    q.Enqueue(now);
                }
                var req = new Req { Id = s_Reqs.Count == 0 ? 1 : s_Reqs.Max(r => r.Id) + 1, Account = name, Note = note, Address = key, At = now, ClaimHash = claimHash };
                s_Reqs.Add(req);
                Save();
                Mod.Log.Info($"[RevivalGuard] PasswordRecovery: request {req.Id} for account '{name}' from {key}");
            }
            return reply;
        }

        static byte[] Poll(string key, bool local, string claimHash)
        {
            var now = DateTime.UtcNow;
            byte status = 0; string pw = null;
            lock (s_Lock)
            {
                PruneHourly(now);
                Load();
                var r = s_Reqs.FirstOrDefault(x => x.ClaimHash == claimHash);
                // A claim that matches no request gets the "unknown" answer without touching the per-address table, so spoofed polls cannot grow it (SEC-003).
                if (r != null && !local)
                {
                    if (s_PollBy.TryGetValue(key, out var last) && now - last < POLL_GAP) return null;
                    s_PollBy[key] = now;
                }
                if (r != null)
                {
                    if (r.Status == "open") status = 1;
                    else if (r.Status == "declined") status = 3;
                    else if (r.Status == "changed") status = 4;
                    else if (r.Status == "approved" && r.TempPw != null) { status = 2; pw = r.TempPw; if (r.Collected == null) { r.Collected = now; Save(); } }
                }
            }
            var pwb = pw == null ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(pw);
            var reply = new byte[5 + pwb.Length];
            BitConverter.GetBytes(POLL_A).CopyTo(reply, 0);
            reply[4] = status;
            pwb.CopyTo(reply, 5);
            return reply;
        }

        static void PruneHourly(DateTime now)
        {
            if (now - s_LastPrune < TimeSpan.FromHours(1)) return;
            s_LastPrune = now;
            foreach (var k in s_ReqsBy.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() > TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList()) s_ReqsBy.Remove(k);
            foreach (var k in s_PollBy.Where(kv => now - kv.Value >= POLL_GAP).Select(kv => kv.Key).ToList()) s_PollBy.Remove(k);
        }

        /// <summary>A dropped request, logged at most once a minute with the count since, so a flood cannot fill the log.</summary>
        static void Dropped(DateTime now, string why)
        {
            s_Dropped++;
            if (now - s_LastDropWarn < TimeSpan.FromMinutes(1)) return;
            s_LastDropWarn = now;
            Mod.Log.Warn($"[RevivalGuard] PasswordRecovery: dropped {s_Dropped} request(s) in the last minute (latest: {why})");
            s_Dropped = 0;
        }

        static string Printable(string s) => new string(s.Where(c => !char.IsControl(c)).ToArray());

        // ---- staff ---------------------------------------------------------------------------------

        static void Staff(Session session, params string[] p)
        {
            string verb = p != null && p.Length > 0 ? p[0].ToLowerInvariant() : "next";
            string who = session?.Player?.Name ?? "the console";
            uint me = session?.AccountId ?? 0;
            try
            {
                lock (s_Lock)
                {
                    Load();
                    var open = s_Reqs.Where(r => r.Status == "open").OrderBy(r => r.At).ToList();
                    if (verb == "list")
                    {
                        if (open.Count == 0) { Say(session, "No open password requests."); return; }
                        foreach (var r in open) Say(session, $"#{r.Id} '{r.Account}' from {r.Address}, {Ago(r.At)} ago");
                        return;
                    }
                    if (verb == "next")
                    {
                        if (open.Count == 0) { Say(session, "No open password requests."); s_Viewing.Remove(me); return; }
                        // after the one this session saw last, so Next walks the queue
                        int seen = s_Viewing.TryGetValue(me, out var v) ? v : 0;
                        var r = open.FirstOrDefault(x => x.Id > seen) ?? open[0];
                        s_Viewing[me] = r.Id;
                        Show(session, r, open.Count);
                        return;
                    }
                    if (verb != "approve" && verb != "decline") { Say(session, "Usage: @pwreq next | approve | decline | list"); return; }
                    if (!s_Viewing.TryGetValue(me, out int id)) { Say(session, "Press Next first: approve and decline act on the request it shows."); return; }
                    var req = s_Reqs.FirstOrDefault(x => x.Id == id);
                    if (req == null || req.Status != "open") { Say(session, $"Request #{id} is no longer open. Press Next."); return; }
                    if (verb == "decline")
                    {
                        Decide(req, "declined", who, null);
                        AuditTrail.Record("pwreq", who, "console", $"declined request {req.Id} for account '{req.Account}'");
                        Say(session, $"Declined #{req.Id} ('{req.Account}'). The player's screen says staff could not confirm it.");
                        return;
                    }
                    var account = DatabaseManager.Authentication.GetAccountByName(req.Account);
                    if (account == null) { Say(session, $"#{req.Id}: there is no account '{req.Account}'. Decline it."); return; }
                    if (account.AccessLevel > 0)
                    {
                        AuditTrail.Record("pwreq", who, "console", $"refused: request {req.Id} is for staff account {account.AccountName}");
                        Say(session, $"#{req.Id}: '{account.AccountName}' is a staff account; its password is not reset here.");
                        return;
                    }
                    string temp = TempPassword();
                    account.SetPassword(temp);
                    account.SetSaltForBCrypt();
                    DatabaseManager.Authentication.UpdateAccount(account);
                    NetworkManager.Find(account.AccountName)?.Terminate(SessionTerminationReason.AccountBooted,
                        new GameMessageBootAccount(" because its password was reset"));
                    // an earlier approval's password stopped working just now: its device is told to ask again
                    foreach (var old in s_Reqs.Where(x => x != req && x.TempPw != null && string.Equals(x.Account, req.Account, StringComparison.OrdinalIgnoreCase)))
                        old.TempPw = null;
                    Decide(req, "approved", who, temp);
                    AuditTrail.Record("pwreq", who, "console", $"approved request {req.Id}: temporary password set on account {account.AccountName}");
                    Mod.Log.Info($"[RevivalGuard] PasswordRecovery: {who} approved request {req.Id} for account '{account.AccountName}'");
                    Say(session, $"Approved #{req.Id}: '{account.AccountName}' has a temporary password. The player's login screen shows it on the device that asked.");
                }
            }
            catch (Exception e)
            {
                Mod.Log.Error("[RevivalGuard] PasswordRecovery: " + e.Message);
                Say(session, "The request could not be handled: " + e.Message);
            }
        }

        static void Show(Session session, Req r, int openCount)
        {
            Say(session, $"Request #{r.Id} ({openCount} open): account '{r.Account}', {Ago(r.At)} ago, from {r.Address}");
            Say(session, $"Note: {(r.Note.Length == 0 ? "(none)" : r.Note)}");
            var account = DatabaseManager.Authentication.GetAccountByName(r.Account);
            if (account == null) { Say(session, "No such account. Decline it."); return; }
            if (account.AccessLevel > 0) Say(session, "This is a STAFF account: it cannot be reset here.");
            var chars = (PlayerManager.GetAccountPlayers(account.AccountId)?.Values ?? Enumerable.Empty<ACE.Server.Entity.IPlayer>())
                .Select(c => $"{c.Name} {c.Level ?? 0}").OrderBy(s => s).ToList();
            Say(session, $"Characters: {(chars.Count == 0 ? "none" : string.Join(", ", chars))}");
            string lastIp = account.LastLoginIP != null && account.LastLoginIP.Length == 4 ? new IPAddress(account.LastLoginIP).ToString() : "?";
            string lastAt = account.LastLoginTime.HasValue ? Ago(account.LastLoginTime.Value) + " ago" : "never";
            bool same = lastIp == r.Address;
            int others = s_Reqs.Count(x => x.Status == "open" && x.Id != r.Id && string.Equals(x.Account, r.Account, StringComparison.OrdinalIgnoreCase));
            Say(session, $"Last login {lastAt} from {lastIp}: {(same ? "SAME address as this request" : "a different address")}"
                + (others > 0 ? $"; {others} other open request(s) for this account" : ""));
            Say(session, "Approve only if the note matches. Approve / Decline act on this request.");
        }

        static void Decide(Req r, string status, string who, string temp)
        {
            r.Status = status; r.DecidedBy = who; r.DecidedAt = DateTime.UtcNow; r.TempPw = temp;
            Save();
        }

        internal static string TempPassword()
        {
            var sb = new StringBuilder(TEMP_WORDS[RandomNumberGenerator.GetInt32(TEMP_WORDS.Length)]);
            for (int i = 0; i < TEMP_DIGITS; i++) sb.Append(TEMP_DIGIT_CHARS[RandomNumberGenerator.GetInt32(TEMP_DIGIT_CHARS.Length)]);
            return sb.ToString();
        }

        static string StrongPassword()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 24; i++) sb.Append(STRONG_CHARS[RandomNumberGenerator.GetInt32(STRONG_CHARS.Length)]);
            return sb.ToString();
        }

        /// <summary>The temporary password the account is still waiting to replace, or null.</summary>
        static string TempFor(string accountName)
        {
            lock (s_Lock)
            {
                Load();
                return s_Reqs.Where(r => r.Status == "approved" && r.TempPw != null
                    && string.Equals(r.Account, accountName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.DecidedAt).FirstOrDefault()?.TempPw;
            }
        }

        // ---- the player's side in game ----------------------------------------------------------------

        static void Tell(Session s, string text) => s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        /// <summary>passwd old new, as ACE's; and passwd new alone while the account is still on its temporary password.</summary>
        static void Passwd(Session session, params string[] p)
        {
            if (session == null) { Console.WriteLine("This command is run from ingame client only"); return; }
            try
            {
                var now = DateTime.UtcNow;
                if (now - session.LastPassTime < TimeSpan.FromSeconds(5)) { Tell(session, "Please wait a few seconds, then try /passwd again."); return; }
                session.LastPassTime = now;
                var account = DatabaseManager.Authentication.GetAccountById(session.AccountId);
                if (account == null) { Tell(session, "Your account was not found. Please tell staff."); return; }
                string temp = TempFor(account.AccountName);
                const string LONG = "To change your password type /passwd, your current password, then your new password.";
                const string SHORT = "To choose your own password type /passwd, a space, then the new password you want.";
                if (p == null || p.Length == 0) { Tell(session, temp != null ? SHORT : LONG); return; }
                bool shortForm = p.Length == 1;
                if (shortForm && temp == null) { Tell(session, LONG); return; }
                string old = shortForm ? temp : p[0], neu = shortForm ? p[0] : p[1];
                if (temp != null && (neu.Length < NEW_MIN || string.Equals(neu, temp, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(neu, EXAMPLE, StringComparison.OrdinalIgnoreCase)))
                {
                    Tell(session, $"Choose a new password of your own, {NEW_MIN} characters or more, different from the temporary one.");
                    return;
                }
                if (!account.PasswordMatches(old))
                {
                    Tell(session, shortForm ? LONG : "Unable to change password: the first password does not match this account's current password.");
                    return;
                }
                account.SetPassword(neu);
                account.SetSaltForBCrypt();
                DatabaseManager.Authentication.UpdateAccount(account);
                if (temp != null)
                {
                    lock (s_Lock)
                    {
                        foreach (var r in s_Reqs.Where(r => r.TempPw != null && string.Equals(r.Account, account.AccountName, StringComparison.OrdinalIgnoreCase)))
                        { r.TempPw = null; r.Status = "changed"; }
                        Save();
                    }
                    AuditTrail.Record("pwreq", account.AccountName, "player", "replaced the temporary password with their own");
                    Mod.Log.Info($"[RevivalGuard] PasswordRecovery: account '{account.AccountName}' replaced its temporary password");
                }
                Tell(session, "Account password successfully changed. Use your new password the next time you log in.");
            }
            catch (Exception e)
            {
                Mod.Log.Error("[RevivalGuard] PasswordRecovery passwd: " + e.Message);
                Tell(session, "The password could not be changed. Please try again, or tell staff.");
            }
        }

        /// <summary>Entering the world on a temporary password: say how to replace it.</summary>
        [HarmonyPatch(typeof(ACE.Server.WorldObjects.Player), nameof(ACE.Server.WorldObjects.Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(ACE.Server.WorldObjects.Player __instance)
            {
                try
                {
                    var p = __instance; var session = p?.Session;
                    if (session == null || TempFor(session.Account) == null) return;
                    var chain = new ACE.Server.Entity.Actions.ActionChain();
                    chain.AddDelaySeconds(12);
                    chain.AddAction(p, () =>
                    {
                        if (p.Session != session || TempFor(session.Account) == null) return;
                        Tell(session, "You logged in with a temporary password. Choose your own now: type /passwd, a space, then the new password you want. Example: /passwd " + EXAMPLE + " (pick your own, not this one).");
                    });
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn("[RevivalGuard] PasswordRecovery hint: " + e.Message); }
            }
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.UtcNow - t;
            return d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min" : d.TotalHours < 48 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalDays} days";
        }

        static void Say(Session s, string text)
        {
            if (s == null) Console.WriteLine(REPLY + text);
            else s.Network?.EnqueueSend(new GameMessageSystemChat(REPLY + text, ChatMessageType.Broadcast));
        }

        static void Load()
        {
            if (s_Reqs != null) { Expire(); return; }
            s_Reqs = new List<Req>();
            try
            {
                string dir = Environment.GetEnvironmentVariable("REVIVAL_REPORTS_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports");
                Directory.CreateDirectory(dir);
                s_File = Path.Combine(dir, "password-requests.json");
                if (File.Exists(s_File)) s_Reqs = JsonSerializer.Deserialize<List<Req>>(File.ReadAllText(s_File)) ?? new List<Req>();
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] PasswordRecovery load: " + e.Message); }
            Expire();
        }

        static void Expire()
        {
            var now = DateTime.UtcNow;
            bool changed = false;
            foreach (var r in s_Reqs)
            {
                if (r.TempPw != null && r.DecidedAt.HasValue && now - r.DecidedAt.Value > TEMP_KEEP)
                {
                    if (r.Status == "approved") RetireUnused(r);
                    r.TempPw = null; changed = true;
                }
                if (r.Status == "open" && now - r.At > OPEN_KEEP) { r.Status = "expired"; changed = true; }
            }
            if (changed) Save();
        }

        /// <summary>A temporary password nobody logged in with stops working when it stops being shown; one in use stays until the player replaces it.</summary>
        static void RetireUnused(Req r)
        {
            try
            {
                var account = DatabaseManager.Authentication.GetAccountByName(r.Account);
                if (account == null || (account.LastLoginTime.HasValue && account.LastLoginTime.Value >= r.DecidedAt.Value)) return;
                if (!account.PasswordMatches(r.TempPw)) return;
                account.SetPassword(StrongPassword());
                account.SetSaltForBCrypt();
                DatabaseManager.Authentication.UpdateAccount(account);
                AuditTrail.Record("pwreq", "the server", "timer", $"request {r.Id}: the unused temporary password on account {account.AccountName} expired");
                Mod.Log.Info($"[RevivalGuard] PasswordRecovery: the unused temporary password on account '{account.AccountName}' expired (request {r.Id})");
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] PasswordRecovery retire: " + e.Message); }
        }

        static readonly object s_WriteLock = new object();
        static int s_SaveQueued;

        /// <summary>Write the requests file off the caller's thread.</summary>
        static void Save()
        {
            if (s_File == null || Interlocked.Exchange(ref s_SaveQueued, 1) == 1) return;
            Task.Run(() =>
            {
                lock (s_WriteLock)
                {
                    Interlocked.Exchange(ref s_SaveQueued, 0);   // a change from here on queues the next write
                    try
                    {
                        string json, file;
                        lock (s_Lock) { json = JsonSerializer.Serialize(s_Reqs, new JsonSerializerOptions { WriteIndented = true }); file = s_File; }
                        string tmp = file + ".tmp";
                        File.WriteAllText(tmp, json);
                        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        File.Move(tmp, file, true);
                    }
                    catch (Exception e) { Mod.Log.Error("[RevivalGuard] PasswordRecovery save: " + e.Message); }
                }
            });
        }
    }
}
