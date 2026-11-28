namespace RevivalGuard
{
    /// <summary>AttunedOnOwnHook's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class AttunedHookRule
    {
        /// <summary>The player owns the house the hook belongs to.</summary>
        public static bool OwnHook(uint? rootHouseOwner, uint player) =>
            rootHouseOwner != null && rootHouseOwner.Value != 0 && rootHouseOwner.Value == player;

        /// <summary>ACE's "attuned" answer for one object while a move is being verified. waiveGuid</summary>
        public static bool Attuned(bool ace, uint objectGuid, uint waiveGuid) =>
            ace && !(waiveGuid != 0 && objectGuid == waiveGuid);
    }
}
