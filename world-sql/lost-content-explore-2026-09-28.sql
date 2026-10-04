-- LOST CONTENT, EXPLORABLE FOR THE OWNER (2026-09-28). docs/LOST-CONTENT-AND-OPPORTUNITIES.md, "Explore it".
-- Admin-only: both placements are in Hotel Swank (0x018A, admin-only, docs/SWANK-AND-TOUR-2026-09-27.md
-- section 9) or in dungeon 0x003F, which nothing outside Swank's portal room and the admin Tour reaches.
-- No portal is added anywhere. Backup of both landblocks' rows as they stood:
-- lost-content-explore-backup-2026-09-28.sql.
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/lost-content-explore-2026-09-28.sql
--   then in game: @clearcache weenie ; @reloadblock 018A ; @reloadblock 003F   (refused while anyone is in the block)
--
-- 901000 KEEPER OF LOST THINGS: a clone of the never-placed Adams' Beach Merchant (26694, one of the 393
--   Settlement Portal Gem merchants), in an Explorer Society Robe, selling 24 Settlement Portal Gems (every
--   one walked live on 2026-09-28: summoned from the Jeeves room and ridden; each landed on its settlement
--   portal's own drop) and the five Tumerok Curse Fetish Clay Totems (11144-11148; Hea Arantah trades each
--   for a House Portal). Stands on the Jeeves room's west wall, the vendors' row, south of Jeeves, facing
--   east like them; 3.7 m off the south door's line, not in a walkway.
-- 7003F4E0 EMPEROR GERAINE I (7120, never spawned in retail or ACE) in dungeon 0x003F, in front of the
--   gold throne (static 0x02000187) on the south wall of the -6 m hall (cell 0x003F0331), facing north
--   into the room. Hostile level 49 Mu-miyah; direct placement, so a kill lasts until the block reloads.
--
-- Undo: DELETE FROM landblock_instance WHERE guid IN (0x7018A4E0, 0x7003F4E0); then the DELETEs just below.
START TRANSACTION;
DELETE FROM weenie_properties_int WHERE object_Id = 901000;
DELETE FROM weenie_properties_int64 WHERE object_Id = 901000;
DELETE FROM weenie_properties_bool WHERE object_Id = 901000;
DELETE FROM weenie_properties_float WHERE object_Id = 901000;
DELETE FROM weenie_properties_string WHERE object_Id = 901000;
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901000;
DELETE FROM weenie_properties_i_i_d WHERE object_Id = 901000;
DELETE FROM weenie_properties_attribute WHERE object_Id = 901000;
DELETE FROM weenie_properties_attribute_2nd WHERE object_Id = 901000;
DELETE FROM weenie_properties_skill WHERE object_Id = 901000;
DELETE FROM weenie_properties_body_part WHERE object_Id = 901000;
DELETE FROM weenie_properties_palette WHERE object_Id = 901000;
DELETE FROM weenie_properties_texture_map WHERE object_Id = 901000;
DELETE FROM weenie_properties_anim_part WHERE object_Id = 901000;
DELETE FROM weenie_properties_spell_book WHERE object_Id = 901000;
DELETE FROM weenie_properties_event_filter WHERE object_Id = 901000;
DELETE FROM weenie_properties_create_list WHERE object_Id = 901000;
DELETE FROM weenie_properties_emote WHERE object_Id = 901000;
DELETE FROM weenie WHERE class_Id = 901000;
DELETE FROM landblock_instance WHERE guid IN (0x7018A4E0, 0x7003F4E0);
INSERT INTO weenie (class_Id, class_Name, type) SELECT 901000, 'keeperoflostthings', type FROM weenie WHERE class_Id = 26694;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901000, type, value FROM weenie_properties_int WHERE object_Id = 26694;
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 901000, type, value FROM weenie_properties_int64 WHERE object_Id = 26694;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901000, type, value FROM weenie_properties_bool WHERE object_Id = 26694;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901000, type, value FROM weenie_properties_float WHERE object_Id = 26694;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 901000, type, value FROM weenie_properties_string WHERE object_Id = 26694;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901000, type, value FROM weenie_properties_d_i_d WHERE object_Id = 26694;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 901000, type, value FROM weenie_properties_i_i_d WHERE object_Id = 26694;
INSERT INTO weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_P_Spent) SELECT 901000, type, init_Level, level_From_C_P, c_P_Spent FROM weenie_properties_attribute WHERE object_Id = 26694;
INSERT INTO weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_P_Spent, current_Level) SELECT 901000, type, init_Level, level_From_C_P, c_P_Spent, current_Level FROM weenie_properties_attribute_2nd WHERE object_Id = 26694;
INSERT INTO weenie_properties_skill (object_Id, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time) SELECT 901000, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time FROM weenie_properties_skill WHERE object_Id = 26694;
INSERT INTO weenie_properties_body_part (object_Id, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b) SELECT 901000, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b FROM weenie_properties_body_part WHERE object_Id = 26694;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 901000, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 26694;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 901000, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 26694;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 901000, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 26694;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 901000, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 26694;
INSERT INTO weenie_properties_event_filter (object_Id, event) SELECT 901000, event FROM weenie_properties_event_filter WHERE object_Id = 26694;
UPDATE weenie_properties_string SET value = 'Keeper of Lost Things' WHERE object_Id = 901000 AND type = 1;
UPDATE weenie_properties_string SET value = 'Keeper' WHERE object_Id = 901000 AND type = 5;
DELETE FROM weenie_properties_string WHERE object_Id = 901000 AND type = 16;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901000, 16, 'Keeps what Turbine built and never shipped. Settlement Portal Gems (announced March 2004, withdrawn in April): each summons its settlement''s portal for a moment. The five Clay Totems of the Tumerok Curse Fetishes: give one to Hea Arantah and he hands back a House Portal. Hotel Swank only.');
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (901000, 2, 12310, 0, 0, 0, 0),
  (901000, 4, 26059, -1, 0, 0, 0),
  (901000, 4, 26229, -1, 0, 0, 0),
  (901000, 4, 26381, -1, 0, 0, 0),
  (901000, 4, 26304, -1, 0, 0, 0),
  (901000, 4, 26137, -1, 0, 0, 0),
  (901000, 4, 26412, -1, 0, 0, 0),
  (901000, 4, 26424, -1, 0, 0, 0),
  (901000, 4, 26202, -1, 0, 0, 0),
  (901000, 4, 26120, -1, 0, 0, 0),
  (901000, 4, 26377, -1, 0, 0, 0),
  (901000, 4, 26431, -1, 0, 0, 0),
  (901000, 4, 26321, -1, 0, 0, 0),
  (901000, 4, 26076, -1, 0, 0, 0),
  (901000, 4, 26144, -1, 0, 0, 0),
  (901000, 4, 26160, -1, 0, 0, 0),
  (901000, 4, 26286, -1, 0, 0, 0),
  (901000, 4, 26088, -1, 0, 0, 0),
  (901000, 4, 26099, -1, 0, 0, 0),
  (901000, 4, 26262, -1, 0, 0, 0),
  (901000, 4, 26281, -1, 0, 0, 0),
  (901000, 4, 26311, -1, 0, 0, 0),
  (901000, 4, 26394, -1, 0, 0, 0),
  (901000, 4, 26084, -1, 0, 0, 0),
  (901000, 4, 26199, -1, 0, 0, 0),
  (901000, 4, 11144, -1, 0, 0, 0),
  (901000, 4, 11145, -1, 0, 0, 0),
  (901000, 4, 11146, -1, 0, 0, 0),
  (901000, 4, 11147, -1, 0, 0, 0),
  (901000, 4, 11148, -1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
  (0x7018A4E0, 901000, 0x018A0249, 96.3, -82.8, 0.005, 0.707107, 0, 0, -0.707107, 0),
  (0x7003F4E0, 7120, 0x003F0331, 70.52, -162.2, -5.995, 1, 0, 0, 0, 0);
COMMIT;
