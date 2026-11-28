using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    public static class Mercenaries
    {
        const string PREFIX = "hcmerc";

        /// <summary>Account-name prefixes no player may take (AccountCreationCap at login, KeepCharacter at @keep).</summary>
        public static bool IsReservedName(string accountName) =>
            accountName != null && accountName.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase);

        static readonly string[] BUILT_IN = { "hcmerc1", "hcmerc2" };
        static HashSet<string> s_Allow;

        static HashSet<string> Allow()
        {
            if (s_Allow != null) return s_Allow;
            var set = new HashSet<string>(BUILT_IN, StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = System.IO.Path.Combine(ModFolder.Path(), "mercenaries.txt");
                if (System.IO.File.Exists(path))
                    foreach (var raw in System.IO.File.ReadAllLines(path))
                    {
                        string n = raw.Trim();
                        if (n.Length == 0 || n.StartsWith("#")) continue;
                        if (IsReservedName(n)) set.Add(n);
                        else Mod.Log.Warn($"[RevivalGuard] Mercenaries: mercenaries.txt names '{n}', not an {PREFIX}* account; ignored");
                    }
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] Mercenaries: mercenaries.txt: " + e.Message); }
            Mod.Log.Info("[RevivalGuard] Mercenaries: " + string.Join(", ", set.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
            return s_Allow = set;
        }

        public static bool IsMerc(Player p) => IsMercAccount(p?.Account?.AccountName);

        /// <summary>By account name, for a character that may be offline (the PK ladder's re-judge).</summary>
        public static bool IsMercAccount(string name) => name != null && IsReservedName(name) && Allow().Contains(name);

        /// <summary>A Hollow Company sellsword, not a Borg (BorgRule.IsHollowCompany): what PK leaves out.</summary>
        public static bool IsHollowCompany(Player p) => p != null && BorgRule.IsHollowCompany(IsMerc(p), p.Name);

        /// <summary>By account and character name, for a character that may be offline (the PK ladder's re-judge).</summary>
        public static bool IsHollowCompanyAccount(string accountName, string characterName) =>
            BorgRule.IsHollowCompany(IsMercAccount(accountName), characterName);

        public static int RealOnline()
        {
            try { return PlayerManager.GetAllOnline().Count(p => !IsMerc(p)); }
            catch { return PlayerManager.GetOnlineCount(); }
        }

        public static int ShownOnline()
        {
            try { return PlayerManager.GetAllOnline().Count(p => !IsMerc(p) || BorgSwitch.IsBorg(p)); }
            catch { return PlayerManager.GetOnlineCount(); }
        }

        public static int MercsOnline()
        {
            try { return PlayerManager.GetAllOnline().Count(IsMerc); }
            catch { return 0; }
        }

        // /mercfollow: a sellsword rejoins its leader
        const float CATCHUP_MIN = 15f;

        public static void Register()
        {
            CommandManager.TryAddCommand(HandleFollow, "mercfollow", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Hollow Company only: rejoin your fellowship leader.", "");
        }

        static void HandleFollow(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null || !IsMerc(p)) return;
            var f = p.Fellowship;
            if (f == null || f.FellowshipLeaderGuid == p.Guid.Full) return;
            var leader = PlayerManager.GetOnlinePlayer(f.FellowshipLeaderGuid);
            if (leader?.Location == null || p.Location == null || leader.Teleporting) return;
            bool far = leader.Location.LandblockId.Landblock != p.Location.LandblockId.Landblock
                    || leader.Location.DistanceTo(p.Location) > CATCHUP_MIN;
            if (!far) return;
            var to = new ACE.Entity.Position(leader.Location);
            p.Teleport(to);
            Mod.Log.Info($"[RevivalGuard] {p.Name} rejoined {leader.Name} (mercfollow)");
        }
    }

    /// <summary>/pop: WHO is online, not only how many.</summary>
    [HarmonyPatch(typeof(PlayerCommands), nameof(PlayerCommands.HandlePop))]
    public static class MercenaryPop
    {
        static bool Prefix(Session session)
        {
            // The borg are listed and counted with the players: one sorted list, the "of Borg" names tell them apart.
            List<string> names;
            try { names = PlayerManager.GetAllOnline().Where(p => !Mercenaries.IsMerc(p) || BorgSwitch.IsBorg(p)).Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { names = new List<string>(); }
            void Say(string line)
            {
                if (session == null) Console.WriteLine(line);
                else session.Network?.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
            }
            Say($"Current world population: {names.Count:N0}" + (names.Count > 0 ? " (" + string.Join(", ", names) + ")" : ""));
            return false;
        }
    }
}
