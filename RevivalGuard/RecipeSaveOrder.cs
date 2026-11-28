using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A recipe's changed tool is saved before what it made.</summary>
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
