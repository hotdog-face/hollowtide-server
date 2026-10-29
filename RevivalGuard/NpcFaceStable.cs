using System;
using System.Reflection;
using System.Threading;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A TOWN NPC KEEPS ITS FACE (player report 261022-0038: "town NPCs' hair changes colour and shape at random", Shoushi and
    /// elsewhere; docs/CLOUD-QUEUE.md item 20). ACE gives every non-player creature a random face each time the object is
    /// made (Creature.SetEphemeralValues -> GenerateNewFace: hair style and colour, eyes, nose, mouth, skin, from
    /// ThreadSafeRandom) unless its weenie stores that part. A landblock nobody is near unloads and is built again on the next
    /// visit, so the same shopkeeper came back with other hair. Here an NPC placed in the world (a static guid, the same on
    /// every load) draws its face from a Random seeded by that guid: ACE's own choice, made the same way every time. A spawned
    /// creature (dynamic guid) keeps ACE's random face. The client draws whatever ObjDesc arrives (ServerLink.ApplyAppearance).
    ///   @modifybool rg_npc_face_stable false   ACE's random faces again
    /// </summary>
    [HarmonyPatch]
    static class NpcFaceStable
    {
        internal const string P_ON = "rg_npc_face_stable";
        static FieldInfo s_Random;   // ACE.Common.ThreadSafeRandom's ThreadLocal<Random> random

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.GenerateNewFace), Type.EmptyTypes);
            if (m == null) Mod.Log.Error("[RevivalGuard] NpcFaceStable: Creature.GenerateNewFace() not found; NPC faces stay ACE's random ones");
            return m;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            s_Random = AccessTools.Field(typeof(ACE.Common.ThreadSafeRandom), "random");
            if (s_Random == null) { Mod.Log.Error("[RevivalGuard] NpcFaceStable: ThreadSafeRandom.random not found; NPC faces stay ACE's random ones"); return false; }
            Mod.Log.Info("[RevivalGuard] NpcFaceStable: an NPC placed in the world keeps one face across landblock reloads (rg_npc_face_stable)");
            return true;
        }

        static void Prefix(Creature __instance, out Random __state)
        {
            __state = null;
            try
            {
                if (__instance == null || __instance is Player || s_Random == null) return;
                uint g = __instance.Guid.Full;
                if (g < 0x70000000u || g >= 0x80000000u) return;   // ACE's static (placed) guids only
                if (!PropertyManager.GetBool(P_ON, true).Item) return;
                if (!(s_Random.GetValue(null) is ThreadLocal<Random> tl)) return;
                __state = tl.Value;
                tl.Value = new Random(unchecked((int)(g * 2654435761u)));
            }
            catch (Exception e) { __state = null; Mod.Log.Warn($"[RevivalGuard] NpcFaceStable: {e.Message}"); }
        }

        /// <summary>Always: this thread's Random goes back, whatever GenerateNewFace did.</summary>
        static Exception Finalizer(Exception __exception, Random __state)
        {
            if (__state != null)
                try { if (s_Random?.GetValue(null) is ThreadLocal<Random> tl) tl.Value = __state; }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NpcFaceStable restore: {e.Message}"); }
            return __exception;
        }
    }
}
