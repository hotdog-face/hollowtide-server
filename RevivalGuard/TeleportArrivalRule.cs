namespace RevivalGuard
{
    /// <summary>TeleportArrival's and TeleportWatchdog's decisions, pure (no ACE types), so tools/nettest compiles and
    /// checks them by path. "Arrival" = ACE's OnTeleportComplete: the pink bubble ends and the player materialises.</summary>
    public static class TeleportArrivalRule
    {
        // Raw motion commands a client sends for a key that moves the body (ACE MotionCommand). Turning is left out:
        // it turns in place, so it is no proof the client walks the world.
        public const uint WalkForward = 0x45000005, WalkBackwards = 0x45000006, RunForward = 0x44000007,
            SideStepRight = 0x6500000F, SideStepLeft = 0x65000010;

        public static bool IsMovement(uint forward, uint sidestep)
            => forward == WalkForward || forward == WalkBackwards || forward == RunForward
            || sidestep == SideStepRight || sidestep == SideStepLeft;

        /// <summary>(a) A pending arrival is completed by the client's own movement: past the grace, a movement key held,
        /// the client's report from the destination landblock, carrying the teleport sequence the server sent (it has
        /// processed the teleport, so this is not a key held at the old spot), and the body moved at least
        /// <paramref name="minMeters"/> from where the server put it. The distance is what proves the client left portal
        /// space: ours relays held keys in the tunnel but holds the body still (WalkController), and completing a player
        /// who still sees the tunnel let a PK kill them (ticket 261103-0036).</summary>
        public static bool MoveArrives(bool on, bool pending, double age, double grace, bool movement, bool sameLandblock,
            bool clientHasTeleport, double movedMeters, double minMeters)
            => on && !pending && age >= grace && movement && sameLandblock && clientHasTeleport && movedMeters >= minMeters;

        /// <summary>(b) How long a client that never said portal space ended is left before the watchdog completes the
        /// arrival itself: the configured seconds, never under the watchdog's own first arm (it would fire first anyway).</summary>
        public static double ClientWait(double configured, double floor) => configured < floor ? floor : configured;

        /// <summary>(c) The run-once latch: this teleport (its start stamp) already arrived and nothing has started a new
        /// one, so a late second completion (the client's LoginComplete after the server's, a second re-check chain) does
        /// not run the arrival side effects again. Not latched while Teleporting is still set: then it has not arrived.</summary>
        public static bool AlreadyArrived(bool on, double latchedStart, double start, bool teleporting)
            => on && !teleporting && latchedStart > 0 && latchedStart == start;

        /// <summary>(d) Which pending-teleport warning to log now: 2 past <paramref name="second"/>, 1 past
        /// <paramref name="first"/>, 0 for none (each logged once; a first look already past both logs only the later).</summary>
        public static int WarnDue(double age, int warned, double first, double second)
        {
            if (warned < 2 && age >= second) return 2;
            if (warned < 1 && age >= first) return 1;
            return 0;
        }

        public const string Stall = "client-stall", LostPackets = "lost-packets", NoLoginComplete = "no-logincomplete";

        /// <summary>(d) Why a teleport is still pending, from the session's traffic since it began.
        /// client-stall: nothing at all heard from the client for <paramref name="quiet"/> s (frozen, hung, or gone).
        /// lost-packets: the client is talking but asked for resends, or has stopped acking what the server sent.
        /// no-logincomplete: alive, acking, nothing lost; it just never said its portal space ended.</summary>
        public static string Verdict(double sinceHeard, int resendAsks, long unacked, double sinceAck, double quiet)
        {
            if (sinceHeard >= quiet) return Stall;
            if (resendAsks > 0 || (unacked > 0 && sinceAck >= quiet)) return LostPackets;
            return NoLoginComplete;
        }
    }
}
