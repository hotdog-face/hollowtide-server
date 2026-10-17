using System;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Network.Sequence;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// FLOATING (outside audit 2026-10-16, owner: "yes build the guard"). MovementGuard budgets the 2D distance only,
    /// and ACE's own z-check (Player_Tick.cs, "z-pos hacking") compares against LastGroundPos and acts only while the
    /// player is jumping, so a modified client that claims ground contact while it rises is never stopped: it can
    /// stand in the air above a fight or a wall.
    ///
    /// Outdoors (a landscape cell, index under 0x100) the ground under the player is known on the server
    /// (Landblock.GetZ, the terrain polygon). A player more than vertical.height metres above it for longer than
    /// vertical.seconds is floating: a jump or a fall off a cliff is over in two or three seconds. Roofs, towers and
    /// bridges are why the height is generous and why this starts as LOG ONLY: vertical.enforce (Tunables, off by
    /// default) turns on the snap back to the last place the player stood within the height. The log line carries
    /// the place, so a few days of logs show which structures honest players stand on before it enforces.
    /// Indoors (dungeons, building interiors) is left to the cell's own walls and ACE's transition. Staff are exempt.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    static class VerticalGuard
    {
        static double HEIGHT => Tunables.D("vertical.height", 20.0);     // metres above the terrain
        static double SECONDS => Tunables.D("vertical.seconds", 6.0);    // held that long
        static bool ENFORCE => Tunables.D("vertical.enforce", 0.0) > 0.5;

        sealed class State { public DateTime AboveSince; public DateTime LoggedAt; public ACE.Entity.Position LastLow; }
        static readonly ConditionalWeakTable<Player, State> s_State = new ConditionalWeakTable<Player, State>();

        static void Postfix(Player __instance, ACE.Entity.Position newPosition, bool forceUpdate)
        {
            try
            {
                var p = __instance;
                if (forceUpdate || p == null || newPosition == null || p.Teleporting) return;
                // ACCEPTED, NOT __result: UpdatePlayerPosition returns "crossed into another landblock", so a guard on
                // __result saw almost nothing (box test 2026-10-17). An accepted update is one ACE copied into Location.
                var at = p.Location;
                if (at == null || at.Cell != newPosition.Cell || System.Numerics.Vector3.DistanceSquared(at.Pos, newPosition.Pos) > 0.0001f) return;
                // staff are exempt, unless vertical.test_staff is set (box tests drive an admin character)
                if (p.Session != null && p.Session.AccessLevel > AccessLevel.Player && Tunables.D("vertical.test_staff", 0.0) < 0.5) return;
                var st = s_State.GetOrCreateValue(p);
                if ((newPosition.Cell & 0xFFFF) >= 0x100) { st.AboveSince = default; return; }   // indoors

                var lb = LScape.get_landblock(newPosition.Cell);
                if (lb == null) return;
                float ground = lb.GetZ(newPosition.Pos);
                double above = newPosition.PositionZ - ground;
                var now = DateTime.UtcNow;
                if (above <= HEIGHT)
                {
                    st.AboveSince = default;
                    st.LastLow = new ACE.Entity.Position(newPosition);
                    return;
                }
                if (st.AboveSince == default) { st.AboveSince = now; return; }
                double held = (now - st.AboveSince).TotalSeconds;
                if (held < SECONDS) return;

                if ((now - st.LoggedAt).TotalSeconds >= 30)
                {
                    st.LoggedAt = now;
                    Offences.Add(p, "vertical", $"{above:F1} m above the ground for {held:F0} s at {newPosition.ToLOCString()}"
                        + (ENFORCE ? "; snapped back" : " (log only)"));
                }
                if (!ENFORCE || st.LastLow == null) return;
                p.Location = new ACE.Entity.Position(st.LastLow);
                p.Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
                p.SendUpdatePosition();
                MovementGuard.Rebaseline(p);
                st.AboveSince = default;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] VerticalGuard: {e.Message}"); }
        }
    }
}
