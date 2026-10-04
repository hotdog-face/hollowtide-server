-- VARROW THE UNBOWED, THE DARKTIDE DUELIST (owner 2026-09-28: "a boss that is a bit more like a real
-- pk duel. Cunning, smart and hard to kill."). docs/DUELIST-BOSS.md has the lore, the design, the
-- counterplay and the staging results. Weenies 901050-901053 (block 901050-901099):
--   901050  Varrow the Unbowed        the boss: a dressed retail human in Nexus armour
--   901051  Varrow's Nexus Blade      his sword (our GfxObj 0x01FF0900, icon 0x06FF0900; never drops)
--   901052  Nexus Pillar              the arena's line-of-sight pillars (retail Dark Monolith, scaled)
--   901053  Varrow the Unbowed Gen    his generator, 15 min respawn
--   901054  Nexus Blade of the Unbowed  his signature drop, a legendary heavy sword (2026-09-29)
--   901055  Replica Nexus Blade         the Keeper of Lost Things' costume copy (no stats)
--   Casting, he wields the retail Shadownether Isparian Wand (46397); DuelistAi makes it and swaps.
--
-- MADE, NOT APPLIED. NOTHING HERE IS ON THE LIVE SHARD. Tested only on a private staging copy
-- (ace_world_duel, start-ace-staging-duel.sh). To add him for real, later and on the owner's word:
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/duelist-boss.sql
--   then in game: @clearcache weenie ; @clearcache landblock ; @reloadblock 2832
--   and turn his brain on: @modifybool duelist_ai true   (RevivalGuard DuelistAi; OFF = a plain ACE monster)
--   and, for the blade's own model in his hand: @modifybool expansion_weapon_parts true (ViridianArms)
--
-- NEW ROWS ONLY, except the arena's three wandering-encounter rows (see the end: they are deleted so
-- Obsidian Plains monsters do not wander into a duel; their exact rows are written there as the
-- revert). Landblock 0x2832 has no landblock_instance rows in retail ace_world. Re-runnable: it
-- deletes its own rows first. REVERT: the DELETEs just below, plus re-inserting the encounters.
START TRANSACTION;

DELETE FROM landblock_instance WHERE guid BETWEEN 0x72832000 AND 0x7283200F;
DELETE FROM landblock_instance WHERE weenie_Class_Id BETWEEN 901050 AND 901099;
DELETE FROM weenie WHERE class_Id BETWEEN 901050 AND 901099;

-- ===== 901050 Varrow the Unbowed =====
-- Base: the retail Soldier (72878), a level-220 human monster already in Koujia plate, with retail's
-- human setup (0x02000001), motion table (0x09000001, every weapon stance and the healing-kit
-- gesture) and human combat table. Cloned whole, then changed below.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 901050, 'acvarrowtheunbowed', type FROM weenie WHERE class_Id = 72878;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901050, type, value FROM weenie_properties_int WHERE object_Id = 72878;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901050, type, value FROM weenie_properties_bool WHERE object_Id = 72878;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901050, type, value FROM weenie_properties_float WHERE object_Id = 72878;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 901050, type, value FROM weenie_properties_string WHERE object_Id = 72878;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901050, type, value FROM weenie_properties_d_i_d WHERE object_Id = 72878;
INSERT INTO weenie_properties_body_part (object_Id, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b) SELECT 901050, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b FROM weenie_properties_body_part WHERE object_Id = 72878;
-- Not cloned: the Soldier's create list (his dress is below), spellbook (below), attributes and skills (below).

UPDATE weenie_properties_string SET value = 'Varrow the Unbowed' WHERE object_Id = 901050 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901050, 16,
  'A duelist of Darktide, one of Bael''Zharon''s Chosen, who never once lost a fair fight. When the Nexus Crystal broke he took its heart by the sword and had it forged into Koujia plate and a blade, and the shadow in the crystal took him in return. He still waits for a worthy opponent.');
-- int: Level 275; Tolerance (67, 128 = attack monsters only) and Faction1Bits (281) removed: he
-- attacks players on sight, like the Chosen he was; XpOverride 30,000,000; CreatureType stays Human.
DELETE FROM weenie_properties_int WHERE object_Id = 901050 AND type IN (25, 67, 146, 281);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901050, 25, 275), (901050, 146, 30000000);
-- bool: AiUsesMana (6) and AiUseHumanMagicAnimations (7): his spells cost mana and he SPEAKS the
-- spell words in chat with a human caster's gestures. That is a tell, as it was in PK: learn the
-- words and you know what is coming.
DELETE FROM weenie_properties_bool WHERE object_Id = 901050 AND type IN (6, 7, 19);
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (901050, 6, 1), (901050, 7, 1), (901050, 19, 1);
-- float: HealthRate 0.5, StaminaRate 3, ManaRate 2 per tick; VisualAwarenessRange 24 m; PowerupTime
-- 0.6 s; HomeRadius 60 m (the arena and its approaches); ResistX (64-70) 0.9 across the board: no
-- one element is his weakness, the fight is about his play, not a lucky element. Drain resists
-- (ResistHealthDrain 125, ResistStaminaDrain 72, ResistManaDrain 74) 0.8.
DELETE FROM weenie_properties_float WHERE object_Id = 901050 AND type IN (3, 4, 5, 31, 34, 55, 64, 65, 66, 67, 68, 69, 70, 72, 74, 125);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901050, 3, 0.5), (901050, 4, 3), (901050, 5, 2), (901050, 31, 24), (901050, 34, 0.6), (901050, 55, 60),
  (901050, 64, 0.9), (901050, 65, 0.9), (901050, 66, 0.9), (901050, 67, 0.9), (901050, 68, 0.9), (901050, 69, 0.9), (901050, 70, 0.9),
  (901050, 72, 0.8), (901050, 74, 0.8), (901050, 125, 0.8);
-- 2026-09-29 (owner: "his attack speed needs to be way faster"): PowerupTime 34 is ACE's random 0..n s pause
-- after every swing and cast (was 0.6), AiUseMagicDelay 80 the least time between two spells (was 2, the
-- Soldier's). A fast PK: 0.1 s and 1.2 s. The swing itself is sped up in DuelistAi (duelist_swing_speed).
DELETE FROM weenie_properties_float WHERE object_Id = 901050 AND type IN (34, 80);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901050, 34, 0.1), (901050, 80, 1.2);
-- d_i_d: the Soldier's setup, motion, sound, combat and physics-effect tables and his ClothingBase
-- (the bare body under the armour); DeathTreasureType 2121 (tier 8, as Old Snapjaw).
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901050 AND type = 35;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901050, 35, 2121);

-- attributes: Str 330, End 360, Coord 330, Quick 340, Focus 320, Self 330
INSERT INTO weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_P_Spent) VALUES
  (901050, 1, 330, 0, 0), (901050, 2, 360, 0, 0), (901050, 3, 330, 0, 0), (901050, 4, 340, 0, 0), (901050, 5, 320, 0, 0), (901050, 6, 330, 0, 0);
-- vitals: Health 2,820 + End/2 = 3,000 (the alligator has 9,000: this one is hard to kill by what
-- he does, not by what he has); Stamina 2,000; Mana 900 (+Self 330): about twenty level VII casts,
-- so a mage who drains him is shutting him down, as it should be.
INSERT INTO weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_P_Spent, current_Level) VALUES
  (901050, 1, 2820, 0, 0, 3000), (901050, 3, 1640, 0, 0, 2000), (901050, 5, 570, 0, 0, 900);
-- skills (specialized). A creature's skill is its init_Level PLUS the attribute formula (ACE
-- CreatureSkill.Base), so the inits below are set for the EFFECTIVE values, before his own buffs:
-- Heavy Weapons 460 (init 240 + (Str+Coord)/3 220), Melee Defense 520 (297 + 223), Missile Defense
-- 470 (336 + 134), Magic Defense 460 (367 + 93), War 460 (298 + 162), Life and Creature 450
-- (288 + 162), Mana Conversion (init 150), Run (init 230) and Jump (init 100) over their formulas.
-- The Soldier's own numbers (540 melee defense
-- init, about 790 effective) made him unhittable; at 440 a buffed level-275 melee bot (Heavy Weapons
-- 421 plus level VIII buffs and a Weeping Mace) landed 210 of 211; at 540 about half, and an archer
-- (Missile Weapons 418) a third at 520 missile defense.
INSERT INTO weenie_properties_skill (object_Id, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time) VALUES
  (901050, 44, 0, 3, 0, 240, 0, 0), (901050, 6, 0, 3, 0, 297, 0, 0), (901050, 7, 0, 3, 0, 336, 0, 0), (901050, 15, 0, 3, 0, 367, 0, 0),
  (901050, 34, 0, 3, 0, 298, 0, 0), (901050, 33, 0, 3, 0, 288, 0, 0), (901050, 31, 0, 3, 0, 288, 0, 0), (901050, 16, 0, 3, 0, 150, 0, 0),
  (901050, 24, 0, 3, 0, 230, 0, 0), (901050, 22, 0, 3, 0, 100, 0, 0);
-- body parts: the Soldier's fists (400 bludgeon) are unused with a blade in hand but are cut to 60
-- all the same; base armour 500 -> 220 (his Koujia plate adds its own).
UPDATE weenie_properties_body_part SET d_Val = LEAST(d_Val, 60), base_Armor = 220, armor_Vs_Slash = 220, armor_Vs_Pierce = 220, armor_Vs_Bludgeon = 220,
  armor_Vs_Cold = 200, armor_Vs_Fire = 200, armor_Vs_Acid = 200, armor_Vs_Electric = 200, armor_Vs_Nether = 200 WHERE object_Id = 901050;

-- spellbook: what he casts when duelist_ai is OFF (ACE's own roll, about one attack in three). With
-- it ON the brain picks every spell itself and this list is not rolled.
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) VALUES
  (901050, 2074, 2.05), (901050, 2170, 2.04), (901050, 2164, 2.04), (901050, 2128, 2.05), (901050, 2136, 2.05),
  (901050, 2330, 2.03), (901050, 2329, 2.03), (901050, 2073, 2.02);

-- dress (destination 2 = Wield: worn at spawn, never dropped; creatures_drop_createlist_wield is off):
-- the Nexus Koujia suit that retail never made (docs/DUELIST-BOSS.md, Lore), the Nexus Commander's
-- Helm, Covenant gauntlets and sollerets, shirt and breeches, and his blade.
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (901050, 2, 901051, 0, 0, 0, 0),
  (901050, 2, 6798, 0, 0, 0, 0), (901050, 2, 6805, 0, 0, 0, 0), (901050, 2, 6803, 0, 0, 0, 0), (901050, 2, 6800, 0, 0, 0, 0),
  (901050, 2, 32300, 0, 0, 0, 0), (901050, 2, 21153, 0, 2, 1, 0), (901050, 2, 21150, 0, 2, 1, 0),
  (901050, 2, 2591, 0, 9, 1, 0), (901050, 2, 117, 0, 9, 1, 0);
-- LOOT (owner 2026-09-29: "loot on varrow was weak. he needs to drop something good"): always his Nexus
-- Blade of the Unbowed (901054) and an Aged Legendary Key (48746), on top of his tier-8 death treasure
-- (which rolls rares like any tier-8 kill). Destination 9 = ContainTreasure, shade 0 = always.
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (901050, 9, 901054, 1, 0, 0, 0), (901050, 9, 48746, 1, 0, 0, 0);

-- ===== 901051 Varrow's Nexus Blade =====
-- Clone of retail Long Sword (351): its setup 0x02000065 (no DefaultScript) gives the held and resting
-- placements. Ours: one anim_part row (index 0 -> GfxObj 0x01FF0900, expansion/models), icon
-- 0x06FF0900; PaletteBase/ClothingBase and DefaultScale dropped (modelled at size, painted itself).
-- Damage 48 slash, variance 0.3, speed 25. Bonded -1 (Destroy): it never reaches a corpse.
-- DuelistAi re-types it (int 45) to whatever element the target's armour is weakest against.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 901051, 'acvarrowsnexusblade', type FROM weenie WHERE class_Id = 351;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901051, type, value FROM weenie_properties_int WHERE object_Id = 351;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901051, type, value FROM weenie_properties_bool WHERE object_Id = 351;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901051, type, value FROM weenie_properties_float WHERE object_Id = 351;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901051, type, value FROM weenie_properties_d_i_d WHERE object_Id = 351;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901051, 1, 'Varrow''s Nexus Blade'),
  (901051, 16, 'A duelling sword whose blade is one long splinter of the Nexus Crystal, on a blackened hilt. The shard answers its wielder: fire, frost, acid or lightning, whatever the fight needs.');
DELETE FROM weenie_properties_int WHERE object_Id = 901051 AND type IN (33, 44, 45, 49);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901051, 33, -1), (901051, 44, 48), (901051, 45, 1), (901051, 49, 25);
DELETE FROM weenie_properties_float WHERE object_Id = 901051 AND type IN (22, 39);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901051, 22, 0.3);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901051 AND type IN (6, 7, 8);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901051, 8, 117377280);   -- 0x06FF0900
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) VALUES (901051, 0, 33491200);   -- 0x01FF0900

-- ===== 901054 Nexus Blade of the Unbowed: the signature drop (docs/DUELIST-BOSS.md, Loot) =====
-- His blade's look (the same anim_part and icon as 901051) on a real legendary heavy sword: 58 slash,
-- variance 0.3 (40.6-58), speed 20, +20% attack and defence, five legendary cantrips, and his opener as
-- a proc: 15% of hits cast Swordsman's Gift (Blade Vulnerability VII) on the target. Attuned: it is his
-- trophy, earned, not bought. Wield: Heavy Weapons 400 (base).
INSERT INTO weenie (class_Id, class_Name, type) SELECT 901054, 'acnexusbladeunbowed', type FROM weenie WHERE class_Id = 351;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901054, type, value FROM weenie_properties_int WHERE object_Id = 351;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901054, type, value FROM weenie_properties_bool WHERE object_Id = 351;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901054, type, value FROM weenie_properties_float WHERE object_Id = 351;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901054, type, value FROM weenie_properties_d_i_d WHERE object_Id = 351;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901054, 1, 'Nexus Blade of the Unbowed'),
  (901054, 16, 'The splinter of the Nexus Crystal that Varrow the Unbowed carried through every duel he ever fought. It remembers what it was made for: find the gap in the armour first, then cut.');
-- 5 Encumbrance, 18 UiEffects (magical), 19 Value, 33 Bonded, 44 Damage, 45 DamageType (slash), 49 WeaponTime,
-- 106 ItemSpellcraft (the proc's roll), 107/108 ItemCurMana/ItemMaxMana, 109 ItemDifficulty (arcane lore),
-- 114 Attuned, 158/159/160 WieldRequirements (base skill) / WieldSkillType (Heavy Weapons 44) / WieldDifficulty
DELETE FROM weenie_properties_int WHERE object_Id = 901054 AND type IN (5, 18, 19, 33, 44, 45, 49, 106, 107, 108, 109, 114, 158, 159, 160);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901054, 5, 450), (901054, 18, 1), (901054, 19, 50000), (901054, 33, 0),
  (901054, 44, 58), (901054, 45, 1), (901054, 49, 20), (901054, 106, 400), (901054, 107, 3000), (901054, 108, 3000), (901054, 109, 300),
  (901054, 114, 1), (901054, 158, 2), (901054, 159, 44), (901054, 160, 400);
-- 5 ManaRate, 22 DamageVariance, 29 WeaponDefense, 62 WeaponOffense, 156 ProcSpellRate; no DefaultScale
DELETE FROM weenie_properties_float WHERE object_Id = 901054 AND type IN (5, 22, 29, 39, 62, 156);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901054, 5, -0.0333), (901054, 22, 0.3), (901054, 29, 1.2), (901054, 62, 1.2), (901054, 156, 0.15);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901054 AND type IN (6, 7, 8, 55);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901054, 8, 117377280), (901054, 55, 2164);   -- icon 0x06FF0900; ProcSpell Swordsman's Gift
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) VALUES (901054, 0, 33491200);   -- 0x01FF0900
-- Legendary Blood Thirst, Heart Thirst, Swift Hunter, Defender and Heavy Weapon Aptitude
INSERT INTO weenie_properties_spell_book (object_Id, spell, probability) VALUES
  (901054, 6089, 2), (901054, 6094, 2), (901054, 6100, 2), (901054, 6091, 2), (901054, 6072, 2);

-- ===== 901055 Replica Nexus Blade: the Keeper of Lost Things sells this, not the real one =====
INSERT INTO weenie (class_Id, class_Name, type) SELECT 901055, 'acreplicanexusblade', type FROM weenie WHERE class_Id = 351;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901055, type, value FROM weenie_properties_int WHERE object_Id = 351;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901055, type, value FROM weenie_properties_bool WHERE object_Id = 351;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901055, type, value FROM weenie_properties_float WHERE object_Id = 351;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901055, type, value FROM weenie_properties_d_i_d WHERE object_Id = 351;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901055, 1, 'Replica Nexus Blade'),
  (901055, 16, 'A copy of Varrow the Unbowed''s blade in painted glass and tin, sold to people who would rather not fight him for it. It would shatter on the first parry.');
DELETE FROM weenie_properties_int WHERE object_Id = 901055 AND type IN (19, 44);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901055, 19, 500), (901055, 44, 1);
DELETE FROM weenie_properties_float WHERE object_Id = 901055 AND type = 39;
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901055 AND type IN (6, 7, 8);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901055, 8, 117377280);
INSERT INTO weenie_properties_anim_part (object_Id, `index`, animation_Id) VALUES (901055, 0, 33491200);

-- ===== 901052 Nexus Pillar =====
-- Retail's Dark Monolith (33060: "A dark obsidian stone. It hums in low and ominous way."), not
-- attackable, at ObjScale 1.6: about 2.1 m wide and 4.2 m tall, enough to hide a man. DuelistAi
-- looks for this weenie (and anything named Pillar, Monolith or Column) when it breaks line of sight.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 901052, 'acnexuspillar', type FROM weenie WHERE class_Id = 33060;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 901052, type, value FROM weenie_properties_int WHERE object_Id = 33060;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 901052, type, value FROM weenie_properties_bool WHERE object_Id = 33060;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 901052, type, value FROM weenie_properties_float WHERE object_Id = 33060;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 901052, type, value FROM weenie_properties_d_i_d WHERE object_Id = 33060;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901052, 1, 'Nexus Pillar'),
  (901052, 16, 'A pillar of black glass fallen from the Shadow Spire that sank here. Duelists fight around it, and behind it.');
DELETE FROM weenie_properties_float WHERE object_Id = 901052 AND type = 39;
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901052, 39, 1.6);

-- ===== 901053 Varrow the Unbowed Gen: one at a time, 15 minutes after he dies =====
-- The Old Snapjaw generator's shape (alligator-boss.sql): Generic, hidden, ethereal, retail's
-- generator setup and icon; places him at the arena's centre (where_Create 4, Specific), facing south.
INSERT INTO weenie (class_Id, class_Name, type) VALUES (901053, 'acvarrowgen', 1);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (901053, 81, 1), (901053, 82, 1), (901053, 93, 1044);
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (901053, 1, 1), (901053, 11, 1), (901053, 18, 1);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (901053, 41, 60), (901053, 43, 0);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (901053, 1, 'Varrow the Unbowed Gen');
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901053, 1, 33555051), (901053, 8, 100667494);
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES
  (901053, -1, 901050, 900, 1, 1, 1, 4, -1, 0, 0, 0x28320025, 96.0, 96.0, 76.0, 0, 0, 0, 1);

-- ===== THE ARENA: Obsidian Plains, landblock 0x2832, about 61.6S 69.6W (placement) =====
-- Open, gently sloping ground (73-77 m high round the ring; the server stood each pillar on it at
-- 76-77 on staging) about 400 m north-east of the crater
-- where the Obsidian Plains Shadow Spire sank and never moved again (AC wiki, Shadow Spire). No
-- landblock_instance rows here or in the eight blocks round it. Six Nexus Pillars in a ring of
-- radius 12 m round the centre (96, 96), every 60 degrees, and the generator in the middle.
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
  (0x72832000, 901053, 0x28320025, 96.0, 96.0, 76.0, 0, 0, 0, 1, 0),
  (0x72832001, 901052, 0x28320025, 108.0, 96.0, 76.0, 1, 0, 0, 0, 0),
  (0x72832002, 901052, 0x28320025, 102.0, 106.4, 76.0, 1, 0, 0, 0, 0),
  (0x72832003, 901052, 0x2832001D, 90.0, 106.4, 76.0, 1, 0, 0, 0, 0),
  (0x72832004, 901052, 0x2832001D, 84.0, 96.0, 76.0, 1, 0, 0, 0, 0),
  (0x72832005, 901052, 0x2832001C, 90.0, 85.6, 76.0, 1, 0, 0, 0, 0),
  (0x72832006, 901052, 0x28320024, 102.0, 85.6, 76.0, 1, 0, 0, 0, 0);

-- The arena's wandering encounters (Obsidian Outer Mix Generator, 1982): a duel is one on one.
-- REVERT: INSERT INTO encounter (id, landblock, weenie_Class_Id, cell_X, cell_Y, last_Modified) VALUES
--   (22645, 10290, 1982, 5, 2, '2005-02-09 10:00:00'), (22646, 10290, 1982, 6, 4, '2005-02-09 10:00:00'), (22647, 10290, 1982, 6, 5, '2005-02-09 10:00:00');
DELETE FROM encounter WHERE landblock = 10290 AND id IN (22645, 22646, 22647);

COMMIT;
