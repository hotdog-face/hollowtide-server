namespace RevivalGuard
{
    /// <summary>LandblockUnloadGuard's and StrandedPlayerWatchdog's rules, pure, so tools/nettest compiles and checks them by path.</summary>
    public static class LandblockUnloadRule
    {
        /// <summary>An idle landblock ACE has queued for unloading is kept when anyone is in it</summary>
        public static bool Keep(bool guardOn, bool shuttingDown, int occupants)
            => guardOn && !shuttingDown && occupants > 0;

        /// <summary>How long a player has been in no landblock: 0 while placed, else now minus when it was first seen unplaced.</summary>
        public static double StrandedAge(bool inLandblock, bool teleporting, bool loggingOut, ref double since, double now)
        {
            if (inLandblock || teleporting || loggingOut) { since = 0; return 0; }
            if (since <= 0) { since = now; return 0; }
            return now - since;
        }

        /// <summary>Stranded: was in a landblock earlier this session and has been in none for at least limit seconds.</summary>
        public static bool Stranded(bool everPlaced, double ageSeconds, double limit)
            => everPlaced && ageSeconds >= limit;
    }
}
