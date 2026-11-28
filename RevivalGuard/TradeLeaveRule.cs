namespace RevivalGuard
{
    /// <summary>TradeLeaveClose's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class TradeLeaveRule
    {
        public enum Act { Nothing, Close, Wait }

        /// <summary>The line the player left behind reads.</summary>
        public static string Line(string leaverName) =>
            string.IsNullOrWhiteSpace(leaverName) ? "The trade was closed because your trading partner left."
                                                  : $"The trade was closed because {leaverName} left.";

        /// <summary>stillTradingWithLeaver: the remaining player is trading and their partner is the leaver.</summary>
        public static Act Decide(bool stillTradingWithLeaver, bool transferInProgress)
        {
            if (!stillTradingWithLeaver) return Act.Nothing;
            return transferInProgress ? Act.Wait : Act.Close;
        }

        /// <summary>The sweep's test: a trading player whose partner is not online, or online and not trading with them.</summary>
        public static bool Orphaned(bool isTrading, bool partnerOnline, bool partnerTradingWithMe) =>
            isTrading && (!partnerOnline || !partnerTradingWithMe);

        /// <summary>A trading partner's client silent this long has lost its link.</summary>
        public static bool LinkLost(double silentS) => silentS >= LINK_SILENT_S;

        public const double LINK_SILENT_S = 20.0;
        public const double WAIT_S = 1.0;   // a retry after the escrow (ACE's chain is 0.5 s)
        public const int WAIT_TRIES = 10;
        public const double SWEEP_S = 2.0;
    }
}
