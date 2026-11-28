using System;
using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(Container), nameof(Container.Heartbeat))]
    public static class ChestUnlockedRelock
    {
        static bool s_Failed;

        static void Postfix(Container __instance)
        {
            try { Run(__instance); }
            catch (Exception e) { if (!s_Failed) { s_Failed = true; Mod.Log.Error("[RevivalGuard] ChestUnlockedRelock: " + e.Message); } }
        }

        static void Run(Container __instance)
        {
            if (!(__instance is Chest c)) return;
            if (c.IsOpen || c.IsLocked || c.CurrentLandblock == null) return;
            if (!(c.GetProperty(PropertyBool.DefaultLocked) ?? false)) return;
            var unlockedAt = c.UseLockTimestamp;
            if (unlockedAt == null || Time.GetUnixTime() - unlockedAt.Value < c.ChestResetInterval) return;

            Mod.Log.Info($"[RevivalGuard] chest relocked: '{c.Name}' 0x{c.Guid.Full:X8} at {c.Location} was unlocked by "
                + $"0x{c.LastUnlocker ?? 0:X8} {Time.GetUnixTime() - unlockedAt.Value:F0} s ago and never opened");
            c.UseLockTimestamp = null;   // once: Reset leaves it set
            c.Reset(c.GetProperty(PropertyFloat.ResetTimestamp));
        }
    }
}
