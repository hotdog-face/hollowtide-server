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
    /// <summary>@KEEP: a browser guest keeps its characters (docs/BROWSER-CLIENT-PLAN.md, "One click to play").</summary>
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
                // The live session takes the new name too.
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
