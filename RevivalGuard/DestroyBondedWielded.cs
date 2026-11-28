using System;
using System.Collections.Generic;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A wielded "destroyed on death" item is destroyed too.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleDestroyBonded))]
    static class DestroyBondedWielded
    {
        static void Postfix(Player __instance, List<WorldObject> __result)
        {
            if (__instance == null || __result == null) return;
            foreach (var item in __result)
            {
                try
                {
                    if (item == null || !__instance.EquippedObjects.ContainsKey(item.Guid)) continue;
                    if (__instance.TryDequipObjectWithNetworking(item.Guid, out _, Player.DequipObjectAction.ConsumeItem))
                        Mod.Log.Info($"[DestroyBondedWielded] {__instance.Name}: {item.Name} (0x{item.Guid.Full:X8}) destroyed from the hand on death");
                }
                catch (Exception e) { Mod.Log.Warn($"[DestroyBondedWielded] {item?.Name}: {e.Message}"); }
            }
        }
    }
}
