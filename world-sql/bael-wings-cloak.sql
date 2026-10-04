-- Bael'Zharon's Wings (900206) and its 10% set on his loot, applied live on its own.
-- The same rows are in new-content-baelzharon-golems.sql (and its generator); this file only
-- replays them without touching anything else. Re-runnable.
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/bael-wings-cloak.sql   then @clearcache weenie
START TRANSACTION;
DELETE FROM weenie WHERE class_Id = 900206;
DELETE FROM weenie_properties_create_list WHERE object_Id = 900200 AND ((weenie_Class_Id = 900206 AND shade = 0.1) OR (weenie_Class_Id = 0 AND shade = 0.9));
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES (900200, 9, 900206, 1, 0, 0.1, 0), (900200, 9, 0, 1, 0, 0.9, 0);
-- ===== 900206 Bael'Zharon's Wings: his wings folded shut, worn as a cloak =====
-- Clone of retail's Mukkir Wings (52193): a named reward cloak worn on cloak part 29 through
-- ClothingBase 0x10000867 (one model for every heritage), wield level 120, max item level 4, the
-- Void Magic cloak set (EquipmentSet 80) and the Clouded Soul proc (5361), all kept: a retail
-- reward cloak, nothing that obsoletes retail gear. Ours: his model on part 29 (anim_part, ACE's
-- ApplyOwnAnimParts replaces by index), our icon (IgnoreCloIcons, else the clothing table stamps
-- Mukkir's over it), the dropped model is expansion/models/w900206.glb.
-- 900206 <- clone of 52193
INSERT INTO weenie (class_Id, class_Name, type) SELECT 900206, 'acbaelzharonwings', type FROM weenie WHERE class_Id = 52193;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900206, type, value FROM weenie_properties_int WHERE object_Id = 52193;
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 900206, type, value FROM weenie_properties_int64 WHERE object_Id = 52193;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900206, type, value FROM weenie_properties_bool WHERE object_Id = 52193;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900206, type, value FROM weenie_properties_float WHERE object_Id = 52193;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 900206, type, value FROM weenie_properties_string WHERE object_Id = 52193;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900206, type, value FROM weenie_properties_d_i_d WHERE object_Id = 52193;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 900206, type, value FROM weenie_properties_i_i_d WHERE object_Id = 52193;
INSERT INTO weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_P_Spent) SELECT 900206, type, init_Level, level_From_C_P, c_P_Spent FROM weenie_properties_attribute WHERE object_Id = 52193;
INSERT INTO weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_P_Spent, current_Level) SELECT 900206, type, init_Level, level_From_C_P, c_P_Spent, current_Level FROM weenie_properties_attribute_2nd WHERE object_Id = 52193;
INSERT INTO weenie_properties_skill (object_Id, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time) SELECT 900206, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time FROM weenie_properties_skill WHERE object_Id = 52193;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 900206, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 52193;
INSERT INTO weenie_properties_body_part (object_Id, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b) SELECT 900206, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b FROM weenie_properties_body_part WHERE object_Id = 52193;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 900206, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 52193;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 900206, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 52193;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 900206, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 52193;
INSERT INTO weenie_properties_event_filter (object_Id, event) SELECT 900206, event FROM weenie_properties_event_filter WHERE object_Id = 52193;
DELETE FROM weenie_properties_string WHERE object_Id = 900206 AND type = 1; INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900206, 1, 'Bael''Zharon''s Wings');
DELETE FROM weenie_properties_string WHERE object_Id = 900206 AND type = 16; INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900206, 16, 'The wings of Bael''Zharon, the Hopeslayer, torn from his re-formed body and folded shut. The torn edges still smoulder with his shadow.');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 900206 AND type = 8; INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (900206, 8, 117375494);
DELETE FROM weenie_properties_bool WHERE object_Id = 900206 AND type = 84; INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (900206, 84, 1);
DELETE FROM weenie_properties_anim_part WHERE object_Id = 900206; INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) VALUES (900206, 29, 33489414);

COMMIT;
