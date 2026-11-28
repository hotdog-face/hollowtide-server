using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(RecipeManager), nameof(RecipeManager.GetRecipeChance))]
    static class NamedTradeSkill
    {
        static bool Prefix(Player player, Recipe recipe, ref double? __result)
        {
            if (player == null || recipe == null || recipe.IsTinkering() || !RecipeManager.HasDifficulty(recipe)) return true;
            var skill = player.GetCreatureSkill((Skill)recipe.Skill);
            if (skill == null) return true;
            var moa = player.GetCreatureSkill(player.ConvertToMoASkill(skill.Skill));
            if (moa == null || moa.AdvancementClass >= SkillAdvancementClass.Trained) return true;
            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                $"You are not trained in {Words(moa.Skill.ToString())}.", ChatMessageType.Broadcast));
            __result = null;
            return false;
        }

        /// <summary>"WeaponTinkering" -> "Weapon Tinkering".</summary>
        static string Words(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
