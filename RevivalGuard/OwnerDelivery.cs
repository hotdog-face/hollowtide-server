using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ITEMS DELIVERED TO THE OWNER'S PACK AT LOGIN (owner 2026-11-03: "i wont remember to run the commands to make the
    /// items for the suits. ill need you to make those appear in my inventory for me").
    ///
    /// A delivery is a package weenie in ace_world whose class name starts "rgdelivery-": its Name (PropertyString 1)
    /// says what it is ("O-Yoroi suit"), and its create list (weenie_properties_create_list, in row order) is what to
    /// give, with the row's palette and shade when set. A datafix adds a package; nothing else is needed. The packages
    /// themselves are never created in the world.
    ///
    /// When a character named in rg_owner_delivery_to (comma list, default "an admin character") enters the world, each item
    /// of each package not yet given to that character is made and put in the pack, and stamped as a quest on the
    /// character ("rgdelivery-<package wcid>-<row>"), so it never comes twice, whatever shard or restart. What does not
    /// fit (slots or burden) waits for the next login, and he is told. He is told what arrived.
    /// </summary>
    static class OwnerDelivery
    {
        internal const string P_TO = "rg_owner_delivery_to";
        const string TO_DEFAULT = "an admin character", PREFIX = "rgdelivery-";
        const double DELAY_S = 8;   // after the world has loaded around him, so the items and the words are seen

        static readonly object s_Lock = new object();
        static readonly HashSet<uint> s_InFlight = new HashSet<uint>();

        static bool IsRecipient(Player p)
        {
            var names = PkCommon.Str(P_TO, TO_DEFAULT).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
        }

        static List<uint> Packages()
        {
            using (var ctx = new ACE.Database.Models.World.WorldDbContext())
                return ctx.Weenie.Where(w => w.ClassName.StartsWith(PREFIX)).OrderBy(w => w.ClassId).Select(w => w.ClassId).ToList();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (!IsRecipient(p)) return;
                    lock (s_Lock) if (!s_InFlight.Add(p.Guid.Full)) return;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(DELAY_S);
                    chain.AddAction(p, () => Deliver(p));
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] owner delivery enter: {e.Message}"); }
            }
        }

        static void Deliver(Player p)
        {
            try
            {
                if (p.Session == null || p.IsDestroyed) return;
                var arrived = new List<string>();
                int waiting = 0;
                foreach (var pkg in Packages())
                {
                    var weenie = DatabaseManager.World.GetCachedWeenie(pkg);
                    var rows = weenie?.PropertiesCreateList?.OrderBy(r => r.DatabaseRecordId).ToList();
                    if (rows == null || rows.Count == 0) continue;
                    string what = weenie.GetProperty(PropertyString.Name) ?? $"delivery {pkg}";
                    int given = 0, left = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        string stamp = $"{PREFIX}{pkg}-{i}";
                        if (p.QuestManager.HasQuest(stamp)) continue;
                        var wo = WorldObjectFactory.CreateNewWorldObject(rows[i].WeenieClassId);
                        if (wo == null) { Mod.Log.Warn($"[RevivalGuard] owner delivery: {what}: no weenie {rows[i].WeenieClassId} (datafix not applied?)"); continue; }
                        if (rows[i].Palette > 0) wo.PaletteTemplate = rows[i].Palette;
                        if (rows[i].Shade > 0) wo.Shade = rows[i].Shade;
                        if (!p.CanAddToInventory(wo) || !p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); left++; continue; }
                        p.QuestManager.Stamp(stamp);
                        given++;
                    }
                    if (given > 0)
                    {
                        arrived.Add(what);
                        Mod.Log.Info($"[RevivalGuard] owner delivery: {p.Name} given {given} item(s) of {what} ({pkg}){(left > 0 ? $", {left} waiting" : "")}");
                    }
                    waiting += left;
                }
                foreach (var what in arrived)
                    Tell(p, $"Your {what} has arrived in your pack.");
                if (waiting > 0)
                    Tell(p, $"{waiting} more delivered item{(waiting == 1 ? " is" : "s are")} waiting: your pack is too full or too heavy. Make room and log in again.");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] owner delivery: {e.Message}"); }
            finally { lock (s_Lock) s_InFlight.Remove(p.Guid.Full); }
        }

        static void Tell(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }
}
