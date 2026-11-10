using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// AERBAX'S PRODIGAL CHILDREN (retail live event, Jan 7-13 2008; revived Feb 9 - 15 yearly, docs/EVENT-TESTS/prodigal.md).
    /// The waves are the EventDirector entry "prodigal-children"; this is only the reward. Retail handed every participant a
    /// Modified Hero Token "at the completion of the Live Event" (wiki "Miniature Hollow Minion Chest"); here everyone near the
    /// site when the last wave's Queen dies gets one, through the world-boss reward queue (into the pack, or owed until the
    /// next login), once per character per window (quest ProdigalChildrenToken, about 11 days). Mercenaries get nothing.
    /// The token goes to Vincadi in Old Yanshi for the Miniature Hollow Minion Chest and the title Unwitting Participant.
    /// The weenie and quest row come from 2027-02-09-prodigal-1-children.sql; without them the entry never runs (Pending).
    /// </summary>
    internal static class ProdigalChildren
    {
        internal const string KEY = "prodigal-children";
        const uint TOKEN = 903741;                       // Modified Hero Token
        const string FLAG = "ProdigalChildrenToken";

        internal static void Won(List<Player> near)
        {
            int given = 0, had = 0;
            foreach (var p in near.Where(x => x != null && !Mercenaries.IsMerc(x)))
            {
                if (!p.QuestManager.CanSolve(FLAG))
                {
                    had++;
                    p.SendMessage("Aerbax has already marked you in this test; no second Modified Hero Token is yours.", ChatMessageType.Broadcast);
                    continue;
                }
                p.QuestManager.Stamp(FLAG);
                p.SendMessage("Vincadi, Aerbax's servant in Old Yanshi, will want to see the Modified Hero Token you were given.", ChatMessageType.Broadcast);
                WorldBossSchedule.Grant(p.Guid.Full, TOKEN, "Aerbax's Prodigal Children");
                given++;
            }
            Mod.Log.Info($"[RevivalGuard] ProdigalChildren: won; {given} Modified Hero Token(s) given, {had} already had one this window");
        }
    }
}
