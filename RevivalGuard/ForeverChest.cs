using System;
using System.Collections.Generic;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The chest beside fispur ansel the grocer in holtburg keeps what players leave in it, FOREVER.</summary>
    public static class ForeverChest
    {
        internal const string P_ON = "rg_forever_chest";

        /// <summary>Static guids of the chests that keep their contents.</summary>
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
