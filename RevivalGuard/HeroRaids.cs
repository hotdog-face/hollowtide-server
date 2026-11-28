using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    internal static class HeroRaids
    {
        static readonly HashSet<string> KEYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hero-knath-hebian", "hero-apostate-hebian" };
        const uint TOKEN = 35810;                        // Hero Token
        const string FLAG = "HeroRaidToken";

        internal static bool IsHeroRaid(string key) => key != null && KEYS.Contains(key);

        /// <summary>A creature from one of the two fights' generators.</summary>
        internal static bool IsRaider(Creature c)
        {
            var row = c?.Generator?.GeneratorEvent;
            if (string.IsNullOrEmpty(row)) return false;
            row = ACE.Server.Managers.EventManager.GetEventName(row);
            return "DirectorHeroKnath".Equals(row, StringComparison.OrdinalIgnoreCase)
                || "DirectorHeroApostate".Equals(row, StringComparison.OrdinalIgnoreCase);
        }

        internal static void Won(string key, List<Player> near)
        {
            int given = 0, had = 0;
            foreach (var p in near.Where(x => x != null && !Mercenaries.IsMerc(x)))
            {
                if (!p.QuestManager.CanSolve(FLAG))
                {
                    had++;
                    p.SendMessage("You have had a Hero Token from these raids in the last day; this one goes to the others.", ChatMessageType.Broadcast);
                    continue;
                }
                p.QuestManager.Stamp(FLAG);
                p.SendMessage("For holding Hebian-To you have earned a Hero Token. Pevilo ibn Djimin in Arwic trades for them.", ChatMessageType.Broadcast);
                WorldBossSchedule.Grant(p.Guid.Full, TOKEN, "the Hero Token raid at Hebian-To");
                given++;
            }
            Mod.Log.Info($"[RevivalGuard] HeroRaids: {key} held; {given} Hero Token(s) given, {had} already had one in the last 20 h");
        }
    }
}
