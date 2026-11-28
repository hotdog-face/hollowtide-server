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
    /// <summary>A monster sent home is a teleport.</summary>
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
