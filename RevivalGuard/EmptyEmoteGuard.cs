using System;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AN EMOTE SET WITH NO STEPS CANNOT CRASH THE SERVER (live crash 2026-10-16 23:16 UTC). EmoteManager.Enqueue reads
    /// action emoteIdx of the chosen set with ElementAt and no check; a heartbeat set left empty by a datafix (the Hotel
    /// Swank pets' talk lines removed) threw ArgumentOutOfRange on the world thread and the process died. A set with no
    /// step at that index is now finished instead, the way Enqueue finishes a null set (Nested--), and logged once per
    /// object kind.
    /// </summary>
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.Enqueue))]
    static class EmptyEmoteGuard
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> s_Said = new System.Collections.Concurrent.ConcurrentDictionary<uint, bool>();

        static bool Prefix(EmoteManager __instance, PropertiesEmote emoteSet, int emoteIdx)
        {
            try
            {
                if (emoteSet == null) return true;   // ACE's own null path
                var actions = emoteSet.PropertiesEmoteAction;
                if (actions != null && emoteIdx >= 0 && emoteIdx < actions.Count) return true;
                __instance.Nested--;
                var wo = __instance.WorldObject;
                uint wcid = wo?.WeenieClassId ?? 0;
                if (s_Said.TryAdd(wcid, true))
                    Mod.Log.Warn($"[RevivalGuard] EmptyEmoteGuard: {wo?.Name} ({wcid}) chose a {emoteSet.Category} emote set with no step {emoteIdx}; skipped instead of crashing");
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] EmptyEmoteGuard: {e.Message}"); return true; }
        }
    }
}
