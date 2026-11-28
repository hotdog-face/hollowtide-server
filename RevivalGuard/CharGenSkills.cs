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
