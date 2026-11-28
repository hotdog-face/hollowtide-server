using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>HeldGroundBanner's and PK Night's banner rules, pure, so tools/nettest compiles and checks them by path.</summary>
    public static class HeldGroundBannerRule
    {
        /// <summary>The Banner of the Held Ground.</summary>
        public const uint WCID = 908120;

        /// <summary>A side: the monarch, or the player themselves when not in an allegiance (PkCommon.Side).</summary>
        public static uint Side(uint guid, uint? monarch) => monarch != null && monarch.Value != 0 ? monarch.Value : guid;

        /// <summary>May this player use a banner hung in this house?</summary>
        public static bool MayUse(uint user, uint? userMonarch, uint? owner, uint? ownerMonarch, bool sameAccount)
        {
            if (owner == null || owner.Value == 0) return false;
            if (user == owner.Value || sameAccount) return true;
            return Side(user, userMonarch) == Side(owner.Value, ownerMonarch);
        }

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
