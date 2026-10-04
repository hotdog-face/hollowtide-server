using System.Linq;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A TELEPORT THAT NEVER COMPLETES BLOCKS EVERY LATER ONE, FOR EVER.
    ///
    /// Found 2026-09-21 while sweeping Hotel Swank. A bot was teleported to a position whose
    /// landblock-local y was out of range (`@teleloc 0x018A0249 91 -83 0.1` -- a dungeon whose own
    /// cells span negative y). From that moment every further teleport was **silently** dropped:
    /// no movement, no message to the player, nothing in the server log, and `@fixbusy` did not
    /// help. Fourteen consecutive @teleloc commands did nothing. Only relogging cleared it.
    ///
    /// The mechanism is in ACE, not in the client. `Player.Teleport` sets `Teleporting = true`
    /// (Player_Location.cs:679) and only `OnTeleportComplete` clears it (:760) -- and that method
    /// refuses to finish while the destination landblock has not reported
    /// `CreateWorldObjectsCompleted`, re-queueing itself every 0.1 s for ever:
    ///
    ///     if (CurrentLandblock != null &amp;&amp; !CurrentLandblock.CreateWorldObjectsCompleted)
    ///         ... AddDelaySeconds(0.1); AddAction(this, OnTeleportComplete); return;
    ///
    /// Meanwhile every entry point that matters is gated on the flag --
    /// `if (IsBusy || Teleporting || ...) return;` in Player.cs:1096, Player_Inventory.cs:1373 and
    /// :3192 -- so a stuck `Teleporting` does not only block teleports. It quietly blocks using and
    /// moving items too. A player can reach this without any admin command: a portal, a recall or a
    /// quest link whose destination has bad coordinates does the same thing, and the symptom they
    /// report is "I'm stuck and nothing works", which is unfalsifiable from the outside.
    ///
    /// ---------------------------------------------------------------------------------------
    /// WHY THE FIRST VERSION OF THIS FILE DID NOT WORK, AND WHY ACE'S OWN SAFETY NET DOES NOT EITHER
    ///
    /// The first version of this watchdog was a postfix on `Player.Heartbeat`.
    ///
    /// A CORRECTION TO WHAT THIS COMMENT FIRST CLAIMED. It said the Hotel Swank reproduction on
    /// 2026-09-21 proved the hook was wrong, because the watchdog never fired. That reproduction
    /// turned out to be a DIFFERENT failure -- `OnTeleportComplete` throwing partway through, with
    /// `Teleporting` already false (see <see cref="TeleportCompleteRescue"/>) -- so the watchdog
    /// stayed silent because there was no stuck flag to find, not because it could not run. The
    /// evidence was real and the conclusion drawn from it was not.
    ///
    /// The hook still had to move, on the argument below, which stands on its own without that
    /// reproduction: it is about where `Heartbeat` comes from, and it is read off ACE's own source.
    ///
    /// `Player.Heartbeat` is driven by the LANDBLOCK the player is in, not by the server:
    ///
    ///     // Landblock.TickSingleThreadedWork, Entity/Landblock.cs:597-606
    ///     while (sortedWorldObjectsByNextHeartbeat.Count > 0)
    ///         ... first.Heartbeat(currentUnixTime);
    ///
    /// A player in the middle of a failed teleport has been taken out of the old landblock and was
    /// never finished into the new one, so they are in nobody's heartbeat list and `Heartbeat`
    /// simply stops being called for them. A watchdog hanging off it cannot fire for precisely the
    /// players it exists to rescue. The same is true of the `OnTeleportComplete` retry chain above:
    /// ActionChains are pumped from the landblock tick too, so the 0.1 s re-check that is supposed
    /// to let the player materialise also stops running.
    ///
    /// **ACE's own guard has the identical flaw**, which is why nothing in stock ACE rescues this
    /// either. Player_Tick.cs:135 -- inside `Heartbeat` -- says:
    ///
    ///     if (Teleporting &amp;&amp; DateTime.UtcNow > ...LastTeleportStartTimestamp....Add(MaximumTeleportTime))
    ///         Session.LogOffPlayer(true);
    ///
    /// with `MaximumTeleportTime = 5 minutes`. So ACE already intends to rescue a stuck teleport by
    /// forcing a relog, and cannot reach a player who is in no landblock either. (The Swank bot that
    /// sat past five minutes without being logged off is NOT evidence of this -- its `Teleporting`
    /// was already false, so ACE's guard correctly did nothing. The flaw is structural and read off
    /// the source above, not demonstrated.)
    ///
    /// SO THIS HANGS OFF `PlayerManager.Tick` INSTEAD. That is called from `WorldManager`
    /// (Managers/WorldManager.cs:524), on the world loop, for the whole server -- it does not care
    /// which landblock a player is in or whether they are in one at all. `PlayerManager.GetAllOnline`
    /// is the same list ACE uses for broadcasts, so a wedged player is still in it.
    ///
    /// THE REMEDY IS GRADED, because the three ways to be stuck do not deserve the same answer:
    ///
    ///  1. The destination landblock HAS finished loading and only the dead retry chain is keeping
    ///     the player in the pink-bubble state. Then the honest fix is to run the thing that should
    ///     have run: `OnTeleportComplete()`. The player materialises where they meant to go and
    ///     loses nothing.
    ///  2. The landblock has not finished, or there is no landblock. Clearing `Teleporting` at
    ///     least gives the player their client back -- item use, item moves and further teleports
    ///     all un-gate -- so they can recall or @teleloc out under their own power.
    ///  3. Still `Teleporting` a minute and a half later, i.e. neither of the above took. Then do
    ///     what ACE always meant to do and force the relog, because a relog is the only remedy
    ///     that is PROVEN to clear this state (2026-09-21: it was the only thing that worked).
    ///     Ninety seconds rather than ACE's five minutes: a player who cannot act is not going to
    ///     wait five minutes, and the position they resume at is the one the server already holds.
    ///
    /// None of this papers over the cause. Every arm logs who was stuck, for how long, and where,
    /// which is the evidence trail for fixing the destination that did it.
    ///
    /// THREADING: `PlayerManager.Tick` runs on the world loop, the same thread that runs landblock
    /// ticks, so this calls `OnTeleportComplete` from exactly the thread that would have called it.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class TeleportWatchdog
    {
        /// <summary>Retail's own teleports settle in a second or two and ACE re-checks every 0.1 s;
        /// twenty seconds is far past any honest landblock load and far short of a play session.</summary>
        const double STUCK_SECONDS = 20.0;

        /// <summary>When the gentle arms have not worked, ACE's own remedy. See the class note for
        /// why this is 90 s and not ACE's five minutes.</summary>
        const double FORCE_LOGOFF_SECONDS = 90.0;

        static void Postfix()
        {
            var now = ACE.Common.Time.GetUnixTime();
            // ToList: the logoff arm can take a player out of the online list underneath us.
            foreach (var p in PlayerManager.GetAllOnline().ToList())
            {
                if (p == null || !p.Teleporting) continue;
                var started = p.LastTeleportStartTimestamp;
                if (started == null || started <= 0) continue;
                var stuck = now - started.Value;
                if (stuck < STUCK_SECONDS) continue;

                var who = $"{p.Name} (0x{p.Guid.Full:X8}) was Teleporting for {stuck:F0}s";

                if (stuck >= FORCE_LOGOFF_SECONDS)
                {
                    Mod.Log.Warn($"[RevivalGuard] teleport watchdog: {who} and did not recover -- "
                        + $"forcing the relog ACE's own five-minute rule intends but never reaches. "
                        + $"The destination it was sent to is the thing to look at: {p.Location}");
                    if (p.Session != null) p.Session.LogOffPlayer(true); else p.LogOut();
                    continue;
                }

                var lb = p.CurrentLandblock;
                if (lb != null && lb.CreateWorldObjectsCompleted)
                {
                    // Case 1: the world is ready and only the dead retry chain was missing.
                    Mod.Log.Warn($"[RevivalGuard] teleport watchdog: {who}; its landblock HAS "
                        + $"finished loading, so the re-check chain died with the landblock tick. "
                        + $"Completing the teleport by hand at {p.Location}");
                    p.OnTeleportComplete();
                    continue;
                }

                // Case 2: give the player their client back and let them move under their own power.
                p.Teleporting = false;
                Mod.Log.Warn($"[RevivalGuard] teleport watchdog: {who} and is now released. Its "
                    + $"landblock {(lb == null ? "is null" : "never reported CreateWorldObjectsCompleted")} "
                    + $"-- the destination it was sent to is the thing to look at: {p.Location}");
            }
        }
    }
}
