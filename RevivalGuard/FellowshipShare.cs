using System;
using System.Reflection;
using ACE.Server.Entity;
using ACE.Server.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Retail's even-share table.</summary>
    [HarmonyPatch]
    static class FellowshipShare
    {
        internal const string P_ON = "rg_fellow_share_retail";

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Fellowship), "GetMemberSharePercent");
            if (m == null) Mod.Log.Error("[RevivalGuard] FellowshipShare: Fellowship.GetMemberSharePercent not found; ACE's share table stays");
            return m;
        }

        static bool s_Registered;
        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            if (!s_Registered)
            {
                s_Registered = true;
                PkCommon.RegisterBool(P_ON, true, "RevivalGuard FellowshipShare: retail's even-share table (9 fellows 31.1% each, not ACE's 30%)");
            }
            return true;
        }

        static void Postfix(Fellowship __instance, ref double __result)
        {
            try
            {
                if (!PropertyManager.GetBool(P_ON, true).Item) return;
                var retail = FellowshipShareRule.EvenSplit(__instance.GetFellowshipMembers().Count);
                if (retail.HasValue) __result = retail.Value;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] FellowshipShare: {e.Message}"); }
        }
    }
}
