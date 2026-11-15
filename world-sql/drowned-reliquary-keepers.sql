-- THE DROWNED RELIQUARY'S TWO MINI BOSSES (content batch 1 go-live, 2026-11-14). Replaces round2-creatures-reliquary_keeper.sql
-- for going live: that file's Reliquary Keeper (924003) is the round-2 model the owner rejected on 2026-11-06 ("they look
-- wrong"); his round-5 picks were the Reliquary Star (924006) and the Great Seahorse (924007), "both read as mini bosses"
-- (docs/content-review/VERDICTS.md). Apply AFTER round5-creature-keeper_star.sql, round5-creature-keeper_seahorse.sql and
-- round2-quest.sql (924104), and with round2-dungeon.sql (which places generator 924050 on the hall floor).
--
--   924050  the hall floor generator (round2-dungeon.sql places it at 0x5950010A 140 -40 -24): now spawns the Reliquary Star,
--           one at a time, 10 minutes after it dies. The Star "grew round the relic" (keeper-star.md), so its corpse always
--           holds the Reliquary Relic 924104, the step-4 hand-in (the role 924003 had).
--   924057  a new generator in the lower hall (0x5950022E 265 -70 -6, beside the wisp pack there, step 3's zone): the Great
--           Seahorse, one at a time, 10 minutes. No quest drop: its base's death treasure only.
-- Re-runnable: deletes its own rows first. Undo: drowned-reliquary-keepers.undo.sql.
START TRANSACTION;

DELETE FROM landblock_instance WHERE guid = 0x759502F0;
DELETE FROM weenie_properties_create_list WHERE object_Id = 924006 AND weenie_Class_Id = 924104;
DELETE FROM weenie WHERE class_Id IN (924050, 924057);

-- 924050 The Reliquary Star's generator (the shape of the round-2 Keeper's: Generic, retail generator setup and icon, one slot,
-- RegenerationType Destruction with a 600 s delay; OnTop where round2-dungeon.sql places it).
INSERT INTO weenie (class_Id, class_Name, type) VALUES (924050, 'ace924050-reliquarystargen', 1);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (924050, 81, 1), (924050, 82, 1), (924050, 93, 1044);
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (924050, 1, 1), (924050, 11, 1), (924050, 18, 1);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (924050, 41, 60), (924050, 43, 0);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (924050, 1, 'The Reliquary Star Gen');
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (924050, 1, 33555051), (924050, 8, 100667494);
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES
  (924050, -1, 924006, 600, 1, 1, 1, 1, -1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0);

-- the quest drop on the Star: 924104 Reliquary Relic, always (Treasure 8, shade 0 = not rolled), one per corpse.
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (924006, 8, 924104, 1, 0, 0, 0);

-- 924057 The Great Seahorse's generator, same shape.
INSERT INTO weenie (class_Id, class_Name, type) VALUES (924057, 'ace924057-greatseahorsegen', 1);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (924057, 81, 1), (924057, 82, 1), (924057, 93, 1044);
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (924057, 1, 1), (924057, 11, 1), (924057, 18, 1);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (924057, 41, 60), (924057, 43, 0);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (924057, 1, 'The Great Seahorse Gen');
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (924057, 1, 33555051), (924057, 8, 100667494);
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES
  (924057, -1, 924007, 600, 1, 1, 1, 1, -1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
  (0x759502F0, 924057, 0x5950022E, 265.0, -70.0, -6.0, 1.0, 0.0, 0.0, 0.0, 0);

COMMIT;
