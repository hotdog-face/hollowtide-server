using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using R = RevivalGuard.TownRaidRule;

namespace RevivalGuard
{
    /// <summary>
    /// @adminspawn &lt;wcid&gt;: one creature in front of the admin that stays until it is killed (owner 2026-11-21: a Thunder
    /// Chicken he made with @create "vanished before he could kill it"). ACE's decay (WorldObject_Decay) takes a dynamic
    /// object with no generator, Lifespan or TimeToRot 5 minutes after it appears, the bug TownRaid.Spawn fixed for raiders;
    /// these get TimeToRot -1. So they never pile up, one with no player within 100 m for an hour is removed
    /// (R.AdminSpawnStale). The admin panel's Turkeys page sends it (AcAdminHud COMMANDS).
    /// </summary>
    internal static class AdminSpawn
    {
        sealed class Entry { public WeakReference<Creature> Ref; public double LastNear; public string Name; }
        static readonly List<Entry> s_Live = new List<Entry>();
        static readonly object s_Lock = new object();
        static double s_NextSweep;
        static double Now => ACE.Common.Time.GetUnixTime();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "adminspawn", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "One creature in front of you that does not rot away after 5 minutes (as @create's do); removed once killed, or after an hour with nobody within 100 m.",
                "<wcid>");
            Mod.Log.Info("[RevivalGuard] AdminSpawn: @adminspawn (creatures that stay until killed)");
        }

        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            if (me == null) return;
            void Reply(string t) => me.Session?.Network?.EnqueueSend(new GameMessageSystemChat(t, ChatMessageType.Broadcast));
            if (args.Length < 1 || !uint.TryParse(args[0], out var wcid)) { Reply("@adminspawn <wcid>"); return; }
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Reply($"No weenie {wcid}."); return; }
            if (!(wo is Creature c)) { wo.Destroy(); Reply($"{wcid} is not a creature: use @create for items."); return; }
            // where ACE's @create puts a creature (AdminCommands.CreateObjectForCommand)
            c.Location = me.Location.InFrontOf(5f, true);
            c.Location.LandblockId = new ACE.Entity.LandblockId(c.Location.GetCell());
            c.TimeToRot = -1;
            if (!c.EnterWorld()) { c.Destroy(); Reply($"{c.Name} could not be placed here."); return; }
            lock (s_Lock) s_Live.Add(new Entry { Ref = new WeakReference<Creature>(c), LastNear = Now, Name = c.Name });
            Reply($"{c.Name} (level {c.Level}) is here; it stays until killed.");
            PlayerManager.BroadcastToAuditChannel(me, $"{me.Name} has created {c.Name} (0x{c.Guid.Full:X8}) at {c.Location.ToLOCString()} (@adminspawn).");
        }

        /// <summary>Once a minute: drop the dead and the gone; remove one nobody has been near for an hour.</summary>
        internal static void Tick()
        {
            double now = Now;
            if (now < s_NextSweep) return;
            s_NextSweep = now + 60;
            List<Entry> list;
            lock (s_Lock) { s_Live.RemoveAll(e => !e.Ref.TryGetTarget(out var c) || c.IsDead || c.IsDestroyed); list = s_Live.ToList(); }
            if (list.Count == 0) return;
            var players = PlayerManager.GetAllOnline().Where(p => p.Location != null).ToList();
            foreach (var e in list)
            {
                if (!e.Ref.TryGetTarget(out var c) || c.Location == null) continue;
                if (players.Any(p => p.Location.DistanceTo(c.Location) <= R.AdminSpawnNearMetres)) { e.LastNear = now; continue; }
                if (!R.AdminSpawnStale(now - e.LastNear)) continue;
                lock (s_Lock) s_Live.Remove(e);
                var gone = c;
                var ch = new ActionChain();
                ch.AddAction(gone, () => { try { if (!gone.IsDead && !gone.IsDestroyed) gone.Destroy(); } catch (Exception x) { TickGuard.Report("AdminSpawn.Sweep", x); } });
                ch.EnqueueChain();
                Mod.Log.Info($"[RevivalGuard] AdminSpawn: {e.Name} 0x{c.Guid.Full:X8} removed (nobody within {R.AdminSpawnNearMetres:0} m for {(now - e.LastNear) / 60:0} min)");
            }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class AdminSpawnTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done) { s_Done = true; try { AdminSpawn.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] AdminSpawn: register: {e.Message}"); } }
            return true;
        }
        static void Postfix()
        {
            try { AdminSpawn.Tick(); }
            catch (Exception e) { TickGuard.Report("AdminSpawn", e); }
        }
    }
}
