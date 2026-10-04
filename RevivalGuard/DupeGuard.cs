using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE CRASH DUPE, CLOSED BY ORDERING. The one retail duplication that really worked
    /// (docs/security/AC-CHEAT-HISTORY.md): move part of a stack across a save boundary, crash the
    /// server, and the restore keeps both halves. ACE has the same shape. A split shrinks the source in
    /// memory and creates the new stack in memory; a merge grows the target and shrinks or destroys the
    /// source; and the periodic save (ShardDatabase.SaveBiotasInParallel) writes each object in its OWN
    /// transaction, in parallel. A crash part-way through that batch can persist the new stack (or the
    /// grown target) without the shrunken source. That is a dupe.
    ///
    /// Every split and merge in Player_Inventory goes through Player.AdjustStack, with a NEGATIVE amount
    /// for the side losing items. So a shrinking stack is saved the moment it shrinks. ACE's shard
    /// database runs its queue one task at a time, in order (SerializedShardDatabase), so the loss is on
    /// disk before any save that could hold the gain (a new stack or a grown target saves later, with the
    /// batch or its own save). The worst a crash can then do is lose the amount in flight, never double
    /// it. A destroyed source (a full merge) is removed through the same queue, also first.
    /// </summary>
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
    /// <summary>...AND THE GAINING SIDE RIGHT BEHIND IT, for the two everyday paths. Saving only the
    /// losing side leaves the amount in flight unsaved until the next batch (up to five minutes), so a
    /// crash in that gap loses it. A split into a pack and a merge each queue the gaining object's save
    /// after the method, which is after the losing side's save (queued inside it by DupeGuard or by
    /// Destroy), so the order still rules out a dupe and the loss window shrinks to the queue's own lag.
    /// Measured 2026-09-19: split 100 off 1000 -> the database read 900 three seconds later with the
    /// new stack unsaved; merge 60 back -> 40 saved, the grown stack still at its old size.</summary>
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
