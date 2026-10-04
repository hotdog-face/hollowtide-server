-- Deru-Bound Keeper (901340) fights like a retail monster (owner 2026-10-02: it stood still at 5 m and never attacked).
-- lore_factions.py had set Tolerance (PropertyInt 67) = 64 Retaliate, which ACE's Player.CheckMonsters and the
-- monster's own scan both skip, so it woke only when struck. docs/FACTION-QUESTS.md section 4: the Keepers "kill
-- anyone who comes near" and are DeruUnbindingKillTask targets. The Falatacot Consort it clones (34973) has no Tolerance.
DELETE FROM weenie_properties_int WHERE object_Id = 901340 AND type = 67;
