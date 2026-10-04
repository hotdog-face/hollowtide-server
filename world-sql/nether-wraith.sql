-- NETHER WRAITH (docs/NETHER-WRAITH.md): a wisp that fed on the nether until it took a shape. Its
-- body is the client's (expansion/creatures/nether_wraith: a two-part glb and AcVfx effects). Generated
-- by tools/gpubox-ace/nether_wraith.py; edit that and regenerate.
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/nether-wraith.sql
--   then in game: @clearcache weenie ; @clearcache landblock ; @reloadblock AE3C
--
-- NEW ROWS ONLY: weenies 901400-901401 and instance guid 0x7AE3CC70. Nothing retail is modified
-- (the landblock had no instances, so there is nothing to back up). Re-runnable: it deletes its own
-- rows first. Cloned from the Ghost Wisp 1987 (setup 0x0200059C, motion table 0x09000031).
START TRANSACTION;

DELETE FROM landblock_instance WHERE weenie_Class_Id BETWEEN 901400 AND 901419;
DELETE FROM weenie WHERE class_Id BETWEEN 901400 AND 901419;

-- ===== 901400 Nether Wraith, level 44 =====
-- Smoke with a heart: hard to cut or pierce, a little easier to crush, burned by fire and lightning,
-- all but proof against the nether it is made of. Its touch is nether; it casts Nether Bolt IV (which
-- lands as the client's void_hollowing) and two void curses. Wisp (20), so its pack call wakes wisps.
-- 901400 <- clone of 1987
INSERT INTO weenie (class_Id, class_Name, type) SELECT 901400, 'acnetherwraith', type FROM weenie WHERE class_Id = 1987;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901400, type, value FROM weenie_properties_int WHERE object_Id = 1987;
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 901400, type, value FROM weenie_properties_int64 WHERE object_Id = 1987;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901400, type, value FROM weenie_properties_bool WHERE object_Id = 1987;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901400, type, value FROM weenie_properties_float WHERE object_Id = 1987;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 901400, type, value FROM weenie_properties_string WHERE object_Id = 1987;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901400, type, value FROM weenie_properties_d_i_d WHERE object_Id = 1987;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 901400, type, value FROM weenie_properties_i_i_d WHERE object_Id = 1987;
INSERT INTO weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_P_Spent) SELECT 901400, type, init_Level, level_From_C_P, c_P_Spent FROM weenie_properties_attribute WHERE object_Id = 1987;
INSERT INTO weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_P_Spent, current_Level) SELECT 901400, type, init_Level, level_From_C_P, c_P_Spent, current_Level FROM weenie_properties_attribute_2nd WHERE object_Id = 1987;
INSERT INTO weenie_properties_skill (object_Id, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time) SELECT 901400, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time FROM weenie_properties_skill WHERE object_Id = 1987;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 901400, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 1987;
INSERT INTO weenie_properties_body_part (object_Id, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b) SELECT 901400, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b FROM weenie_properties_body_part WHERE object_Id = 1987;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 901400, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 1987;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 901400, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 1987;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 901400, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 1987;
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 901400, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 1987;
INSERT INTO weenie_properties_event_filter (object_Id, event) SELECT 901400, event FROM weenie_properties_event_filter WHERE object_Id = 1987;
DELETE FROM weenie_properties_string WHERE object_Id = 901400 AND type = 1; INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901400, 1, 'Nether Wraith');
DELETE FROM weenie_properties_int WHERE object_Id = 901400 AND type = 2; INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901400, 2, 20);
DELETE FROM weenie_properties_int WHERE object_Id = 901400 AND type = 25; INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901400, 25, 44);
DELETE FROM weenie_properties_int WHERE object_Id = 901400 AND type = 146; INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901400, 146, 11000);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 31; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 31, 20);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 80; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 80, 3.0);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901400 AND type = 35; INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901400, 35, 459);
DELETE FROM weenie_properties_attribute WHERE object_Id = 901400;
INSERT INTO weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_P_Spent) VALUES (901400, 1, 120, 0, 0), (901400, 2, 150, 0, 0), (901400, 3, 140, 0, 0), (901400, 4, 130, 0, 0), (901400, 5, 170, 0, 0), (901400, 6, 180, 0, 0);
DELETE FROM weenie_properties_attribute_2nd WHERE object_Id = 901400;
INSERT INTO weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_P_Spent, current_Level) VALUES (901400, 1, 120, 0, 0, 195), (901400, 3, 150, 0, 0, 300), (901400, 5, 200, 0, 0, 380);
DELETE FROM weenie_properties_skill WHERE object_Id = 901400;
INSERT INTO weenie_properties_skill (object_Id, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time) VALUES (901400, 6, 0, 2, 0, 170, 0, 0), (901400, 7, 0, 2, 0, 190, 0, 0), (901400, 15, 0, 2, 0, 180, 0, 0), (901400, 20, 0, 2, 0, 20, 0, 0), (901400, 22, 0, 2, 0, 25, 0, 0), (901400, 24, 0, 2, 0, 150, 0, 0), (901400, 31, 0, 2, 0, 150, 0, 0), (901400, 43, 0, 2, 0, 190, 0, 0), (901400, 45, 0, 2, 0, 160, 0, 0);
UPDATE weenie_properties_body_part SET base_Armor = 90, armor_Vs_Slash = 90, armor_Vs_Pierce = 90, armor_Vs_Bludgeon = 90, armor_Vs_Cold = 90, armor_Vs_Fire = 90, armor_Vs_Acid = 90, armor_Vs_Electric = 90, armor_Vs_Nether = 90, d_Val = 0, d_Var = 0 WHERE object_Id = 901400;
UPDATE weenie_properties_body_part SET d_Type = 1024, d_Val = 24, d_Var = 0.5 WHERE object_Id = 901400 AND `key` IN (0, 17);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 64; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 64, 0.6);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 65; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 65, 0.5);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 66; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 66, 0.8);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 67; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 67, 1.25);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 68; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 68, 0.8);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 69; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 69, 1.0);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 70; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 70, 1.3);
DELETE FROM weenie_properties_float WHERE object_Id = 901400 AND type = 166; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901400, 166, 0.2);
DELETE FROM weenie_properties_spell_book WHERE object_Id = 901400;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) VALUES (901400, 5352, 2.15), (901400, 5373, 2.08), (901400, 5381, 2.05);

-- ===== 901401 Nether Wraith Gen: three, scattered 15 m, five minutes to return =====
INSERT INTO weenie (class_Id, class_Name, type) VALUES (901401, 'acnetherwraithgen', 1);
DELETE FROM weenie_properties_int WHERE object_Id = 901401 AND type = 81; INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901401, 81, 3);
DELETE FROM weenie_properties_int WHERE object_Id = 901401 AND type = 82; INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901401, 82, 3);
DELETE FROM weenie_properties_int WHERE object_Id = 901401 AND type = 93; INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901401, 93, 1044);
DELETE FROM weenie_properties_bool WHERE object_Id = 901401 AND type = 1; INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (901401, 1, 1);
DELETE FROM weenie_properties_bool WHERE object_Id = 901401 AND type = 11; INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (901401, 11, 1);
DELETE FROM weenie_properties_bool WHERE object_Id = 901401 AND type = 18; INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (901401, 18, 1);
DELETE FROM weenie_properties_float WHERE object_Id = 901401 AND type = 41; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901401, 41, 60);
DELETE FROM weenie_properties_float WHERE object_Id = 901401 AND type = 43; INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901401, 43, 15);
DELETE FROM weenie_properties_string WHERE object_Id = 901401 AND type = 1; INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901401, 1, 'Nether Wraith Gen');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901401 AND type = 1; INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901401, 1, 33555051);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901401 AND type = 8; INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901401, 8, 100667494);
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (901401, -1, 901400, 300, 3, 3, 1, 2, -1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0);

-- 53.6S 37.6E, open grassland
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES (0x7AE3CC70, 901401, 0xAE3C0025, 96.0, 96.0, 52.0, 1, 0, 0, 0, 0);

COMMIT;
