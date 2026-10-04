using System.Collections.Concurrent;
using System.Reflection;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// @RELOADBLOCK: ONE LANDBLOCK BACK FROM THE DATABASE, WITHOUT A SHARD RESTART.
    ///
    /// ACE caches every landblock's `landblock_instance` rows the first time the block loads
    /// (WorldDatabaseWithEntityCache.GetCachedInstancesByLandblock) and never re-reads them. A block
    /// that idles out and loads again still comes back from that cache, so an edited row waits for a
    /// world restart. 2026-09-22: sixteen new gem placements in Hotel Swank (0x018A) cost a restart.
    ///
    /// THE TEARDOWN IS ACE'S OWN. When a block has been idle for UnloadInterval, Landblock.Tick calls
    /// LandblockManager.AddToDestructionQueue and, at the end of that world tick, UnloadLandblocks
    /// runs Landblock.Unload (save, destroy, release physics) and drops the block from the manager.
    /// This verb queues the block the same way and then runs UnloadLandblocks itself, straight away,
    /// so that nothing can walk into the block between the occupancy check and the unload. Then it
    /// clears the instance cache and loads the block again through LandblockManager.GetLandblock,
    /// the call every visit makes. No new teardown, no new loader.
    ///
    /// PLAYERS IN THE BLOCK: REFUSED. Landblock.Unload removes every object including players, and a
    /// player whose CurrentLandblock is null is the wedge this project knows too well (WHERE and FIND
    /// keep answering, teleports are ignored, only a relog clears it). Teleporting someone out is a
    /// worse answer than refusing, so the verb names who is there and stops. Outdoors, players in the
    /// eight adjacent blocks count too: their clients hold this block's objects and ACE's unload path
    /// does not tell them, so they would keep ghosts.
    ///
    /// THE WORK RUNS ON THE WORLD THREAD, from the PlayerManager.Tick postfix, not from the command
    /// handler. A chat command runs inside NetworkManager.DoSessionWork at the end of a world tick,
    /// but the console runs its commands on its own thread, and LandblockManager's lock and
    /// Landblock.Unload both assume the world thread. The handler only queues the request; the next
    /// PlayerManager.Tick (the first thing in a world tick, before any landblock ticks) does it. That
    /// ordering is what makes the occupancy check trustworthy: between the check and the unload no
    /// landblock ticks, no action queues run and no session is read.
    ///
    /// ACE ships @reload-landblock (Developer) which does the in-place variant: destroy every
    /// non-player object in the block you stand in and Init(true) it again with the cache cleared,
    /// keeping players where they are. It is not used here because it has not been tried on this
    /// shard and it acts on the block the admin is standing in; this verb takes the block by id and
    /// refuses an occupied one.
    ///
    ///   @reloadblock 018A        unload and reload landblock 0x018A (hex; 0x018A and 018A0000 work)
    ///
    /// Admin command feedback is ours, not retail's.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class LandblockReload
    {
        sealed class Request { public ushort Landblock; public Session Session; public string Who; }

        static readonly ConcurrentQueue<Request> s_Requests = new ConcurrentQueue<Request>();

        // ACE's own queue drain. Private static, so by name; checked at Register so a rename in a
        // future ACE build makes the verb refuse rather than throw inside the world loop.
        static readonly MethodInfo s_UnloadLandblocks =
            AccessTools.Method(typeof(LandblockManager), "UnloadLandblocks");

        internal static void Register()
        {
            if (s_UnloadLandblocks == null)
                Mod.Log.Error("[RevivalGuard] LandblockManager.UnloadLandblocks not found; @reloadblock will refuse");
            CommandManager.TryAddCommand(Handle, "reloadblock", AccessLevel.Admin, CommandHandlerFlag.None,
                "Unload one landblock and load it again from the database, so edited landblock_instance rows appear without a restart. Refused while anyone is in it.",
                "<landblock in hex, e.g. 018A>");
        }

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static void Handle(Session session, params string[] parameters)
        {
            if (parameters.Length == 0 || !TryParseLandblock(parameters[0], out var lb))
            {
                var here = session?.Player?.Location != null
                    ? $" You are in 0x{session.Player.Location.LandblockId.Landblock:X4}."
                    : "";
                Say(session, "Usage: @reloadblock <landblock in hex, e.g. 018A>. The block must be empty of players." + here);
                return;
            }
            if (s_UnloadLandblocks == null)
            {
                Say(session, "Not available: this ACE build has no LandblockManager.UnloadLandblocks. Nothing was changed.");
                return;
            }
            s_Requests.Enqueue(new Request { Landblock = lb, Session = session, Who = session?.Player?.Name ?? "console" });
        }

        /// <summary>Hex only: 018A, 0x018A, 018A0000, 0x018AFFFF. Decimal is refused rather than
        /// guessed, because 0394 and 394 name different blocks.</summary>
        internal static bool TryParseLandblock(string arg, out ushort landblock)
        {
            landblock = 0;
            if (string.IsNullOrWhiteSpace(arg)) return false;
            var s = arg.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (s.Length == 8) s = s.Substring(0, 4);
            if (s.Length != 4) return false;
            return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out landblock);
        }

        static void Postfix()
        {
            while (s_Requests.TryDequeue(out var r))
            {
                try { Run(r); }
                catch (Exception ex)
                {
                    Mod.Log.Error($"[RevivalGuard] @reloadblock 0x{r.Landblock:X4} by {r.Who} failed", ex);
                    Say(r.Session, $"0x{r.Landblock:X4}: the reload threw ({ex.GetType().Name}); see the server log. The block may need a visit to load again.");
                }
            }
        }

        static void Run(Request r)
        {
            var id = new LandblockId(((uint)r.Landblock << 16) | 0xFFFF);
            var world = DatabaseManager.World;

            if (!LandblockManager.IsLoaded(id))
            {
                // Nothing to tear down: dropping the cache is the whole fix, the next visit reads the rows.
                world.ClearCachedInstancesByLandblock(r.Landblock);
                int rows = world.GetCachedInstancesByLandblock(r.Landblock).Count;
                Say(r.Session, $"0x{r.Landblock:X4} is not loaded. Its cached instances are dropped; it will read {rows} instance rows from the database when someone next enters.");
                Mod.Log.Info($"[RevivalGuard] @reloadblock 0x{r.Landblock:X4} by {r.Who}: not loaded, cache dropped ({rows} rows now)");
                return;
            }

            var block = LandblockManager.GetLandblock(id, false);   // already loaded: returns it, loads nothing
            bool outdoors = block.PhysicsLandblock == null || !block.PhysicsLandblock.IsDungeon;

            var inside = new List<string>();
            var nearby = new List<string>();
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (p == null) continue;
                var loc = p.Location?.LandblockId;
                bool here = p.CurrentLandblock == block || (loc.HasValue && loc.Value.Landblock == r.Landblock);
                if (here) { inside.Add(p.Name); continue; }
                if (!outdoors) continue;
                bool adjacent = (p.CurrentLandblock != null && block.Adjacents.Contains(p.CurrentLandblock))
                             || (loc.HasValue && loc.Value.IsAdjacentTo(id));
                if (adjacent) nearby.Add($"{p.Name} in 0x{loc?.Landblock ?? p.CurrentLandblock.Id.Landblock:X4}");
            }
            // The block's own list, in case a player is mid-transfer and the two views above disagree.
            try
            {
                var own = Traverse.Create(block).Field<List<Player>>("players").Value;
                if (own != null)
                    foreach (var p in own.ToList())
                        if (p?.Name != null && !inside.Contains(p.Name)) inside.Add(p.Name);
            }
            catch { }

            if (inside.Count > 0 || nearby.Count > 0)
            {
                var why = inside.Count > 0
                    ? $"{string.Join(", ", inside)} {(inside.Count == 1 ? "is" : "are")} in it"
                    : $"{string.Join(", ", nearby)} {(nearby.Count == 1 ? "is" : "are")} next to it and would keep its objects";
                if (inside.Count > 0 && nearby.Count > 0) why += $"; {string.Join(", ", nearby)} next to it";
                Say(r.Session, $"Not reloaded: 0x{r.Landblock:X4} is occupied. {why}. Unloading a block with a player in it leaves them in no cell, so this refuses. Ask them to step out and try again.");
                return;
            }

            int before = world.GetCachedInstancesByLandblock(r.Landblock).Count;
            bool permaload = block.Permaload;

            // ACE's idle path, run to completion here on the world thread: nothing can enter the
            // block between the occupancy check above and the unload.
            world.ClearCachedInstancesByLandblock(r.Landblock);
            LandblockManager.AddToDestructionQueue(block);
            s_UnloadLandblocks.Invoke(null, null);

            if (LandblockManager.IsLoaded(id))
            {
                Say(r.Session, $"0x{r.Landblock:X4}: ACE could not unload it (see the server log for 'failed to unload'). The instance cache is dropped, so the next load after it idles out reads the database.");
                Mod.Log.Warn($"[RevivalGuard] @reloadblock 0x{r.Landblock:X4} by {r.Who}: UnloadLandblocks left it loaded");
                return;
            }

            var fresh = LandblockManager.GetLandblock(id, false, permaload);   // the load every visit makes
            int after = world.GetCachedInstancesByLandblock(r.Landblock).Count;

            var summary = $"0x{r.Landblock:X4} unloaded and loaded again: {after} instance rows read from the database (the old cache held {before}).";
            Say(r.Session, summary + (fresh != null ? " Objects appear as the block finishes loading." : ""));
            Mod.Log.Info($"[RevivalGuard] @reloadblock 0x{r.Landblock:X4} by {r.Who}: {after} rows (cache held {before}), permaload={permaload}");
            PlayerManager.BroadcastToAuditChannel(r.Session?.Player, $"{r.Who} reloaded landblock 0x{r.Landblock:X4} ({after} instance rows, cache held {before})");
        }
    }
}
