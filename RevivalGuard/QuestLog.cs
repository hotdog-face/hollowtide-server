using System;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>
    /// @questlog: the player's own quest registry as machine-readable lines, for the client's quest
    /// journal (AcJournalHud; docs/PLAYER-FEATURES-2026-09-24.md section 3). Ours.
    ///
    /// ACE's /myquests prints the same registry as free text and is off on this shard
    /// (quest_info_enabled). This is read-only, answers only about the caller, and is open to every
    /// player: it reveals nothing a player's own client did not already cause. Each line is
    ///     \u0007QL|flag|solves|lastSolvedUnix|maxSolves|minDeltaSeconds|message
    /// and the list ends with \u0007QL|END. The client hides every \u0007QL line from chat.
    /// </summary>
    public static class QuestLog
    {
        const string TAG = "\u0007QL|";

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "questlog", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Your quest flags and timers, for the client's journal.", "");
        }

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            // OVER THE MOD CHANNEL when this connection reads it (ModChannel): one message, a JSON
            // array of [flag, solves, lastSolvedUnix, maxSolves, minDeltaSeconds, message].
            if (ModChannel.Capable(session))
            {
                var sb = new System.Text.StringBuilder("[");
                int n = 0;
                foreach (var q in p.QuestManager.GetQuests())
                {
                    string qn = QuestManager.GetQuestName(q.QuestName);
                    var d = DatabaseManager.World.GetCachedQuest(qn);
                    uint md = d?.MinDelta ?? 0;
                    if (d != null && QuestManager.CanScaleQuestMinDelta(d))
                        md = (uint)(d.MinDelta * PropertyManager.GetDouble("quest_mindelta_rate").Item);
                    if (n++ > 0) sb.Append(',');
                    sb.Append('[').Append(Json(q.QuestName)).Append(',').Append(q.NumTimesCompleted).Append(',')
                      .Append(q.LastTimeCompleted).Append(',').Append(d?.MaxSolves ?? -1).Append(',').Append(md).Append(',')
                      .Append(Json(d?.Message ?? "")).Append(']');
                }
                sb.Append(']');
                ModChannel.Send(session, "questlog", sb.ToString());
                return;
            }
            int sent = 0;
            foreach (var q in p.QuestManager.GetQuests())
            {
                string name = QuestManager.GetQuestName(q.QuestName);
                var def = DatabaseManager.World.GetCachedQuest(name);
                uint minDelta = def?.MinDelta ?? 0;
                if (def != null && QuestManager.CanScaleQuestMinDelta(def))
                    minDelta = (uint)(def.MinDelta * PropertyManager.GetDouble("quest_mindelta_rate").Item);
                string msg = (def?.Message ?? "").Replace("|", "/").Replace("\n", " ");
                Send(session, $"{TAG}{q.QuestName}|{q.NumTimesCompleted}|{q.LastTimeCompleted}|{def?.MaxSolves ?? -1}|{minDelta}|{msg}");
                if (++sent >= 2000) break;
            }
            Send(session, TAG + "END");
        }

        static string Json(string v)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in v ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static void Send(Session s, string line) =>
            s.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
    }
}
