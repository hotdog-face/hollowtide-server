-- INERT: content review, not applied anywhere. Undo for round2-creatures-reliquary_keeper.sql (never copy an .undo.sql to OVH's datafixes).
-- Every property, emote, emote-action, generator and create-list row cascades from the weenie row (ace_world foreign keys,
-- ON DELETE CASCADE). The dungeon track's landblock_instance row for 924050, if any, is the dungeon track's undo.
DELETE FROM weenie WHERE class_Id IN (924003, 924050);
