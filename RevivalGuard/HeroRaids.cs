using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// THE 2009 HERO TOKEN RAIDS (retail live events Mar 16 and Apr 1 2009; revived Mar 14 - 28 yearly,
    /// docs/EVENT-TESTS/hero-raids.md). The fights are the EventDirector entries "hero-knath-hebian" and "hero-apostate-hebian";
    /// this is only the reward. Retail rained Hero Tokens on the ground at the end of each developer-run fight, and mules
    /// picked them up (the 2009 page's complaint); here everyone near the site when the hour ends with the town held gets one
    /// Hero Token through the world-boss reward queue (into the pack, or owed until the next login), once per character
    /// every 20 hours (quest HeroRaidToken). Mercenaries get nothing. Tokens buy from Pevilo ibn Djimin in Arwic.
    /// The quest row comes from 2027-03-14-heroraids-1-hebian.sql; without it the entries never run (Pending).
    /// </summary>
    internal static class HeroRaids
    {
        static readonly HashSet<string> KEYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hero-knath-hebian", "hero-apostate-hebian" };
        const uint TOKEN = 35810;                        // Hero Token
        const string FLAG = "HeroRaidToken";

        internal static bool IsHeroRaid(string key) => key != null && KEYS.Contains(key);

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
