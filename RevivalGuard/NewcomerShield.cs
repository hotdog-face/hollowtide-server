using System;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A NEW PLAYER'S FIRST STEPS (owner, 2026-10-11: browser guests start in Holtburg, "a lively game with people
    /// in Holtberg"; "protected until level 5").
    ///
    /// THE SHIELD. This shard is a PK server: ACE makes every character a player killer, and with
    /// pk_server_safe_training_academy a new one is non-PK in the Training Academy and for its grace period after
    /// leaving (PlayerFactory, Player_Death.cs PK_DeathTick). A character that starts in town (allow_town_spawn,
    /// every browser guest) never had the Academy, and anyone leaving it at level 2 was fair game ten minutes
    /// later. Now the countdown to PK does not run below level SHIELD_LEVEL (tunable newcomer.shield_level, 5):
    /// the character stays non-PK, and from that level on ACE's own countdown makes it PK as before. Told on
    /// entering the world and on reaching the level.
    ///
    /// THE TOWN KIT. The Academy's exit guard hands out an Academy Coat (one of ten colours), a Facility Hub
    /// portal gem and two Oil of Rendering (world db, academyguardexitholtburg: its Give emote for the exit token).
    /// A character that starts in town gets the same at creation, so skipping the tutorial costs nothing it would
    /// have given (the oil was missing until F7 261023-0043).
    /// </summary>
    static class NewcomerShield
    {
        static int SHIELD_LEVEL => (int)Tunables.D("newcomer.shield_level", 5);
        const uint COAT_FIRST = 13210, GEM = 49563, OIL = 12711;   // coatacademycolor1..10, ace49563-facilityhubportalgem, oilacademy
        static readonly Random s_Rng = new Random();

        static bool PkServer => PropertyManager.GetBool("pk_server").Item;

        internal static bool Shielded(Player p) =>
            PkServer && p != null && (p.Level ?? 1) < SHIELD_LEVEL && p.PlayerKillerStatus == PlayerKillerStatus.NPK;

        static void Tell(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        // the countdown to PK waits for the level
        [HarmonyPatch(typeof(Player), nameof(Player.PK_DeathTick))]
        static class HoldCountdown
        {
            static bool Prefix(Player __instance)
            {
                try { return !Shielded(__instance); }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class SayOnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    if (Shielded(__instance))
                        Tell(__instance, $"You are a newcomer: other players cannot attack you, and you cannot attack them, until you reach level {SHIELD_LEVEL}.");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] newcomer shield enter: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), "CheckForLevelup")]
        static class SayOnLevel
        {
            static void Prefix(Player __instance, out int __state) { __state = __instance.Level ?? 1; }
            static void Postfix(Player __instance, int __state)
            {
                try
                {
                    int now = __instance.Level ?? 1;
                    if (!PkServer || __state >= SHIELD_LEVEL || now < SHIELD_LEVEL || __instance.PlayerKillerStatus != PlayerKillerStatus.NPK) return;
                    // a character that never had a countdown (made before the Academy rule) starts one now
                    if (__instance.MinimumTimeSincePk == null) __instance.MinimumTimeSincePk = 0;
                    Tell(__instance, $"You have reached level {SHIELD_LEVEL}: your newcomer protection is ending. In a few minutes you can attack, and be attacked by, other players.");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] newcomer shield level: {e.Message}"); }
            }
        }

        // the Academy's parting gifts for a character that starts in town
        [HarmonyPatch(typeof(PlayerFactory), nameof(PlayerFactory.Create))]
        static class TownKit
        {
            static void Postfix(ref Player player, PlayerFactory.CreateResult __result)
            {
                try
                {
                    if (__result != PlayerFactory.CreateResult.Success || player == null) return;
                    if (player.GetProperty(PropertyBool.RecallsDisabled) == true) return;   // in the Academy: its guards give these
                    foreach (uint wcid in new[] { COAT_FIRST + (uint)s_Rng.Next(10), GEM, OIL, OIL })
                    {
                        var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                        if (wo != null && !player.TryAddToInventory(wo)) wo.Destroy();
                    }
                    Mod.Log.Info($"[RevivalGuard] town start: {player.Name} given the Academy coat, portal gem and two Oil of Rendering");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] town kit: {e.Message}"); }
            }
        }
    }
}
