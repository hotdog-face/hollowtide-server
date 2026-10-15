using System;
using System.Collections.Generic;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ONE REFUSAL, NOT A WALL OF THEM (owner 2026-10-15: walking into a portal you are too low for printed "You are not
    /// powerful enough to interact with that portal!" over and over: "it should only say it once").
    ///
    /// ACE's Portal.OnCollideObject runs OnActivate on every collision, and a player standing in a portal collides on
    /// every movement update, so each one re-ran the requirement check and sent its refusal again. After a collision
    /// that did NOT send the player through (Teleporting stays false), further collisions of that player with that
    /// portal are ignored for HUSH_S seconds. A collision that does teleport is never held back; a deliberate Use
    /// (OnActivate through use) is not touched at all.
    /// </summary>
    [HarmonyPatch(typeof(Portal), nameof(Portal.OnCollideObject))]
    static class PortalRefusalThrottle
    {
        const double HUSH_S = 5.0;
        static readonly Dictionary<(uint player, uint portal), DateTime> s_Refused = new Dictionary<(uint, uint), DateTime>();
        static readonly object s_Lock = new object();

        static bool Prefix(Portal __instance, Player player)
        {
            if (__instance == null || player == null) return true;
            var key = (player.Guid.Full, __instance.Guid.Full);
            lock (s_Lock)
            {
                if (s_Refused.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < HUSH_S) return false;
            }
            return true;
        }

        static void Postfix(Portal __instance, Player player)
        {
            try
            {
                if (__instance == null || player == null) return;
                var key = (player.Guid.Full, __instance.Guid.Full);
                lock (s_Lock)
                {
                    if (player.Teleporting) { s_Refused.Remove(key); return; }
                    s_Refused[key] = DateTime.UtcNow;
                    if (s_Refused.Count > 2000)   // stale pairs from players long gone
                    {
                        var cut = DateTime.UtcNow.AddSeconds(-HUSH_S);
                        var old = new List<(uint, uint)>();
                        foreach (var kv in s_Refused) if (kv.Value < cut) old.Add(kv.Key);
                        foreach (var k in old) s_Refused.Remove(k);
                    }
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PortalRefusalThrottle: {e.Message}"); }
        }
    }
}
