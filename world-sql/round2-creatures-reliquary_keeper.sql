-- INERT: content review, not applied anywhere. Round 2, The Drowned Reliquary (docs/content-review/round2-drowned-reliquary-concept.md).
-- 924003 The Reliquary Keeper, level 79: the boss of step 4. docs/content-review/round2/creatures-keeper.md has the design, the
-- base measurement, the hit timing, the phase wiring and what is verified.
-- 924050 its generator: one at a time, 10 minutes after it dies.
-- The client draws it with our own glb (expansion/creatures/round2/reliquary_keeper, AcGlbCreature): this weenie only gives the
-- SERVER its collision (setup 0x02000BEC at ObjScale 3.0), attack timing (motion table 0x09000081, combat table 0x30000008) and
-- its sounds (the golem sound table 0x20000015).
-- Defines the weenies only: no landblock_instance. The dungeon track places 924050 in the great hall (0x5950).
-- Needs from the other tracks (not defined here): 924004 Brine Wisp (the Keeper's call, the wisp track) and 924104 Reliquary Relic
-- (the quest track). Until both exist the call spawns nothing and the corpse holds no relic; nothing else breaks.
-- Re-runnable: it deletes its own rows first (every property, emote, generator and create-list table cascades from weenie).
-- Undo: round2-creatures-reliquary_keeper.undo.sql.
START TRANSACTION;

DELETE FROM weenie WHERE class_Id IN (924003, 924050);

-- ===== base: Glacial Golem Margrave (19544, level 70, setup 0x02000BEC, motion table 0x09000081, ObjScale 1.75) =====
INSERT INTO weenie (class_Id, class_Name, type) SELECT 924003, 'ace924003-reliquarykeeper', type FROM weenie WHERE class_Id = 19544;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 924003, type, value FROM weenie_properties_int WHERE object_Id = 19544;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 924003, type, value FROM weenie_properties_bool WHERE object_Id = 19544;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 924003, type, value FROM weenie_properties_float WHERE object_Id = 19544;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 924003, type, value FROM weenie_properties_string WHERE object_Id = 19544;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 924003, type, value FROM weenie_properties_d_i_d WHERE object_Id = 19544;
INSERT INTO weenie_properties_body_part (object_Id, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b) SELECT 924003, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b FROM weenie_properties_body_part WHERE object_Id = 19544;
-- Not cloned: its generator (six Glacial Golems, 14512), its create list (6353 and a blank), its emotes, its spellbook,
-- attributes, vitals and skills (set below).

-- name
UPDATE weenie_properties_string SET value = 'The Reliquary Keeper' WHERE object_Id = 924003 AND type = 1;
-- int: CreatureType (2) Elemental (62): a thing that grew out of mana-soaked brine, not a golem (golem slayers should not bite).
-- Level (25) 79. XpOverride (146) 500,000: retail's level 70-80 bosses give 30,000-55,683 (Scold 25753, Glacial Golem Margrave
-- 19544), the batch 1 quest boss gives 2,000,000 at 95; this is a fellowship quest boss at 79.
-- PaletteTemplate (3) dropped with the palette (the glb paints itself).
-- The call: MaxGeneratedObjects (81) 3, InitGeneratedObjects (82) 0 (nothing at spawn: only the Generate emote at half health
-- spawns, as retail's Blind Keeper 38349 summons its Sleech), GeneratorDestructionType (103) 2 Destroy: the wisps go when it dies.
DELETE FROM weenie_properties_int WHERE object_Id = 924003 AND type IN (2, 3, 25, 81, 82, 103, 146);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES
  (924003, 2, 62), (924003, 25, 79), (924003, 81, 3), (924003, 82, 0), (924003, 103, 2), (924003, 146, 500000);
-- float: ObjScale (39) 3.0: the setup's two spheres (r 0.55 m at z 0.54 and 1.525, Height 2.085, Radius 0.778, measured from
-- client_portal.dat) become r 1.65 m at z 1.62 and 4.575, top 6.225 m, Height 6.26, Radius 2.33; the drawn body is 6.05 m to the
-- dome. HomeRadius (55) 40 m: it keeps to the great hall. RegenerationInterval (41) dropped: the call is only ever the emote's,
-- never a timer. GeneratorRadius (43) 8 m: the wisps scatter round it.
DELETE FROM weenie_properties_float WHERE object_Id = 924003 AND type IN (39, 41, 43, 55);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (924003, 39, 3.0), (924003, 43, 8), (924003, 55, 40);
-- d_i_d: keep Setup (1) 0x02000BEC, MotionTable (2) 0x09000081, SoundTable (3) 0x20000015, CombatTable (4) 0x30000008, Icon (8),
-- PhysicsEffectTable (22) 0x3400005B (it has no 0x78-0x81, so nothing retail plays beside the phase clips); drop PaletteBase (6) and
-- ClothingBase (7) (docs/EXPANSION-ITEMS.md section 13). DeathTreasureType (35): the Margrave's 33 is tier 3 (5-10 items, quality
-- 0.25); 35 is the same shape at tier 5, for level 79.
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 924003 AND type IN (6, 7, 35);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (924003, 35, 35);

-- attributes (Str, End, Quick, Coord, Focus, Self)
INSERT INTO weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_P_Spent) VALUES
  (924003, 1, 240, 0, 0), (924003, 2, 200, 0, 0), (924003, 3, 120, 0, 0), (924003, 4, 160, 0, 0), (924003, 5, 150, 0, 0), (924003, 6, 150, 0, 0);
-- vitals: Health 6,300 + End/2 = 6,400. That is 8 x 800: a level 76 gatekeeper sized at about 2.7 x retail's level 70-82 median
-- (300, n 271), at the Crystal Golem / Gotrok Commander end (1,000 / 660); the Sentinel's own number is the Sentinel track's.
-- 6,400 sits above retail's level 70-80 golem bosses (5,000) and well under the batch 1 quest boss at 95 (12,000).
-- Stamina 2,800 + End = 3,000; Mana 850 + Self = 1,000.
INSERT INTO weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_P_Spent, current_Level) VALUES
  (924003, 1, 6300, 0, 0, 6400), (924003, 3, 2800, 0, 0, 3000), (924003, 5, 850, 0, 0, 1000);
-- skills: MeleeDefense 300, MissileDefense 330, UnarmedCombat (its claws, 13) 330, ArcaneLore 150, MagicDefense 260, Deception 200,
-- Jump 10, Run 160, Life 150, War 280 (the Tide Surge's ring is cast on its War skill)
INSERT INTO weenie_properties_skill (object_Id, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time) VALUES
  (924003, 6, 0, 3, 0, 300, 0, 0), (924003, 7, 0, 3, 0, 330, 0, 0), (924003, 13, 0, 3, 0, 330, 0, 0), (924003, 14, 0, 2, 0, 150, 0, 0),
  (924003, 15, 0, 3, 0, 260, 0, 0), (924003, 20, 0, 2, 0, 200, 0, 0), (924003, 22, 0, 2, 0, 10, 0, 0), (924003, 24, 0, 2, 0, 160, 0, 0),
  (924003, 33, 0, 3, 0, 150, 0, 0), (924003, 34, 0, 3, 0, 280, 0, 0);
-- No spellbook: it fights with its claws; the casters are its wisps. Its only spells are the emotes' (below).

-- body: the golem's nine parts keep their hit tables. The claws (key 5, the part the high and medium AttackHooks name) slash;
-- the rake (key 8, the low hook's part) bludgeons. Armour as a shell-grown sea thing: strong against cold, weak to lightning and
-- bludgeon (as its Sentinels, concept appendix D).
UPDATE weenie_properties_body_part SET base_Armor = 250, armor_Vs_Slash = 225, armor_Vs_Pierce = 250, armor_Vs_Bludgeon = 175,
  armor_Vs_Cold = 400, armor_Vs_Fire = 200, armor_Vs_Acid = 250, armor_Vs_Electric = 150, armor_Vs_Nether = 250 WHERE object_Id = 924003;
UPDATE weenie_properties_body_part SET d_Type = 1, d_Val = 75, d_Var = 0.6 WHERE object_Id = 924003 AND `key` = 5;
UPDATE weenie_properties_body_part SET d_Type = 4, d_Val = 70, d_Var = 0.6 WHERE object_Id = 924003 AND `key` = 8;

-- the quest drop: 924104 Reliquary Relic, always (Treasure 8, shade 0 = not rolled), one per corpse. The item is the quest track's.
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (924003, 8, 924104, 1, 0, 0, 0);

-- the call: three Brine Wisps (924004, the wisp track's copy without loot or xp) when the Generate emote fires. Scatter (2) round
-- it within GeneratorRadius; RegenerationType Destruction (1); the delay never runs a respawn, as it has no RegenerationInterval.
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES
  (924003, -1, 924004, 600, 3, 3, 1, 2, -1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0);

-- ===== emotes (player-facing text: no em dashes) =====
-- emote category: 3 Death, 5 HeartBeat, 12 QuestSuccess, 13 QuestFailure, 14 Taunt (after each completed swing), 15 WoundedTaunt
-- (each hit taken, filtered by min/max health), 17 NewEnemy.
-- action type: 0 Invalid (no-op), 5 Motion, 7 PhysScript, 17 LocalBroadcast, 19 CastSpellInstant, 72 Generate, 81 StampMyQuest,
-- 82 InqMyQuestSolves. The PlayScripts are SpecialState1-3 (120-122, 0x78-0x7A): reliquary_keeper.clips.json scripts[] turns each
-- into its clip (TideSurge, Call, Enrage).

-- NewEnemy: it notices you
INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (924003, 17, 1);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES
  (@e, 0, 17, 0, 1, 'Brine pours from the shrine on its back as the Reliquary Keeper rises from the deep. Its pale eye fixes on you.');

-- TIDE SURGE: after a completed swing, 18%. PlayScript 0x78 plays TideSurge (it rears and slams at 1.0 s); the ring goes out at
-- the slam: Halo of Frost (1787), a ring of frost cast from the Keeper. The chain is short (about 1 s busy), so it rarely holds
-- off a phase below.
INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (924003, 14, 0.18);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message, p_Script, spell_Id) VALUES
  (@e, 0, 7, 0, 1, NULL, 120, NULL),
  (@e, 1, 19, 1.0, 1, NULL, NULL, 1787),
  (@e, 2, 17, 0, 1, 'The Reliquary Keeper rears up and slams its claws down. A ring of icy brine rolls out across the hall.', NULL, NULL);

-- THE CALL, once, between 50% and 20% health. Every hit in that band asks the Keeper's own quest registry whether it has called
-- (InqMyQuestSolves at least 1); the first time it has not (QuestFailure), it stamps it, the relic flares (PlayScript 0x79 plays
-- Call), and 1.3 s later, as the flare peaks, Generate brings the three wisps. A new spawn starts with an empty registry.
INSERT INTO weenie_properties_emote (object_Id, category, probability, min_Health, max_Health) VALUES (924003, 15, 1, 0.20, 0.50);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message, min) VALUES
  (@e, 0, 82, 0, 1, 'ReliquaryKeeperCall', 1);
INSERT INTO weenie_properties_emote (object_Id, category, probability, quest) VALUES (924003, 13, 1, 'ReliquaryKeeperCall');
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message, p_Script) VALUES
  (@e, 0, 81, 0, 1, 'ReliquaryKeeperCall', NULL),
  (@e, 1, 7, 0, 1, NULL, 121),
  (@e, 2, 17, 0, 1, 'The relic in the Keeper''s shrine flares white. The brine around it stirs, and wisps of living water rise to its call.', NULL),
  (@e, 3, 72, 1.3, 1, NULL, NULL);

-- THE ENRAGE, once, at 20% health and below: PlayScript 0x7A plays Enrage; then it moves faster (Sprint Self VI raises its Run,
-- and so the server's run rate) and hits harder (Strength Self VI, Quickness Self VI).
INSERT INTO weenie_properties_emote (object_Id, category, probability, min_Health, max_Health) VALUES (924003, 15, 1, 0.0, 0.20);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message, min) VALUES
  (@e, 0, 82, 0, 1, 'ReliquaryKeeperEnrage', 1);
INSERT INTO weenie_properties_emote (object_Id, category, probability, quest) VALUES (924003, 13, 1, 'ReliquaryKeeperEnrage');
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message, p_Script, spell_Id) VALUES
  (@e, 0, 81, 0, 1, 'ReliquaryKeeperEnrage', NULL, NULL),
  (@e, 1, 7, 0, 1, NULL, 122, NULL),
  (@e, 2, 17, 0, 1, 'The Reliquary Keeper shudders and drops low, its shrine grinding on its back. It comes on faster now.', NULL, NULL),
  (@e, 3, 19, 0.8, 1, NULL, NULL, 987),
  (@e, 4, 19, 0.2, 1, NULL, NULL, 1332),
  (@e, 5, 19, 0.2, 1, NULL, NULL, 1402);

-- HeartBeat (idle): it shakes the water off (Twitch1, the glb's Twitch)
INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (924003, 5, 0.05);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, motion) VALUES (@e, 0, 5, 0, 1, 268435537);

-- Death
INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (924003, 3, 1);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES
  (@e, 0, 17, 0, 1, 'The Reliquary Keeper sinks onto its side and the shrine on its back settles into the silt. The relic comes loose in its cradle.');

-- ===== 924050 The Reliquary Keeper's generator: one at a time, 10 minutes after it dies =====
-- The alligator boss generator's shape (tools/gpubox-ace/alligator-boss.sql, 900402): Generic, the retail generator setup and icon,
-- one slot, RegenerationType Destruction with a 600 s delay. OnTop (1): it spawns where the dungeon track places this generator.
INSERT INTO weenie (class_Id, class_Name, type) VALUES (924050, 'ace924050-reliquarykeepergen', 1);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (924050, 81, 1), (924050, 82, 1), (924050, 93, 1044);
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (924050, 1, 1), (924050, 11, 1), (924050, 18, 1);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (924050, 41, 60), (924050, 43, 0);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (924050, 1, 'The Reliquary Keeper Gen');
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (924050, 1, 33555051), (924050, 8, 100667494);
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES
  (924050, -1, 924003, 600, 1, 1, 1, 1, -1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0);

COMMIT;
