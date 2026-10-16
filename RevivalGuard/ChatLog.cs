using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Network.Handlers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// PUBLIC CHAT, KEPT FOR READING BUG REPORTS OUT OF IT (owner 2026-10-16: "lots of good bugs coming in through chat in
    /// discord and in game chat. can we log in game chat so you can review it for bugs too?").
    ///
    /// One line per message in ~/ace/reports/chat/&lt;yyyy-MM-dd&gt;.log (UTC): the public channels (General, Trade, LFG,
    /// Roleplay, Society, Olthoi), speech said aloud nearby, and what Discord's #in-game said into General. NOT tells,
    /// NOT allegiance or fellowship chat: those are private. Admins (a '+' name) are included, mercenaries are not.
    ///
    /// ACE has its own switches for this (chat_log_general and the rest, into the server log), but they cover the
    /// channels only, not speech, and need console commands to set; this is one file per day beside the tickets.
    /// </summary>
    static class ChatLog
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports", "chat");
        static readonly object s_Lock = new object();

        public static void Write(string channel, string who, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var now = DateTime.UtcNow;
                string line = $"{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} [{channel}] {who ?? "?"}: {text.Replace('\n', ' ').Replace('\r', ' ')}\n";
                lock (s_Lock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(Path.Combine(Dir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"), line);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChatLog: {e.Message}"); }
        }

        /// <summary>The channel's name for a public ChatType, or null for one that is not logged.</summary>
        internal static string ChannelName(ChatType t)
        {
            switch (t)
            {
                case ChatType.General: return "General";
                case ChatType.Trade: return "Trade";
                case ChatType.LFG: return "LFG";
                case ChatType.Roleplay: return "Roleplay";
                case ChatType.Olthoi: return "Olthoi";
                case ChatType.Society:
                case ChatType.SocietyCelHan:
                case ChatType.SocietyEldWeb:
                case ChatType.SocietyRadBlo: return "Society";
                default: return null;   // Allegiance and anything else: private or unknown
            }
        }
    }

    /// <summary>The public channels, as ACE's own LogTurbineChat sees them (it runs for every channel message).</summary>
    [HarmonyPatch]
    static class ChatLogChannels
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(TurbineChatHandler), "LogTurbineChat");
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Postfix(uint channelID, string name, string message, uint senderID, ChatType chatType)
        {
            try
            {
                string ch = ChatLog.ChannelName(chatType);
                if (ch == null) return;
                var p = ACE.Server.Managers.PlayerManager.GetOnlinePlayer(senderID);
                if (p != null && Mercenaries.IsMerc(p)) return;
                ChatLog.Write(ch, name, message);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChatLog channels: {e.Message}"); }
        }
    }

    /// <summary>Speech said aloud nearby (the chat box with no channel).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionTalk))]
    static class ChatLogSpeech
    {
        static void Postfix(Player __instance, string message)
        {
            try
            {
                if (__instance == null || __instance.IsGagged || Mercenaries.IsMerc(__instance)) return;
                ChatLog.Write("Say", __instance.Name, message);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ChatLog speech: {e.Message}"); }
        }
    }
}
