using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>The pure part of a fellowship's listing on the Looking for Group board.</summary>
    public static class LfgListingRule
    {
        public const string OPEN = "open", PASSWORD = "password", REQUEST = "request";

        /// <summary>A mode word as typed ("open", "pw", "Request"...), or null when it is none.</summary>
        public static string Parse(string word)
        {
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "open": case "anyone": return OPEN;
                case "password": case "pass": case "pw": return PASSWORD;
                case "request": case "ask": case "invite": return REQUEST;
                default: return null;
            }
        }

        public static string Effective(string chosen, bool hasPassword)
        {
            if (chosen == OPEN) return OPEN;
            if (chosen == PASSWORD && hasPassword) return PASSWORD;
            return REQUEST;
        }

        /// <summary>The lowest and highest level among the members (1, 1 for none).</summary>
        public static (int lo, int hi) Range(IEnumerable<int> levels)
        {
            int lo = int.MaxValue, hi = 0;
            foreach (int l in levels) { if (l < lo) lo = l; if (l > hi) hi = l; }
            return hi == 0 ? (1, 1) : (lo, hi);
        }

        /// <summary>The chat line a leader reads after picking a mode.</summary>
        public static string ModeLine(string mode, bool listed) => mode switch
        {
            OPEN => listed ? "Your fellowship is listed open: anyone can join from the Looking for Group list."
                           : "Join mode set to open. List your fellowship to let others join.",
            PASSWORD => listed ? "Your fellowship is listed with a password: players join with it."
                               : "Join mode set to password. List your fellowship to let others join with it.",
            _ => listed ? "Your fellowship is listed on request: you will be asked to accept each player."
                        : "Join mode set to request.",
        };
    }
}
