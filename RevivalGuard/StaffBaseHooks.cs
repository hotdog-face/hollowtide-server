using System.Collections.Concurrent;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE STAFF BASE'S WEAPON RACKS AND ARMOR STANDS WORK (owner LIVE ticket 261101-0012, "weapon rack and
    /// armor stand dont let me use them"; owner 2026-11-01: "yes build the mod change that makes the rack
    /// and stand work").
    ///
    /// The Staff Base (landblock 0x576B, staff only) is not a house, so it has no House object. The Mansion
    /// Furniture (MansionFurniture.cs) only shows what it holds while it hangs on a Hook, and ACE's Hook
    /// assumes every hook belongs to a house: Hook.OnAddItem and OnRemoveItem do
    /// House.HouseCurrentHooksUsable-- / ++, AppraiseInfo reads hook.ParentLink.HouseOwner, and
    /// Player.HandleActionPutItemInContainer reads hook.House.HouseMaxHooksUsable. With no house each of
    /// those throws a NullReferenceException.
    ///
    /// So this, and ONLY for a hook with no house on landblock 0x576B (every player house hook has its
    /// house, and nothing here touches one):
    ///   - Hook.OnAddItem / OnRemoveItem run ACE's own steps without the house's hook count;
    ///   - the hooks of datafix 2026-11-01-staff-base-decor-3 (SEED below) are given their rack or stand
    ///     when the landblock first loads them empty; ACE then saves the hook (a Hook with inventory is a
    ///     static object that persists, WorldObject.IsStaticThatShouldPersistToShard) and the furniture
    ///     (ContainerId = the hook), so after a restart the hook loads from the shard with its furniture,
    ///     and the furniture loads what is on it (MansionFurniture.Load);
    ///   - anyone on the landblock may use the hook (it is staff only); a double-click shows the piece's
    ///     status, as a house with hooks hidden does, never opening the hook as a container;
    ///   - the rack or stand itself is fixed: it can never be taken off, and nothing else is hung on the
    ///     hook. Dropping an item on the hook goes to the furniture (MansionFurniture.PutFromContainer);
    ///   - appraising the hook appraises it as ACE does, with no "This hook is owned by" line.
    /// What goes on a rack or stand, and who may take it down, is MansionFurniture's (MayHang / MayTake).
    ///
    /// THE FREE HOOKS (owner 2026-11-01: "Yes, build them", his 2026-10-29 ask for "lots of house hooks populated
    /// so its decorated like a living space"): every other houseless hook on 0x576B (datafix
    /// 2026-11-02-staff-base-decor-5, FREE_HOOKS in gen_staff_base_home.py) takes one item of the owner's own, as a
    /// house hook does: drop a housing item from the pack onto an empty hook to hang it (its HookType must fit the
    /// hook's, wall or floor; equipped items, packs and chests do not go up), double-click the hook to take it back
    /// into the pack. ACE's own drag path reads hook.House for the hook limits and throws without one, so the move
    /// is done here, as MansionFurniture moves a piece onto a rack. An empty free hook stays visible (no house
    /// hides it). ACE keeps a hook with an item in ace_shard, so what hangs there survives restarts.
    /// </summary>
    static class StaffBaseHooks
    {
        internal const ushort LANDBLOCK = 0x576B;

        /// <summary>The hooks of 2026-11-01-staff-base-decor-3.sql (tools/gpubox-ace/gen_staff_base_home.py
        /// HOOKS) and the furniture each holds.</summary>
        internal static readonly Dictionary<uint, uint> SEED = new Dictionary<uint, uint>
        {
            [0x7576B200] = MansionFurniture.RACK, [0x7576B201] = MansionFurniture.RACK,
            [0x7576B202] = MansionFurniture.STAND, [0x7576B203] = MansionFurniture.STAND,
            // the owner's wardrobe (2026-11-02, "ill need about 10 so I can have my various armors"): ten armor stands of
            // datafix 2026-11-02-staff-base-decor-6 (WARDROBE in gen_staff_base_home.py); MansionFurniture.TradeSuits
            [0x7576B230] = MansionFurniture.STAND, [0x7576B231] = MansionFurniture.STAND, [0x7576B232] = MansionFurniture.STAND,
            [0x7576B233] = MansionFurniture.STAND, [0x7576B234] = MansionFurniture.STAND, [0x7576B235] = MansionFurniture.STAND,
            [0x7576B236] = MansionFurniture.STAND, [0x7576B237] = MansionFurniture.STAND, [0x7576B238] = MansionFurniture.STAND,
            [0x7576B239] = MansionFurniture.STAND,
        };

        /// <summary>The owner's estate (2026-11-03, test-only for now): the wood island, landblocks EF9A to F49F (x 0xEF-0xF4,
        /// y 0x9A-0x9F), where the houses he picks from the showroom stand, with room for more. Its houseless hooks are free
        /// hooks like the Staff Base's.</summary>
        static bool Estate(ushort lb) => (lb >> 8) >= 0xEF && (lb >> 8) <= 0xF4 && (lb & 0xFF) >= 0x9A && (lb & 0xFF) <= 0x9F;

        static bool Ours(ushort lb) => lb == LANDBLOCK || Estate(lb);

        /// <summary>A hook with no house on the Staff Base landblock or the owner's estate.</summary>
        internal static bool Is(WorldObject wo)
        {
            if (!(wo is Hook hook) || hook.ParentLink != null) return false;
            var lb = hook.Location?.LandblockId.Landblock;
            if (lb != null) return Ours(lb.Value);
            uint g = hook.Guid.Full;   // a static guid is 0x7LLLLnnn
            return (g & 0xF0000000) == 0x70000000 && Ours((ushort)((g >> 12) & 0xFFFF));
        }

        static readonly ConcurrentDictionary<uint, Hook> s_Hooks = new ConcurrentDictionary<uint, Hook>();

        /// <summary>The staff hook holding this item (the rack or stand itself), if any.</summary>
        internal static Hook HolderOf(uint itemGuid)
        {
            foreach (var h in s_Hooks.Values)
                if (h.Item?.Guid.Full == itemGuid) return h;
            return null;
        }

        static void Refuse(Player p, uint itemGuid, string why)
        {
            if (why != null) p.SendTransientError(why);
            p.Session?.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, itemGuid, WeenieError.None));
        }

        // ------------------------------------------------------------------ seeding

        /// <summary>Give an empty seeded hook its furniture. Runs on the hook's landblock.</summary>
        static void Seed(Hook hook)
        {
            try
            {
                if (!Is(hook) || !SEED.TryGetValue(hook.Guid.Full, out var wcid)) return;
                s_Hooks[hook.Guid.Full] = hook;
                if (!hook.InventoryLoaded || hook.HasItem) return;
                var item = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (item == null) { Mod.Log.Warn($"[StaffBaseHooks] no weenie {wcid} for hook 0x{hook.Guid.Full:X8}"); return; }
                if (!hook.TryAddToInventory(item, 0, false, false))
                {
                    item.Destroy(false);
                    Mod.Log.Warn($"[StaffBaseHooks] hook 0x{hook.Guid.Full:X8} would not take the {item.Name}");
                    return;
                }
                item.SaveBiotaToDatabase();   // the hook saved itself in OnAddItem
                Mod.Log.Info($"[StaffBaseHooks] hung a new {item.Name} 0x{item.Guid.Full:X8} on hook 0x{hook.Guid.Full:X8}");
            }
            catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] seed 0x{hook?.Guid.Full:X8}: {e}"); }
        }

        /// <summary>A hook made from its weenie (never saved yet) has its inventory at once.</summary>
        [HarmonyPatch(typeof(Landblock), nameof(Landblock.AddWorldObject))]
        static class Added
        {
            static void Postfix(WorldObject wo, bool __result)
            {
                try
                {
                    if (!__result || !(wo is Hook hook) || !SEED.ContainsKey(hook.Guid.Full)) return;
                    hook.EnqueueAction(new ActionEventDelegate(() => Seed(hook)));
                }
                catch (Exception e) { TickGuard.Report("StaffBaseHooks.Added", e); }
            }
        }

        /// <summary>A hook restored from the shard loads its inventory after it is made.</summary>
        [HarmonyPatch(typeof(Hook), "OnInitialInventoryLoadCompleted")]
        static class Loaded
        {
            static void Postfix(Hook __instance)
            {
                if (SEED.ContainsKey(__instance.Guid.Full)) Seed(__instance);
            }
        }

        // ------------------------------------------------------------------ ACE's hook steps, without a house

        /// <summary>Hook.OnAddItem without House.HouseCurrentHooksUsable--. MansionFurniture.Hung's postfix
        /// still runs (Harmony runs postfixes after a skipping prefix).</summary>
        [HarmonyPatch(typeof(Hook), "OnAddItem")]
        static class AddItem
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Hook __instance)
            {
                if (!Is(__instance)) return true;
                try
                {
                    s_Hooks[__instance.Guid.Full] = __instance;
                    var item = __instance.Inventory.Values.FirstOrDefault();
                    if (item == null) return false;
                    __instance.NoDraw = false;
                    __instance.UiHidden = false;
                    __instance.Ethereal = false;
                    __instance.SetupTableId = item.SetupTableId;
                    __instance.MotionTableId = item.MotionTableId;
                    __instance.PhysicsTableId = item.PhysicsTableId;
                    __instance.SoundTableId = item.SoundTableId;
                    __instance.ObjScale = item.ObjScale;
                    __instance.Name = item.NameWithMaterial;
                    if (__instance.MotionTableId != 0)
                        __instance.CurrentMotionState = new Motion(MotionStance.Invalid);
                    __instance.Placement = (Placement)(item.HookPlacement ?? (int)Placement.Hook);
                    item.EmoteManager.SetProxy(__instance);
                    __instance.SaveBiotaToDatabase();   // as ACE: never lose an item to a crash before the next save
                    __instance.EnqueueBroadcastUpdateObject();
                }
                catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] add 0x{__instance.Guid.Full:X8}: {e}"); }
                return false;
            }
        }

        static readonly ConcurrentDictionary<uint, WorldObject> s_Bare = new ConcurrentDictionary<uint, WorldObject>();

        /// <summary>Hook.OnRemoveItem without House.HouseCurrentHooksUsable++: the hook goes back to a bare hook
        /// (a free hook's item taken back; nothing takes a seeded rack or stand off, but if anything ever does,
        /// nothing throws).</summary>
        [HarmonyPatch(typeof(Hook), "OnRemoveItem")]
        static class RemoveItem
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Hook __instance, WorldObject removedItem)
            {
                if (!Is(__instance)) return true;
                try
                {
                    var bare = s_Bare.GetOrAdd(__instance.WeenieClassId,
                        w => WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie(w), ObjectGuid.Invalid));
                    __instance.SetupTableId = bare.SetupTableId;
                    __instance.MotionTableId = bare.MotionTableId;
                    __instance.PhysicsTableId = bare.PhysicsTableId;
                    __instance.SoundTableId = bare.SoundTableId;
                    __instance.Placement = bare.Placement;
                    __instance.ObjScale = bare.ObjScale;
                    __instance.Name = bare.Name;
                    __instance.NoDraw = false;
                    __instance.UiHidden = false;
                    __instance.Ethereal = true;
                    if (__instance.MotionTableId == 0) __instance.CurrentMotionState = null;
                    removedItem?.EmoteManager.ClearProxy();
                    __instance.EnqueueBroadcastUpdateObject();
                    __instance.SaveBiotaToDatabase();
                    if (SEED.ContainsKey(__instance.Guid.Full))
                        Mod.Log.Warn($"[StaffBaseHooks] {removedItem?.Name} 0x{removedItem?.Guid.Full:X8} came off hook 0x{__instance.Guid.Full:X8}");
                }
                catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] remove 0x{__instance.Guid.Full:X8}: {e}"); }
                return false;
            }
        }

        // ------------------------------------------------------------------ using it

        /// <summary>Anyone on the staff landblock may use its hooks (ACE refuses everyone not in a house).</summary>
        [HarmonyPatch(typeof(Hook), nameof(Hook.CheckUseRequirements))]
        static class UseRequirements
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Hook __instance, WorldObject activator, ref ActivationResult __result)
            {
                if (!Is(__instance) || !(activator is Player p)) return true;
                if (p.IsOlthoiPlayer)
                {
                    p.SendWeenieError(WeenieError.OlthoiCannotInteractWithThat);
                    __result = new ActivationResult(false);
                    return false;
                }
                __result = new ActivationResult(true);
                return false;
            }
        }

        /// <summary>A double-click is the furniture's own (its status and how to use it), as with a house's
        /// hooks hidden; the hook is never opened as a container, so the furniture cannot be dragged out.</summary>
        [HarmonyPatch(typeof(Hook), nameof(Hook.ActOnUse))]
        static class Use
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Hook __instance, WorldObject wo)
            {
                if (!Is(__instance)) return true;
                try
                {
                    var item = __instance.Item;
                    if (!SEED.ContainsKey(__instance.Guid.Full) && wo is Player owner)
                    {
                        // A hooked item with a use of its own is used, as on a house with its hooks hidden (Hook.ActOnUse):
                        // the Fireworks go off (owner 2026-11-03, "yes it should set it off"). Anything else comes back to the
                        // pack. Any item also comes back when dragged off the hook into the pack (TakeOff below).
                        if (item != null && Usable(item)) { owner.LasUsedHookId = __instance.Guid; item.OnActivate(owner); }
                        else if (item != null) TakeBack(owner, __instance);
                        else owner.SendTransientError("This hook is empty. Drag a housing item from your pack onto it to hang it.");
                    }
                    else if (item != null) item.OnActivate(wo);
                    else if (wo is Player p) p.SendTransientError("This hook is empty.");
                }
                catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] use 0x{__instance.Guid.Full:X8}: {e}"); }
                return false;
            }
        }

        /// <summary>The furniture stays on its hook, and a drop on the hook (a client that sends
        /// PutItemInContainer for it) goes to the furniture. ACE's own path reads hook.House and throws.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionPutItemInContainer))]
        static class PutIn
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Player __instance, uint itemGuid, uint containerGuid)
            {
                try
                {
                    // DRAGGED OFF THE HOOK: the client lifts the hook itself (it is what the client sees) and puts it in the pack.
                    if (__instance.CurrentLandblock?.GetObject(itemGuid) is Hook off && Is(off))
                    {
                        if (SEED.ContainsKey(off.Guid.Full) || !off.HasItem) { Refuse(__instance, itemGuid, off.HasItem ? $"The {off.Item.Name} is fixed to the wall here." : null); return false; }
                        var tp = __instance;
                        tp.CreateMoveToChain(off, ok =>
                        {
                            try { if (ok) TakeBack(tp, off); else Refuse(tp, itemGuid, null); }
                            catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] take off: {e}"); Refuse(tp, itemGuid, "That did not work. It has been logged for staff."); }
                        });
                        return false;
                    }
                    var holder = HolderOf(itemGuid);
                    if (holder != null && SEED.ContainsKey(holder.Guid.Full))
                    {
                        Refuse(__instance, itemGuid, $"The {holder.Item?.Name ?? "furniture"} is fixed to the wall here.");
                        return false;
                    }
                    if (!(__instance.CurrentLandblock?.GetObject(containerGuid) is Hook hook) || !Is(hook)) return true;
                    if (!SEED.ContainsKey(hook.Guid.Full))
                    {
                        var p = __instance;
                        p.CreateMoveToChain(hook, ok =>
                        {
                            try { if (ok) Hang(p, hook, itemGuid); else Refuse(p, itemGuid, null); }
                            catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] hang: {e}"); Refuse(p, itemGuid, "That did not work. It has been logged for staff."); }
                        });
                        return false;
                    }
                    if (!MansionFurniture.PutFromContainer(__instance, hook, itemGuid))
                        Refuse(__instance, itemGuid, "Nothing can be hung on this hook.");
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[StaffBaseHooks] put in container: {e}");
                    Refuse(__instance, itemGuid, null);
                    return false;
                }
            }
        }

        /// <summary>A Hooker (the Fireworks, the Pyreal Bell, the Snowmaker: house items used only while hooked) on a free
        /// staff hook may be used: ACE's Hooker.CheckUseRequirements wants the hook to belong to an owned house and refuses
        /// "You are not permitted to use that hook" otherwise.</summary>
        [HarmonyPatch(typeof(Hooker), nameof(Hooker.CheckUseRequirements))]
        static class HookerUse
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Hooker __instance, WorldObject activator, ref ActivationResult __result)
            {
                try
                {
                    if (!(activator is Player p) || !__instance.IsHooked(p, out var hook) || !Is(hook) || SEED.ContainsKey(hook.Guid.Full)) return true;
                    __result = new ActivationResult(true);
                    return false;
                }
                catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] hooker 0x{__instance.Guid.Full:X8}: {e}"); return true; }
            }
        }

        /// <summary>A storage chest with no house on the Staff Base or the estate (the Wardrobe Chest, decor-10; an admin character's
        /// Storage; the estate's chests) opens for staff. ACE's Storage.CheckUseRequirements wants a house, and without one
        /// lets in only a player with IgnoreHouseBarriers set (and logs "couldn't find RootHouse" for anyone else).</summary>
        [HarmonyPatch(typeof(Storage), nameof(Storage.CheckUseRequirements))]
        static class StaffStorage
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Storage __instance, WorldObject activator, ref ActivationResult __result)
            {
                try
                {
                    var lb = __instance.Location?.LandblockId.Landblock;
                    if (__instance.House != null || lb == null || !Ours(lb.Value) || !(activator is Player p)) return true;
                    if (p.IgnoreHouseBarriers || (p.Session?.AccessLevel ?? AccessLevel.Player) >= AccessLevel.Advocate)
                    {
                        __result = new ActivationResult(true);
                        return false;
                    }
                    p.SendTransientError($"Only staff can open the {__instance.Name}.");
                    __result = new ActivationResult(false);
                    return false;
                }
                catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] storage 0x{__instance.Guid.Full:X8}: {e}"); return true; }
            }
        }

        /// <summary>A drop on a free hook sent as a give (this client drops every item on an object as GiveObjectRequest,
        /// AcNpcInteraction.Give): ACE would hand it to GiveObjectToNPC, which a hook ignores, so nothing was hung (owner M5
        /// 261103-0056; the fireworks' HookType was the second refusal behind it). Hung as PutIn does. Runs after
        /// MansionFurniture.GiveTo (a rack or stand on the hook takes its own drops) and ItemLock's refusal.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
        static class GiveTo
        {
            [HarmonyPriority(Priority.VeryLow)]
            static bool Prefix(Player __instance, uint targetGuid, uint itemGuid, bool __runOriginal)
            {
                if (!__runOriginal) return false;
                try
                {
                    if (!(__instance.CurrentLandblock?.GetObject(targetGuid) is Hook hook) || !Is(hook) || SEED.ContainsKey(hook.Guid.Full)) return true;
                    var p = __instance;
                    p.CreateMoveToChain(hook, ok =>
                    {
                        try { if (ok) Hang(p, hook, itemGuid); else Refuse(p, itemGuid, null); }
                        catch (Exception e) { Mod.Log.Error($"[StaffBaseHooks] hang (give): {e}"); Refuse(p, itemGuid, "That did not work. It has been logged for staff."); }
                    });
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"[StaffBaseHooks] give to hook: {e}");
                    Refuse(__instance, itemGuid, null);
                    return false;
                }
            }
        }

        static void Say(Player p, string line) =>
            p.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

        /// <summary>One of the player's items onto an empty free hook (on the hook's landblock, after the walk).</summary>
        static void Hang(Player p, Hook hook, uint itemGuid)
        {
            var item = p.FindObject(itemGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems, out _, out _, out bool equipped);
            if (item == null) { Refuse(p, itemGuid, null); return; }
            if (equipped) { Refuse(p, itemGuid, $"Take off the {item.Name} first."); return; }
            if (hook.HasItem) { Refuse(p, itemGuid, $"This hook already holds the {hook.Item.Name}. Double-click it to take that down first."); return; }
            if (item is Container) { Refuse(p, itemGuid, "Packs and chests do not go on a hook."); return; }
            // Retail puts an item only on a hook its HookType names (Floor 1, Wall 2, Ceiling 4, Yard 8, Roof 0x10), and a house's
            // hooks keep that rule. The Staff Base's free hooks take any hookable item (owner 2026-11-03 on M5 261103-0056, the
            // yard/roof-only Fireworks 30735 on a floor hook: "Yes, allow it").
            if ((item.HookType ?? 0) == 0) { Refuse(p, itemGuid, $"The {item.Name} does not go on a hook."); return; }
            if (p.IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(p.ItemsInTradeWindow)) { Refuse(p, itemGuid, $"The {item.Name} is being traded."); return; }
            if (!p.TryRemoveFromInventoryWithNetworking(item.Guid, out var real, Player.RemoveFromInventoryAction.GiveItem)) { Refuse(p, itemGuid, null); return; }
            real.Location = null;
            real.PlacementPosition = null;
            if (!hook.TryAddToInventory(real, 0, false, false))
            {
                p.TryCreateInInventoryWithNetworking(real);   // back in the pack, nothing lost
                Refuse(p, itemGuid, "That did not work. It has been logged for staff.");
                Mod.Log.Warn($"[StaffBaseHooks] hook 0x{hook.Guid.Full:X8} would not take {real.Name} 0x{real.Guid.Full:X8} from {p.Name}");
                return;
            }
            real.SaveBiotaToDatabase();   // the hook saved itself in OnAddItem
            Mod.Log.Info($"[StaffBaseHooks] {p.Name} hung {real.Name} 0x{real.Guid.Full:X8} on hook 0x{hook.Guid.Full:X8}");
            Say(p, $"You hang the {real.Name} on the hook.");
        }

        /// <summary>A free hook's item back into the player's pack (the double-click, after the walk).</summary>
        /// <summary>Has a use of its own: a Use emote (the Fireworks 30735, the Pyreal Bell, the Snowmaker), as retail's
        /// "Once hooked, use this ...".</summary>
        static bool Usable(WorldObject item) =>
            item is Hooker || (item.Biota.PropertiesEmote?.Any(e => e.Category == EmoteCategory.Use) ?? false);

        static void TakeBack(Player p, Hook hook)
        {
            var item = hook.Item;
            if (item == null) return;
            if (!hook.TryRemoveFromInventory(item.Guid, out var real)) { p.SendTransientError("That did not work. It has been logged for staff."); return; }
            real.ContainerId = null;
            if (!p.TryCreateInInventoryWithNetworking(real))
            {
                hook.TryAddToInventory(real, 0, false, false);   // back on the hook, nothing lost
                real.SaveBiotaToDatabase();
                Say(p, $"You have no room for the {real.Name}.");
                return;
            }
            real.SaveBiotaToDatabase();
            Mod.Log.Info($"[StaffBaseHooks] {p.Name} took {real.Name} 0x{real.Guid.Full:X8} off hook 0x{hook.Guid.Full:X8}");
            Say(p, $"You take the {real.Name} off the hook.");
        }

        /// <summary>ACE's appraisal of a hook reads hook.ParentLink.HouseOwner. For a staff hook the
        /// appraisal runs with a stand-in parent (a bare hook made from the weenie, never in the world, with
        /// no owner), which is put back to none afterwards.</summary>
        [HarmonyPatch(typeof(AppraiseInfo), nameof(AppraiseInfo.BuildProfile))]
        static class Appraise
        {
            static void Prefix(WorldObject wo, ref bool __state)
            {
                __state = false;
                if (!Is(wo)) return;
                var stand = s_Bare.GetOrAdd(wo.WeenieClassId,
                    w => WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie(w), ObjectGuid.Invalid));
                if (stand == null) return;
                wo.ParentLink = stand;
                __state = true;
            }

            static Exception Finalizer(WorldObject wo, bool __state, Exception __exception)
            {
                if (__state && wo != null) wo.ParentLink = null;
                return __exception;
            }
        }
    }
}
