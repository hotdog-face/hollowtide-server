using ACE.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A shop's stock can be appraised again.</summary>
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

        /// <summary>The for-sale lists of every vendor this player currently knows about.</summary>
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
