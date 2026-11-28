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
    /// <summary>Public chat, kept for reading bug reports out of it.</summary>
    static class ChatLog
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports", "chat");

        public static void Write(string channel, string who, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var now = DateTime.UtcNow;
                string line = $"{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} [{channel}] {who ?? "?"}: {text.Replace('\n', ' ').Replace('\r', ' ')}";
                LogFiles.Shared.Enqueue(Path.Combine(Dir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"), line);
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

    static class LogFiles
    {
        public static readonly BackgroundAppender Shared = Make();

        static BackgroundAppender Make()
        {
            var a = new BackgroundAppender(m => Mod.Log.Warn($"[RevivalGuard] log writer: {m}"));
            try { AppDomain.CurrentDomain.ProcessExit += (_, __) => a.Flush(2000); } catch { }
            return a;
        }
    }
}
