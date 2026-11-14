using System;
using System.Reflection;
using ACE.Server.Entity;
using ACE.Server.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// RETAIL'S EVEN-SHARE TABLE. ACE's Fellowship.GetMemberSharePercent gives a 9-member fellowship 30% each (270% in
    /// all); retail gave 31.1% (FellowshipSystem::GetEvenSplitXPPctg @005b9ba0, and the Feb 2002 Fever Dreams notes:
    /// "now share xp at 31% instead of 30"), so the total stays at 280% from 7 members on. Sizes 1 to 8 agree to 7 digits
    /// (retail's are floats). FellowshipShareRule holds the table. Switch: rg_fellow_share_retail (default on; false = ACE's table).
    /// </summary>
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
