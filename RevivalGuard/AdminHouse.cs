using System;
using System.Collections.Generic;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ADMIN TEST PURCHASE OF A HOUSE. Ours, not retail; admins only. Owner, 2026-09-29, testing the
    /// mansion hooks and allegiance hall: "yes. no changing of the retail rules" (admin accounts bypass
    /// the monarch, rank and price rules for testing; players keep retail's).
    ///
    /// `@freehouse` buys the nearest dwelling for sale within 40 m through ACE's own
    /// HandleActionBuyHouse, so ownership, the deed, the hooks and the allegiance hall all come out
    /// exactly as a real purchase makes them. For the length of that one call only:
    ///   the slumlord's HouseRequiresMonarch and AllegianceMinLevel are lifted (and put back after),
    ///   VerifyPurchase answers true and GetConsumeItems takes nothing, so no price is charged.
    /// DevHousing (same call) already skips the 15-day account and 30-day cooldown for dev accounts.
    /// ACE's one-house-per-character rule stays.
    ///
    /// MAINTENANCE-FREE (2026-10-01). The owner logged in to "You have not paid your maintenance costs" and
    /// "Your allegiance rank is now below the requirements for owning a mansion": ProcessRent would have
    /// evicted him at the first rent date. A house an ADMIN ACCOUNT owns is set HouseStatus.InActive, ACE's
    /// own maintenance-free state (ProcessRent keeps an InActive house whatever is paid or ranked; retail
    /// has five such houses in ace_world). @freehouse does it on purchase; at start-up every house owned by
    /// an admin account's character is swept the same way. Players' houses keep retail's rules.
    /// On release (abandon, eviction, HouseRelease) the house goes back to its weenie's own status, so
    /// the next buyer gets the house as retail made it.
    /// </summary>
    public static class AdminHouse
    {
        [ThreadStatic] internal static bool t_Free;
        const float REACH = 40f;
        static bool s_Registered;

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            CommandManager.TryAddCommand(FreeHouse, "freehouse", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Admin test aid (ours, not retail): buy the nearest dwelling for sale, free, skipping monarch and rank.", "");
            Mod.Log.Info("[RevivalGuard] AdminHouse: @freehouse (admin only)");
        }

        static void FreeHouse(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p?.CurrentLandblock == null || p.Location == null) return;
            SlumLord best = null; float bestD = REACH;
            foreach (var o in p.CurrentLandblock.GetAllWorldObjectsForDiagnostics())
            {
                if (!(o is SlumLord sl) || sl.Location == null) continue;
                float d = p.Location.DistanceTo(sl.Location);
                if (d < bestD) { bestD = d; best = sl; }
            }
            if (best == null)
            {
                Say(session, $"[House] No dwelling for sale within {REACH:F0} m. Stand beside its sale sign or covenant crystal (ours, not retail).");
                return;
            }
            bool monarch = best.HouseRequiresMonarch; int? rank = best.AllegianceMinLevel;
            t_Free = true;
            try
            {
                best.HouseRequiresMonarch = false;
                best.AllegianceMinLevel = null;
                p.HandleActionBuyHouse(best.Guid.Full, new List<uint>());
            }
            finally
            {
                t_Free = false;
                best.HouseRequiresMonarch = monarch;
                best.AllegianceMinLevel = rank;
                // The purchase saved the slumlord while its rules were lifted; save them back, or the
                // database copy (which the owner's login and ProcessRent load) keeps no rank rule.
                best.SaveBiotaToDatabase();
            }
            Mod.Log.Info($"[RevivalGuard] AdminHouse: {p.Name} used @freehouse on 0x{best.Guid.Full:X8} ({best.Name})");
            var bought = best.House;          // the live object on this landblock (Player.House is not set yet)
            if (bought != null && bought.HouseOwner == p.Guid.Full && MakeMaintenanceFree(bought, p.Name))
                Say(session, "[House] Maintenance-free: an admin's test house owes no rent and has no rank rule (ours, not retail).");
        }

        internal static bool IsAdmin(IPlayer player) =>
            player?.Account != null && player.Account.AccessLevel >= (uint)AccessLevel.Admin;

        /// <summary>Sets ACE's maintenance-free status on a LIVE house object and saves it.</summary>
        internal static bool MakeMaintenanceFree(House house, string owner)
        {
            if (house == null || house.HouseStatus != HouseStatus.Active) return false;
            house.HouseStatus = HouseStatus.InActive;
            house.SaveBiotaToDatabase();
            Mod.Log.Info($"[RevivalGuard] AdminHouse: house 0x{house.Guid.Full:X8} ({owner}) set maintenance-free");
            return true;
        }

        /// <summary>A released house takes back its weenie's own status (Active unless retail made it InActive).</summary>
        internal static void RestoreStatus(House house)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(house.WeenieClassId);
            var own = (HouseStatus?)weenie?.GetProperty(PropertyInt.HouseStatus) ?? HouseStatus.Active;
            if (house.HouseStatus == own) return;
            Mod.Log.Info($"[RevivalGuard] AdminHouse: released house 0x{house.Guid.Full:X8} back to {own}");
            house.HouseStatus = own;
        }

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(Player), nameof(Player.VerifyPurchase))]
    static class AdminHouseVerify
    {
        static bool Prepare() { AdminHouse.Register(); return true; }
        static bool Prefix(ref bool __result)
        {
            if (!AdminHouse.t_Free) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetConsumeItems))]
    static class AdminHouseConsume
    {
        static bool Prefix(ref List<WorldObjectInfo<int>> __result)
        {
            if (!AdminHouse.t_Free) return true;
            __result = new List<WorldObjectInfo<int>>();
            return false;
        }
    }

    /// <summary>Release paths (abandon, eviction, HouseRelease) all clear the owner and then call ClearPermissions
    /// before saving the house, so the status goes back there.</summary>
    [HarmonyPatch(typeof(House), nameof(House.ClearPermissions))]
    static class AdminHouseRelease
    {
        static void Postfix(House __instance)
        {
            if (__instance != null && __instance.HouseOwner == null) AdminHouse.RestoreStatus(__instance);
        }
    }

    /// <summary>The login rank warning (HandleHouseOnLogin_Inner) asks HasRequirements without looking at the
    /// status, so an owned InActive house, which ProcessRent never evicts, warned anyway. Only that case
    /// changes: the owner of an InActive house meets its requirements. Purchases never call this method.</summary>
    [HarmonyPatch(typeof(SlumLord), nameof(SlumLord.HasRequirements))]
    static class AdminHouseNoRankWarning
    {
        static void Postfix(SlumLord __instance, Player player, ref bool __result)
        {
            if (__result || player == null) return;
            var house = __instance.House;
            if (house != null && house.HouseStatus == HouseStatus.InActive && house.HouseOwner == player.Guid.Full)
                __result = true;
        }
    }

    /// <summary>Start-up: every house owned by an admin account's character becomes maintenance-free, on the
    /// live landblock object (ApplyLive), so no cached copy holds the old status.</summary>
    [HarmonyPatch(typeof(HouseManager), nameof(HouseManager.BuildRentQueue))]
    static class AdminHouseSweep
    {
        static void Postfix()
        {
            try
            {
                var queue = AccessTools.Field(typeof(HouseManager), "RentQueue")?.GetValue(null) as SortedSet<PlayerHouse>;
                if (queue == null) return;
                var todo = new List<(uint house, string owner)>();
                foreach (var ph in queue)
                {
                    if (ph?.House == null || ph.House.HouseStatus != HouseStatus.Active) continue;
                    if (AdminHouse.IsAdmin(PlayerManager.FindByGuid(ph.PlayerGuid))) todo.Add((ph.House.Guid.Full, ph.PlayerName));
                }
                if (todo.Count == 0) return;
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    foreach (var (houseGuid, owner) in todo) ApplyLive(houseGuid, owner, 30);
                }));
                Mod.Log.Info($"[RevivalGuard] AdminHouse: {todo.Count} admin-owned house(s) will be set maintenance-free");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AdminHouse sweep: {e.Message}"); }
        }

        /// <summary>Sets the status on the LANDBLOCK'S house object, loading the landblock and retrying every 2 s
        /// until its objects exist. Not HouseManager.GetHouse: for an unloaded landblock it hands its callback a
        /// House.Load copy, and the live object, created from the database before the copy saved, stayed Active
        /// and saved Active back over it (seen 2026-10-01 on the first deploy).</summary>
        static void ApplyLive(uint houseGuid, string owner, int tries)
        {
            var id = new LandblockId((uint)(((houseGuid >> 12) & 0xFFFF) << 16 | 0xFFFF));
            var lb = LandblockManager.GetLandblock(id, false);
            if (lb != null && lb.CreateWorldObjectsCompleted && lb.GetObject(new ObjectGuid(houseGuid)) is House live)
            {
                AdminHouse.MakeMaintenanceFree(live, owner);
                return;
            }
            if (tries <= 0) { Mod.Log.Warn($"[RevivalGuard] AdminHouse: house 0x{houseGuid:X8} ({owner}) never loaded"); return; }
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
                WorldManager.EnqueueAction(new ActionEventDelegate(() => ApplyLive(houseGuid, owner, tries - 1))));
        }
    }
}
