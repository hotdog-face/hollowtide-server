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
    /// <summary>@RELOADBLOCK: one landblock back from the database, without a shard restart.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    public static class LandblockReload
    {
        sealed class Request { public ushort Landblock; public Session Session; public string Who; }

        static readonly ConcurrentQueue<Request> s_Requests = new ConcurrentQueue<Request>();

        // ACE's own queue drain.
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

        /// <summary>Hex only: 018A, 0x018A, 018A0000, 0x018AFFFF.</summary>
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

            // ACE's idle path, run to completion here on the world thread: nothing can enter the block between the occupancy check above and the unload.
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
