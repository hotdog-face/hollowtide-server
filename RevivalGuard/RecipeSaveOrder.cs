using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A RECIPE'S CHANGED TOOL IS SAVED BEFORE WHAT IT MADE (the keyring dupe, docs/findings/dupe-recheck-2026-11-02.md).
    /// RecipeManager.CreateDestroyItems saves a created item at once (TryCreateInInventoryWithNetworking) but leaves the
    /// mods it makes to the source and target (ModifyItem: a keyring's NumKeys -1, a tool's uses) to the player's
    /// periodic save, up to five minutes later. Measured 2026-11-03 on the M5 shard: carving tool on a ring of 2 keys,
    /// kill -9 two seconds later, and the restart held the ring at 2 keys AND the loose Master Key. So here every save
    /// queued during a recipe is held back, the source and target (when still ours) are saved first, then the held
    /// saves in their order. The shard queue runs in order, so a crash can at worst lose the made item, never double it.
    /// </summary>
    [HarmonyPatch(typeof(RecipeManager), nameof(RecipeManager.CreateDestroyItems))]
    static class RecipeSaveOrder
    {
        [System.ThreadStatic] internal static List<WorldObject> t_Held;

        static void Prefix() => t_Held = new List<WorldObject>();

        static void Postfix(Player player, WorldObject source, WorldObject target)
        {
            var held = t_Held; t_Held = null;
            if (held == null) return;
            foreach (var wo in new[] { source, target })
                if (wo != null && !wo.IsDestroyed && player?.FindObject(wo.Guid.Full, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems) != null)
                    wo.SaveBiotaToDatabase();
            foreach (var wo in held)
                if (!wo.IsDestroyed) wo.SaveBiotaToDatabase();
        }

        // an exception inside the recipe must not leave saves held on this thread
        static Exception Finalizer(Exception __exception)
        {
            var held = t_Held; t_Held = null;
            if (held != null) foreach (var wo in held) if (!wo.IsDestroyed) wo.SaveBiotaToDatabase();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.SaveBiotaToDatabase))]
    static class RecipeSaveHold
    {
        static bool Prefix(WorldObject __instance, bool enqueueSave)
        {
            var held = RecipeSaveOrder.t_Held;
            if (held == null || !enqueueSave) return true;
            if (!held.Contains(__instance)) held.Add(__instance);
            return false;
        }
    }
}
