-- Undo of drowned-reliquary-keepers.sql. (round2-dungeon.undo.sql removes 924050's placement.)
START TRANSACTION;
DELETE FROM landblock_instance WHERE guid = 0x759502F0;
DELETE FROM weenie_properties_create_list WHERE object_Id = 924006 AND weenie_Class_Id = 924104;
DELETE FROM weenie WHERE class_Id IN (924050, 924057);
COMMIT;
