using System;
using System.Collections.Generic;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A USE ON AN OBJECT THE SERVER CANNOT FIND (261025-0022, Akuma): eleven uses of Door 0x7C174000 outside the Folthid
    /// cottage (landblock 0xC174, a plain door-aluvian-house with no links) each logged only ACE's debug line
    /// "HandleActionUseItem(7C174000): couldn't find object" while the client drew the door 8 m away. ACE looks in the
    /// player's CurrentLandblock and its adjacents only (Player.FindObject, SearchLocations.Landblock), so either the door
    /// was gone from the landblock or the player's CurrentLandblock was not where his Location was (he had just come back
    /// from dungeon 0x0007 through the cellar). Nothing in the logs told the two apart. This logs, once per player and
    /// object a minute, where the server has the player (Location vs CurrentLandblock) and every loaded landblock that does
    /// hold the object. Diagnostic only: ACE's own path (UseDone, no use) is unchanged.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseItem))]
    public static class UseTargetMissing
    {
        static readonly Dictionary<(uint, uint), DateTime> s_Said = new Dictionary<(uint, uint), DateTime>();
        static readonly object s_Lock = new object();

        static void Prefix(Player __instance, uint itemGuid)
        {
            try { Check(__instance, itemGuid); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] use target missing check: {e.Message}"); }
        }

        static void Check(Player p, uint guid)
        {
            if (p == null || guid == 0) return;
            if (p.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems | Player.SearchLocations.Landblock) != null) return;
            var now = DateTime.UtcNow;
            lock (s_Lock)
            {
                if (s_Said.TryGetValue((p.Guid.Full, guid), out var at) && (now - at).TotalSeconds < 60) return;
                if (s_Said.Count > 2000) s_Said.Clear();
                s_Said[(p.Guid.Full, guid)] = now;
            }
            uint locBlock = (p.Location?.Cell ?? 0) >> 16;
            var cur = p.CurrentLandblock;
            uint curBlock = cur != null ? cur.Id.Raw >> 16 : 0;
            var holders = new List<string>();
            foreach (var lb in LandblockManager.GetLoadedLandblocks())
            {
                var wo = lb?.GetObject(new ACE.Entity.ObjectGuid(guid), false);
                if (wo != null) holders.Add($"0x{lb.Id.Raw >> 16:X4} ({wo.Name}, cell 0x{wo.Location?.Cell ?? 0:X8}{(wo.IsDestroyed ? ", destroyed" : "")})");
            }
            string where = cur == null ? "CurrentLandblock NULL"
                : curBlock == locBlock ? $"CurrentLandblock 0x{curBlock:X4} (= Location)"
                : $"CurrentLandblock 0x{curBlock:X4} but Location in 0x{locBlock:X4}";
            Mod.Log.Warn($"[RevivalGuard] use target missing: {p.Name} (0x{p.Guid.Full:X8}) used 0x{guid:X8}, not in the pack or within"
                + $" its landblock and adjacents; Location 0x{p.Location?.Cell ?? 0:X8}, {where}; loaded landblocks holding it:"
                + $" {(holders.Count == 0 ? "none" : string.Join(", ", holders))}");
            BusyLedger.Note(p, $"use target 0x{guid:X8} not found ({where}; held by {(holders.Count == 0 ? "no loaded landblock" : string.Join(", ", holders))})");
        }
    }
}
