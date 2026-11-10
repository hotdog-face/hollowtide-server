namespace RevivalGuard
{
    /// <summary>LandblockUnloadGuard's and StrandedPlayerWatchdog's rules, pure (no ACE types), so tools/nettest compiles
    /// and checks them by path.</summary>
    public static class LandblockUnloadRule
    {
        /// <summary>An idle landblock ACE has queued for unloading is kept when anyone is in it: Landblock.Unload removes
        /// every object, players included, and leaves them in no landblock (ticket 261109-0037). A server shutdown unloads
        /// everything on purpose, after every session has gone.</summary>
        public static bool Keep(bool guardOn, bool shuttingDown, int occupants)
            => guardOn && !shuttingDown && occupants > 0;

        /// <summary>How long a player has been in no landblock: 0 while placed (the clock restarts), else now minus when
        /// it was first seen unplaced. Teleporting and logging out are normal ways to be between blocks and do not count.</summary>
        public static double StrandedAge(bool inLandblock, bool teleporting, bool loggingOut, ref double since, double now)
        {
            if (inLandblock || teleporting || loggingOut) { since = 0; return 0; }
            if (since <= 0) { since = now; return 0; }
            return now - since;
        }

        /// <summary>Stranded: was in a landblock earlier this session (so not a login that never placed) and has been in
        /// none for at least <paramref name="limit"/> seconds.</summary>
        public static bool Stranded(bool everPlaced, double ageSeconds, double limit)
            => everPlaced && ageSeconds >= limit;
    }
}
