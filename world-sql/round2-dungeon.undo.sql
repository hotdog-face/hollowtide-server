-- INERT: content review, not applied anywhere. Round 2, The Drowned Reliquary (docs/content-review/round2/dungeon-quest.md).
-- Undo for round2-dungeon.sql (never copy an .undo.sql to OVH's datafixes). Removes every row it wrote: the landblock
-- instances (guids 0x75950100-0x759503FF, 0x726F1E00-0x726F1E0F, and any row of its weenies or of 924050), then the weenies
-- (every property, position, generator and emote row cascades from the weenie row). 924050 itself is the Keeper track's.
START TRANSACTION;
DELETE FROM landblock_instance WHERE guid BETWEEN 0x75950100 AND 0x759503FF OR guid BETWEEN 0x726F1E00 AND 0x726F1E0F;
DELETE FROM landblock_instance WHERE weenie_Class_Id = 924050 OR weenie_Class_Id BETWEEN 924051 AND 924099 OR weenie_Class_Id BETWEEN 924200 AND 924299;
DELETE FROM weenie WHERE class_Id BETWEEN 924051 AND 924099 OR class_Id BETWEEN 924200 AND 924299;
COMMIT;
