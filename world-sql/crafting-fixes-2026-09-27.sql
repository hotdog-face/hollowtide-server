-- Alchemy / cooking world-DB fixes, 2026-09-27 (docs/CRAFTING-ALCHEMY.md, docs/CRAFTING-COOKING.md).
-- Backup of every row touched: tools/gpubox-ace/crafting-backup-2026-09-27.sql.
-- After applying: @clearcache weenie (AGENT-BRIEF trap 7). Items already in the world keep their old copy.
USE ace_world;

-- ALCH-01. Weenie 24758 is the TREATED gypsum crucible (recipe 4544: Aqua Vitae on 24703 makes it;
-- recipe 4624 turns it into Gem of Lesser Mana Renewal), and its own description already says
-- "A treated eyebright and gypsum concoction in a crucible." Only its name and plural were the
-- untreated item's, so both stages read "Gypsum and Eyebright Crucible". Every other treated crucible
-- (54 of them) is "Treated <mineral> and <herb> Crucible"; the AC wiki's item page for this one is
-- titled and named "Treated Gypsum and Eyebright Crucible" with this exact description.
UPDATE weenie_properties_string SET value = 'Treated Gypsum and Eyebright Crucible'  WHERE object_Id = 24758 AND type = 1;
UPDATE weenie_properties_string SET value = 'Treated Gypsum and Eyebright Crucibles' WHERE object_Id = 24758 AND type = 20;

-- ALCH-02. Concentrated Alembic Incanta (52524) is used on the twelve mineral Peas (recipes 9096-9107)
-- and on nothing else, but its TargetType 0x04800480 (alchemy base/intermediate, misc, useless) does not
-- include SpellComponents (0x1000), the Peas' ItemType. The client therefore drew the red "invalid
-- target" cursor over every Pea, and retail's own targeting check (ItemHolder::TargetCompatibleWithObject
-- @00587520, called from TargetAcquired @00588ef0) refuses a target whose type is outside the source's
-- TargetType. Give it the mask its sibling Alembic Incanta (52525) and the Alembic (4747) carry: 0x002DFBEF.
UPDATE weenie_properties_int SET value = 3013615 WHERE object_Id = 52524 AND type = 94;
