using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A MONSTER SENT HOME IS A TELEPORT (watchdog 261025-0027: Gotrok Extas drawn 48 m away and 21.9 m up the hill for
    /// seconds, gliding home in Ready). ACE's Creature.ForceHome (a returning monster whose MoveTo made no progress for 5 s:
    /// CancelMoveTo) puts the body at Home with SetPosition(Teleport) but sends an ordinary position update, the teleport
    /// stamp unchanged. A client then treats it as a move (InterpolateTo: under 100 m outdoors) and slides the body there at
    /// catch-up speed, 50 m in 5-7 s. Retail's server bumped the teleport stamp on every teleport (teleport_hook), so here the
    /// position goes out again with the next stamp, as ACE's own admin moves send it (SendUpdatePosition(true)); a client
    /// places the body at home at once. Retail had no ForceHome; this only makes ACE's say what it did.
    ///   @modifybool rg_forcehome_stamp false   ACE's plain update again
    /// </summary>
    [HarmonyPatch]
    static class ForceHomeStamp
    {
        internal const string P_ON = "rg_forcehome_stamp";
        static readonly ConcurrentDictionary<uint, bool> s_Logged = new();
        static int s_Count;

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.ForceHome), Type.EmptyTypes);
            if (m == null) Mod.Log.Error("[RevivalGuard] ForceHomeStamp: Creature.ForceHome() not found; monsters sent home keep ACE's plain update");
            return m;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            Mod.Log.Info("[RevivalGuard] ForceHomeStamp: a monster ACE sends home is sent as a teleport (rg_forcehome_stamp)");
            return true;
        }

        static void Prefix(Creature __instance, out Vector3 __state)
        {
            __state = Vector3.Zero;
            try { if (__instance.Location != null) __state = __instance.Location.ToGlobal(); } catch { }
        }

        static void Postfix(Creature __instance, Vector3 __state)
        {
            try
            {
                if (__instance is Player || __instance.Location == null || __instance.PhysicsObj == null) return;
                if (!PropertyManager.GetBool(P_ON, true).Item) return;
                __instance.SendUpdatePosition(true);   // PositionPack: adminMove takes the next teleport stamp
                int n = System.Threading.Interlocked.Increment(ref s_Count);
                float moved = __state == Vector3.Zero ? -1f : Vector3.Distance(__state, __instance.Location.ToGlobal());
                if (s_Logged.TryAdd(__instance.WeenieClassId, true) || n % 100 == 0)
                    Mod.Log.Info($"[RevivalGuard] ForceHomeStamp: {__instance.Name} ({__instance.WeenieClassId}) sent home {moved:0.0} m as a teleport "
                                 + $"(0x{__instance.Location.Cell:X8}); {n} since start; once per kind");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ForceHomeStamp: {e.Message}"); }
        }
    }
}
