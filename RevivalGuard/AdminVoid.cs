using System.Collections.Generic;
using System.Linq;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// `@voidtest` (Developer and Admin, acts on the caller; ours, not retail; owner 2026-10-20: "id also like to test void
    /// spells on an admin character"). Void Magic specialised at full ranks (as @crafttest) and lifted to VOID_SKILL so level 8
    /// spells (power 400; SkillCheck factor 0.07) land almost always; every player void spell learned (ACE's
    /// PlayerSpellTable, School Void, levels 1-8, as @addallspells); the components those spells' formulas take for
    /// THIS account (tapers differ per account), topped up to COMP_TARGET; and one Heart of Darkest Flame (nether caster,
    /// no wield requirement, 1.22 elemental damage; casters have no school restriction, the nether damage type is the bonus).
    /// Then every spell this shard adds or newly makes obtainable (ShardSpells, the one list; owner 2026-10-20: "any new
    /// spells made need to be added to my an admin character spellbook to test"), with their components too.
    /// `@testspells` is the same command. Idempotent: known spells are skipped, components and the caster are topped up.
    /// </summary>
    public static class AdminVoid
    {
        const uint VOID_SKILL = 450;      // level 8 power 400: 450 gives ~97% (SkillCheck.GetMagicSkillChance)
        const int COMP_TARGET = 100;      // of each void component; a cast burns at most one of each (250 of 40 kinds encumbered a test character)
        const int EXTRA_TARGET = 20;      // of a component only the shard's other spells take (the fireworks alone take a dozen kinds)
        const uint CASTER_WCID = 43848;   // Heart of Darkest Flame (ace_world 2026-10-20: DamageType Nether, ElementalDamageMod 1.22, no wield req)

        internal static void Register()
        {
            CommandManager.TryAddCommand(VoidTest, "voidtest", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "Admin test aid (ours, not retail): Void Magic specialized and high, every void spell and every spell this shard adds, their components and a nether caster.", "");
            CommandManager.TryAddCommand(VoidTest, "testspells", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "Admin test aid (ours, not retail): the same as @voidtest; every spell this shard adds, Void Magic, components, a nether caster.", "");
            Mod.Log.Info("[RevivalGuard] AdminVoid: @voidtest / @testspells (developer and admin)");
        }

        static void VoidTest(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;

            // Skill: as @crafttest (full specialised ranks), plus ACE's specialise InitLevel 10, then lifted to VOID_SKILL.
            var cs = p.GetCreatureSkill(Skill.VoidMagic);
            cs.AdvancementClass = SkillAdvancementClass.Specialized;
            var table = Player.GetSkillXPTable(SkillAdvancementClass.Specialized);
            if (table != null && table.Count > 0)
            {
                cs.Ranks = (ushort)(table.Count - 1);
                cs.ExperienceSpent = table[table.Count - 1];
            }
            if (cs.InitLevel < 10) cs.InitLevel = 10;
            if (cs.Base < VOID_SKILL) cs.InitLevel += VOID_SKILL - cs.Base;
            session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(p, cs));

            // Spells, and the components each one's formula takes for this account.
            var spellTable = DatManager.PortalDat.SpellTable;
            int learned = 0, known = 0;
            var need = new Dictionary<uint, int>();
            var names = new SortedDictionary<uint, List<string>>();
            foreach (var id in Player.PlayerSpellTable)
            {
                if (!spellTable.Spells.ContainsKey(id)) continue;
                var spell = new Spell(id, false);
                if (spell._spellBase == null || spell.School != MagicSchool.VoidMagic) continue;   // loadDB false: NotFound is always true
                known++;
                if (p.AddKnownSpell(id)) { learned++; session.Network.EnqueueSend(new GameEventMagicUpdateSpell(session, (ushort)id)); }
                var lvl = spell.Formula.Level;
                if (!names.TryGetValue(lvl, out var list)) names[lvl] = list = new List<string>();
                list.Add($"{spell.Name} ({id})");
                AddComps(p, spell, need);
            }

            // Ours and the retail spells we unlocked. LearnSpellWithNetworking, not AddKnownSpell, so a learn hook
            // (NetherRift: a quest flag, never the spellbook) still decides where the spell goes.
            int xLearned = 0;
            var extra = new List<string>();
            var needX = new Dictionary<uint, int>();
            foreach (var id in ShardSpells.All())
            {
                var spell = new Spell(id);
                if (spell.NotFound) { extra.Add($"{id} NOT FOUND"); continue; }
                extra.Add($"{spell.Name} ({id})");
                if (!p.SpellIsKnown(id)) { p.LearnSpellWithNetworking(id, false); if (p.SpellIsKnown(id)) xLearned++; }
                AddComps(p, spell, needX);
            }

            int stacks = 0, failed = 0;
            foreach (var wcid in need.Keys)
                TopUp(p, wcid, COMP_TARGET, ref stacks, ref failed);
            foreach (var wcid in needX.Keys)
                if (!need.ContainsKey(wcid)) TopUp(p, wcid, EXTRA_TARGET, ref stacks, ref failed);
            int casters = 0;
            if (p.GetNumInventoryItemsOfWCID(CASTER_WCID) == 0 && !p.EquippedObjects.Values.Any(o => o.WeenieClassId == CASTER_WCID))
                TopUp(p, CASTER_WCID, 1, ref casters, ref failed);

            Mod.Log.Info($"[RevivalGuard] @voidtest {p.Name}: void {cs.Base} (init {cs.InitLevel}, ranks {cs.Ranks}); spells {learned} new of {known}; "
                + $"components [{string.Join(",", need.Keys)}] topped to {COMP_TARGET}, others [{string.Join(",", needX.Keys.Where(k => !need.ContainsKey(k)))}] to {EXTRA_TARGET}, {stacks} stacks made; caster {(casters > 0 ? "given" : "already held")}; {failed} failed");
            foreach (var kv in names)
                Mod.Log.Info($"[RevivalGuard] @voidtest level {kv.Key}: {string.Join(", ", kv.Value)}");
            Mod.Log.Info($"[RevivalGuard] @voidtest shard spells {xLearned} new of {extra.Count}: {string.Join(", ", extra)}");

            session.Network.EnqueueSend(new GameMessageSystemChat(
                $"[Test spells] Void Magic is specialized at {cs.Base}. {learned} new void spells learned ({known} in all); "
                + $"{xLearned} new of the {extra.Count} spells this shard adds or unlocked. "
                + $"Components topped up ({need.Count} void kinds to {COMP_TARGET}, the rest to {EXTRA_TARGET})" + (casters > 0 ? ", and a Heart of Darkest Flame is in your pack" : "")
                + (failed > 0 ? $". {failed} items did not fit: make room and run it again" : "") + " (ours, not retail).", ChatMessageType.Broadcast));
        }

        static void AddComps(Player p, Spell spell, Dictionary<uint, int> need)
        {
            spell.Formula.GetPlayerFormula(p);
            foreach (var c in spell.Formula.PlayerFormula)
            {
                var wcid = Spell.GetComponentWCID(c);
                if (wcid != 0) need[wcid] = 1;
            }
        }

        /// <summary>Adds stacks of `wcid` until the pack holds `target` (counting what is already there).</summary>
        static void TopUp(Player p, uint wcid, int target, ref int made, ref int failed)
        {
            int have = p.GetNumInventoryItemsOfWCID(wcid);
            while (have < target)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null) { failed++; return; }
                int n = System.Math.Min(target - have, System.Math.Max(1, (int)(wo.MaxStackSize ?? 1)));
                if (n > 1) wo.SetStackSize(n);
                if (!p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); failed++; return; }
                have += wo.StackSize ?? 1;
                made++;
            }
        }
    }
}
