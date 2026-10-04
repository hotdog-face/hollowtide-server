using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A DELETED CHARACTER'S HOUSE GOES BACK ON THE MARKET. Retail released a house when its owner was
    /// deleted. ACE does too (PlayerManager.HandlePlayerDelete -> HouseManager.DoHandlePlayerDelete ->
    /// HandleEviction), but every step looks the owner up in PlayerManager, and the shard's
    /// char_delete_instant_teardown drops the character from PlayerManager straight after queueing that
    /// work on the world thread. So the lookup fails ("couldn't find player guid"), the house stays owned
    /// by nobody, and it is never in the rent queue or the for-sale list again. Found 2026-09-19: test
    /// character Firsthour Eight (0x50000076), deleted, still owned cottage 0x7B2B309A.
    ///
    /// Three patches:
    ///  - DoHandlePlayerDelete: when the owner is already gone, find the house through the rent queue
    ///    (keyed by the owner's guid, which outlives the player cache) and evict anyway.
    ///  - HandleEviction: with no owner to update, release the house and stop (stock code would throw on
    ///    the missing player).
    ///  - BuildRentQueue (startup): any house whose owner no longer exists is released on the first world
    ///    tick. This repairs houses orphaned before this fix and any other path to the same state.
    /// </summary>
    static class HouseRelease
    {
        /// <summary>HandleEviction's own steps, minus the owner's properties. Runs on the world thread.</summary>
        internal static void Release(House house, string why)
        {
            var slumlord = house.SlumLord;
            slumlord?.ClearInventory();

            house.HouseOwner = null;
            house.MonarchId = null;
            house.HouseOwnerName = null;
            house.ClearPermissions();
            house.SaveBiotaToDatabase();
            house.UpdateLinks();
            if (house.HasDungeon) house.GetDungeonHouse()?.UpdateLinks();

            if (slumlord != null)
            {
                slumlord.Off();
                slumlord.SetAndBroadcastName();
                slumlord.SaveBiotaToDatabase();
                HouseList.AddToAvailable(slumlord, house);
            }
            house.ClearRestrictions();
            Mod.Log.Warn($"[RevivalGuard] house 0x{house.Guid.Full:X8} released: {why}");
        }
    }

    [HarmonyPatch(typeof(HouseManager), nameof(HouseManager.HandleEviction), new[] { typeof(House), typeof(uint), typeof(bool), typeof(bool) })]
    static class HouseReleaseEviction
    {
        static bool Prefix(House house, uint playerGuid)
        {
            if (house == null || PlayerManager.FindByGuid(playerGuid) != null) return true;
            HouseRelease.Release(house, $"owner 0x{playerGuid:X8} no longer exists");
            return false;
        }
    }

    [HarmonyPatch(typeof(HouseManager), "DoHandlePlayerDelete")]
    static class HouseReleaseOnDelete
    {
        static bool Prefix(uint playerGuid)
        {
            if (PlayerManager.FindByGuid(playerGuid) != null) return true;   // stock path works
            var find = AccessTools.Method(typeof(HouseManager), "FindPlayerHouse");
            if (!(find?.Invoke(null, new object[] { playerGuid }) is PlayerHouse playerHouse)) return false;   // owned no house
            HouseManager.GetHouse(playerHouse.House.Guid.Full, house =>
            {
                HouseManager.HandleEviction(house, playerGuid, false, true);
                HouseManager.RemoveRentQueue(house.Guid.Full);
                HouseManager.DecrementTotalOwnedHousingByType(house.HouseType);
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(HouseManager), nameof(HouseManager.BuildRentQueue))]
    static class HouseReleaseOrphans
    {
        static void Postfix()
        {
            try
            {
                var idToGuid = AccessTools.Property(typeof(HouseManager), "HouseIdToGuid")?.GetValue(null) as Dictionary<uint, List<uint>>;
                var guidOf = AccessTools.Method(typeof(HouseManager), "GetHouseGuid");
                if (idToGuid == null || guidOf == null) return;

                var orphans = new List<(uint house, uint owner)>();
                foreach (var slumlord in DatabaseManager.Shard.BaseDatabase.GetBiotasByType(WeenieType.SlumLord))
                {
                    var owner = slumlord.BiotaPropertiesIID.FirstOrDefault(i => i.Type == (ushort)PropertyInstanceId.HouseOwner);
                    if (owner == null || PlayerManager.FindByGuid(owner.Value) != null) continue;
                    var houseId = slumlord.BiotaPropertiesDID.FirstOrDefault(i => i.Type == (ushort)PropertyDataId.HouseId);
                    if (houseId == null || !idToGuid.TryGetValue(houseId.Value, out var guids)) continue;
                    var houseGuid = (uint)guidOf.Invoke(null, new object[] { slumlord.Id, guids });
                    if (houseGuid != 0) orphans.Add((houseGuid, owner.Value));
                }
                if (orphans.Count == 0) return;

                // After start-up: GetHouse on an unloaded landblock only registers a callback that runs
                // once the slumlord's inventory loads, so load each house's landblock to run it now.
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    foreach (var (houseGuid, ownerGuid) in orphans)
                    {
                        HouseManager.GetHouse(houseGuid, house => HouseManager.HandleEviction(house, ownerGuid, false, true));
                        var lb = (ushort)((houseGuid >> 12) & 0xFFFF);
                        LandblockManager.GetLandblock(new LandblockId((uint)(lb << 16 | 0xFFFF)), false);
                    }
                }));
                Mod.Log.Warn($"[RevivalGuard] {orphans.Count} house(s) owned by deleted characters will be released");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] orphan house sweep: {e.Message}"); }
        }
    }
}
