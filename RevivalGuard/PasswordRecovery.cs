using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// <summary>
    /// FORGOT PASSWORD (docs/PASSWORD-RECOVERY.md, owner yes 2026-11-07: Olivia thought she had lost her account, and the
    /// shard holds no player emails). The login screen sends a request with the account name, a note only the owner
    /// would know, and a random claim code it keeps; staff look at the evidence and approve or decline from the admin
    /// panel; the device holding the claim code then collects a temporary password. No login is needed for either.
    ///
    /// Wire, on the login port like <see cref="AccountQuery"/> (an AC header with Sequence = magic, Id 0xFFFF, Flags 0;
    /// the browser's datagrams arrive through the gateway and are judged by the player's own address):
    ///   'HTRQ' claim(16) n(1) name(n) note(rest, UTF-8)  ->  'HTRA' 1     (always: a typo, a limit, an unknown name
    ///                                                                      and a real request look the same)
    ///   'HTRP' claim(16)                                  ->  'HTRB' status [password]
    ///                                                        status 0 unknown, 1 waiting, 2 approved, 3 declined
    /// Limits per address (none for the shard's own network, StaffLoginGate.IsLocal): REQ_PER_DAY requests a day, one
    /// poll per POLL_GAP. Staff: @pwreq next | approve | decline | list (Admin), the admin panel's buttons; approve and
    /// decline act on the request that staff session last saw with next, so nobody types an id or a password.
    /// The claim code is stored only as its SHA-256; the temporary password stays until TEMP_KEEP after approval.
    /// Changing the password ends every "stay logged in" token on the account by itself (LoginTokens fingerprint).
    /// </summary>
    [HarmonyPatch]
    static class PasswordRecovery
    {
        internal const uint REQ_Q = 0x51525448, REQ_A = 0x41525448;     // "HTRQ" / "HTRA"
        internal const uint POLL_Q = 0x50525448, POLL_A = 0x42525448;   // "HTRP" / "HTRB"
        internal const string REPLY = "[PwReq] ";
        const int CLAIM = 16, MAX_NAME = 50, MAX_NOTE = 300, REQ_PER_DAY = 3;
        static readonly TimeSpan POLL_GAP = TimeSpan.FromSeconds(5), TEMP_KEEP = TimeSpan.FromHours(48), OPEN_KEEP = TimeSpan.FromDays(14);
        const string TEMP_CHARS = "abcdefghjkmnpqrstuvwxyz23456789";   // no 0/o, 1/l/i

        internal sealed class Req
        {
            public int Id { get; set; }
            public string Account { get; set; } = "";       // as typed
            public string Note { get; set; } = "";
            public string Address { get; set; } = "";
            public DateTime At { get; set; }
            public string ClaimHash { get; set; } = "";
            public string Status { get; set; } = "open";    // open, approved, declined
            public string TempPw { get; set; }              // approved: until collected window ends
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
            lock (s_Lock) Load();
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

                var real = Gateway.RealAddress(endPoint);
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
                if (!local)
                {
                    if (!s_ReqsBy.TryGetValue(key, out var q)) s_ReqsBy[key] = q = new Queue<DateTime>();
                    while (q.Count > 0 && now - q.Peek() > TimeSpan.FromDays(1)) q.Dequeue();
                    if (q.Count >= REQ_PER_DAY) { Mod.Log.Warn($"[RevivalGuard] PasswordRecovery: {key} passed {REQ_PER_DAY} requests a day; dropped"); return reply; }
                    q.Enqueue(now);
                }
                Load();
                if (s_Reqs.Any(r => r.ClaimHash == claimHash)) return reply;   // a resend of the same request
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
                if (!local)
                {
                    if (s_PollBy.TryGetValue(key, out var last) && now - last < POLL_GAP) return null;
                    s_PollBy[key] = now;
                }
                Load();
                var r = s_Reqs.FirstOrDefault(x => x.ClaimHash == claimHash);
                if (r != null)
                {
                    if (r.Status == "open") status = 1;
                    else if (r.Status == "declined") status = 3;
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
                    DatabaseManager.Authentication.UpdateAccount(account);
                    NetworkManager.Find(account.AccountName)?.Terminate(SessionTerminationReason.AccountBooted,
                        new GameMessageBootAccount(" because its password was reset"));
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
            var chars = PlayerManager.GetAccountPlayers(account.AccountId).Values
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

        static string TempPassword()
        {
            var b = RandomNumberGenerator.GetBytes(10);
            return new string(b.Select(x => TEMP_CHARS[x % TEMP_CHARS.Length]).ToArray());
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

        // ---- storage: ~/ace/reports/password-requests.json, owner-only ----------------------------

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

        /// <summary>Temporary passwords leave the file TEMP_KEEP after approval; open requests close after OPEN_KEEP.</summary>
        static void Expire()
        {
            var now = DateTime.UtcNow;
            bool changed = false;
            foreach (var r in s_Reqs)
            {
                if (r.TempPw != null && r.DecidedAt.HasValue && now - r.DecidedAt.Value > TEMP_KEEP) { r.TempPw = null; changed = true; }
                if (r.Status == "open" && now - r.At > OPEN_KEEP) { r.Status = "expired"; changed = true; }
            }
            if (changed) Save();
        }

        static void Save()
        {
            if (s_File == null) return;
            try
            {
                string tmp = s_File + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_Reqs, new JsonSerializerOptions { WriteIndented = true }));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(tmp, s_File, true);
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] PasswordRecovery save: " + e.Message); }
        }
    }
}
