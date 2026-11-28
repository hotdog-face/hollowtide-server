using System;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>An attuned trophy on your own hook.</summary>
    static class AttunedOnOwnHook
    {
        [ThreadStatic] static uint t_Waive;

        [HarmonyPatch(typeof(Player), "HandleActionPutItemInContainer_Verify")]
        static class Verify
        {
            static void Prefix(Player __instance, uint itemGuid, uint containerGuid)
            {
                t_Waive = 0;
                try
                {
                    if (!(__instance?.CurrentLandblock?.GetObject(containerGuid) is Hook hook)) return;
                    if (AttunedHookRule.OwnHook(hook.House?.RootHouse?.HouseOwner, __instance.Guid.Full)) t_Waive = itemGuid;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AttunedOnOwnHook: {e.Message}"); }
            }

            static void Finalizer() => t_Waive = 0;
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsAttunedOrContainsAttuned), MethodType.Getter)]
        static class Getter
        {
            static void Postfix(WorldObject __instance, ref bool __result)
            {
                if (!__result || t_Waive == 0 || __instance == null) return;
                if (!AttunedHookRule.Attuned(true, __instance.Guid.Full, t_Waive))
                {
                    __result = false;
                    Mod.Log.Info($"[RevivalGuard] AttunedOnOwnHook: {__instance.Name} (0x{__instance.Guid.Full:X8}) allowed onto its owner's hook");
                }
            }
        }
    }
}
