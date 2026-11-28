using System;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Fellowship passwords.</summary>
    [HarmonyPatch(typeof(Fellowship), nameof(Fellowship.AssignNewLeader))]
    public static class FellowPassword
    {
        internal const string P_ON = "rg_fellow_password";
        internal static readonly FellowPasswordRule Rule = new FellowPasswordRule();

        sealed class Entry { public string Password; public uint Leader; }
        static readonly ConditionalWeakTable<Fellowship, Entry> s_Passwords = new ConditionalWeakTable<Fellowship, Entry>();

        static bool On => PropertyManager.GetBool(P_ON, true).Item;

        public static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard FellowPassword: /fpass <word> and /fjoin <leader> <password> (false: off)");
            CommandManager.TryAddCommand(HandlePass, "fpass", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Fellowship password: the leader sets one (shown to the fellowship); off clears it; alone shows it.",
                "<word> | off");
            CommandManager.TryAddCommand(HandleJoin, "fjoin", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Join a fellowship from anywhere with its password.",
                "<leader> <password>");
            Mod.Log.Info($"[RevivalGuard] FellowPassword: {(On ? "on" : "off")} ({P_ON}); /fpass | /fjoin");
        }

        internal static string PasswordOf(Fellowship f)
        {
            if (f == null || !s_Passwords.TryGetValue(f, out var e)) return null;
            if (e.Leader != f.FellowshipLeaderGuid) { s_Passwords.Remove(f); return null; }
            return e.Password;
        }

        internal static void Drop(Fellowship f) => s_Passwords.Remove(f);

        static void Say(Session s, string text) => s?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void HandlePass(Session session, params string[] parameters) => Handle(session, parameters, SetPassword);
        static void HandleJoin(Session session, params string[] parameters) => Handle(session, parameters, Join);

        static void Handle(Session session, string[] parameters, Action<Session, Player, string> verb)
        {
            var p = session?.Player;
            if (p == null) return;
            try
            {
                if (!On) { Say(session, FellowPasswordRule.Line(FellowPasswordRule.Outcome.Off, "", 0)); return; }
                verb(session, p, string.Join(" ", parameters ?? Array.Empty<string>()).Trim());
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] FellowPassword: {e.Message}");
                Say(session, "That did not work. Try again in a moment.");
            }
        }

        internal static void SetPassword(Session session, Player p, string rest)
        {
            var f = p.Fellowship;
            if (f == null) { Say(session, "You are not in a fellowship."); return; }
            if (rest.Length == 0)
            {
                string cur = PasswordOf(f);
                Say(session, cur == null ? "Your fellowship has no password." : $"Your fellowship's password is {cur}. Others join with /fjoin {LeaderName(f)} {cur}");
                return;
            }
            if (p.Guid.Full != f.FellowshipLeaderGuid) { Say(session, "Only the fellowship's leader can set its password."); return; }
            if (rest.Equals("off", StringComparison.OrdinalIgnoreCase) || rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (PasswordOf(f) == null) { Say(session, "Your fellowship has no password."); return; }
                s_Passwords.Remove(f);
                f.BroadcastToFellow($"{p.Name} cleared the fellowship password.");
                Mod.Log.Info($"[RevivalGuard] FellowPassword: {p.Name} cleared the password of {f.FellowshipName}");
                return;
            }
            if (Mercenaries.IsMerc(p)) { Say(session, "Mercenaries and Borg do not take fellowship passwords."); return; }
            string word = FellowPasswordRule.Clean(rest);
            if (word == null) { Say(session, $"A fellowship password is one word of up to {FellowPasswordRule.MAX_LEN} letters, digits or symbols."); return; }
            s_Passwords.AddOrUpdate(f, new Entry { Password = word, Leader = p.Guid.Full });
            f.BroadcastToFellow($"{p.Name} set the fellowship password to {word}. Anyone can join with /fjoin {p.Name} {word}");
            Mod.Log.Info($"[RevivalGuard] FellowPassword: {p.Name} set a password on {f.FellowshipName}");
        }

        static string LeaderName(Fellowship f) =>
            PlayerManager.GetOnlinePlayer(f.FellowshipLeaderGuid)?.Name ?? "<leader>";

        static void Join(Session session, Player p, string rest)
        {
            if (!FellowPasswordRule.SplitJoin(rest, out string name, out string given)) { Say(session, "Type /fjoin <leader> <password>"); return; }
            var leader = PlayerManager.GetOnlinePlayer(name);
            if (leader == null && !name.StartsWith("+")) leader = PlayerManager.GetOnlinePlayer("+" + name);
            TryJoin(session, p, leader, name, given, false);
        }

        /// <summary>A merc or Borg leads or rides in this fellowship.</summary>
        internal static bool HasBot(Fellowship f)
        {
            if (f == null) return false;
            // Borg are merc-account characters (BorgRule.IsBorg), so IsMerc covers both.
            foreach (var m in f.GetFellowshipMembers().Values)
                if (m != null && Mercenaries.IsMerc(m)) return true;
            return Mercenaries.IsMerc(PlayerManager.GetOnlinePlayer(f.FellowshipLeaderGuid));
        }

        /// <summary>Join leader's fellowship as if they had recruited p and they had said yes</summary>
        internal static FellowPasswordRule.Outcome TryJoin(Session session, Player p, Player leader, string name, string given, bool open)
        {
            if (!On && !open) { Say(session, FellowPasswordRule.Line(FellowPasswordRule.Outcome.Off, "", 0)); return FellowPasswordRule.Outcome.Off; }
            var lf = leader?.Fellowship;
            bool leads = lf != null && lf.FellowshipLeaderGuid == leader.Guid.Full;
            string pw = leads ? PasswordOf(lf) : null;
            // A leader hidden by Appear Offline reads as offline unless they set a password or listed the fellowship (they asked to be found).
            bool hidden = leader != null && leader.GetAppearOffline() && pw == null && !open && session.AccessLevel < AccessLevel.Sentinel;
            var facts = new FellowPasswordRule.Facts
            {
                On = true,
                Open = open && leads,
                JoinerIsLeader = leader != null && leader.Guid.Full == p.Guid.Full,
                JoinerInLeadersFellowship = lf != null && p.Fellowship == lf,
                JoinerInFellowship = p.Fellowship != null,
                JoinerIsOlthoi = p.IsOlthoiPlayer,
                LeaderOnline = leader != null && leader.Session != null && !hidden,
                Password = pw,
                LeaderRefused = leader != null && (Mercenaries.IsMerc(leader) || (leads && HasBot(lf))),
                Locked = lf != null && lf.IsLocked,
                Members = lf?.FellowshipMembers.Count ?? 0,
                Max = Fellowship.MaxFellows,
            };
            string shown = leader != null && !hidden ? leader.Name : name;
            var o = Rule.Decide(p.Guid.Full, facts, given, DateTime.UtcNow);
            if (o != FellowPasswordRule.Outcome.Ok)
            {
                Say(session, FellowPasswordRule.Line(o, shown, Fellowship.MaxFellows));
                if (o == FellowPasswordRule.Outcome.Wrong) Mod.Log.Info($"[RevivalGuard] FellowPassword: {p.Name} gave a wrong password for {leader?.Name}'s fellowship");
                return o;
            }
            lf.AddConfirmedMember(leader, p, true);
            if (p.Fellowship != lf) { Say(session, FellowPasswordRule.Line(FellowPasswordRule.Outcome.Full, shown, Fellowship.MaxFellows)); return FellowPasswordRule.Outcome.Full; }
            Say(session, FellowPasswordRule.Line(o, shown, Fellowship.MaxFellows));
            lf.BroadcastToFellow(facts.Open ? $"{p.Name} joined from the Looking for Group list." : $"{p.Name} joined with the fellowship password.");
            Mod.Log.Info($"[RevivalGuard] FellowPassword: {p.Name} joined {leader.Name}'s fellowship {(facts.Open ? "from the LFG list (open)" : "by password")} ({lf.FellowshipMembers.Count} members)");
            return o;
        }

        // A new leader (passed on, or picked when the leader left) clears the password: it was the old leader's.
        static void Prefix(Fellowship __instance, out uint __state) => __state = __instance?.FellowshipLeaderGuid ?? 0;

        static void Postfix(Fellowship __instance, uint __state)
        {
            try
            {
                if (__instance == null || __instance.FellowshipLeaderGuid == __state) return;
                if (!s_Passwords.TryGetValue(__instance, out _)) return;
                s_Passwords.Remove(__instance);
                __instance.BroadcastToFellow("The fellowship password is cleared: the fellowship has a new leader.");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FellowPassword: new leader: {e.Message}"); }
        }
    }

    /// <summary>A disband drops the password at once (the object may live on a while in a weak table).</summary>
    [HarmonyPatch(typeof(Fellowship), nameof(Fellowship.QuitFellowship))]
    static class FellowPasswordDisband
    {
        static void Prefix(Fellowship __instance, Player player, bool disband)
        {
            try
            {
                if (__instance != null && disband && player != null && player.Guid.Full == __instance.FellowshipLeaderGuid)
                    FellowPassword.Drop(__instance);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FellowPassword: disband: {e.Message}"); }
        }
    }
}
