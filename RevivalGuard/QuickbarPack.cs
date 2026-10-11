using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACE.Database.Models.Shard;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE MAIN PACK ON KEY 9 (owner, 2026-10-11: "start all players with the default of having the main pack in the
    /// slot 9 of the quick actions bar"). A shortcut to your own character is the main pack (the client draws the
    /// backpack and opens the inventory with it, InventoryHud.MainPackShortcut).
    ///   * a new character is made with it in slot 9 (ACE index 8);
    ///   * a character made before this went live gets it once, and only if its slot 9 is empty, so nobody's own
    ///     arrangement is touched. The shortcuts went to the client with the login, so it shows from the next
    ///     login. Who has had it: QuickbarPack/given.txt beside the mod (one character id a line), so a player who
    ///     clears the slot does not find it back.
    /// </summary>
    static class QuickbarPack
    {
        const uint SLOT = 8;   // the ninth key, 0-based as ACE's Shortcut.Index

        static readonly object s_Lock = new object();
        static HashSet<uint> s_Given;

        static string FilePath()
        {
            var dir = ModFolder.Sub("QuickbarPack");
            return dir == null ? null : Path.Combine(dir, "given.txt");
        }

        static HashSet<uint> Given()
        {
            if (s_Given != null) return s_Given;
            s_Given = new HashSet<uint>();
            try
            {
                var f = FilePath();
                if (f != null && File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                        if (uint.TryParse(line.Trim(), out uint id)) s_Given.Add(id);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] quickbar pack: could not read the list: {e.Message}"); }
            return s_Given;
        }

        static void Stamp(uint charId)
        {
            if (!Given().Add(charId)) return;
            try { var f = FilePath(); if (f != null) File.AppendAllText(f, charId + "\n"); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] quickbar pack: could not record {charId}: {e.Message}"); }
        }

        static bool SlotFree(Player p) => p.GetShortcuts().All(s => s.Index != SLOT);

        static void Give(Player p)
        {
            p.Character.AddOrUpdateShortcut(SLOT, p.Guid.Full, p.CharacterDatabaseLock);
            p.CharacterChangesDetected = true;
        }

        [HarmonyPatch(typeof(PlayerFactory), nameof(PlayerFactory.Create))]
        static class OnCreate
        {
            static void Postfix(ref Player player, PlayerFactory.CreateResult __result)
            {
                try
                {
                    if (__result != PlayerFactory.CreateResult.Success || player?.Character == null) return;
                    if (SlotFree(player)) Give(player);
                    lock (s_Lock) Stamp(player.Guid.Full);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] quickbar pack at creation: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p?.Character == null || Mercenaries.IsMerc(p)) return;
                    lock (s_Lock)
                    {
                        if (Given().Contains(p.Guid.Full)) return;
                        if (SlotFree(p)) Give(p);
                        Stamp(p.Guid.Full);
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] quickbar pack on entering: {e.Message}"); }
            }
        }
    }
}
