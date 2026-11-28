using System;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Network.Sequence;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Floating.</summary>
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
