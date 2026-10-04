using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Packets;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// STAY LOGGED IN (docs/STAY-LOGGED-IN.md, owner 2026-10-01: "once you log in once leave the
    /// person logged in"). Ours; retail's launcher took the password every time.
    ///
    /// After a PASSWORD login our client asks "@logintoken" once it is in the world; this answers on
    /// the mod channel ("login-token") with a random 256-bit token tied to that account. The client
    /// keeps only the token, and on a later launch sends it in the login packet's password field as
    /// PREFIX + token. A prefix on ACE's `AccountExtensions.PasswordMatches` accepts it in place of the
    /// password, so ACE's own login path runs unchanged around it: StaffLoginGate still refuses a
    /// staff account from outside the shard's network (a token never bypasses that), bans still boot,
    /// and a session takeover still needs a verified credential.
    ///
    /// Stored HASHED (SHA-256) in LoginTokens.json beside the dll, mode 0600. A token dies when:
    /// unused for EXPIRY (each use slides it), the account's password changes (a fingerprint of the
    /// stored password hash is kept with it), the account is banned, the player logs out (the client
    /// sends 'HTTR' + token on the login port), or the account has more than PER_ACCOUNT newer ones.
    /// The token is never logged. REVIVAL_LOGIN_TOKENS=0 turns all of it off (tokens then fail).
    /// </summary>
    static class LoginTokens
    {
        internal const string PREFIX = "~htlt1~";            // AcLogin.TOKEN_PREFIX on the client
        internal const uint REVOKE_Q = 0x52545448;           // "HTTR" little-endian
        static readonly TimeSpan EXPIRY = TimeSpan.FromDays(90);
        const int PER_ACCOUNT = 8;
        const string BAD_PASSWORD = " because the password entered for this account was not correct";

        static readonly bool s_On = Environment.GetEnvironmentVariable("REVIVAL_LOGIN_TOKENS") != "0";

        sealed class Entry
        {
            public uint Account { get; set; }
            public string Hash { get; set; } = "";
            public string Pw { get; set; } = "";             // fingerprint of the password hash at issue
            public DateTime Created { get; set; }
            public DateTime Used { get; set; }
        }

        static readonly object s_Lock = new object();
        static Dictionary<string, Entry> s_ByHash;          // token hash -> entry
        static string s_File;

        /// <summary>Sessions that logged in with a real password (not a token): only these may mint one.</summary>
        sealed class Mark { }
        static readonly ConditionalWeakTable<Session, Mark> s_PasswordLogin = new ConditionalWeakTable<Session, Mark>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Issue, "logintoken", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client asks for a stay-logged-in token after a password login.", "");
            lock (s_Lock) Load();
        }

        // ---- the check, in ACE's own password test -------------------------------------------------

        [HarmonyPatch(typeof(AccountExtensions), nameof(AccountExtensions.PasswordMatches))]
        static class PasswordMatchesPatch
        {
            static bool Prefix(Account account, string password, ref bool __result)
            {
                if (password == null || !password.StartsWith(PREFIX, StringComparison.Ordinal)) return true;
                // ONLY ON THE LOGIN PATH. Elsewhere (@passwd's "old password") a token is no password:
                // a stolen token must not be able to change the password and lock the owner out.
                try { __result = s_On && s_InLogin && account != null && Check(account, password.Substring(PREFIX.Length)); }
                catch (Exception e) { Mod.Log.Error("[RevivalGuard] LoginTokens check: " + e.Message); __result = false; }
                return false;                                 // a token is never tried as a password
            }
        }

        /// <summary>A token for an account that does not exist must not auto-create one with the
        /// token as its password: refuse it as a wrong password before DoLogin gets that far.</summary>
        [HarmonyPatch]
        static class DoLoginPatch
        {
            static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method("ACE.Server.Network.Handlers.AuthenticationHandler:DoLogin");

            static bool Prefix(Session session, PacketInboundLoginRequest loginRequest)
            {
                try
                {
                    if (session == null || loginRequest?.Password == null) return true;
                    if (!loginRequest.Password.StartsWith(PREFIX, StringComparison.Ordinal)) return true;
                    if (DatabaseManager.Authentication.GetAccountByName(loginRequest.Account) != null) return true;
                    session.Terminate(SessionTerminationReason.NotAuthorizedPasswordMismatch, new GameMessageBootAccount(BAD_PASSWORD));
                    return false;
                }
                catch (Exception e) { Mod.Log.Error("[RevivalGuard] LoginTokens DoLogin: " + e.Message); return true; }
            }
        }

        /// <summary>Set on the login thread for the length of AccountSelectCallback (StaffLoginGate's
        /// prefix included, which is why this prefix runs first): the only place a token counts.</summary>
        [ThreadStatic] static bool s_InLogin;

        /// <summary>Remember which sessions proved a real password.</summary>
        [HarmonyPatch]
        static class AccountSelectPatch
        {
            static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method("ACE.Server.Network.Handlers.AuthenticationHandler:AccountSelectCallback");

            [HarmonyPriority(Priority.First)]
            static void Prefix() => s_InLogin = true;

            static Exception Finalizer(Exception __exception) { s_InLogin = false; return __exception; }

            static void Postfix(Account account, Session session, PacketInboundLoginRequest loginRequest)
            {
                try
                {
                    if (account == null || session == null || loginRequest == null) return;
                    if (loginRequest.NetAuthType != NetAuthType.AccountPassword) return;
                    if (loginRequest.Password == null || loginRequest.Password.StartsWith(PREFIX, StringComparison.Ordinal)) return;
                    if (session.AccountId != account.AccountId || session.PendingTermination != null) return;
                    s_PasswordLogin.Remove(session);
                    s_PasswordLogin.Add(session, new Mark());
                }
                catch (Exception e) { Mod.Log.Error("[RevivalGuard] LoginTokens mark: " + e.Message); }
            }
        }

        // ---- issue -----------------------------------------------------------------------------------

        static void Issue(Session session, params string[] parameters)
        {
            if (!s_On || session?.Player == null) return;
            if (!s_PasswordLogin.TryGetValue(session, out _) || !ModChannel.Capable(session))
            {
                Mod.Log.Info($"[RevivalGuard] login token refused for account '{session.Account}': not a password login on our client");
                return;
            }
            var account = DatabaseManager.Authentication.GetAccountByName(session.Account);
            if (account == null) return;
            string token = Base64Url(RandomNumberGenerator.GetBytes(32));
            int n;
            lock (s_Lock)
            {
                Load();
                var now = DateTime.UtcNow;
                s_ByHash[Hash(token)] = new Entry { Account = account.AccountId, Hash = Hash(token), Pw = Fingerprint(account), Created = now, Used = now };
                // Keep the newest PER_ACCOUNT for this account.
                var mine = s_ByHash.Values.Where(e => e.Account == account.AccountId).OrderByDescending(e => e.Used).ToList();
                foreach (var old in mine.Skip(PER_ACCOUNT)) s_ByHash.Remove(old.Hash);
                n = Math.Min(mine.Count, PER_ACCOUNT);
                Save();
            }
            // The mark is spent: one token per password login.
            s_PasswordLogin.Remove(session);
            ModChannel.Send(session, "login-token", JsonSerializer.Serialize(new { account = account.AccountName, token }));
            Mod.Log.Info($"[RevivalGuard] login token issued for account '{account.AccountName}' ({n} active)");
        }

        // ---- check -----------------------------------------------------------------------------------

        static bool Check(Account account, string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 64) return false;
            string h = Hash(token);
            lock (s_Lock)
            {
                Load();
                if (!s_ByHash.TryGetValue(h, out var e) || e.Account != account.AccountId) return false;
                var now = DateTime.UtcNow;
                string why = null;
                if (now - e.Used > EXPIRY) why = "expired";
                else if (e.Pw != Fingerprint(account)) why = "password changed";
                else if (account.BanExpireTime.HasValue && now < account.BanExpireTime.Value) why = "account banned";
                if (why != null)
                {
                    // A changed password or a ban ends every token on the account, not just this one.
                    if (why == "expired") s_ByHash.Remove(h);
                    else foreach (var k in s_ByHash.Where(kv => kv.Value.Account == account.AccountId).Select(kv => kv.Key).ToList()) s_ByHash.Remove(k);
                    Save();
                    Mod.Log.Info($"[RevivalGuard] login token refused for account '{account.AccountName}': {why}");
                    return false;
                }
                if (now - e.Used > TimeSpan.FromHours(1)) { e.Used = now; Save(); }
                return true;
            }
        }

        // ---- revoke: 'HTTR' + token on the login port, from the client's Log Out ---------------------

        [HarmonyPatch]
        static class RevokePatch
        {
            static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method("ACE.Server.Network.Managers.NetworkManager:ProcessPacket");

            static bool Prefix(ClientPacket packet, IPEndPoint endPoint)
            {
                try
                {
                    if (packet == null || endPoint == null) return true;
                    var hd = packet.Header;
                    if (hd.Sequence != REVOKE_Q || hd.Id != 0xFFFF || (uint)hd.Flags != 0) return true;
                    var d = packet.Data;
                    if (d == null || BadSize(hd.Size, d.Length)) return true;
                    long pos = d.Position;
                    d.Position = 0;
                    var buf = new byte[d.Length];
                    int got = d.Read(buf, 0, buf.Length);
                    d.Position = pos;
                    if (got != buf.Length || BitConverter.ToUInt32(buf, 0) != REVOKE_Q) return true;
                    string token = Encoding.ASCII.GetString(buf, 4, buf.Length - 4);
                    string h = Hash(token);
                    bool gone;
                    lock (s_Lock)
                    {
                        Load();
                        gone = s_ByHash.Remove(h);
                        if (gone) Save();
                    }
                    if (gone) Mod.Log.Info($"[RevivalGuard] login token revoked (Log Out) from {endPoint.Address}");
                    return false;                             // ours: never hand it to ACE
                }
                catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LoginTokens revoke: " + e.Message); return true; }
            }

            static bool BadSize(int size, long len) => size < 5 || size > 4 + 64 || len != size;
        }

        // ---- storage ---------------------------------------------------------------------------------

        static void Load()
        {
            if (s_ByHash != null) return;
            s_ByHash = new Dictionary<string, Entry>();
            try
            {
                s_File = Path.Combine(ModFolder.Path(), "LoginTokens.json");
                if (!File.Exists(s_File)) return;
                var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(s_File)) ?? new List<Entry>();
                var now = DateTime.UtcNow;
                foreach (var e in list)
                    if (!string.IsNullOrEmpty(e.Hash) && now - e.Used <= EXPIRY) s_ByHash[e.Hash] = e;
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] LoginTokens load: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                s_File ??= Path.Combine(ModFolder.Path(), "LoginTokens.json");
                string tmp = s_File + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_ByHash.Values.ToList()));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(tmp, s_File, true);
            }
            catch (Exception e) { Mod.Log.Error("[RevivalGuard] LoginTokens save: " + e.Message); }
        }

        static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        static string Fingerprint(Account a) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((a.PasswordHash ?? "") + "|" + (a.PasswordSalt ?? "")))).Substring(0, 32);

        static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
