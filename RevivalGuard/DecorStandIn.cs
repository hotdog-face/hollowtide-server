using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Map pieces on live, seen by everyone.</summary>
    internal static partial class Decor
    {
        const uint PIECE_TEMPLATE = 292;   // decor.py PIECE_TEMPLATE: a plain retail decoration, no use, no emotes
        static readonly TimeSpan STAND_HIDE_FOR = TimeSpan.FromMinutes(3);
        const float STAND_NEAR = 300f;     // stand-ins sent or taken back around a decorator who opens or closes Decorate

        /// <summary>Layer key (n:id) -> its stand-in's guid; and the guids, for the save, track and info checks (any thread).</summary>
        static readonly ConcurrentDictionary<string, uint> s_Stand = new ConcurrentDictionary<string, uint>();
        static readonly ConcurrentDictionary<uint, string> s_StandGuids = new ConcurrentDictionary<uint, string>();
        /// <summary>Key -> the layer entry it was made from (the world pose can differ a hair once physics sets it down).</summary>
        static readonly ConcurrentDictionary<string, string> s_StandFrom = new ConcurrentDictionary<string, string>();
        /// <summary>Player guid -> their last @decor command, while Decorate is open.</summary>
        static readonly ConcurrentDictionary<uint, DateTime> s_Decorating = new ConcurrentDictionary<uint, DateTime>();

        internal static bool IsStandIn(WorldObject wo) => wo != null && s_StandGuids.ContainsKey(wo.Guid.Full);

        /// <summary>An added map piece a server object can show: on live, a Setup or GfxObj model, placed (not "gone").</summary>
        static bool StandEligible(JsonObject e)
        {
            if (!LiveMode || e == null || (string)e["kind"] != "staticadd" || !(e["after"] is JsonObject)) return false;
            try { uint cls = Hex((string)e["model"]!) >> 24; return cls == 0x02 || cls == 0x01; } catch { return false; }
        }

        /// <summary>Make the world's stand-in for one key match the layer (made, put back, resized or taken away).</summary>
        static bool SyncStand(string key)
        {
            if (key == null || !key.StartsWith("n:", StringComparison.Ordinal)) return false;
            var e = Get(key);
            bool want = StandEligible(e);
            WorldObject cur = s_Stand.TryGetValue(key, out var g) ? Find(null, g) : null;
            if (cur != null && want && s_StandFrom.TryGetValue(key, out var from) && from == e.ToJsonString()) return false;
            if (cur != null) cur.Destroy();
            if (s_Stand.TryRemove(key, out var old)) s_StandGuids.TryRemove(old, out _);
            if (!want) return cur != null;
            var after = (JsonObject)e["after"]!;
            uint blk = Hex((string)after["cell"]!) >> 16;
            // its landblock must be loaded (it is made again when it loads: ApplyBlock)
            if (!LandblockManager.GetLoadedLandblocks().Any(l => l.Id.Landblock == blk)) return cur != null;
            var wo = WorldObjectFactory.CreateNewWorldObject(PIECE_TEMPLATE);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] Decor: no weenie {PIECE_TEMPLATE} for a map piece's stand-in"); return cur != null; }
            wo.SetupTableId = Hex((string)e["model"]!);
            wo.Name = "Decoration";
            wo.SetProperty(PropertyBool.Stuck, true);
            wo.TimeToRot = -1;
            if (e["scale"] != null) wo.ObjScale = N(e["scale"]);
            NoFall(wo);   // a piece on a table or a wall stands where it was put (its template has Gravity)
            wo.Location = PosFrom(after);
            wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
            s_Stand[key] = wo.Guid.Full;
            s_StandGuids[wo.Guid.Full] = key;
            s_StandFrom[key] = e.ToJsonString();
            if (!wo.EnterWorld())
            {
                s_Stand.TryRemove(key, out _);
                s_StandGuids.TryRemove(wo.Guid.Full, out _);
                wo.Destroy();
                Mod.Log.Warn($"[RevivalGuard] Decor: map piece {key} ({e["model"]}) could not stand at {after["cell"]}");
                return cur != null;
            }
            return true;
        }

        /// <summary>Every stand-in matching the layer (Decorate opened, a revert, undo or redo): stale ones taken away too.</summary>
        static void SyncAllStands()
        {
            foreach (var key in s_Stand.Keys.ToList()) SyncStand(key);
            foreach (var ar in s_Areas.Values)
                foreach (var kv in ar.Edits.ToList())
                    if (kv.Key.StartsWith("n:", StringComparison.Ordinal)) SyncStand(kv.Key);
        }

        static bool HidesStands(Player p) =>
            p != null && s_Decorating.TryGetValue(p.Guid.Full, out var at) && DateTime.UtcNow - at < STAND_HIDE_FOR;

        /// <summary>Each @decor command from a decorator keeps the stand-ins off their client</summary>
        static void Decorating(Player me)
        {
            bool was = HidesStands(me);
            s_Decorating[me.Guid.Full] = DateTime.UtcNow;
            if (!was) SendStands(me, false);
        }

        /// <summary>@decor bye (Decorate closed): the stand-ins around them are sent again.</summary>
        static void StopDecorating(Player me)
        {
            s_Decorating.TryRemove(me.Guid.Full, out _);
            SendStands(me, true);
        }

        static void SendStands(Player me, bool show)
        {
            if (me?.Session?.Network == null || me.Location == null) return;
            foreach (var g in s_StandGuids.Keys.ToList())
            {
                var wo = Find(null, g);
                if (wo?.Location == null || wo.Location.DistanceTo(me.Location) > STAND_NEAR) continue;
                if (show) me.TrackObject(wo);
                else me.Session.Network.EnqueueSend(new GameMessageDeleteObject(wo));
            }
        }

        internal static bool KeepFrom(Player p, WorldObject wo) => IsStandIn(wo) && HidesStands(p);

        /// <summary>A teleport ends the hiding</summary>
        internal static void Teleported(Player p) { if (p != null) s_Decorating.TryRemove(p.Guid.Full, out _); }

        const string STAND_WHY = "This is how players see a map piece you placed. Click again to select the piece itself.";
    }

    /// <summary>A map piece's stand-in is not sent to someone decorating (their client draws the piece itself).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Teleport))]
    static class DecorStandInTeleport
    {
        static void Postfix(Player __instance)
        {
            try { Decor.Teleported(__instance); } catch { }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TrackObject))]
    static class DecorStandInTrack
    {
        static bool Prefix(Player __instance, WorldObject worldObject)
        {
            try { return !Decor.KeepFrom(__instance, worldObject); }
            catch { return true; }
        }
    }
}
