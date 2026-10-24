using System;
using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CHEST UNLOCKED AND NEVER OPENED STAYS UNLOCKED FOR EVER.
    ///
    /// Newcomer bot, box shard 2026-10-24: one newcomer used the Pathwarden Supply Key on the shared Aluvian Pathwarden
    /// Chest in Holtburg and never opened it. The next newcomer's key met "The lock is already unlocked.", was never used
    /// up, and their Journal step sat on "use the key" for good. Live has the same weenie (33609) and the same ACE code:
    /// 29 Supply Keys are still carried there, 2 of them by characters who also hold the chest's gear (2026-10-24).
    ///
    /// WHY ACE NEVER RELOCKS IT. `Chest.Open` is the only thing that arms a reset (Chest.cs:188-197: an ActionChain after
    /// `ChestResetInterval`, then `Reset`, which closes, relocks a `DefaultLocked` chest and regenerates it). Unlocking
    /// arms nothing: `Chest.Unlock` only stamps `UseLockTimestamp` and `LastUnlocker` for the ninja-looting window. A
    /// chest unlocked and walked away from is unlocked until someone opens it or the landblock reloads.
    ///
    /// SO AN UNLOCKED, CLOSED CHEST RESETS ON ITS OWN INTERVAL. From the chest's heartbeat (as <see cref="ChestStuckOpen"/>):
    /// a `DefaultLocked` chest that is closed, unlocked, and was unlocked (`UseLockTimestamp`, cleared by `Open`) at least
    /// `ChestResetInterval` ago gets the same `Reset` an opened chest gets. The wait is the chest's own reset interval,
    /// the number ACE already uses for an opened one (this weenie says 1 s, which ACE floors to its 120 s default). The
    /// retail server's code is not available to check; a lock that resets on the chest's interval is what the data asks
    /// for, and nothing here acts on a chest someone has open.
    /// </summary>
    /// <para>Patched on <c>Container</c> (the type that declares <c>Heartbeat</c>), filtered by <c>is Chest</c>; see
    /// <see cref="ChestStuckOpen"/> for why patching <c>Chest</c> would take the whole mod down.</para>
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
