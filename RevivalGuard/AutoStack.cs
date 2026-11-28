using System.Linq;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>New stacks join the ONES you already carry.</summary>
    public static class AutoStack
    {
        static readonly ConditionalWeakTable<Session, object> s_Off = new ConditionalWeakTable<Session, object>();

        internal static void Register() =>
            CommandManager.TryAddCommand(Option, "rgautostack", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client's Auto-stack option: 0 = no automatic merges for this connection.", "1|0");

        static void Option(Session session, params string[] parameters)
        {
            if (session == null) return;
            s_Off.Remove(session);
            if (parameters != null && parameters.Length > 0 && parameters[0] == "0") s_Off.Add(session, null);
        }

        internal static void Later(Player p, WorldObject item)
        {
            if (p == null || item == null || !(item is Stackable) || (item.MaxStackSize ?? 1) <= 1) return;
            if (p.Session != null && s_Off.TryGetValue(p.Session, out _)) return;
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
