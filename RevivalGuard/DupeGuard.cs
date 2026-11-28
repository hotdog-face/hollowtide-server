using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The crash dupe, closed by ordering.</summary>
    [HarmonyPatch(typeof(Player), "AdjustStack")]
    static class DupeGuard
    {
        static void Postfix(WorldObject stack, int amount, bool __result)
        {
            if (!__result || amount >= 0 || stack == null || stack.IsDestroyed) return;
            stack.SaveBiotaToDatabase();
        }
    }
}

namespace RevivalGuard
{
    /// <summary>...and the gaining side right behind it, for the two everyday paths.</summary>
    [HarmonyPatch(typeof(Player), "DoHandleActionStackableSplitToContainer")]
    static class DupeGuardSplitGain
    {
        static void Postfix(WorldObject newStack, bool __result)
        {
            if (__result && newStack != null && !newStack.IsDestroyed) newStack.SaveBiotaToDatabase();
        }
    }

    [HarmonyPatch(typeof(Player), "DoHandleActionStackableMerge")]
    static class DupeGuardMergeGain
    {
        static void Postfix(WorldObject targetStack, bool __result)
        {
            if (__result && targetStack != null && !targetStack.IsDestroyed) targetStack.SaveBiotaToDatabase();
        }
    }
}
