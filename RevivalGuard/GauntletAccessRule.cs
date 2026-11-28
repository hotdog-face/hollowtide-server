namespace RevivalGuard
{
    /// <summary>GauntletAccess's rules, pure (no ACE types), so tools/nettest compiles and checks them by path.</summary>
    public static class GauntletAccessRule
    {
        /// <summary>The one line all three Gauntlet Arena portals (53025, 53249, 53250) send for every refusal.</summary>
        public const string AceRefusal = "You do not have access to this arena.";

        /// <summary>An arena portal's society and the fellowship quest stamps its Use emote checks</summary>
        public sealed class Arena
        {
            public readonly uint Wcid; public readonly string Society, Code;
            public Arena(uint wcid, string society, string code) { Wcid = wcid; Society = society; Code = code; }
            public string Stamp1 => "GauntletArena1" + Code;
            public string Stamp2 => "GauntletArena2" + Code;
            public string MemberFlag => Society.Replace(" ", "") + "Member";   // the Master's IsPlayerInMyFaction check
        }

        public static readonly Arena[] Arenas =
        {
            new Arena(53025, "Celestial Hand", "CH"),
            new Arena(53249, "Eldrytch Web", "EW"),
            new Arena(53250, "Radiant Blood", "RB"),
        };

        public static Arena ArenaFor(uint wcid)
        {
            foreach (var a in Arenas) if (a.Wcid == wcid) return a;
            return null;
        }

        /// <summary>The reason a player was refused, in the order the arena and the Master check it.</summary>
        public static string Reason(Arena a, bool inFellowship, bool stampLive, bool stampExpired, string otherSociety, bool member)
        {
            if (a == null || stampLive) return null;
            if (!inFellowship)
                return "You are not in a fellowship. The Gauntlet only lets in the fellowship that gave a ticket to the Master of the Gauntlet: "
                     + "rejoin that fellowship, or form one and give him a ticket, then use the arena again.";
            if (stampExpired)
                return "Your fellowship's hour in the Gauntlet is over. Give the Master of the Gauntlet a new ticket to go again.";
            if (otherSociety != null)
                return $"Your fellowship's ticket is for the {otherSociety} arena, not this {a.Society} one. Use the {otherSociety} Gauntlet Arena.";
            if (!member)
                return $"This arena is for the {a.Society}, and your fellowship has no {a.Society} ticket. A {a.Society} member must give a "
                     + $"Gauntlet Ticket to the Master of the Gauntlet beside this arena while in your fellowship, or a fellowship already inside can give you a guest ticket.";
            return $"Your fellowship has not turned in a ticket for this arena. Give a Gauntlet Ticket to the Master of the Gauntlet beside it while you are in "
                 + "this fellowship, then use the arena. A ticket belongs to the fellowship it was given in: a new or re-formed fellowship needs its own.";
        }
    }
}
