using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>`@crafttest`.</summary>
    public static class AdminCraft
    {
        static readonly Skill[] SKILLS =
        {
            Skill.WeaponTinkering, Skill.ArmorTinkering, Skill.ItemTinkering, Skill.MagicItemTinkering, Skill.Salvaging,
            Skill.Cooking, Skill.Fletching, Skill.Alchemy,
        };

        static readonly uint[] ITEMS = { 20646, 29581, 29576, 29573, 29577, 20986, 20988, 20987, 21059, 41956, 51445, 42622 };

        internal static void Register()
        {
            CommandManager.TryAddCommand(CraftTest, "crafttest", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Admin test aid (ours, not retail): the crafting skills at full, plus Ust, salvage, tailoring kits and loot to work on.", "");
            Mod.Log.Info("[RevivalGuard] AdminCraft: @crafttest (admin only)");
        }

        static void CraftTest(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            foreach (var s in SKILLS)
            {
                var cs = p.GetCreatureSkill(s);
                if (cs == null) continue;
                cs.AdvancementClass = SkillAdvancementClass.Specialized;
                // Full ranks, consistently: Ranks is LevelFromPP and ExperienceLeft reads ExperienceSpent, so both go to the specialized table's top.
                var table = Player.GetSkillXPTable(SkillAdvancementClass.Specialized);
                if (table != null && table.Count > 0)
                {
                    cs.Ranks = (ushort)(table.Count - 1);
                    cs.ExperienceSpent = table[table.Count - 1];
                }
                session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(p, cs));
            }
            int made = 0;
            foreach (var wcid in ITEMS)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo != null && p.TryCreateInInventoryWithNetworking(wo)) made++;
            }
            DeveloperCommands.HandleCILoot(session, "6", "5");
            session.Network.EnqueueSend(new GameMessageSystemChat(
                $"[Craft test] Weapon, armor, item and magic item tinkering, salvaging, cooking, fletching and alchemy are specialized at full. "
                + $"{made} crafting items and 5 pieces of loot are in your pack (ours, not retail).", ChatMessageType.Broadcast));
        }
    }
}
