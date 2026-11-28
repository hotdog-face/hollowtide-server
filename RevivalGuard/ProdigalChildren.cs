using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
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
