using System;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// FELLOWSHIP INVITES ARE ACCEPTED BY DEFAULT (owner 2026-10-16: "make automatically accept fellowship requests an option
    /// thats on by default for all players").
    ///
    /// ACE's CharacterOptions1.Default carries IgnoreFellowshipRequests, so a new character silently ignored every invite,
    /// and AutoAcceptFellowRequest is off. For a new character (ACE's CharacterOptions1Default being set at creation) and
    /// once for every existing character (the next time it enters the world, remembered by a quest stamp), this turns
    /// "Automatically Accept Fellowship Requests" on and "Ignore Fellowship Requests" off. Both stay on the Options page
    /// as before, so a player who changes them afterwards keeps their choice.
    /// </summary>
    static class FellowshipDefaults
    {
        const string STAMP = "RevivalGuardFellowAutoAccept1";

        internal static void Apply(Player p)
        {
            p.SetCharacterOption(CharacterOption.AutomaticallyAcceptFellowshipRequests, true);
            p.SetCharacterOption(CharacterOption.IgnoreFellowshipRequests, false);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetCharacterOption))]
        static class AtCreation
        {
            static void Postfix(Player __instance, CharacterOption option, bool value)
            {
                try
                {
                    if (option != CharacterOption.CharacterOptions1Default || !value || __instance == null) return;
                    Apply(__instance);
                    __instance.QuestManager?.Stamp(STAMP);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FellowshipDefaults at creation: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnceForEveryone
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p == null || p.QuestManager == null || p.QuestManager.HasQuest(STAMP)) return;
                    Apply(p);
                    p.QuestManager.Stamp(STAMP);
                    Mod.Log.Info($"[RevivalGuard] FellowshipDefaults: {p.Name} now accepts fellowship invites automatically");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FellowshipDefaults on login: {e.Message}"); }
            }
        }
    }
}
