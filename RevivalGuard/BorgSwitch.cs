using System;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE BORG'S OFF SWITCH (docs/proposals/the-borg.md; owner rule: an admin panel button, no typed commands). The Borg
    /// are the disclosed bot guild (tools/borg/borg.py) on the Hollow Company's allowlisted accounts (Mercenaries.IsMerc).
    /// Switch: the file borg-off beside the dll (no file = ON, so the mercs and Borg that run today keep running): "@borg off" logs every bot out within
    /// a second and keeps them out (a bot that logs in while it is off is logged out again two seconds after it enters the
    /// world); "@borg on" lets them back; "@borg" or "@borg status" says which, and who is in. Persisted by ACE's property
    /// folder, so a restart keeps it. Sentinel and up (the admin panel's switch runs it).
    ///
    /// The brains need no change: a Borg logged out by the server sits at character select, its brain finds it there and
    /// tries again later; while the switch is off every try ends the same way.
    /// </summary>
    public static class BorgSwitch
    {
        // A FILE, NOT AN ACE PROPERTY (shard2 test 2026-11-08: "rg_borg" set off by @borg was back on after a restart; ACE's
        // property table did not keep a key it does not define). borg-off beside the dll = off; no file = on.
        static string OffFile => System.IO.Path.Combine(ModFolder.Path(), "borg-off");

        public static bool On => !System.IO.File.Exists(OffFile);

        /// <summary>ONLY "... of Borg" COUNTS (Hollowtide session 2026-11-08): the Hollow Company's mercs (Brannoc and Sefa
        /// the Mercenary) are on the same allowlisted accounts, and stay untouched by /pop's Borg line and by @borg off
        /// until they are renamed "of Borg".</summary>
        public static bool IsBorgName(string name) =>
            name != null && name.TrimEnd().EndsWith(" of Borg", StringComparison.OrdinalIgnoreCase);

        public static bool IsBorg(Player p) => p != null && Mercenaries.IsMerc(p) && IsBorgName(p.Name);

        static void Set(bool on)
        {
            if (on) { if (System.IO.File.Exists(OffFile)) System.IO.File.Delete(OffFile); }
            else System.IO.File.WriteAllText(OffFile, DateTime.UtcNow.ToString("u") + "\n");
        }

        public static void Register()
        {
            CommandManager.TryAddCommand(Handle, "borg", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "The Borg (disclosed bot guild, characters named '... of Borg'): on, off (logs every Borg out and keeps them out), status.", "[on|off|status]");
        }

        static void Say(Session s, string line)
        {
            if (s == null) Console.WriteLine(line);
            else s.Network?.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        static string Online()
        {
            try
            {
                var n = PlayerManager.GetAllOnline().Where(IsBorg).Select(p => p.Name).OrderBy(x => x).ToList();
                return n.Count == 0 ? "none online" : string.Join(", ", n) + " online";
            }
            catch { return "?"; }
        }

        static void Handle(Session session, params string[] parameters)
        {
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";
            string who = session?.Player?.Name ?? "console";
            if (verb == "on" || verb == "off")
            {
                try { Set(verb == "on"); }
                catch (Exception e) { Say(session, "[Borg] could not change the switch: " + e.Message); return; }
                Mod.Log.Info($"[RevivalGuard] Borg: turned {verb} by {who}");
                if (verb == "off") WorldManager.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(BootAll));
            }
            Say(session, $"[Borg] {(On ? "on" : "off")}: {Online()}.");
        }

        static void BootAll()
        {
            try
            {
                foreach (var p in PlayerManager.GetAllOnline().Where(IsBorg).ToList())
                    Boot(p, "the Borg switch is off");
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] Borg boot: " + e.Message); }
        }

        internal static void Boot(Player p, string why)
        {
            Mod.Log.Info($"[RevivalGuard] Borg: logging out {p.Name} ({why})");
            // A WHOLE DISCONNECT, NOT A LOGOFF (shard2 test 2026-11-08): after LogOffPlayer the bot's client went on
            // thinking it was in the world. Booted, the client is told why and its re-login is refused at the login request
            // (AccountCreationCap's prefix asks RefuseLogin) for as long as the switch is off.
            if (p.Session != null) p.Session.Terminate(SessionTerminationReason.AccountBooted, new GameMessageBootAccount(" because the Borg are switched off"));
            else p.LogOut();
        }

        /// <summary>The login request of a bot account while the switch is off: refused (AccountCreationCap's prefix).</summary>
        internal static bool RefuseLogin(Session session, string account)
        {
            if (On || !Mercenaries.IsMercAccount(account)) return false;
            // a Borg account: one with a character named "... of Borg" (the mercs' accounts log in as before)
            try
            {
                var acct = ACE.Database.DatabaseManager.Authentication.GetAccountByName(account);
                if (acct == null) return false;
                var chars = ACE.Database.DatabaseManager.Shard.BaseDatabase.GetCharacters(acct.AccountId, false);
                if (chars == null || !chars.Any(c => IsBorgName(c.Name))) return false;
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] Borg login check: " + e.Message); return false; }
            Mod.Log.Info($"[RevivalGuard] Borg: login of {account} refused (the Borg switch is off)");
            session.Terminate(SessionTerminationReason.AccountBooted, new GameMessageBootAccount(" because the Borg are switched off"));
            return true;
        }

        /// <summary>A bot entering the world while the switch is off: out again two seconds later.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class KeepOut
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    if (On || !IsBorg(__instance)) return;
                    var p = __instance;
                    var chain = new ACE.Server.Entity.Actions.ActionChain();
                    chain.AddDelaySeconds(2);
                    chain.AddAction(p, () => Boot(p, "entered while the Borg switch is off"));
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn("[RevivalGuard] Borg keep-out: " + e.Message); }
            }
        }
    }
}
