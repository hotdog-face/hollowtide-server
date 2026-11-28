using System;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The two friends-list refusals in retail's own words.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionAddFriend))]
    public static class FriendsRetailWords_Add
    {
        static bool Prefix(Player __instance, string friendName)
        {
            try
            {
                if (__instance?.Session == null || __instance.Character == null) return true;
                if (string.Equals(friendName, __instance.Name, StringComparison.CurrentCultureIgnoreCase)) return true;
                var friend = PlayerManager.FindByName(friendName);
                if (friend == null) return true;
                if (!__instance.Character.HasAsFriend(friend.Guid.Full, __instance.CharacterDatabaseLock)) return true;
                __instance.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(__instance.Session,
                    WeenieErrorWithString._IsAlreadyOnYourFriendsList, friend.Name));
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] friends add words: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionRemoveFriend))]
    public static class FriendsRetailWords_Remove
    {
        static bool Prefix(Player __instance, uint friendGuid)
        {
            try
            {
                if (__instance?.Session == null || __instance.Character == null) return true;
                if (__instance.Character.HasAsFriend(friendGuid, __instance.CharacterDatabaseLock)) return true;
                __instance.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(__instance.Session,
                    (WeenieErrorWithString)0x0563, ""));
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] friends remove words: {e.Message}"); return true; }
        }
    }
}
