using System;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE TWO FRIENDS-LIST REFUSALS IN RETAIL'S OWN WORDS (docs/SYSTEMS-SOCIAL.md, 2026-09-27).
    ///
    /// ACE answers both with plain system chat that retail never printed:
    ///   Player_Character.cs:162  "That character is already in your friends list"
    ///   Player_Character.cs:182  "That character is not in your friends list!"
    /// Retail's client has a WeenieErrorWithString case for each (acclient.exe HandleFailureEvent,
    /// docs/RETAIL-ERROR-STRINGS.md): 0x0562 @0x57439F "%s is already on your friends list!" and
    /// 0x0563 @0x5743B3 "That character is not on your friends list!", and ACE's own enum names the
    /// first (WeenieErrorWithString._IsAlreadyOnYourFriendsList). So the server sends the error the
    /// client formats, and the words become the client's.
    ///
    /// Prefixes that answer ONLY those two cases and return false; everything else (the self-add, the
    /// unknown name, the success paths) runs ACE's handler untouched. Patched on Player, the type
    /// that declares both methods.
    /// </summary>
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
