-- INERT: content review, not applied anywhere. Content review batch 1, quest line (docs/content-review/quest-line.md).
-- Undo for batch1-quest-props.sql (never copy an .undo.sql to OVH's datafixes). Every property, book, position
-- and emote row cascades from the weenie row (ace_world foreign keys, ON DELETE CASCADE).
START TRANSACTION;
DELETE FROM landblock_instance WHERE weenie_Class_Id BETWEEN 920204 AND 920210;
DELETE FROM weenie WHERE class_Id IN (920200, 920201, 920202, 920203, 920204, 920205, 920206, 920207, 920208, 920209, 920210);
COMMIT;
