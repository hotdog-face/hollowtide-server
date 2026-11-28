using System;

namespace RevivalGuard
{
    /// <summary>TeleportArrival's and TeleportWatchdog's decisions, pure, so tools/nettest compiles and checks them by path.</summary>
    public static class TeleportArrivalRule
    {
        // Raw motion commands a client sends for a key that moves the body (ACE MotionCommand).
        public const uint WalkForward = 0x45000005, WalkBackwards = 0x45000006, RunForward = 0x44000007,
            SideStepRight = 0x6500000F, SideStepLeft = 0x65000010;

        public static bool IsMovement(uint forward, uint sidestep)
            => forward == WalkForward || forward == WalkBackwards || forward == RunForward
            || sidestep == SideStepRight || sidestep == SideStepLeft;

        /// <summary>A pending arrival is completed by the client's own movement</summary>
        public static bool MoveArrives(bool on, bool pending, double age, double grace, bool movement, bool sameLandblock,
            bool clientHasTeleport, double movedMeters, double minMeters)
            => on && !pending && age >= grace && movement && sameLandblock && clientHasTeleport && movedMeters >= minMeters;

        public static double ClientWait(double configured, double floor) => configured < floor ? floor : configured;

        /// <summary>The run-once latch: this teleport already arrived and nothing has started a new one</summary>
        public static bool AlreadyArrived(bool on, double latchedStart, double start, bool teleporting)
            => on && !teleporting && latchedStart > 0 && latchedStart == start;

        /// <summary>Which pending-teleport warning to log now: 2 past second, 1 past first, 0 for none.</summary>
        public static int WarnDue(double age, int warned, double first, double second)
        {
            if (warned < 2 && age >= second) return 2;
            if (warned < 1 && age >= first) return 1;
            return 0;
        }

        /// <summary>(d) The two warning times for this pending teleport.</summary>
        public static (double first, double second) WarnTimes(bool login, double loginFirst, double first = 10, double second = 30)
        {
            if (!login) return (first, second);
            double f = loginFirst < first ? first : loginFirst;
            return (f, Math.Max(second, 2 * f));
        }

        public const string Stall = "client-stall", LostPackets = "lost-packets", NoLoginComplete = "no-logincomplete";

        public static string Verdict(double sinceHeard, int resendAsks, long unacked, double sinceAck, double quiet)
        {
            if (sinceHeard >= quiet) return Stall;
            if (resendAsks > 0 || (unacked > 0 && sinceAck >= quiet)) return LostPackets;
            return NoLoginComplete;
        }
    }
}
