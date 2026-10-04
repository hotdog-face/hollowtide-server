using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CHEST WHOSE RESET CHAIN DIED STAYS OPEN FOR EVER.
    ///
    /// Owner playtest 2026-09-22 (WORLD-GEO-01), Arwic at 33.6N 56.9E: "reset perpetual 'Open' chest
    /// state to default closed/lootable state."
    ///
    /// IT IS NOT A MISSING RESET INTERVAL, which was the first guess and is wrong. `Chest` is
    /// careful about that: `ChestResetInterval` falls back to `Default_ChestResetInterval` (120 s)
    /// when the weenie has none AND refuses any value under 15 s, so every chest in the world has a
    /// real interval whatever the data says. Checked: the four chest weenies placed in 0xC6A9 carry
    /// no `ResetInterval` at all and would all get the 120 s default.
    ///
    /// THE RESET IS AN ActionChain, AND ActionChains DIE WITH THEIR LANDBLOCK. `Chest.Open` arms the
    /// reset by queueing one (Chest.cs:188-197) behind `AddDelaySeconds(ChestResetInterval)` and
    /// setting `ResetMessagePending = true`. Action chains are pumped from the landblock's own tick
    /// -- the same fact that made the teleport watchdog unable to fire from `Player.Heartbeat` (see
    /// <see cref="TeleportWatchdog"/>) -- so if the landblock unloads while that 120 s is still
    /// running, the chain is simply gone. Nothing closes the chest, and nothing re-arms:
    /// `ResetMessagePending` is only cleared by a reset that now never happens.
    ///
    /// A town like Arwic is exactly where this bites. Someone opens a chest, walks out of range, the
    /// landblock unloads a minute later with the chain still pending, and the chest is open for the
    /// rest of the server's life -- looted, un-resettable, and visibly wrong to everyone who passes.
    ///
    /// SO THE CHEST HEALS ITSELF, FROM ITS OWN HEARTBEAT. A chest IS in its landblock's heartbeat
    /// list (Landblock walks `sortedWorldObjectsByNextHeartbeat` over every world object, not just
    /// players), so when the landblock is loaded the chest ticks even though its dead chain does
    /// not. If it is open and the player it is open FOR is not there any more, the chain that should
    /// have closed it is not coming, and `Reset` is exactly the right thing to run.
    ///
    /// THE VIEWER TEST IS WHAT KEEPS THIS SAFE. `Chest.Open` already refuses a second player while
    /// `Viewer` is a live player, and `Reset` itself closes against whatever `Viewer` resolves to.
    /// Only acting when the viewer has GONE means a player standing at an open chest is never
    /// interrupted -- the case this could get wrong, and the reason the check is the viewer rather
    /// than a timer.
    /// </summary>
    /// <para>PATCHED ON <c>Container</c>, NOT <c>Chest</c>. `Chest` does not declare `Heartbeat`, it
    /// inherits it -- `Container_Tick.cs:13` is the override -- and Harmony resolves a
    /// `[HarmonyPatch(typeof(Chest), "Heartbeat")]` to nothing and throws "Undefined target method"
    /// at PatchAll. That does not fail quietly: it aborts `Mod.Initialize`, so the whole of
    /// RevivalGuard loads as **Inactive** and every other guard in it is off. It happened on
    /// 2026-09-22 and the shard ran unguarded until the next restart. Patch the type that DECLARES
    /// the method and filter by `is Chest` inside.</para>
    [HarmonyPatch(typeof(Container), nameof(Container.Heartbeat))]
    public static class ChestStuckOpen
    {
        static void Postfix(Container __instance)
        {
            if (!(__instance is Chest c)) return;
            if (!c.IsOpen || c.CurrentLandblock == null) return;

            // Who is it open for? Gone, or never resolvable, means the reset is not coming.
            var viewerGuid = c.Viewer;
            if (viewerGuid != 0 && c.CurrentLandblock.GetObject(viewerGuid) is Player) return;

            Mod.Log.Info($"[RevivalGuard] chest stuck open: '{c.Name}' 0x{c.Guid.Full:X8} at "
                + $"{c.Location} was open with no viewer present (viewer 0x{viewerGuid:X8}), so its "
                + $"reset ActionChain died with its landblock -- resetting it. "
                + $"resetPending={c.ResetMessagePending}");
            // `ResetTimestamp` is internal to ACE, and Reset() compares its argument against it to
            // discard a stale chain. Reading it by reflection rather than passing null: null would
            // fail that comparison whenever the field is set, and the reset would silently do
            // nothing -- the exact failure mode this patch exists to end.
            var f = typeof(WorldObject).GetProperty("ResetTimestamp",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
            var ts = f?.GetValue(c) as double?;
            c.Reset(ts);
        }
    }
}
