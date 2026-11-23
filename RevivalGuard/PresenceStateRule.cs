using System;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of PresenceWatch's activity word for `@who` (no ACE types: tools/nettest compiles it by path,
    /// PresenceStateCase.cs). Owner ticket 261121-0020: the admin panel's players list said "still 3m"; he wants
    /// "macroing, playing actively, etc.". Only what the server truly knows (owner rule: nothing low-confidence in UI):
    ///
    ///   in portal space   the teleport has not finished
    ///   in combat         dealt or took damage in the last COMBAT_SECONDS (DamageHistory.Add; a miss writes nothing)
    ///   idle Nm           no move of 0.5 m, no damage, no successful cast for IDLE_MINUTES or more
    ///   active            otherwise
    ///   Assistant, ...    the client said its Assistant is running (the away box "while the Assistant runs", AUTO).
    ///                     Without that box the server cannot tell the Assistant from a player, so it never guesses.
    ///   away, ...         the player's own /away, or retail's AFK
    /// </summary>
    public static class PresenceStateRule
    {
        public const double COMBAT_SECONDS = 20.0;
        public const double IDLE_MINUTES = 5.0;

        /// <summary>The word for one player. Times are unix seconds; 0 (or less) = never.</summary>
        public static string State(double now, double movedAt, double lastDamageAt, double lastCastAt,
            bool teleporting, bool assistant, bool away)
        {
            string act;
            double last = Math.Max(movedAt, Math.Max(lastDamageAt, lastCastAt));
            if (teleporting) act = "in portal space";
            else if (lastDamageAt > 0 && now - lastDamageAt <= COMBAT_SECONDS) act = "in combat";
            else if (last > 0 && now - last >= IDLE_MINUTES * 60) act = "idle " + Span(now - last);
            else act = "active";
            if (assistant) return "Assistant, " + act;
            if (away) return "away, " + act;
            return act;
        }

        /// <summary>"12m", "1.5h": the same short form as @who's "still".</summary>
        public static string Span(double seconds)
        {
            if (seconds < 60) return $"{(int)seconds}s";
            if (seconds < 3600) return $"{(int)(seconds / 60)}m";
            return (seconds / 3600).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "h";
        }

        /// <summary>Where, as one word: outdoors, a building on the surface, or a dungeon.</summary>
        public static string Kind(bool indoors, bool dungeon) => !indoors ? "outdoors" : dungeon ? "dungeon" : "building";
    }
}
