using System.Collections.Concurrent;
using System.Reflection;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using AcLock = ACE.Server.WorldObjects.Lock;   // net10 has System.Threading.Lock under implicit usings

namespace RevivalGuard
{
    /// <summary>Keyrings open their chests.</summary>
    static class Keyrings
    {
        internal sealed class Ring
        {
            public string Code;          // the lock code of the key the ring holds
            public int KeyTargetType;    // that key's TargetType (a Master Key: 640, Container | Misc)
            public int RestTargetType;   // the ring weenie's own TargetType (16384, Key)
            public int KeyUsable;        // that key's ItemUseable (0x200008, SourceContainedTargetRemote)
            public int RestUsable;       // the ring weenie's own ItemUseable (0x80008, SourceContainedTargetContained)
        }

        static readonly ConcurrentDictionary<uint, Ring> s_Rings = new ConcurrentDictionary<uint, Ring>();
        static List<CookBook> s_Books;
        internal static int Hooked;
        internal static long Opened;

        public static void Register()
        {
            Mod.Log.Info($"[RevivalGuard] Keyrings: {Hooked}/3 hooks on (use-with-target, recipe target type, load target type). "
                       + "The banner proves nothing about reachability: grep ACE_Log.txt for 'Keyrings: ' + ' used ' to see the hook run.");
        }

        /// <summary>A keyring is a CraftTool with no KeyCode of its own whose add-a-key recipe names the key it holds. keys is NumKeys.</summary>
        internal static bool IsRing(WorldObject wo, out int keys, out Ring ring)
        {
            keys = 0; ring = null;
            if (wo == null) return false;
            if (wo.WeenieType != WeenieType.CraftTool && wo.WeenieType != WeenieType.Key) return false;
            if (!string.IsNullOrEmpty(wo.GetProperty(PropertyString.KeyCode))) return false;
            ring = s_Rings.GetOrAdd(wo.WeenieClassId, Derive);
            if (ring == null) return false;
            keys = wo.GetProperty(PropertyInt.NumKeys) ?? 0;
            return true;
        }

        static Ring Derive(uint wcid)
        {
            try
            {
                var ring = DatabaseManager.World.GetCachedWeenie(wcid);
                if (ring == null || (ring.WeenieType != WeenieType.CraftTool && ring.WeenieType != WeenieType.Key)) return null;
                if (s_Books == null) s_Books = DatabaseManager.World.GetAllCookbooks() ?? new List<CookBook>();
                foreach (var cb in s_Books)
                {
                    if (cb.SourceWCID != wcid) continue;
                    var key = DatabaseManager.World.GetCachedWeenie(cb.TargetWCID);
                    if (key == null || key.WeenieType != WeenieType.Key) continue;
                    var code = key.GetProperty(PropertyString.KeyCode);
                    if (string.IsNullOrEmpty(code)) continue;
                    return new Ring
                    {
                        Code = code,
                        KeyTargetType = key.GetProperty(PropertyInt.TargetType) ?? 0,
                        RestTargetType = ring.GetProperty(PropertyInt.TargetType) ?? (int)ItemType.Key,
                        KeyUsable = key.GetProperty(PropertyInt.ItemUseable) ?? 0,
                        RestUsable = ring.GetProperty(PropertyInt.ItemUseable) ?? (int)Usable.SourceContainedTargetContained,
                    };
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Keyrings: {e.GetType().Name} resolving the key for wcid {wcid}: {e.Message}"); }
            return null;
        }

        /// <summary>The TargetType and ItemUseable a ring should carry for this many keys.</summary>
        internal static bool RefreshUseFlags(WorldObject wo, int keys, Ring ring)
        {
            int target = keys > 0 ? (ring.RestTargetType | ring.KeyTargetType) : ring.RestTargetType;
            int usable = keys > 0 ? (ring.RestUsable | ring.KeyUsable) : ring.RestUsable;
            bool changed = false;
            if ((int)(wo.TargetType ?? ItemType.None) != target) { wo.TargetType = (ItemType)target; changed = true; }
            if ((int)(wo.ItemUseable ?? Usable.Undef) != usable) { wo.ItemUseable = (Usable)usable; changed = true; }
            return changed;
        }
    }

    /// <summary>Hook 1: a ring holding keys, used on a lock, opens it.</summary>
    [HarmonyPatch]
    static class KeyringUseOnLock
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.HandleActionUseWithTarget), new[] { typeof(uint), typeof(uint) });
            if (m == null) { Mod.Log.Error("[RevivalGuard] Keyrings: Player.HandleActionUseWithTarget(uint, uint) not found; a ring on a chest fails as ACE has it"); yield break; }
            Keyrings.Hooked++;
            yield return m;
        }

        static bool Prefix(Player __instance, uint sourceObjectGuid, uint targetObjectGuid)
        {
            try
            {
                var player = __instance;
                if (player == null || player.PKLogout) return true;
                var ring = player.FindObject(sourceObjectGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);
                if (!Keyrings.IsRing(ring, out var keys, out var info) || keys <= 0) return true;
                var target = player.FindObject(targetObjectGuid,
                    Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems | Player.SearchLocations.Landblock);
                if (!(target is AcLock)) return true;          // a ring on a key or a tool: ACE's recipes
                if (player.IsTrading && (ring.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)
                                         || target.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)))
                {
                    player.SendUseDoneEvent(WeenieError.TradeItemBeingTraded);
                    return false;
                }
                // The reachability line.
                Mod.Log.Info($"[RevivalGuard] Keyrings: {player.Name} used {ring.Name} (0x{ring.Guid}, {keys} keys, code {info.Code}) on {target.Name} (0x{target.Guid})");
                player.StopExistingMoveToChains();
                if (target.CurrentLandblock != null && target != player)
                {
                    if (player.IsBusy) { player.SendUseDoneEvent(WeenieError.YoureTooBusy); return false; }
                    player.CreateMoveToChain(target, ok =>
                    {
                        if (ok) Open(player, ring, target, keys, info);
                        else player.SendUseDoneEvent();
                    });
                }
                else Open(player, ring, target, keys, info);
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] Keyrings: {e.GetType().Name} in use-with-target: {e.Message}; ACE's path runs");
                return true;
            }
        }

        /// <summary>The Key branch of UnlockerHelper.UseUnlocker, with the ring's held key code in place of a Key object</summary>
        static void Open(Player player, WorldObject ring, WorldObject target, int keys, Keyrings.Ring info)
        {
            try
            {
                var req = ring.CheckUseRequirements(player);
                if (!req.Success)
                {
                    if (req.Message != null) player.Session.Network.EnqueueSend(req.Message);
                    player.SendUseDoneEvent();
                    return;
                }
                if (target is Door door && door.LockCode == "")   // the door is not to be opened with keys
                {
                    player.SendUseDoneEvent(WeenieError.YouCannotLockOrUnlockThat);
                    return;
                }
                var result = ((AcLock)target).Unlock(player.Guid.Full, null, info.Code);
                switch (result)
                {
                    case UnlockResults.UnlockSuccess:
                        keys = Math.Max(0, (ring.GetProperty(PropertyInt.NumKeys) ?? keys) - 1);
                        player.UpdateProperty(ring, PropertyInt.NumKeys, keys);
                        ring.SaveBiotaToDatabase();   // the spent key on disk now, not at the 5-minute save (a crash would hand it back)
                        if (Keyrings.RefreshUseFlags(ring, keys, info))
                            player.EnqueueBroadcast(new GameMessageUpdateObject(ring));   // the PWD carries both flags
                        Keyrings.Opened++;
                        var msg = $"The {target.Name} has been unlocked.\n"
                            + (keys == 0 ? "Your keyring is now empty." : $"Your keyring has {keys} key{(keys > 1 ? "s" : "")} left.");
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
                        Mod.Log.Info($"[RevivalGuard] Keyrings: {target.Name} (0x{target.Guid}) unlocked by {player.Name}'s {ring.Name}; {keys} keys left");
                        player.SendUseDoneEvent();
                        break;
                    case UnlockResults.Open:
                        player.SendUseDoneEvent(WeenieError.YouCannotLockWhatIsOpen);
                        break;
                    case UnlockResults.AlreadyUnlocked:
                        player.SendUseDoneEvent(WeenieError.LockAlreadyUnlocked);
                        break;
                    case UnlockResults.IncorrectKey:
                        player.SendUseDoneEvent(WeenieError.KeyDoesntFitThisLock);
                        break;
                    default:
                        player.SendUseDoneEvent(WeenieError.YouCannotLockOrUnlockThat);
                        break;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] Keyrings: {e.GetType().Name} opening {target?.Name}: {e.Message}");
                player.SendUseDoneEvent();
            }
        }
    }

    /// <summary>Hook 2: after a recipe changes a ring (a key added or popped), its TargetType follows its key count.</summary>
    [HarmonyPatch]
    static class KeyringRecipeTargetType
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(RecipeManager), nameof(RecipeManager.ModifyInt));
            if (m == null) { Mod.Log.Error("[RevivalGuard] Keyrings: RecipeManager.ModifyInt not found; a ring's use flags stay a fresh ring's after a recipe"); yield break; }
            Keyrings.Hooked++;
            yield return m;
        }

        static void Postfix(WorldObject source, WorldObject target)
        {
            try
            {
                foreach (var wo in new[] { source, target })
                    if (Keyrings.IsRing(wo, out var keys, out var info) && Keyrings.RefreshUseFlags(wo, keys, info))
                        Mod.Log.Info($"[RevivalGuard] Keyrings: {wo.Name} (0x{wo.Guid}) now targets 0x{(int)(wo.TargetType ?? 0):X}, usable 0x{(int)(wo.ItemUseable ?? 0):X}, with {keys} keys");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Keyrings: {e.GetType().Name} after a recipe: {e.Message}"); }
        }
    }

    /// <summary>Hook 3: a ring loaded from the database carries the TargetType its key count calls for</summary>
    [HarmonyPatch]
    static class KeyringLoadTargetType
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(WorldObjectFactory), nameof(WorldObjectFactory.CreateWorldObject), new[] { typeof(Biota) });
            if (m == null) { Mod.Log.Error("[RevivalGuard] Keyrings: WorldObjectFactory.CreateWorldObject(Biota) not found; a ring filled before this mod keeps a fresh ring's use flags until a recipe touches it"); yield break; }
            Keyrings.Hooked++;
            yield return m;
        }

        static void Postfix(WorldObject __result)
        {
            try
            {
                if (__result == null || __result.WeenieType != WeenieType.CraftTool) return;
                if (Keyrings.IsRing(__result, out var keys, out var info)) Keyrings.RefreshUseFlags(__result, keys, info);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Keyrings: {e.GetType().Name} on load: {e.Message}"); }
        }
    }
}
