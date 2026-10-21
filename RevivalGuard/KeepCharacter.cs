using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>
    /// @KEEP: A BROWSER GUEST KEEPS ITS CHARACTERS (docs/BROWSER-CLIENT-PLAN.md, "One click to play").
    ///
    /// The browser makes a guest account by itself ("guest" and ten letters and digits, a random password kept in
    /// that browser), so a player is in the world with one click. Until they keep it, their characters live only in
    /// that browser, and a guest unused for 30 days is deleted (owner, 2026-10-10). `@keep <account> <password>`
    /// turns the guest account into an ordinary one: the account is renamed and given the password. Characters
    /// belong to the account by id, so they come along untouched, and the name logs in from any device or the
    /// desktop client. The session that asked is renamed with it (see the SetAccount note below).
    ///
    /// Only a guest account may do this, so no player can rename an account that already is somebody's. The new
    /// name is 3 to 24 letters and digits, starts with a letter, is not taken, and may not itself look like a
    /// guest's. The password is 6 to 64 characters, no spaces. Player-level commands are not recorded by
    /// AuditTrail, so the password goes into no log. One attempt every 3 seconds per session.
    ///
    /// The browser client sends it from its "Keep your character" form and reads the answer, which starts with
    /// <see cref="REPLY"/> ("[Keep] OK name" or "[Keep] reason").
    /// </summary>
    public static class KeepCharacter
    {
        public const string REPLY = "[Keep] ";
        static readonly Regex NAME = new Regex("^[a-z][a-z0-9]{2,23}$", RegexOptions.Compiled);
        static readonly ConcurrentDictionary<uint, DateTime> s_Last = new ConcurrentDictionary<uint, DateTime>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "keep", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "A browser guest keeps its characters: the guest account becomes an ordinary one with this name and password.",
                "<account name> <password>");
            Mod.Log.Info("[RevivalGuard] KeepCharacter: @keep registered (Player; guest accounts only)");
        }

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(REPLY + text, ChatMessageType.Broadcast));

        static void Handle(Session session, params string[] p)
        {
            if (session == null) return;
            var now = DateTime.UtcNow;
            if (s_Last.TryGetValue(session.AccountId, out var last) && (now - last).TotalSeconds < 3) { Say(session, "Wait a moment and try again."); return; }
            s_Last[session.AccountId] = now;

            if (p == null || p.Length != 2) { Say(session, "Usage: @keep <account name> <password>"); return; }
            string name = p[0].Trim().ToLowerInvariant(), pw = p[1];
            try
            {
                var account = DatabaseManager.Authentication.GetAccountById(session.AccountId);
                if (account == null) { Say(session, "Your account could not be read."); return; }
                if (!account.AccountName.StartsWith("guest", StringComparison.OrdinalIgnoreCase)) { Say(session, "This account is already yours to keep; nothing to do."); return; }
                if (!NAME.IsMatch(name)) { Say(session, "Choose 3 to 24 letters and digits, starting with a letter."); return; }
                if (name.StartsWith("guest")) { Say(session, "Choose a name that does not start with \"guest\"."); return; }
                // the reserved prefixes (AccountCreationCap refuses them at login; a kept guest must not slip one in)
                if (Mercenaries.IsReservedName(name))
                {
                    Mod.Log.Warn($"[RevivalGuard] KeepCharacter: '{account.AccountName}' asked for the reserved name '{name}'; refused");
                    Say(session, "That account name is reserved; choose another.");
                    return;
                }
                if (pw.Length < 6 || pw.Length > 64) { Say(session, "Choose a password of 6 to 64 characters, no spaces."); return; }
                if (DatabaseManager.Authentication.GetAccountByName(name) != null) { Say(session, "That account name is taken; choose another."); return; }

                string was = account.AccountName;
                account.AccountName = name;
                account.SetPassword(pw);
                DatabaseManager.Authentication.UpdateAccount(account);
                // THE LIVE SESSION TAKES THE NEW NAME TOO (2026-10-16: Levistras and Deadlyflorists could not get back
                // in). ACE's session keeps the name it logged in with, and three things read it: the login takeover
                // (NetworkManager.Find(accountName)) boots the account's old session when it logs in again, and by the
                // new name it found none, so a character left standing by the browser tab stayed "in the world"
                // (CharacterError 0x0D, a portal loop on the desktop app) for as long as that tab lived; the
                // CharacterEnterWorld/Create/Delete checks (clientString != session.Account) refuse the name the
                // client now sends; LoginTokens looks the account up by it.
                session.SetAccount(account.AccountId, account.AccountName, session.AccessLevel);
                Mod.Log.Info($"[RevivalGuard] KeepCharacter: guest account '{was}' kept as '{name}'");
                Telemetry.Emit("keep", session.Player, ",\"web\":1");   // the funnel's "kept their character"
                Say(session, "OK " + name);
            }
            catch (Exception e)
            {
                Mod.Log.Error("[RevivalGuard] KeepCharacter: " + e.Message);
                Say(session, "The account could not be saved; try again later.");
            }
        }
    }
}
