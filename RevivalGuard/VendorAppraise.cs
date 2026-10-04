using ACE.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A SHOP'S STOCK CAN BE APPRAISED AGAIN. Retail let you click an item in a vendor's window and read
    /// its stats before buying, and that mattered: appraisal is the most used system in the whole game
    /// after movement. The 2017 captures make the case on their own -- IdentifyObject, its response and
    /// ItemAppraiseDone are 5.35% of every named message between them, more than all of combat
    /// (docs/CAPTURE-TEST-LIST.md). Buying armour blind is not the game.
    ///
    /// ACE cannot answer the request. A vendor's stock lives only in Vendor.DefaultItemsForSale and
    /// UniqueItemsForSale -- it is never created in a landblock and never registered with object
    /// maintenance -- so Player.FindObject(guid, SearchLocations.Everywhere) misses it, and
    /// HandleActionIdentifyObject sends the empty "couldn't find it" response. The client then draws the
    /// panel with nothing in it, which is what the owner reported on 2026-09-20: "I cant inspect items
    /// for sale in the NPC for sale UI panel."
    ///
    /// So: when the guid is one this player cannot otherwise see, look through the for-sale lists of the
    /// vendors they can see, and answer from there. Narrow on purpose --
    ///
    ///   * it only runs when the stock request has ALREADY failed the normal lookup, so nothing about
    ///     ordinary appraisal changes;
    ///   * only vendors in this player's own known-object set are searched, so a guid cannot be used to
    ///     read the stock of a shop on the other side of the world;
    ///   * success is what retail gave: a vendor's stock is not a skill check, it is a price tag.
    ///
    /// RequestedAppraisalTarget and CurrentAppraisalTarget are set the way the stock path sets them, so
    /// a second click on the same item is the "continued success" case rather than a fresh lookup.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionIdentifyObject))]
    public static class VendorAppraise
    {
        static bool Prefix(Player __instance, uint objectGuid)
        {
            if (objectGuid == 0 || __instance?.Session == null) return true;

            // Anything the player can reach normally is ACE's business, not ours.
            if (__instance.FindObject(objectGuid, Player.SearchLocations.Everywhere) != null)
                return true;

            var item = FindInVisibleVendors(__instance, new ObjectGuid(objectGuid));
            if (item == null) return true;

            __instance.RequestedAppraisalTarget = objectGuid;
            __instance.CurrentAppraisalTarget = objectGuid;
            __instance.AppraisalRequestedTimestamp = ACE.Common.Time.GetUnixTime();
            __instance.Session.Network.EnqueueSend(
                new GameEventIdentifyObjectResponse(__instance.Session, item, true));
            return false;            // handled here; skip ACE's empty response
        }

        /// <summary>The for-sale lists of every vendor this player currently knows about. A vendor holds
        /// two: DefaultItemsForSale, the shop's own stock, and UniqueItemsForSale, the things players
        /// have sold it.</summary>
        static WorldObject FindInVisibleVendors(Player player, ObjectGuid guid)
        {
            var known = player.PhysicsObj?.ObjMaint?.GetKnownObjectsValuesWhere(
                o => o?.WeenieObj?.WorldObject is Vendor);
            if (known == null) return null;

            foreach (var phys in known)
            {
                if (!(phys.WeenieObj.WorldObject is Vendor vendor)) continue;
                if (vendor.DefaultItemsForSale.TryGetValue(guid, out var stock)) return stock;
                if (vendor.UniqueItemsForSale.TryGetValue(guid, out var used)) return used;
            }
            return null;
        }
    }
}
