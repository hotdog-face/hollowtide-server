-- INERT: content review, not applied anywhere. Undo for round2-creatures-tidebound_sentinel.sql (never copy an .undo.sql to OVH's datafixes).
-- Every property row cascades from the weenie row (ace_world foreign keys, ON DELETE CASCADE). The dungeon track's
-- generator and landblock_instance rows that place 924001, if any, are the dungeon track's undo.
DELETE FROM weenie WHERE class_Id = 924001;
