using System;

namespace RevivalGuard
{
    /// <summary>The pure part of PresenceWatch's activity word for `@who`.</summary>
    public static class PresenceStateRule
    {
        public const double COMBAT_SECONDS = 20.0;
        public const double IDLE_MINUTES = 5.0;

        /// <summary>The word for one player.</summary>
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
