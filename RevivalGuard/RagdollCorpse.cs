using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;
using AcePosition = ACE.Entity.Position;

namespace RevivalGuard
{
    /// <summary>
    /// THE CORPSE LIES WHERE THE RAGDOLL LANDED (owner, 2026-09-27: "ragdolls look great, but the corpse
    /// pops back to where it was hit"; approved). Client-only ragdolls threw the body metres away while
    /// ACE left the corpse at the death spot, so it popped back. Server-authoritative fix: for a kill
    /// chosen by an OPTED-IN client (the killing player, or a player's own death), the corpse is placed a
    /// few metres along killer -> victim on safe ground, and the chooser's client is told the exact offset
    /// so its ragdoll comes to rest there. Everyone else gets retail.
    ///
    /// Opt-in: the client sends `@ragdollcorpse 1|0` after this mod's "ragdoll" hello (sent from
    /// ModChannel.Hello), and again when the player toggles ragdolls. Stored per player guid.
    ///
    /// The walk: 0.5 m steps up to D = clamp(5 * clamp((1.8/h)^0.4, 0.6, 1.2), 3, 6) metres, h the victim's
    /// height; it stops at the first step that is indoors or in a building cell, in water, more than 1 m
    /// up or down from the last good step, or not walkable. Under 1.5 m of good ground: no move. If ACE
    /// then cannot place the corpse at the new spot, it goes back to the death spot and is placed again,
    /// exactly as ACE would have. Outdoor deaths only.
    ///
    /// THE PLAN IS MADE AT DEATH (Creature.Die / Player.Die, both declare it and Player's does not call
    /// base), not when the corpse appears 2+ s later: the chooser's client is told at once, before the
    /// Dead motion, so its ragdoll is thrown at the right spot from the first frame. When the corpse
    /// will NOT move (not opted in, indoors, no good ground, no recorded killer) the message says so
    /// explicitly ({"x":0,"y":0}), so no client ever throws the body somewhere the corpse will not be.
    /// Every opted-in mod client within 60 m hears it, not only the chooser, so a watcher's ragdoll
    /// lands right too. Corpse.EnterWorld then applies the stored plan, with the same fallback.
    /// </summary>
    static class RagdollCorpse
    {
        static readonly ConcurrentDictionary<uint, byte> s_OptedIn = new ConcurrentDictionary<uint, byte>();
        [ThreadStatic] static bool t_Retrying;

        public static void Register()
        {
            CommandManager.TryAddCommand(Handle, "ragdollcorpse", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client's ragdoll deaths: place your kills' corpses where the body lands.", "1|0");
        }

        /// <summary>Tell a mod-channel client that this shard places ragdoll corpses.</summary>
        public static void Hello(Session session) => ModChannel.Send(session, "ragdoll", "{\"ready\":1}");

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            bool on = parameters != null && parameters.Length > 0 && parameters[0] == "1";
            if (on) s_OptedIn[p.Guid.Full] = 1; else s_OptedIn.TryRemove(p.Guid.Full, out _);
        }

        sealed class Plan { public AcePosition From, To; public DateTime At; }
        static readonly ConcurrentDictionary<uint, Plan> s_Plans = new ConcurrentDictionary<uint, Plan>();
        const float HEAR_RANGE = 60f;

        [HarmonyPatch(typeof(Creature), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
        static class CreatureDie
        {
            static void Prefix(Creature __instance, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
                => NoteDeath(__instance, lastDamager, topDamager);
        }

        [HarmonyPatch(typeof(Player), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
        static class PlayerDie
        {
            static void Prefix(Player __instance, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
                => NoteDeath(__instance, lastDamager, topDamager);
        }

        /// <summary>At death: decide where the corpse will lie and say so at once.</summary>
        static void NoteDeath(Creature victim, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            try
            {
                if (victim?.Location == null) return;
                uint victimId = victim.Guid.Full;
                bool victimIsPlayer = victim is Player;
                // The corpse's KillerId is the TOP damager (Creature.Die); use the same one.
                var who = topDamager ?? lastDamager;
                var killer = who?.TryGetAttacker();
                Player chooser = victimIsPlayer ? (Player)victim : (killer as Player ?? who?.TryGetPetOwner());

                AcePosition good = null;
                var start = victim.Location;
                if (chooser != null && s_OptedIn.ContainsKey(chooser.Guid.Full) && killer?.Location != null && killer != victim
                    && !start.Indoors && (start.Cell & 0xFFFF) < 0x100)
                {
                    float h = victim.PhysicsObj != null ? victim.PhysicsObj.GetHeight() : 1.8f;
                    if (h <= 0.1f) h = 1.8f;
                    good = Walk(killer.Location, start, h);
                }
                if (s_Plans.Count > 256)                                 // corpses that never came (no-corpse creatures)
                    foreach (var kv in s_Plans)
                        if ((DateTime.UtcNow - kv.Value.At).TotalSeconds > 120) s_Plans.TryRemove(kv.Key, out _);
                s_Plans[victimId] = new Plan { From = new AcePosition(start), To = good, At = DateTime.UtcNow };

                float dx = 0f, dy = 0f;
                if (good != null)
                {
                    var g = good.ToGlobal(); var a = start.ToGlobal();
                    dx = g.X - a.X; dy = g.Y - a.Y;
                }
                string msg = "{\"v\":" + victimId + ",\"x\":" + dx.ToString("0.###", CultureInfo.InvariantCulture)
                           + ",\"y\":" + dy.ToString("0.###", CultureInfo.InvariantCulture) + "}";
                foreach (var p in PlayerManager.GetAllOnline())
                {
                    if (p?.Session == null || p.Location == null || !s_OptedIn.ContainsKey(p.Guid.Full)) continue;
                    if (!ModChannel.Capable(p.Session)) continue;
                    if (p != chooser && p.Location.DistanceTo(start) > HEAR_RANGE) continue;
                    ModChannel.Send(p.Session, "ragdoll", msg);
                }
            }
            catch (Exception)
            {
                // never let a placement nicety break a death
            }
        }

        /// <summary>The walk along killer -> victim; null for "stay where it died".</summary>
        static AcePosition Walk(AcePosition killerAt, AcePosition start, float h)
        {
            var from = killerAt.ToGlobal();
            var at = start.ToGlobal();
            var dir = new Vector2(at.X - from.X, at.Y - from.Y);
            if (dir.LengthSquared() < 0.0001f) return null;
            dir = Vector2.Normalize(dir);

            float k = Math.Clamp((float)Math.Pow(1.8 / h, 0.4), 0.6f, 1.2f);
            float reach = Math.Clamp(5f * k, 3f, 6f);

            AcePosition good = null;
            float goodDist = 0f, lastZ = start.GetTerrainZ();
            for (float s = 0.5f; s <= reach + 0.001f; s += 0.5f)
            {
                var p = start.FromGlobal(new Vector3(at.X + dir.X * s, at.Y + dir.Y * s, at.Z));
                if (p.Indoors || (p.Cell & 0xFFFF) >= 0x100) break;
                float z = p.GetTerrainZ();
                if (Math.Abs(z - lastZ) > 1f) break;
                p.PositionZ = z;
                if (!p.IsWalkable()) break;
                var cell = LScape.get_landcell(p.GetOutdoorCell());
                if (cell != null && cell.WaterType != LandDefs.WaterType.NotWater) break;
                good = p; goodDist = s; lastZ = z;
            }
            if (good == null || goodDist < 1.5f) return null;
            good.Rotation = start.Rotation;
            return good;
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.EnterWorld))]
        static class Place
        {
            static void Prefix(WorldObject __instance, out AcePosition __state)
            {
                __state = null;
                if (t_Retrying || !(__instance is Corpse corpse)) return;
                try
                {
                    if (corpse.VictimId == null || corpse.Location == null) return;
                    if (!s_Plans.TryRemove(corpse.VictimId.Value, out var plan) || plan.To == null) return;
                    if ((DateTime.UtcNow - plan.At).TotalSeconds > 60) return;
                    if (plan.From.DistanceTo(corpse.Location) > 3f) return;      // it moved before the corpse was made
                    var to = new AcePosition(plan.To) { Rotation = corpse.Location.Rotation };
                    __state = new AcePosition(corpse.Location);
                    corpse.Location = to;
                }
                catch (Exception)
                {
                    // never let a placement nicety break a death: ACE places the corpse as it would have
                    __state = null;
                }
            }

            static void Postfix(WorldObject __instance, AcePosition __state, ref bool __result)
            {
                if (__state == null || __result || t_Retrying) return;
                // ACE could not place it at the new spot: put it back where it died, as ACE would have
                try
                {
                    t_Retrying = true;
                    __instance.Location = __state;
                    __result = __instance.EnterWorld();
                }
                finally { t_Retrying = false; }
            }
        }
    }
}
