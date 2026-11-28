using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Locked items, on the server.</summary>
    public static class ItemLock
    {
        public const PropertyBool LOCKED = (PropertyBool)29101;

        public static bool IsLocked(WorldObject wo) => wo != null && (wo.GetProperty(LOCKED) ?? false);

        internal static void Register()
        {
            // Named static handlers: ACE rebuilds each handler from its MethodInfo, so a lambda stops the whole mod's Initialize.
            CommandManager.TryAddCommand(Lock, "lockitem", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Lock an item so it cannot be dropped, given, salvaged, sold or drained.", "<0xguid | item name>");
            CommandManager.TryAddCommand(Unlock, "unlockitem", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Unlock an item you locked with @lock.", "<0xguid | item name>");
            CommandManager.TryAddCommand(List, "lockeditems", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Your locked items.", "");
        }

        static void Lock(Session session, params string[] parameters) => Handle(session, parameters, true);
        static void Unlock(Session session, params string[] parameters) => Handle(session, parameters, false);

        static void Handle(Session session, string[] parameters, bool lockIt)
        {
            var p = session?.Player;
            if (p == null) return;
            string arg = parameters == null ? "" : string.Join(" ", parameters).Trim();
            if (arg.Length == 0) { Say(session, $"Usage: @{(lockIt ? "lockitem" : "unlockitem")} <item name>"); return; }
            var item = Find(p, arg);
            if (item == null) { Say(session, $"You have nothing called \"{arg}\"."); return; }
            if (IsLocked(item) == lockIt) { if (!ModChannel.Capable(session)) Say(session, $"{item.Name} is already {(lockIt ? "locked" : "unlocked")}."); Push(session); return; }
            if (lockIt) item.SetProperty(LOCKED, true); else item.RemoveProperty(LOCKED);
            item.SaveBiotaToDatabase();
            Mod.Log.Info($"[RevivalGuard] ItemLock: {(lockIt ? "locked" : "unlocked")} 0x{item.Guid.Full:X8} {item.Name} for {p.Name}");   // who set a lock, for a report
            if (!ModChannel.Capable(session))
                Say(session, lockIt ? $"{item.Name} is locked: it can't be dropped, given away, salvaged, sold or drained. @unlockitem to undo."
                                    : $"{item.Name} is unlocked.");
            Push(session);
        }

        static void List(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            if (ModChannel.Capable(session)) { Push(session); return; }
            var locked = p.GetAllPossessions().Where(IsLocked).ToList();
            if (locked.Count == 0) { Say(session, "You have no locked items. @lockitem <item name> to lock one."); return; }
            Say(session, $"--- {locked.Count} locked ---");
            foreach (var wo in locked.Take(40)) Say(session, wo.Name);
        }

        /// <summary>By 0xguid, else the first possession whose name matches (exact first, then prefix).</summary>
        static WorldObject Find(Player p, string arg)
        {
            var all = p.GetAllPossessions();
            if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(arg.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint g))
                return all.FirstOrDefault(w => w.Guid.Full == g);
            return all.FirstOrDefault(w => string.Equals(w.Name, arg, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(w => w.Name != null && w.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The player's locked guids to our client, when it reads the channel.</summary>
        public static void Push(Session session)
        {
            var p = session?.Player;
            if (p == null || !ModChannel.Capable(session)) return;
            var sb = new StringBuilder("[");
            int n = 0;
            foreach (var wo in p.GetAllPossessions())
                if (IsLocked(wo)) { if (n++ > 0) sb.Append(','); sb.Append(wo.Guid.Full); }
            ModChannel.Send(session, "locks", sb.Append(']').ToString());
        }

        internal static WorldObject Owned(Player p, uint guid) =>
            p?.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);

        internal static void Refuse(Player p, WorldObject item, string verb)
        {
            // How to unlock, in the words of the client it goes to: ours has the examine window's padlock, a retail client has the command.
            bool ours = ModChannel.Capable(p.Session);
            Say(p.Session, $"Your {item.Name} is locked, so it was not {verb}. " + (ours
                ? "To unlock it, examine it and click the padlock beside Wiki."
                : $"To unlock it, type @unlockitem {item.Name}."));
            // our client refused nothing, so its list was out of date: the padlock would have read unlocked (and a click locked)
            if (ours) Push(p.Session);
            Mod.Log.Info($"[RevivalGuard] ItemLock: refused {verb} of 0x{item.Guid.Full:X8} {item.Name} for {p.Name}");
        }

        internal static void Say(Session s, string line) =>
            s?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionDropItem))]
    static class ItemLockDrop
    {
        static bool Prefix(Player __instance, uint itemGuid)
        {
            var item = ItemLock.Owned(__instance, itemGuid);
            if (!ItemLock.IsLocked(item)) return true;
            ItemLock.Refuse(__instance, item, "dropped");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
    static class ItemLockGive
    {
        static bool Prefix(Player __instance, uint itemGuid)
        {
            var item = ItemLock.Owned(__instance, itemGuid);
            if (!ItemLock.IsLocked(item)) return true;
            ItemLock.Refuse(__instance, item, "given away");
            __instance.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(__instance.Session, itemGuid));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleSalvaging))]
    static class ItemLockSalvage
    {
        static bool Prefix(Player __instance, List<uint> salvageItems)
        {
            if (salvageItems == null) return true;
            foreach (var g in salvageItems.ToList())
            {
                var item = ItemLock.Owned(__instance, g);
                if (!ItemLock.IsLocked(item)) continue;
                salvageItems.Remove(g);
                ItemLock.Refuse(__instance, item, "salvaged");
            }
            return salvageItems.Count > 0;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionSellItem))]
    static class ItemLockSell
    {
        static bool Prefix(Player __instance, List<ItemProfile> itemProfiles)
        {
            if (itemProfiles == null) return true;
            foreach (var ip in itemProfiles.ToList())
            {
                var item = ItemLock.Owned(__instance, ip.ObjectGuid);
                if (!ItemLock.IsLocked(item)) continue;
                itemProfiles.Remove(ip);
                ItemLock.Refuse(__instance, item, "sold");
            }
            if (itemProfiles.Count > 0) return true;
            __instance.SendUseDoneEvent();
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseWithTarget))]
    static class ItemLockUseWith
    {
        static bool Prefix(Player __instance, uint sourceObjectGuid, uint targetObjectGuid)
        {
            var target = ItemLock.Owned(__instance, targetObjectGuid);
            if (!ItemLock.IsLocked(target)) return true;
            var source = ItemLock.Owned(__instance, sourceObjectGuid);
            // a charged mana stone only fills its target
            if (source is ManaStone && (source.ItemCurMana ?? 0) > 0) return true;
            ItemLock.Refuse(__instance, target, "drained or tinkered");
            __instance.SendUseDoneEvent();
            return false;
        }
    }
}
