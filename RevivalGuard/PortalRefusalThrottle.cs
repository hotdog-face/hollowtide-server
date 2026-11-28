using System;
using System.Collections.Generic;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
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
