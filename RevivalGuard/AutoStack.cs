using System.Linq;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// NEW STACKS JOIN THE ONES YOU ALREADY CARRY (owner, 2026-09-25: "items need to auto stack as
    /// well"). Ours: retail merged only when you dropped one stack onto another
    /// (ItemHolder::AttemptMerge, decompile ~385060), and ACE never merges on its own, so every
    /// purchase, reward, pickup and the change from a broken note arrived as a separate stack.
    ///
    /// Covered: anything created into the pack (vendor purchases, quest and emote gives, note
    /// change, admin creates) and anything picked up from outside your possessions (the ground, a
    /// corpse, a chest). NOT covered on purpose: moving or splitting a stack you already carry,
    /// since a split is deliberate and merging it back would undo it.
    ///
    /// The merge is ACE's own HandleActionStackableMerge, a moment later so the client has seen the
    /// new stack first. The new stack pours into the fullest stack of the same kind that has room,
    /// then the next, until it is empty or everything is full.
    /// </summary>
    public static class AutoStack
    {
        internal static void Later(Player p, WorldObject item)
        {
            if (p == null || item == null || !(item is Stackable) || (item.MaxStackSize ?? 1) <= 1) return;
            var guid = item.Guid.Full;
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.3);
            chain.AddAction(p, () => Merge(p, guid));
            chain.EnqueueChain();
        }

        static void Merge(Player p, uint guid)
        {
            for (int guard = 0; guard < 16; guard++)
            {
                var src = p.FindObject(guid, Player.SearchLocations.MyInventory);
                if (src == null || (src.StackSize ?? 1) <= 0) return;
                var target = p.GetInventoryItemsOfWCID(src.WeenieClassId)
                    .Where(t => t != src && t is Stackable && (t.StackSize ?? 1) < (t.MaxStackSize ?? 1))
                    .OrderByDescending(t => t.StackSize ?? 1)
                    .FirstOrDefault();
                if (target == null) return;
                int room = (target.MaxStackSize ?? 1) - (target.StackSize ?? 1);
                int amount = System.Math.Min(room, src.StackSize ?? 1);
                p.HandleActionStackableMerge(guid, target.Guid.Full, amount);
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TryCreateInInventoryWithNetworking),
        new[] { typeof(WorldObject), typeof(Container) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    static class AutoStack_Create
    {
        static void Postfix(Player __instance, WorldObject item, bool __result)
        {
            if (__result) AutoStack.Later(__instance, item);
        }
    }

    [HarmonyPatch(typeof(Player), "DoHandleActionPutItemInContainer")]
    static class AutoStack_Pickup
    {
        static void Postfix(Player __instance, WorldObject item, Container itemRootOwner, Container containerRootOwner, bool __result)
        {
            if (__result && itemRootOwner != __instance && containerRootOwner == __instance)
                AutoStack.Later(__instance, item);
        }
    }
}
