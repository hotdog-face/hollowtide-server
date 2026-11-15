-- INERT: content review, not applied anywhere. Undo for round5-creature-keeper_star.sql (never copy an .undo.sql to OVH's datafixes).
-- Every property row cascades from the weenie row (ace_world foreign keys, ON DELETE CASCADE).
DELETE FROM weenie WHERE class_Id = 924006;
