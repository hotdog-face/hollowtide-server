using System;
using System.Collections.Generic;
using System.Reflection;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CREATE THAT ASKS FOR A SKILL THIS SHARD DOES NOT HAVE IS NOT REFUSED WHOLE. ACE's PlayerFactory.Create returns
    /// InvalidSkillRequested at the first non-Inactive skill its portal SkillTable lacks, and the client shows that as a
    /// name/database refusal. Creation offers the skills of the PLAYER's portal.dat, so an install with older DATs
    /// (Awareness, 25, still offered) could not make any character: live log 2026-10-23 00:01-00:59, 27 refusals under
    /// seven names ("tried to create with skill 25 that was not found in Portal dat"). Those entries are set Inactive
    /// before ACE reads them, the way a current client sends a skill it does not offer; the rest of the create is ACE's.
    /// The client sends only end-of-retail skills from 0.1.78 (AcServerSkills); this covers the clients already out.
    /// Switch: rg_chargen_skill_fix.
    /// </summary>
    [HarmonyPatch]
    public static class CharGenSkills
    {
        const string P_ON = "rg_chargen_skill_fix";

        static MethodBase TargetMethod() => AccessTools.Method(typeof(PlayerFactory), nameof(PlayerFactory.Create));
        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            bool found = TargetMethod() != null;
            if (!found) Mod.Log.Warn("[RevivalGuard] CharGenSkills: PlayerFactory.Create not found; not patched");
            return found;
        }

        static void Prefix(CharacterCreateInfo characterCreateInfo)
        {
            try
            {
                if (!ACE.Server.Managers.PropertyManager.GetBool(P_ON, true).Item) return;
                var list = characterCreateInfo?.SkillAdvancementClasses;
                var table = DatManager.PortalDat?.SkillTable?.SkillBaseHash;
                if (list == null || table == null) return;
                List<int> dropped = null;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == SkillAdvancementClass.Inactive || table.ContainsKey((uint)i)) continue;
                    list[i] = SkillAdvancementClass.Inactive;
                    (dropped ??= new List<int>()).Add(i);
                }
                if (dropped != null)
                    Mod.Log.Info($"[RevivalGuard] CharGenSkills: {characterCreateInfo.Name} asked for skill(s) {string.Join(",", dropped)} "
                                 + "this shard does not have (older game files); created without them");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CharGenSkills: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
