-- PK TROPHIES AND THE DARKTIDE VENDORS, weenies 908400-908419 (docs/PK-EXPANSION.md "Season 1, trophies and vendors";
-- RevivalGuard PkTrophies). Owner 2026-11-21: "yes all 1-5 ... the pk currency".
-- Weenies only. The live datafix tools/public-shard/datafixes/2026-11-24-pk-1-trophy-vendor.sql carries these rows
-- plus the placement (the Free Weapon Vendor in Arwic becomes the Bloodprice Trader, owner 11-21).
-- New rows only: no retail weenie is changed (every stock item is a CLONE of a retail weenie with a new wcid, its
-- Value set to its price in trophies, since ACE prices an alternate-currency vendor's stock by Value x SellPrice).
-- Re-runnable: deletes its own rows first. Generated once by a scratch script; edit here.
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/pk-trophies.sql     (then @clearcache weenie)
--
-- 908400 Darktide Trophy: the retail skull (setup 0x02000959, icon 0x06001E38, as Skull of High Acolyte 7817),
--        stackable to 100 like the Imbue Swap Coin (45494), bonded (no trade, no drop, kept on death).
--        RevivalGuard PkTrophies gives one to the killer for an honourable kill (pk_trophies_enabled).
-- 908401 Bloodprice Trader: a vendor cloned from the Weapon Imbue Vendor (45501, an ACE weenie; the same look
--        as the Free Weapon Vendor it replaces) with AlternateCurrency (DID 57) = 908400; it buys nothing.
-- 908403 Combat Arena Writ: a Gem with the look of the Writ of Authorization (38721). Using it at level 150 as a
--        player killer attunes you to the Derethian Combat Arena (quest DCAAccess), as the Arena Master does for
--        4 MMD; RevivalGuard PkTrophies handles the use.
START TRANSACTION;

DELETE FROM weenie_properties_int WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_int64 WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_bool WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_float WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_d_i_d WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_i_i_d WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_string WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_spell_book WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_palette WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_texture_map WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_anim_part WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_create_list WHERE object_Id BETWEEN 908400 AND 908419;
DELETE a FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id WHERE e.object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie_properties_emote WHERE object_Id BETWEEN 908400 AND 908419;
DELETE FROM weenie WHERE class_Id BETWEEN 908400 AND 908419;

-- 908400 Darktide Trophy
INSERT INTO weenie (class_Id, class_Name, type) VALUES (908400, 'pkdarktidetrophy', 51);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES
  (908400, 1, 128), (908400, 5, 10), (908400, 11, 100), (908400, 12, 1), (908400, 13, 10), (908400, 15, 0),
  (908400, 16, 1), (908400, 19, 0), (908400, 33, 1), (908400, 93, 1044), (908400, 114, 0);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value)
  SELECT 908400, type, value FROM weenie_properties_d_i_d WHERE object_Id = 7817 AND type IN (1, 3, 8, 22);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (908400, 1, 'Darktide Trophy'), (908400, 20, 'Darktide Trophies'),
  (908400, 14, 'Spend it with the Bloodprice Trader in Arwic.'),
  (908400, 16, 'The skull of a worthy foe, taken in honourable player killer combat.');

-- 908403 Combat Arena Writ (3 trophies)
INSERT INTO weenie (class_Id, class_Name, type) VALUES (908403, 'pkcombatarenawrit', 38);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES
  (908403, 1, 128), (908403, 5, 5), (908403, 11, 1), (908403, 12, 1), (908403, 13, 5), (908403, 15, 3),
  (908403, 16, 8), (908403, 19, 3), (908403, 93, 1044);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value)
  SELECT 908403, type, value FROM weenie_properties_d_i_d WHERE object_Id = 38721 AND type IN (1, 3, 8, 22);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (908403, 1, 'Combat Arena Writ'),
  (908403, 14, 'Use this writ to be attuned to the Derethian Combat Arena in Baishi. You must be a player killer of level 150 or more.'),
  (908403, 16, 'A writ sealed by the Derethian Combat Arena Master.');

-- 908404 clone of 2458, 1 trophy
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908404, 'pkhealthelixir', type FROM weenie WHERE class_Id = 2458;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908404, type, value FROM weenie_properties_int WHERE object_Id = 2458 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908404, type, value FROM weenie_properties_int64 WHERE object_Id = 2458;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908404, type, value FROM weenie_properties_bool WHERE object_Id = 2458;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908404, type, value FROM weenie_properties_float WHERE object_Id = 2458;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908404, type, value FROM weenie_properties_d_i_d WHERE object_Id = 2458;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908404, type, value FROM weenie_properties_i_i_d WHERE object_Id = 2458;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908404, type, value FROM weenie_properties_string WHERE object_Id = 2458;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908404, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 2458;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908404, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 2458;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908404, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 2458;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908404, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 2458;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908404, 19, 1), (908404, 15, 1);
-- 908405 clone of 2470, 1 trophy
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908405, 'pkstaminaelixir', type FROM weenie WHERE class_Id = 2470;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908405, type, value FROM weenie_properties_int WHERE object_Id = 2470 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908405, type, value FROM weenie_properties_int64 WHERE object_Id = 2470;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908405, type, value FROM weenie_properties_bool WHERE object_Id = 2470;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908405, type, value FROM weenie_properties_float WHERE object_Id = 2470;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908405, type, value FROM weenie_properties_d_i_d WHERE object_Id = 2470;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908405, type, value FROM weenie_properties_i_i_d WHERE object_Id = 2470;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908405, type, value FROM weenie_properties_string WHERE object_Id = 2470;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908405, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 2470;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908405, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 2470;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908405, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 2470;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908405, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 2470;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908405, 19, 1), (908405, 15, 1);
-- 908406 clone of 2461, 1 trophy
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908406, 'pkmanaelixir', type FROM weenie WHERE class_Id = 2461;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908406, type, value FROM weenie_properties_int WHERE object_Id = 2461 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908406, type, value FROM weenie_properties_int64 WHERE object_Id = 2461;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908406, type, value FROM weenie_properties_bool WHERE object_Id = 2461;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908406, type, value FROM weenie_properties_float WHERE object_Id = 2461;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908406, type, value FROM weenie_properties_d_i_d WHERE object_Id = 2461;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908406, type, value FROM weenie_properties_i_i_d WHERE object_Id = 2461;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908406, type, value FROM weenie_properties_string WHERE object_Id = 2461;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908406, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 2461;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908406, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 2461;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908406, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 2461;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908406, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 2461;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908406, 19, 1), (908406, 15, 1);
-- 908407 clone of 632, 1 trophy
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908407, 'pkpeerlesshealingkit', type FROM weenie WHERE class_Id = 632;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908407, type, value FROM weenie_properties_int WHERE object_Id = 632 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908407, type, value FROM weenie_properties_int64 WHERE object_Id = 632;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908407, type, value FROM weenie_properties_bool WHERE object_Id = 632;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908407, type, value FROM weenie_properties_float WHERE object_Id = 632;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908407, type, value FROM weenie_properties_d_i_d WHERE object_Id = 632;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908407, type, value FROM weenie_properties_i_i_d WHERE object_Id = 632;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908407, type, value FROM weenie_properties_string WHERE object_Id = 632;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908407, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 632;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908407, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 632;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908407, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 632;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908407, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 632;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908407, 19, 1), (908407, 15, 1);
-- 908408 clone of 22449, 2 trophies
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908408, 'pkplentifulhealingkit', type FROM weenie WHERE class_Id = 22449;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908408, type, value FROM weenie_properties_int WHERE object_Id = 22449 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908408, type, value FROM weenie_properties_int64 WHERE object_Id = 22449;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908408, type, value FROM weenie_properties_bool WHERE object_Id = 22449;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908408, type, value FROM weenie_properties_float WHERE object_Id = 22449;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908408, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22449;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908408, type, value FROM weenie_properties_i_i_d WHERE object_Id = 22449;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908408, type, value FROM weenie_properties_string WHERE object_Id = 22449;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908408, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 22449;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908408, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 22449;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908408, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 22449;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908408, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 22449;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908408, 19, 2), (908408, 15, 2);
-- 908409 clone of 8151, 3 trophies
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908409, 'pkskullmask', type FROM weenie WHERE class_Id = 8151;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908409, type, value FROM weenie_properties_int WHERE object_Id = 8151 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908409, type, value FROM weenie_properties_int64 WHERE object_Id = 8151;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908409, type, value FROM weenie_properties_bool WHERE object_Id = 8151;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908409, type, value FROM weenie_properties_float WHERE object_Id = 8151;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908409, type, value FROM weenie_properties_d_i_d WHERE object_Id = 8151;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908409, type, value FROM weenie_properties_i_i_d WHERE object_Id = 8151;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908409, type, value FROM weenie_properties_string WHERE object_Id = 8151;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908409, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 8151;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908409, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 8151;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908409, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 8151;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908409, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 8151;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908409, 19, 3), (908409, 15, 3);
-- 908410 clone of 25702, 3 trophies
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908410, 'pkbanditmask', type FROM weenie WHERE class_Id = 25702;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908410, type, value FROM weenie_properties_int WHERE object_Id = 25702 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908410, type, value FROM weenie_properties_int64 WHERE object_Id = 25702;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908410, type, value FROM weenie_properties_bool WHERE object_Id = 25702;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908410, type, value FROM weenie_properties_float WHERE object_Id = 25702;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908410, type, value FROM weenie_properties_d_i_d WHERE object_Id = 25702;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908410, type, value FROM weenie_properties_i_i_d WHERE object_Id = 25702;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908410, type, value FROM weenie_properties_string WHERE object_Id = 25702;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908410, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 25702;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908410, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 25702;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908410, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 25702;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908410, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 25702;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908410, 19, 3), (908410, 15, 3);
-- 908411 clone of 8153, 5 trophies
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908411, 'pkvirindimask', type FROM weenie WHERE class_Id = 8153;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908411, type, value FROM weenie_properties_int WHERE object_Id = 8153 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908411, type, value FROM weenie_properties_int64 WHERE object_Id = 8153;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908411, type, value FROM weenie_properties_bool WHERE object_Id = 8153;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908411, type, value FROM weenie_properties_float WHERE object_Id = 8153;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908411, type, value FROM weenie_properties_d_i_d WHERE object_Id = 8153;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908411, type, value FROM weenie_properties_i_i_d WHERE object_Id = 8153;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908411, type, value FROM weenie_properties_string WHERE object_Id = 8153;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908411, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 8153;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908411, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 8153;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908411, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 8153;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908411, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 8153;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908411, 19, 5), (908411, 15, 5);
-- 908412 clone of 8206, 5 trophies
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908412, 'pkogremask', type FROM weenie WHERE class_Id = 8206;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908412, type, value FROM weenie_properties_int WHERE object_Id = 8206 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908412, type, value FROM weenie_properties_int64 WHERE object_Id = 8206;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908412, type, value FROM weenie_properties_bool WHERE object_Id = 8206;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908412, type, value FROM weenie_properties_float WHERE object_Id = 8206;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908412, type, value FROM weenie_properties_d_i_d WHERE object_Id = 8206;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908412, type, value FROM weenie_properties_i_i_d WHERE object_Id = 8206;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908412, type, value FROM weenie_properties_string WHERE object_Id = 8206;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908412, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 8206;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908412, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 8206;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908412, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 8206;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908412, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 8206;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908412, 19, 5), (908412, 15, 5);
-- 908413 clone of 12252, 10 trophies
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908413, 'pkobsidiandirectorsmask', type FROM weenie WHERE class_Id = 12252;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908413, type, value FROM weenie_properties_int WHERE object_Id = 12252 AND type NOT IN (15, 19);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908413, type, value FROM weenie_properties_int64 WHERE object_Id = 12252;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908413, type, value FROM weenie_properties_bool WHERE object_Id = 12252;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908413, type, value FROM weenie_properties_float WHERE object_Id = 12252;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908413, type, value FROM weenie_properties_d_i_d WHERE object_Id = 12252;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908413, type, value FROM weenie_properties_i_i_d WHERE object_Id = 12252;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908413, type, value FROM weenie_properties_string WHERE object_Id = 12252;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908413, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 12252;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908413, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 12252;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908413, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 12252;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908413, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 12252;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (908413, 19, 10), (908413, 15, 10);

-- 908401 Bloodprice Trader
INSERT INTO weenie (class_Id, class_Name, type) SELECT 908401, 'pkbloodpricetrader', type FROM weenie WHERE class_Id = 45501;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 908401, type, value FROM weenie_properties_int WHERE object_Id = 45501;
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 908401, type, value FROM weenie_properties_int64 WHERE object_Id = 45501;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 908401, type, value FROM weenie_properties_bool WHERE object_Id = 45501;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 908401, type, value FROM weenie_properties_float WHERE object_Id = 45501;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 908401, type, value FROM weenie_properties_d_i_d WHERE object_Id = 45501 AND type <> 57;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 908401, type, value FROM weenie_properties_i_i_d WHERE object_Id = 45501;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 908401, type, value FROM weenie_properties_string WHERE object_Id = 45501 AND type <> 1;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 908401, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 45501;
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 908401, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 45501;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 908401, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 45501;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 908401, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 45501;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (908401, 57, 908400);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (908401, 1, 'Bloodprice Trader');
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (908401, 4, 908403, -1, 0, 0, 0),
  (908401, 4, 908404, -1, 0, 0, 0),
  (908401, 4, 908405, -1, 0, 0, 0),
  (908401, 4, 908406, -1, 0, 0, 0),
  (908401, 4, 908407, -1, 0, 0, 0),
  (908401, 4, 908408, -1, 0, 0, 0),
  (908401, 4, 908409, -1, 0, 0, 0),
  (908401, 4, 908410, -1, 0, 0, 0),
  (908401, 4, 908411, -1, 0, 0, 0),
  (908401, 4, 908412, -1, 0, 0, 0),
  (908401, 4, 908413, -1, 0, 0, 0);
INSERT INTO weenie_properties_emote (object_Id, category, probability, vendor_Type) VALUES (908401, 2, 1, 1);
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (LAST_INSERT_ID(), 0, 10, 0, 1, 'Trophies, not pyreals. Earn them from worthy foes and I will keep you fighting.');
INSERT INTO weenie_properties_emote (object_Id, category, probability, vendor_Type) VALUES (908401, 2, 1, 2);
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (LAST_INSERT_ID(), 0, 10, 0, 1, 'Go and take another.');

COMMIT;
