namespace RevivalGuard
{
    /// <summary>
    /// TradeLeaveClose's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.
    /// What to do with the player left behind when their trade partner leaves the world.
    /// </summary>
    public static class TradeLeaveRule
    {
        public enum Act { Nothing, Close, Wait }

        /// <summary>The line the player left behind reads. No em dashes (player text).</summary>
        public static string Line(string leaverName) =>
            string.IsNullOrWhiteSpace(leaverName) ? "The trade was closed because your trading partner left."
                                                  : $"The trade was closed because {leaverName} left.";

        /// <summary>
        /// <paramref name="stillTradingWithLeaver"/>: the remaining player is trading and their partner is the leaver
        /// (anything else is a different trade, which is not ours to close). <paramref name="transferInProgress"/>: either
        /// side's TradeTransferInProgress, the 0.5 s escrow of an accepted trade; the items are out of both packs then, so
        /// closing waits for ACE's chain to put them in their new owner's pack (ACE's own close returns early there and
        /// left the window open for good).
        /// </summary>
        public static Act Decide(bool stillTradingWithLeaver, bool transferInProgress)
        {
            if (!stillTradingWithLeaver) return Act.Nothing;
            return transferInProgress ? Act.Wait : Act.Close;
        }

        /// <summary>The sweep's test: a trading player whose partner is not online, or online and not trading with them.</summary>
        public static bool Orphaned(bool isTrading, bool partnerOnline, bool partnerTradingWithMe) =>
            isTrading && (!partnerOnline || !partnerTradingWithMe);

        /// <summary>A trading partner's client silent this long has lost its link. A live client acks and pings
        /// every 1-2 s (box 2026-10-29: idle trading clients, 52 samples, max 2.0 s); closing a trade on a long hitch only closes the window.</summary>
        public static bool LinkLost(double silentS) => silentS >= LINK_SILENT_S;

        public const double LINK_SILENT_S = 20.0;
        public const double WAIT_S = 1.0;   // a retry after the escrow (ACE's chain is 0.5 s)
        public const int WAIT_TRIES = 10;
        public const double SWEEP_S = 2.0;
    }
}
