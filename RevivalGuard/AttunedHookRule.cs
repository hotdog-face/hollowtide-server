namespace RevivalGuard
{
    /// <summary>AttunedOnOwnHook's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.
    /// Retail let an attuned housing item (Windreave Stalker Plaque: "Attuned, Bonded, Ivoryable ... Can be placed on house
    /// wall hooks", AC wiki) hang on the owner's own hooks. ACE's put-item verify refuses EVERY attuned item headed for a
    /// container on the landscape, hooks included (ticket 261104-0023). The waiver is for the house's owner only, and only
    /// for the item itself: a pack that holds an attuned item is still refused (ACE refuses containers on hooks anyway).</summary>
    public static class AttunedHookRule
    {
        /// <summary>The player owns the house the hook belongs to (its root house's HouseOwner).</summary>
        public static bool OwnHook(uint? rootHouseOwner, uint player) =>
            rootHouseOwner != null && rootHouseOwner.Value != 0 && rootHouseOwner.Value == player;

        /// <summary>ACE's "attuned" answer for one object while a move is being verified. waiveGuid: the item being moved
        /// onto the mover's own hook, or 0 when no such move is being verified.</summary>
        public static bool Attuned(bool ace, uint objectGuid, uint waiveGuid) =>
            ace && !(waiveGuid != 0 && objectGuid == waiveGuid);
    }
}
