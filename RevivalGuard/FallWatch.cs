using System;
using System.Reflection;
using ACE.Server.Physics.Collision;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// FALL DAMAGE, LOGGED (report 261020-0045: "Running on a hill at 69.0S, 79.8W, almost killed me and I wasnt even
    /// jumping"). Diagnostic only: nothing is changed. ACE's Player.HandleFallingDamage judges a landing from the SERVER
    /// body's own velocity (a FastTick PK's body is simulated between position reports), so a fall the client never
    /// made shows up here with the client's last ground report far from where the server landed. One line per hurt
    /// landing: the server's landing speed, where it landed, the client's last ground report and how far below it.
    /// The box could not reproduce the report (runs down the 45 degree slope at 0x1B29 (120, 4..30), 0.1.71 and 0.1.70
    /// pacing, no damage); this names the cause the next time it happens on the live shard.
    /// </summary>
    [HarmonyPatch]
    static class FallWatch
    {
        const float JUMP_V = 11.25434f, LEEWAY = 4.5f;   // ACE Player_Move.HandleFallingDamage's own constants

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Player), nameof(Player.HandleFallingDamage), new[] { typeof(EnvCollisionProfile) });
            if (m == null) Mod.Log.Error("[RevivalGuard] FallWatch: Player.HandleFallingDamage not found; fall logging is off");
            return m;
        }

        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Prefix(Player __instance)
        {
            try
            {
                var p = __instance; var po = p?.PhysicsObj;
                if (po == null || p.IsDead || p.Invincible) return;
                float vz = po.Velocity.Z;
                if (JUMP_V + vz + LEEWAY >= 0f) return;          // ACE deals no damage: not news
                var at = p.Location; var g = p.LastGroundPos;
                string ground = g == null ? "none"
                    : $"{g.ToLOCString()} ({(g.PositionZ - (at?.PositionZ ?? 0f)):0.0} m above the landing, " +
                      $"{(at == null ? -1f : Math.Sqrt(at.SquaredDistanceTo(g))):0.0} m away)";
                Mod.Log.Info($"[RevivalGuard] FallWatch: {p.Name} landed at {vz:0.0} m/s (ACE damage {-(JUMP_V + vz + LEEWAY) / JUMP_V * 87.29381f:0}) " +
                             $"at {at?.ToLOCString()}; client's last ground report {ground}; client contact={p.LastContact} " +
                             $"jumping={p.IsJumping} since jump {(DateTime.UtcNow - p.LastJumpTime).TotalSeconds:0.0}s pk={p.IsPKType}");
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] FallWatch: " + e.Message); }
        }
    }
}
