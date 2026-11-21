using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>HeldGroundBanner's and PK Night's banner rules, pure (no ACE types), so tools/nettest compiles and checks
    /// them by path (nettest heldbanner). Owner 2026-11-20, option C of docs/proposals/pk-night-buff-item.md.</summary>
    public static class HeldGroundBannerRule
    {
        /// <summary>The Banner of the Held Ground (tools/public-shard/datafixes/2026-11-21-pknight-banner.sql).</summary>
        public const uint WCID = 908120;

        /// <summary>A side: the monarch, or the player themselves when not in an allegiance (PkCommon.Side).</summary>
        public static uint Side(uint guid, uint? monarch) => monarch != null && monarch.Value != 0 ? monarch.Value : guid;

        /// <summary>May this player use a banner hung in this house? The owner, the owner's other characters
        /// (sameAccount), and anyone whose side is the owner's side (the owner's allegiance, by its monarch). An
        /// unowned house: nobody.</summary>
        public static bool MayUse(uint user, uint? userMonarch, uint? owner, uint? ownerMonarch, bool sameAccount)
        {
            if (owner == null || owner.Value == 0) return false;
            if (user == owner.Value || sameAccount) return true;
            return Side(user, userMonarch) == Side(owner.Value, ownerMonarch);
        }

        /// <summary>One banner per allegiance, ever: the winning side gets one only if it never had one.</summary>
        public static bool Owed(uint winnerSide, ICollection<uint> given) =>
            winnerSide != 0 && (given == null || !given.Contains(winnerSide));

        // An item owed at login sits in PkNight's Pending list beside title keys, as "item:<wcid>".
        const string ITEM = "item:";

        public static string ItemKey(uint wcid) => ITEM + wcid;

        public static bool TryItem(string key, out uint wcid)
        {
            wcid = 0;
            return key != null && key.StartsWith(ITEM) && uint.TryParse(key.Substring(ITEM.Length), out wcid) && wcid != 0;
        }
    }
}
