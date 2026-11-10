using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// AN IDLE LANDBLOCK IS NOT UNLOADED WITH A PLAYER IN IT (ticket 261109-0037, Rocket, Facility Hub Portal Gem).
    ///
    /// ACE unloads a block whose lastActiveTime is 5 minutes old: its heartbeat (every 5 s) queues it and UnloadLandblocks
    /// runs Landblock.Unload at the end of that world tick. lastActiveTime is refreshed only by a player's own Heartbeat
    /// (every 5 s) in that block. A player who teleports into a block in the seconds before its unload, before their first
    /// heartbeat there, is unloaded with it: RemoveWorldObjectInternal sets CurrentLandblock = null and destroys the
    /// physics body. Nothing ticks them after that: their action queue (the gem's UseDone, IsBusy = false) never runs,
    /// positions are never read, every use answers "You're too busy!" and only a relog clears it. Rocket's server_busy
    /// was exactly that body: cell NULL, cell id 0, not active, no contact, Location still the gem's destination.
    ///
    /// Here, just before ACE drains its destruction queue, a queued block with anyone in it (its players list, a player
    /// pending addition, or an online player whose CurrentLandblock is it) is taken out of the queue and marked active,
    /// as the player's own heartbeat would have done a moment later. Everything else unloads as before. A shutdown
    /// (ShutdownInProgress) unloads everything, as ACE intends. @reloadblock already refuses occupied blocks.
    /// Switch: rg_unload_guard (default on).
    /// </summary>
    [HarmonyPatch]
    public static class LandblockUnloadGuard
    {
        const string P_ON = "rg_unload_guard";

        static readonly MethodInfo s_Target = AccessTools.Method(typeof(LandblockManager), "UnloadLandblocks");
        static readonly FieldInfo s_Queue = AccessTools.Field(typeof(LandblockManager), "destructionQueue");
        static readonly FieldInfo s_Players = AccessTools.Field(typeof(Landblock), "players");
        static readonly FieldInfo s_Pending = AccessTools.Field(typeof(Landblock), "pendingAdditions");

        // A missing private member in a future ACE build skips this patch instead of throwing in PatchAll.
        static bool Prepare()
        {
            RegisterSwitches();
            if (s_Target != null && s_Queue != null && s_Players != null && s_Pending != null) return true;
            Mod.Log.Error("[RevivalGuard] LandblockUnloadGuard: LandblockManager.UnloadLandblocks/destructionQueue or "
                + "Landblock.players/pendingAdditions not found in this ACE build; occupied blocks are not protected");
            return false;
        }

        static MethodBase TargetMethod() => s_Target;

        // so @modifybool can switch them (ACE only modifies properties it knows)
        static int s_Registered;
        static void RegisterSwitches()
        {
            if (System.Threading.Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard LandblockUnloadGuard: an idle landblock with a player in it is not unloaded (false: ACE)");
                if (!b.ContainsKey(StrandedPlayerWatchdog.P_ON)) b[StrandedPlayerWatchdog.P_ON] = new Property<bool>(true, "RevivalGuard StrandedPlayerWatchdog: a player in no landblock for stranded.seconds is relogged");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LandblockUnloadGuard: switches not registered with @modifybool ({e.Message}); still read at their defaults"); }
        }

        static void Prefix()
        {
            try { Run(); }
            catch (Exception e) { TickGuard.Report("LandblockUnloadGuard", e); }
        }

        static void Run()
        {
            if (!(s_Queue.GetValue(null) is ConcurrentBag<Landblock> queue) || queue.IsEmpty) return;
            bool on = PropertyManager.GetBool(P_ON, true).Item;
            bool shutting = ServerManager.ShutdownInProgress;
            if (!on || shutting) return;

            var all = new List<Landblock>();
            while (queue.TryTake(out var lb)) all.Add(lb);

            var online = PlayerManager.GetAllOnline();
            foreach (var lb in all)
            {
                var who = Occupants(lb, online);
                if (!LandblockUnloadRule.Keep(on, shutting, who.Count))
                {
                    queue.Add(lb);
                    continue;
                }
                lb.SetActive();
                Mod.Log.Warn($"[RevivalGuard] unload guard: kept landblock 0x{lb.Id.Landblock:X4} loaded; ACE was about to "
                    + $"unload it as idle with {string.Join(", ", who)} in it (arrived before their first heartbeat there)");
            }
        }

        static List<string> Occupants(Landblock lb, IEnumerable<Player> online)
        {
            var names = new List<string>();
            void Add(Player p) { if (p != null && !names.Contains(p.Name)) names.Add(p.Name); }
            if (s_Players.GetValue(lb) is List<Player> players)
                foreach (var p in players.ToList()) Add(p);
            if (s_Pending.GetValue(lb) is Dictionary<ObjectGuid, WorldObject> pending)
                foreach (var wo in pending.Values.ToList()) Add(wo as Player);
            foreach (var p in online)
                if (p?.CurrentLandblock == lb) Add(p);
            return names;
        }
    }
}
