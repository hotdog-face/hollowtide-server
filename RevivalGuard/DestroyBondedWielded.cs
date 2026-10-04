using System;
using System.Collections.Generic;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A WIELDED "DESTROYED ON DEATH" ITEM IS DESTROYED TOO. Death sweep 2026-09-30
    /// (docs/SWEEP-DEATH-SOCIAL-2026-09-30.md s4); owner, 2026-09-30: "yes".
    ///
    /// ACE's `Player.HandleDestroyBonded` takes every possession with Bonded -2 (Destroy) and calls
    /// `TryConsumeFromInventoryWithNetworking`, which only finds pack items. A wielded one (the Ensorcelled
    /// Mace 47227, quest weapons) stayed in the hand, yet was put on the list the death message reads from:
    /// "You've lost ... your Ensorcelled Mace!", and it was "lost" again at every death after.
    ///
    /// Postfix: whatever ACE listed and is still equipped is dequipped with `ConsumeItem`, ACE's own
    /// dequip-and-destroy path (it sends the unwield, the delete and the burden update, and saves). ACE's
    /// returned list, and so its message, is left as it was.
    /// </summary>
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
