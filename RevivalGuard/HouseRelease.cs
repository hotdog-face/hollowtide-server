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
    /// <summary>A deleted character's house goes back on the market.</summary>
    static class HouseRelease
    {
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

                // After start-up: GetHouse on an unloaded landblock only registers a callback that runs once the slumlord's inventory loads
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
