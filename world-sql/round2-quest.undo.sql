-- INERT: content review, not applied anywhere. Round 2, The Drowned Reliquary (docs/content-review/round2/dungeon-quest.md).
-- Undo for round2-quest.sql (never copy an .undo.sql to OVH's datafixes): Ottavia's placement, the drop rows it added
-- to the creatures, the quest rows, and the weenies (every property, emote and create-list row cascades from the weenie).
-- Characters' quest registry rows (ace_shard) are left: harmless without the quest rows.
START TRANSACTION;
DELETE FROM landblock_instance WHERE guid = 0x727ECE00 OR weenie_Class_Id = 924120;
DELETE FROM weenie_properties_create_list WHERE weenie_Class_Id IN (924100, 924101) AND object_Id IN (924000, 924001);
DELETE FROM weenie_properties_create_list WHERE object_Id = 924000 AND weenie_Class_Id = 0 AND destination_Type = 8 AND ABS(shade - 0.6667) < 0.0001;
DELETE FROM quest WHERE name IN ('DrownedReliquary1', 'DrownedReliquary2', 'DrownedReliquary3', 'DrownedReliquary4', 'DrownedReliquaryTimer');
DELETE FROM weenie WHERE class_Id BETWEEN 924100 AND 924104 OR class_Id = 924120;
COMMIT;
