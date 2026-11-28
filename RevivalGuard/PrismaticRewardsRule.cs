using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>The pure half of PrismaticRewards (no ACE types; tools/nettest `prismatic`).</summary>
    public static class PrismaticRewardsRule
    {
        public const string P_ON = "rg_prismatic_rewards";
        public const uint PIECE0 = 918000;
        public static readonly string[] SLOT = { "Helm", "Breastplate", "Pauldrons", "Bracers", "Gauntlets", "Girth", "Tassets",
                                                 "Greaves", "Sollerets", "Shield", "Cloak" };

        public static uint Piece(string slot) => PIECE0 + (uint)Array.IndexOf(SLOT, slot);

        public sealed class Deed
        {
            public string Key, What;          // What: "for {What}" in the player's line
            public uint[] Items;
            /// <summary>Any of these quest flags (name before any @), stamped: the deed is done.</summary>
            public string[] Quests = new string[0];
            /// <summary>Non-zero: the flag is a bit field and the deed is done when these bits are all set.</summary>
            public int Bits;
            /// <summary>Or: one of these bosses died with the player on its damage list (or its killer's fellows there).</summary>
            public uint[] Bosses = new uint[0];
        }

        public static readonly Deed[] Deeds =
        {
            new Deed { Key = "markers", What = "finding all 100 of Sean the Speedy's Exploration Markers", Items = new[] { Piece("Helm") },
                       Quests = new[] { "ExplorationMarkersGroups" }, Bits = 512 },          // Sean's 100x emote sets bit 512
            new Deed { Key = "aerbax", What = "defeating Aerbax's Shadow", Items = new[] { Piece("Pauldrons") },
                       Quests = new[] { "aerbaxshadowdefeatedrepeat" } },                    // the Emissary of Asheron's reward
            new Deed { Key = "bael", What = "the fall of Bael'Zharon", Items = new[] { Piece("Breastplate") },
                       Bosses = new uint[] { 900200 } },                                     // WorldBoss.BAEL, the Direlands world boss
            new Deed { Key = "gauntlet", What = "beating the Gauntlet", Items = new[] { Piece("Gauntlets") },
                       Quests = new[] { "GauntletWinsCounter" } },                           // Master of the Gauntlet, stage 12
            new Deed { Key = "colosseum", What = "all eighteen rounds of the Colosseum", Items = new[] { Piece("Shield") },
                       Quests = new[] { "CompletedColosseum" } },                            // the Master Arbitrator, round 18
            new Deed { Key = "hoshino", What = "defeating Hoshino Kei", Items = new[] { Piece("Tassets") },
                       Quests = new[] { "DefeatHoshinoKeiCompleted_0113" } },                // the Spirit of Bist'elle
            new Deed { Key = "viridian", What = "climbing Viridian Rise", Items = new[] { Piece("Sollerets") },
                       Quests = new[] { "ClimbedViridianDeru" } },                           // Doriathazaar at the top
            new Deed { Key = "harbinger", What = "slaying the Harbinger", Items = new[] { Piece("Bracers") },
                       Bosses = new uint[] { 24856, 24857, 33231, 36178 } },                 // level 999; not 71239 (the weakened waves')
            new Deed { Key = "tthuun", What = "the Tentacles of T'thuun", Items = new[] { Piece("Girth") },
                       Quests = new[] { "TentaclesTthuunComplete0408" } },                   // Merwart Mundagurg's hand-in
            new Deed { Key = "martine", What = "slaying Martine", Items = new[] { Piece("Greaves") },
                       Bosses = new uint[] { 20028, 20917, 21137, 21138, 21139, 21140, 21275, 21276, 21277, 21278 } },   // the level 750s
            new Deed { Key = "gaerlan", What = "the defeat of Gaerlan", Items = new[] { Piece("Cloak") },
                       Quests = new[] { "GaerlanDefeated" } },                               // the Phylacteric Prison
        };

        /// <summary>The once-per-character flag of one reward item.</summary>
        public static string Given(uint wcid) => "PrismaticGiven" + wcid;
        /// <summary>Held back by a full pack, handed over at the next login.</summary>
        public static string Owed(uint wcid) => "PrismaticOwed" + wcid;

        static string Name(string q) { int at = q?.IndexOf('@') ?? -1; return at >= 0 ? q.Substring(0, at) : q ?? ""; }

        /// <summary>The deeds this quest flag's change from before to after completes anew</summary>
        public static IEnumerable<Deed> ForChange(string quest, int before, int after)
        {
            string n = Name(quest);
            foreach (var d in Deeds)
                if (d.Quests.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))
                    && (d.Bits == 0 ? after > before && after >= 1 : (before & d.Bits) != d.Bits && (after & d.Bits) == d.Bits))
                    yield return d;
        }

        public static bool WatchesQuest(string quest)
        {
            string n = Name(quest);
            return Deeds.Any(d => d.Quests.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)));
        }

        public static Deed ForBoss(uint wcid) => Deeds.FirstOrDefault(d => d.Bosses.Contains(wcid));

        /// <summary>What the player is told (no em dashes in player text).</summary>
        public static string Line(string item, Deed d) => $"For {d.What}, you are given the {item}. It is yours alone.";
    }
}
