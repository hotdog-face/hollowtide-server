using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A WORLD CHEST GIVES BACK WHAT A PLAYER LEFT IN IT, INSTEAD OF DESTROYING IT.
    ///
    /// Report 261025-0021 (Rock Frog, 2026-10-25 12:58 UTC, Holtburg Meeting Hall chest 0x70125000): moving gear to a
    /// mule through a chest, the mule's Take All moved 16 of the 30 items, then "it closed out and said my bags were
    /// full and now the chest is empty". 14 items were destroyed, among them 3 Coalesced Aetheria, Amuli Coat and
    /// Leggings, Covenant Gauntlets (docs/findings/chest-reset-item-loss-261025-0021.md).
    ///
    /// ACE: `Chest.Open` arms `Reset` for `ChestResetInterval` later (120 s for every chest with no ResetInterval,
    /// counted from the FIRST open, not the last). `Reset` closes the chest on whoever is looking and calls
    /// `Container.ClearUnmanagedInventory`, which runs `Destroy()` on every item the chest's generator did not make,
    /// and `Destroy` deletes the item's rows from the shard. A landblock unload (5 min with nobody there) destroys the
    /// chest and everything in it the same way. Retail did this too; with mules on the same account it is a trap that
    /// eats whole inventories, and item loss is the one kind of bug we never accept.
    ///
    /// SO: who put each item into a world chest is remembered (the put, and a split stack's new half). When the chest
    /// resets or is destroyed, each item with a remembered owner is taken out of the chest first and handed back:
    ///   - the owner is online in the chest's own landblock: straight into the pack, with ACE's networking;
    ///   - otherwise (logged out to a mule, gone elsewhere, full or over burden): saved into the owner's main pack in
    ///     the database, where ACE's login loads it (GetPossessedBiotasInParallel reads Container rows; ACE's own
    ///     SaveBiotaToDatabase keeps the biota cache in step, so a relog does not serve a stale copy).
    /// Items with no remembered owner (made by an emote, or put in before this mod loaded) are left to ACE, logged.
    /// The chest's generated loot still resets as before. Storage (house chests) is never cleared by ACE and is skipped.
    /// </summary>
    public static class ChestReturnsItems
    {
        sealed class Placer { public uint Guid; public string Name; }

        // Keyed by the item object: nothing to clean up when an item is destroyed or leaves the world.
        static readonly ConditionalWeakTable<WorldObject, Placer> s_Placed = new ConditionalWeakTable<WorldObject, Placer>();
        static bool s_Failed;

        /// <summary>The world chest an item now sits in (directly or in a pack inside it), else null.</summary>
        internal static Chest WorldChestOf(Container container)
        {
            WorldObject root = container;
            for (int i = 0; root?.Container != null && i < 4; i++) root = root.Container;
            if (root is Chest chest && !(chest is Storage)) return chest;
            return null;
        }

        internal static void Remember(WorldObject item, Container container, Player player)
        {
            if (item == null || player == null || WorldChestOf(container) == null) return;
            s_Placed.AddOrUpdate(item, new Placer { Guid = player.Guid.Full, Name = player.Name });
        }

        internal static bool TryGetPlacer(WorldObject item, out uint guid, out string name)
        {
            guid = 0; name = null;
            if (item == null || !s_Placed.TryGetValue(item, out var p)) return false;
            guid = p.Guid; name = p.Name;
            return true;
        }

        /// <summary>Takes every player-placed item out of the chest and hands it back. Called before ACE clears or
        /// destroys the chest's inventory. Never throws.</summary>
        internal static void ReturnAll(Container container, string why)
        {
            try
            {
                if (!(container is Chest chest) || chest is Storage || chest.Inventory.Count == 0) return;

                var toOwner = new Dictionary<uint, List<WorldObject>>();
                var names = new Dictionary<uint, string>();
                foreach (var item in chest.Inventory.Values.Where(i => i.GeneratorId == null).ToList())
                {
                    if (!TryGetPlacer(item, out var owner, out var ownerName))
                    {
                        Mod.Log.Info($"[ChestReturnsItems] {why}: {chest.Name} 0x{chest.Guid.Full:X8} keeps ACE's rule for {item.Name} 0x{item.Guid.Full:X8} (no one recorded putting it in)");
                        continue;
                    }
                    if (!chest.TryRemoveFromInventory(item.Guid, out var taken) || taken == null) continue;
                    if (!toOwner.TryGetValue(owner, out var list)) toOwner[owner] = list = new List<WorldObject>();
                    list.Add(taken);
                    names[owner] = ownerName;
                }

                foreach (var kv in toOwner)
                    GiveBack(chest, kv.Key, names[kv.Key], kv.Value, why);
            }
            catch (Exception e)
            {
                if (!s_Failed) { s_Failed = true; Mod.Log.Error("[ChestReturnsItems] " + e); }
            }
        }

        static void GiveBack(Chest chest, uint ownerGuid, string ownerName, List<WorldObject> items, string why)
        {
            var online = PlayerManager.GetOnlinePlayer(ownerGuid);
            bool here = online != null && online.Session != null && online.CurrentLandblock != null
                && chest.CurrentLandblock != null && online.CurrentLandblock == chest.CurrentLandblock;
            string notHere = online == null ? "offline" : online.Session == null ? "no session"
                : online.CurrentLandblock == null ? "between landblocks"
                : online.CurrentLandblock != chest.CurrentLandblock ? $"in landblock 0x{online.CurrentLandblock.Id.Raw >> 16:X4}" : null;

            int toPack = 0, saved = 0;
            var names = new List<string>();
            foreach (var item in items)
            {
                string label = item.Name + ((item.StackSize ?? 1) > 1 ? $" x{item.StackSize}" : "");
                names.Add($"{label} 0x{item.Guid.Full:X8}");
                if (here)
                {
                    if (online.TryCreateInInventoryWithNetworking(item))
                    {
                        item.SaveBiotaToDatabase();
                        toPack++;
                        continue;
                    }
                    notHere = "no room or burden";
                }
                // The owner's main pack in the database: loaded at their next login, whatever the pack holds.
                item.Location = null;
                item.WielderId = null;
                item.OwnerId = ownerGuid;
                item.ContainerId = ownerGuid;
                item.PlacementPosition = 0;
                item.SaveBiotaToDatabase();
                saved++;
            }

            string where = chest.Location != null ? $"0x{chest.Location.Cell:X8}" : "?";
            string text = $"{chest.Name} 0x{chest.Guid.Full:X8} {why}: returned to {ownerName} 0x{ownerGuid:X8} "
                + $"({toPack} into the pack, {saved} saved for next login{(saved > 0 ? ": owner " + notHere : "")}): {string.Join(", ", names)}";
            Mod.Log.Info("[ChestReturnsItems] " + text);
            AuditTrail.Record("chest-return", ownerName, where, text);

            if (online?.Session == null) return;
            int n = items.Count;
            string what = n == 1 ? $"your {items[0].Name}" : $"the {n} things you left in it";
            string msg = saved == 0
                ? $"The {chest.Name} resets and hands back {what}."
                : $"The {chest.Name} you used has reset. It kept {what} safe for you: {(n == 1 ? "it" : "they")} will be in your pack the next time you log in.";
            online.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
        }

        // ---- where items go in -----------------------------------------------------------------

        /// <summary>A whole item put into a container. Private in ACE; one overload.</summary>
        [HarmonyPatch(typeof(Player), "DoHandleActionPutItemInContainer")]
        static class Put
        {
            static void Postfix(Player __instance, bool __result, WorldObject item, Container container)
            {
                if (!__result) return;
                try { Remember(item, container, __instance); } catch (Exception) { }
            }
        }

        /// <summary>Part of a stack split off into a container: the new half is the item that lands in the chest.</summary>
        [HarmonyPatch(typeof(Player), "DoHandleActionStackableSplitToContainer")]
        static class Split
        {
            static void Postfix(Player __instance, bool __result, Container container, WorldObject newStack)
            {
                if (!__result) return;
                try { Remember(newStack, container, __instance); } catch (Exception) { }
            }
        }

        // ---- where ACE would destroy them ------------------------------------------------------

        /// <summary>Chest.Reset and Container.Reset both clear through this (declared on Container).</summary>
        [HarmonyPatch(typeof(Container), nameof(Container.ClearUnmanagedInventory))]
        static class Clear
        {
            static void Prefix(Container __instance) => ReturnAll(__instance, "reset");
        }

        /// <summary>The chest itself destroyed: landblock unload, a cleared chest fading out, a landblock reload.
        /// Declared on WorldObject; the type test keeps it to chests.</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Destroy))]
        static class Destroyed
        {
            static void Prefix(WorldObject __instance)
            {
                if (__instance is Chest c && !c.IsDestroyed) ReturnAll(c, "destroyed");
            }
        }
    }
}
