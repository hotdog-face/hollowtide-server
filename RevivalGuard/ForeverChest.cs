using System;
using System.Collections.Generic;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE CHEST BESIDE FISPUR ANSEL THE GROCER IN HOLTBURG KEEPS WHAT PLAYERS LEAVE IN IT, FOREVER (owner 2026-11-21: "the chest in the
    /// building near holt. i want that to hold anything players put in it indefinitely").
    ///
    /// The owner pinned it the same day: "chest i meant is next to fespur ansel the grocer in holt" (Chest 0x7A9B401D,
    /// weenie 143, cell 0xA9B4016A, 3 m from Fispur Ansel 0x7A9B4024). ACE empties it two ways: `Chest.Open` arms `Reset` 120 s after the
    /// first open, and `Reset` runs `Container.ClearUnmanagedInventory`, which destroys every item the chest did not
    /// generate; and `Landblock.Unload` destroys a static object that was never saved to the shard, contents and all.
    ///
    /// SO this one chest becomes a saved static, the way ACE keeps a house's Storage chest: its biota is written to the
    /// shard the first time it is opened (`IsStaticThatShouldPersistToShard` says yes, so every landblock save keeps
    /// it), the landblock then reloads it from the shard with its inventory (Container(Biota) reads the rows whose
    /// Container is the chest), it never resets (`ChestResetInterval` is infinite, so no timer and no close-on-viewer),
    /// and `ClearUnmanagedInventory` leaves it alone. Every put saves the chest and the item at once, so a crash loses
    /// nothing. ChestReturnsItems skips it (nothing to hand back: it keeps everything).
    ///
    /// LIMITS: the chest's own 255 item slots (weenie 143's ItemsCapacity -1, read as a byte); when full ACE refuses the
    /// put. No packs: ACE refuses any container put into a non-Player, non-Storage container (Player_Inventory.cs, the
    /// quest-stamp bypass guard; tested 11-21, the pack bounces back with no message).
    /// Every other chest keeps ACE's rule (and ChestReturnsItems).
    /// </summary>
    public static class ForeverChest
    {
        internal const string P_ON = "rg_forever_chest";

        /// <summary>Static guids of the chests that keep their contents. The chest beside Fispur Ansel the Grocer, Holtburg.</summary>
        internal static readonly HashSet<uint> Guids = new HashSet<uint> { 0x7A9B401D };

        static bool s_Failed;

        internal static bool On
        {
            get { try { return PropertyManager.GetBool(P_ON, true).Item; } catch { return true; } }
        }

        internal static bool Is(WorldObject wo) => wo is Chest && Guids.Contains(wo.Guid.Full) && On;

        static Chest Of(Container container) => container is Chest c && Is(c) ? c : null;

        static void Fail(string where, Exception e)
        {
            if (!s_Failed) { s_Failed = true; Mod.Log.Error($"[RevivalGuard] ForeverChest {where}: {e}"); }
        }

        /// <summary>Writes the chest to the shard if it never was. Idempotent.</summary>
        static void Adopt(Chest c)
        {
            bool first = !c.BiotaOriginatedFromOrHasBeenSavedToDatabase();
            if (first || c.ChangesDetected) c.SaveBiotaToDatabase();
            if (first) Mod.Log.Info($"[RevivalGuard] ForeverChest: {c.Name} 0x{c.Guid.Full:X8} at {c.Location} saved to the shard for good ({c.Inventory.Count} items inside)");
        }

        /// <summary>Saved like a house's Storage: SaveDB keeps it, Unload leaves it (and its contents) in the shard.</summary>
        [HarmonyPatch(typeof(ACE.Server.WorldObjects.WorldObject), nameof(ACE.Server.WorldObjects.WorldObject.IsStaticThatShouldPersistToShard))]
        static class Persist
        {
            static void Postfix(ACE.Server.WorldObjects.WorldObject __instance, ref bool __result)
            {
                if (__result) return;
                try { if (Is(__instance)) __result = true; } catch (Exception e) { Fail("Persist", e); }
            }
        }

        /// <summary>No reset timer: Open arms none for an infinite interval, and ChestRegenOnClose stays false.</summary>
        [HarmonyPatch(typeof(Chest), nameof(Chest.ChestResetInterval), MethodType.Getter)]
        static class NoReset
        {
            static void Postfix(Chest __instance, ref double __result)
            {
                try { if (Is(__instance)) __result = double.PositiveInfinity; } catch (Exception e) { Fail("NoReset", e); }
            }
        }

        /// <summary>Opening it is the first moment a player can put anything in: make sure it is a saved static by then.</summary>
        [HarmonyPatch(typeof(Chest), nameof(Chest.Open))]
        static class Opened
        {
            static void Prefix(Chest __instance)
            {
                try { if (Is(__instance)) Adopt(__instance); } catch (Exception e) { Fail("Open", e); }
            }
        }

        /// <summary>Whatever resets it (ChestStuckOpen, a Close with regen, Container.Reset) never clears it.</summary>
        [HarmonyPatch(typeof(Container), nameof(Container.ClearUnmanagedInventory))]
        static class NoClear
        {
            static bool Prefix(Container __instance, ref bool __result)
            {
                try
                {
                    if (!Is(__instance)) return true;
                    __result = false;
                    return false;
                }
                catch (Exception e) { Fail("NoClear", e); return true; }
            }
        }

        static void SaveNow(WorldObject item, Container container)
        {
            var c = Of(container);
            if (c == null || item == null) return;
            Adopt(c);
            item.SaveBiotaToDatabase();
        }

        /// <summary>A whole item put in: saved with the chest straight away.</summary>
        [HarmonyPatch(typeof(Player), "DoHandleActionPutItemInContainer")]
        static class Put
        {
            static void Postfix(bool __result, WorldObject item, Container container)
            {
                if (!__result) return;
                try { SaveNow(item, container); } catch (Exception e) { Fail("Put", e); }
            }
        }

        /// <summary>Part of a stack split into it: the new half is saved.</summary>
        [HarmonyPatch(typeof(Player), "DoHandleActionStackableSplitToContainer")]
        static class Split
        {
            static void Postfix(bool __result, Container container, WorldObject newStack)
            {
                if (!__result) return;
                try { SaveNow(newStack, container); } catch (Exception e) { Fail("Split", e); }
            }
        }

        /// <summary>Said once at load, with the switch.</summary>
        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard ForeverChest: the chest beside the Holtburg grocer keeps what players put in it across resets, unloads and restarts (false: ACE, it empties)");
        }
    }
}
