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
    /// <summary>
    /// KEYRINGS OPEN THEIR CHESTS. Retail's keyring (Master Keyring 23196 and its siblings, wiki item
    /// pages and the weenie's own Use text): "Use this ring on a master key to add the key to the
    /// ring. Use an intricate carving tool on the keyring to pop a key off again. Adding a key uses
    /// up one of the ring's remaining uses, but removing a key does not." Holds 24 keys, 50 uses in
    /// all; keys leave the ring by being used on a lock. ACE's world data carries all of that except
    /// the last step: recipe 4267 adds a key (Structure -1, NumKeys +1), recipe 4272 pops one off
    /// (NumKeys -1), and nothing in ACE reads NumKeys. docs/ACE-HOLES-2026-09-22.md has the audit.
    ///
    /// THE FIRST VERSION OF THIS FILE (8eb8f13) WAS UNREACHABLE, and its "3/3 hooks on" banner said
    /// nothing about that. Measured 2026-09-22 (OPEN-BUGS KEYRING-01):
    ///   (a) weenie 23196 is WeenieType 44 CraftTool, not 22 Key. Every retail ring is. So hooks on
    ///       Key.HandleActionUseOnTarget, LockHelper.Unlock(.., Key, ..) and the Key branch of
    ///       UnlockerHelper.UseUnlocker never see a ring; a ring falls to CraftTool's recipe path and
    ///       there is no ring-on-chest recipe.
    ///   (b) before any HandleActionUseOnTarget runs, Player.HandleActionUseWithTarget rejects the
    ///       pair on `(source.TargetType & target.ItemType) == 0` (Player_Use.cs:142): the ring's
    ///       TargetType is 16384 Key, a chest's ItemType is 512 Container. That is the "Cannot use
    ///       the Master Keyring with the Master's Holding" line the owner saw.
    ///
    /// So this version sits AHEAD of that check and on the type that declares each method:
    ///   1. Player.HandleActionUseWithTarget (prefix): a ring holding keys, used on a Lock (Chest or
    ///      Door), is opened here with the lock code of the key it holds, then one key comes off
    ///      (NumKeys -1). Structure is left alone, since retail spent uses on adding keys, not on
    ///      opening locks. Everything else (an empty ring, a ring on a key, a ring on a carving
    ///      tool) falls through to ACE, whose recipes work.
    ///   2. RecipeManager.ModifyInt (postfix) and 3. WorldObjectFactory.CreateWorldObject(Biota)
    ///      (postfix): while a ring holds keys, its TargetType is widened to Key | the key's own
    ///      TargetType (640, Container | Misc) and its ItemUseable to its own | the key's (the ring
    ///      ships as SourceContainedTargetContained, 0x80008, a key as SourceContainedTargetRemote,
    ///      0x200008); both narrow back when it is empty. That is what lets a CLIENT offer the drop
    ///      at all: the retail client refuses a use-with whose TargetType misses the target's ItemType
    ///      or whose target-use flags forbid a remote target (ItemHolder::IsTargetCompatibleWith
    ///      TargetingObject @00588070), and ours does the same in AcUseTarget.Compatible. ACE's own
    ///      RecipeManager.VerifyUse ORs the flags into search locations, so the wider ring still
    ///      finds its key in the pack for the add recipe. The recipe path already re-sends the object
    ///      (RecipeManager.UpdateObj) after its mods, so widening inside the mod is enough there;
    ///      the load path runs before the object is ever sent. Whether retail's server did exactly
    ///      this or shipped rings with a wider TargetType is NOT established (the pcap weenie is an
    ///      empty ring); the observable result, a loaded ring drops onto its chest, is retail's.
    ///
    /// The key it holds is read from the ring's own add-a-key recipe (cook_book source = ring, target
    /// = the key), so every ring in the data resolves without a table here; the casino rings, which
    /// ACE's authors gave a KeyCode of their own, are untouched. "The X has been unlocked." is ACE's
    /// line for any key; the keys-left line after it is OURS, patterned on ACE's "Your key has N uses
    /// left."; retail's wording for it is not established.
    ///
    /// REACHABILITY IS LOGGED FROM INSIDE THE HOOK, past every guard: the "Keyrings: <player> used
    /// <ring> ... on <target>" line at Info only prints when hook 1 actually intercepts a use, so a
    /// grep of ACE_Log.txt for it is the test, not the load banner.
    /// </summary>
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

        /// <summary>A keyring is a CraftTool (or a Key) with no KeyCode of its own whose add-a-key
        /// recipe names the key it holds. keys is NumKeys (a fresh ring has none).</summary>
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

        /// <summary>The TargetType and ItemUseable a ring should carry for this many keys. Sets them
        /// on the object and returns true when either changed; sending them (both ride in the
        /// PublicWeenieDesc, so an UpdateObject) is the caller's business.</summary>
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

    /// <summary>Hook 1: a ring holding keys, used on a lock, opens it. Runs ahead of ACE's
    /// TargetType pre-check, which is why it is on Player.HandleActionUseWithTarget and not on any
    /// HandleActionUseOnTarget.</summary>
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
                // THE REACHABILITY LINE. Past every guard; nothing else in this file prints at Info.
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

        /// <summary>The Key branch of UnlockerHelper.UseUnlocker, with the ring's held key code in
        /// place of a Key object, and a key spent instead of a use.</summary>
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

    /// <summary>Hook 2: after a recipe changes a ring (a key added or popped), its TargetType follows
    /// its key count. RecipeManager re-sends every modified object after its mods, so the client sees
    /// the new value without a send from here.</summary>
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

    /// <summary>Hook 3: a ring loaded from the database carries the TargetType its key count calls
    /// for, so a ring filled before this mod, or on another shard, drops onto its chest too.</summary>
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
