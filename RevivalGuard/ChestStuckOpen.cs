using System;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A chest whose reset chain DIED stays open for ever.</summary>
    [HarmonyPatch(typeof(Container), nameof(Container.Heartbeat))]
    public static class ChestStuckOpen
    {
        static bool s_Failed;

        static void Postfix(Container __instance)
        {
            // A throw here would reach the landblock's tick: caught, said once.
            try { Run(__instance); }
            catch (Exception e) { if (!s_Failed) { s_Failed = true; Mod.Log.Error("[RevivalGuard] ChestStuckOpen: " + e.Message); } }
        }

        static void Run(Container __instance)
        {
            if (!(__instance is Chest c)) return;
            if (!c.IsOpen || c.CurrentLandblock == null) return;

            // Who is it open for?
            var viewerGuid = c.Viewer;
            if (viewerGuid != 0 && c.CurrentLandblock.GetObject(viewerGuid) is Player) return;

            Mod.Log.Info($"[RevivalGuard] chest stuck open: '{c.Name}' 0x{c.Guid.Full:X8} at "
                + $"{c.Location} was open with no viewer present (viewer 0x{viewerGuid:X8}), so its "
                + $"reset ActionChain died with its landblock -- resetting it. "
                + $"resetPending={c.ResetMessagePending}");
            // `ResetTimestamp` is internal to ACE, and Reset() compares its argument against it to discard a stale chain.
            var f = typeof(WorldObject).GetProperty("ResetTimestamp",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
            var ts = f?.GetValue(c) as double?;
            c.Reset(ts);
        }
    }
}
