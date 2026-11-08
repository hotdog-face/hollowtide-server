using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A BIG VENDOR'S STOCK IS MADE OFF THE WORLD THREAD (owner 2026-11-06, "yes look into the lag please").
    ///
    /// Live 2026-11-06: the Staff Base (0x576B) stalled the whole world 2-6 s at a time ("slow landblock tick ... RunActions
    /// 6480"), each time the owner first used one of its vendors after the block loaded. ACE's Vendor.LoadInventory makes
    /// every shop item (WorldObjectFactory + CalculateObjDesc) inside the use, on the landblock's tick: the Curators hold
    /// 447-453 items, the Dev Vault 161-299, Jeeves 356 (retail vendors hold tens). M5 shard: one first use 677-895 ms;
    /// OVH's slower cores make it seconds, and every landblock waits for the slowest.
    ///
    /// SO: when a vendor with at least `vendor.prewarm_min_items` (default 60) shop entries enters the world, its stock is
    /// made on a worker thread exactly as LoadInventoryItem makes it (ACE already makes WorldObjects on many landblock
    /// threads at once), then handed over in one action on the vendor's landblock: added to DefaultItemsForSale and the
    /// vendor marked loaded, so ACE's LoadInventory returns at once. If a player got there first (ACE loaded it itself),
    /// the worker's copies are dropped. Smaller vendors keep ACE's path; it costs them little.
    /// </summary>
    static class VendorPrewarm
    {
        static int MinItems => (int)Tunables.L("vendor.prewarm_min_items", 60);

        static readonly PropertyInfo s_Loaded = AccessTools.Property(typeof(Vendor), "inventoryloaded");
        static int s_Count;

        internal static int ShopEntries(Vendor v) =>
            v.Biota?.PropertiesCreateList?.Count(x => x.DestinationType == DestinationType.Shop) ?? 0;

        static bool Loaded(Vendor v) => (bool)(s_Loaded.GetValue(v) ?? false);

        static void Start(Vendor v)
        {
            if (s_Loaded == null || Loaded(v)) return;
            // copy the list on this thread: the worker never touches the vendor itself
            var list = v.Biota.PropertiesCreateList.Where(x => x.DestinationType == DestinationType.Shop)
                .Select(x => (wcid: x.WeenieClassId, palette: x.Palette, shade: x.Shade, stack: x.StackSize)).ToList();
            uint vendorGuid = v.Guid.Full;
            string name = v.Name;
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var made = new List<WorldObject>(list.Count);
                try
                {
                    foreach (var e in list)
                    {
                        if (MissingSpellGuard.LeftOffShop(name, e.wcid)) continue;
                        // Vendor.LoadInventoryItem, line for line
                        var wo = WorldObjectFactory.CreateNewWorldObject(e.wcid);
                        if (wo == null) continue;
                        if (e.palette > 0) wo.PaletteTemplate = e.palette;
                        if (e.shade > 0) wo.Shade = e.shade;
                        wo.ContainerId = vendorGuid;
                        wo.CalculateObjDesc();
                        wo.VendorShopCreateListStackSize = e.stack;
                        made.Add(wo);
                    }
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"[RevivalGuard] VendorPrewarm: {name} 0x{vendorGuid:X8}: {ex.Message}; ACE loads it on first use");
                    return;
                }
                double ms = sw.Elapsed.TotalMilliseconds;
                v.EnqueueAction(new ActionEventDelegate(() => HandOver(v, made, ms)));
            });
        }

        static void HandOver(Vendor v, List<WorldObject> made, double ms)
        {
            try
            {
                if (v.IsDestroyed || Loaded(v))
                {
                    Mod.Log.Info($"[RevivalGuard] VendorPrewarm: {v.Name} 0x{v.Guid.Full:X8} was {(v.IsDestroyed ? "gone" : "loaded by a first use")} before its {made.Count} items were ready; dropped them");
                    return;
                }
                foreach (var wo in made) v.DefaultItemsForSale.Add(wo.Guid, wo);
                s_Loaded.SetValue(v, true);
                if (s_Count++ < 50)
                    Mod.Log.Info($"[RevivalGuard] VendorPrewarm: {v.Name} 0x{v.Guid.Full:X8} stocked {made.Count} items off the world thread ({ms:F0} ms)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] VendorPrewarm: hand-over {v?.Name}: {e.Message}"); }
        }

        [HarmonyPatch(typeof(Landblock), nameof(Landblock.AddWorldObject))]
        static class Added
        {
            static bool Prepare(MethodBase original)
            {
                if (original == null)
                    Mod.Log.Info(s_Loaded != null
                        ? "[RevivalGuard] VendorPrewarm: vendors with many shop items are stocked off the world thread (tunable vendor.prewarm_min_items)"
                        : "[RevivalGuard] VendorPrewarm: Vendor.inventoryloaded not found, off");
                return s_Loaded != null;
            }

            static void Postfix(WorldObject wo, bool __result)
            {
                try
                {
                    if (!__result || !(wo is Vendor v)) return;
                    int min = MinItems;
                    if (min <= 0 || ShopEntries(v) < min) return;
                    Start(v);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] VendorPrewarm: {e.Message}"); }
            }
        }
    }
}
