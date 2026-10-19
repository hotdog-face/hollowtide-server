using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// `@crafttest` (admin only, ours, not retail; owner 2026-10-18: "add those skills and whats needed to an admin character so
    /// I can test those systems out"). ACE's own @trainskill is an empty stub, so this sets the invoking admin up for the
    /// crafting systems in one go: the crafting skills specialised at full ranks (weapon, armor, item and magic item
    /// tinkering, salvaging, cooking, fletching, alchemy), then Ust, salvage bags of the common materials, the tailoring kits and a reduction tool,
    /// and five pieces of treasure-generated tier 6 loot to work on (ACE's @ciloot: only looted items can be tinkered).
    /// Registered from AdminHouse.Register, so it adds no Harmony patch of its own.
    /// </summary>
    public static class AdminCraft
    {
        static readonly Skill[] SKILLS =
        {
            Skill.WeaponTinkering, Skill.ArmorTinkering, Skill.ItemTinkering, Skill.MagicItemTinkering, Skill.Salvaging,
            Skill.Cooking, Skill.Fletching, Skill.Alchemy,   // owner 2026-10-19: "tinkering and cooking"; fletching makes crystal ammo
        };

        // Ust 20646; salvage bags (retail weenies) of steel 29581, granite 29576, black opal 29573, imperial topaz 29577,
        // iron 20986, mahogany 20988, linen 20987, leather 21059; Armor and Weapon Tailoring Kits 41956, 51445; Armor
        // Main Reduction Tool 42622. Ids read from ace_world 2026-10-18.
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
                // FULL RANKS, consistently: Ranks is LevelFromPP and ExperienceLeft reads ExperienceSpent, so both go to
                // the specialized table's top (CreatureSkill.IsMaxRank / ExperienceLeft).
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
