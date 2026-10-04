-- The Necklace of the Night Swarm (901700), and the Keeper of Lost Things (901000, Hotel Swank) selling it
-- for the owner to try (owner 2026-10-01: "a new necklace ... like the butterflies one ... but it uses this
-- bat instead 0x02000493"). docs/NIGHT-SWARM.md. No player source yet. Re-runnable.
--   ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/night-swarm-necklace.sql 018A'
--
-- Clone of retail's Raeta's Necklace (11336): same value, burden, mana, spellcraft, Arcane Lore 35,
-- bonded, and its two "Other II" heals kept. Changed:
--   * Laying on of Hands (2436) -> Epic Deception Prowess (4020): the same +25 to one skill. 2436 had to
--     go, because its TargetEffect (PlayScript 155) IS Raeta's butterfly swarm.
--   * PropertyInt 29130 = 0x10001 (65537): RevivalGuard WornEffect plays our PlayScript 0x10001 on the
--     wearer when it is put on, which our client draws as expansion/vfx/night_swarm (retail's ambient bat,
--     setup 0x02000493, flapping round the wearer). Retail clients draw nothing for it.
--   * our name, descriptions and icon 0x06FF1700 (117380864, expansion/textures/); IgnoreCloIcons so the
--     ClothingBase it inherits never stamps Raeta's icon back; no inscription. Dropped model:
--     expansion/models/w901700.glb.
START TRANSACTION;
DELETE FROM weenie WHERE class_Id = 901700;
DELETE FROM weenie_properties_create_list WHERE object_Id = 901000 AND destination_Type = 4 AND weenie_Class_Id = 901700;

INSERT INTO weenie (class_Id, class_Name, type) SELECT 901700, 'acnightswarmnecklace', type FROM weenie WHERE class_Id = 11336;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901700, type, value FROM weenie_properties_int WHERE object_Id = 11336;
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 901700, type, value FROM weenie_properties_int64 WHERE object_Id = 11336;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901700, type, value FROM weenie_properties_bool WHERE object_Id = 11336;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901700, type, value FROM weenie_properties_float WHERE object_Id = 11336;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 901700, type, value FROM weenie_properties_string WHERE object_Id = 11336 AND type NOT IN (7, 8);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901700, type, value FROM weenie_properties_d_i_d WHERE object_Id = 11336;
INSERT INTO weenie_properties_i_i_d (object_Id, type, value) SELECT 901700, type, value FROM weenie_properties_i_i_d WHERE object_Id = 11336;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) SELECT 901700, spell, probability FROM weenie_properties_spell_book WHERE object_Id = 11336 AND spell <> 2436;
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) VALUES (901700, 4020, 2);
INSERT INTO weenie_properties_palette (object_Id, sub_Palette_Id, offset, length) SELECT 901700, sub_Palette_Id, offset, length FROM weenie_properties_palette WHERE object_Id = 11336;
INSERT INTO weenie_properties_texture_map (object_Id, `index`, old_Id, new_Id) SELECT 901700, `index`, old_Id, new_Id FROM weenie_properties_texture_map WHERE object_Id = 11336;
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) SELECT 901700, `index`, animation_Id FROM weenie_properties_anim_part WHERE object_Id = 11336;

DELETE FROM weenie_properties_string WHERE object_Id = 901700 AND type IN (1, 15, 16);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (901700, 1, 'Necklace of the Night Swarm'),
  (901700, 15, 'A pendant in the shape of a bat.'),
  (901700, 16, 'A pendant of blackened silver carved in the shape of a bat, its wings spread wide. Whoever puts it on is wrapped for a moment in a swarm of bats.');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901700 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901700, 8, 117380864);
DELETE FROM weenie_properties_bool WHERE object_Id = 901700 AND type = 84;
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (901700, 84, 1);
DELETE FROM weenie_properties_int WHERE object_Id = 901700 AND type = 29130;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901700, 29130, 65537);

INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
  VALUES (901000, 4, 901700, -1, 0, 0, 0);
COMMIT;
