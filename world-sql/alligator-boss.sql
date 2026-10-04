-- OLD SNAPJAW, THE BLACKMIRE ALLIGATOR (owner 2026-09-27: "place him where it makes the most sense").
-- docs/ALLIGATOR-BOSS.md has where, why, stats, behaviour, test and revert. Weenies 900400-900402:
--   900400  Old Snapjaw            the boss (sculpted glb: expansion/creatures/alligator)
--   900401  Mounted Snapjaw Head   his trophy (RevivalGuard AlligatorBoss inscribes it)
--   900402  Old Snapjaw Gen        his generator at the lair, 10 min respawn
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/alligator-boss.sql
--   then in game: @clearcache weenie ; @clearcache landblock ; @reloadblock CB60
--   (AGENT-BRIEF trap 7; @reloadblock is refused while anyone stands in CB60)
--
-- NEW ROWS ONLY: every weenie is ours and the lair landblock (0xCB60) held no landblock_instance
-- rows at all (tools/gpubox-ace/alligator-boss-backup-2026-09-28.sql is that empty dump, kept as
-- the proof). Re-runnable: it deletes its own rows first. REVERT: the three DELETEs just below.
START TRANSACTION;

DELETE FROM landblock_instance WHERE guid = 0x7CB60C00;
DELETE FROM landblock_instance WHERE weenie_Class_Id BETWEEN 900400 AND 900419;
DELETE FROM weenie WHERE class_Id BETWEEN 900400 AND 900419;

-- ===== 900400 Old Snapjaw =====
-- Base: the Adult Reedshark (221), retail's water-edge beast. Its setup (0x02000039, one 0.5 m
-- sphere) and motion table (0x0900001A: HandCombat AttackHigh1/Med1/Low1, RunForward) give the
-- SERVER his collision and attack timing; the client draws the glb instead (AcGlbCreature).
INSERT INTO weenie (class_Id, class_Name, type) SELECT 900400, 'acoldsnapjaw', type FROM weenie WHERE class_Id = 221;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900400, type, value FROM weenie_properties_int WHERE object_Id = 221;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900400, type, value FROM weenie_properties_bool WHERE object_Id = 221;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900400, type, value FROM weenie_properties_float WHERE object_Id = 221;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 900400, type, value FROM weenie_properties_string WHERE object_Id = 221;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900400, type, value FROM weenie_properties_d_i_d WHERE object_Id = 221;
INSERT INTO weenie_properties_body_part (object_Id, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b) SELECT 900400, `key`, d_Type, d_Val, d_Var, base_Armor, armor_Vs_Slash, armor_Vs_Pierce, armor_Vs_Bludgeon, armor_Vs_Cold, armor_Vs_Fire, armor_Vs_Acid, armor_Vs_Electric, armor_Vs_Nether, b_h, h_l_f, m_l_f, l_l_f, h_r_f, m_r_f, l_r_f, h_l_b, m_l_b, l_l_b, h_r_b, m_r_b, l_r_b FROM weenie_properties_body_part WHERE object_Id = 221;
-- Not cloned: the Reedshark's create list (a 3% hide), its emotes (heartbeat twitches) and loot table.

-- name
UPDATE weenie_properties_string SET value = 'Old Snapjaw' WHERE object_Id = 900400 AND type = 1;
-- int: Level 220; no CreatureType (2 removed: RevivalGuard MonsterAi would make a Reedshark FLEE at
-- 25% and pack-call kin; a boss does neither); XpOverride 25,000,000; PaletteTemplate (3) dropped
-- with the palette (the glb paints itself).
DELETE FROM weenie_properties_int WHERE object_Id = 900400 AND type IN (2, 3, 25, 146);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (900400, 25, 220), (900400, 146, 25000000);
-- float: VisualAwarenessRange 16 m (he waits until you reach the water's edge), ObjScale 2.4 (the
-- server sphere becomes 1.19 m round his shoulders; the client ignores it, ignoreServerScale),
-- HomeRadius 55 m (past that he goes back to the water; MonsterAi heals him there), HealthRate
-- 0.5/s, PowerupTime 0.8 s (random pause after each bite)
-- Resist* multipliers (64-70: slash, pierce, bludgeon, fire, cold, acid, electric): the same hide
-- story as the body-part armour below; cold 1.2 means 20% MORE damage.
DELETE FROM weenie_properties_float WHERE object_Id = 900400 AND type IN (3, 12, 31, 34, 39, 55, 64, 65, 66, 67, 68, 69, 70);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (900400, 3, 0.5), (900400, 31, 16), (900400, 34, 0.8), (900400, 39, 2.4), (900400, 55, 55),
  (900400, 64, 0.8), (900400, 65, 0.85), (900400, 66, 1.0), (900400, 67, 0.7), (900400, 68, 1.2), (900400, 69, 0.7), (900400, 70, 1.0);
-- d_i_d: keep Setup, MotionTable, SoundTable, CombatTable, PhysicsEffectTable, Icon; drop
-- PaletteBase (6) and ClothingBase (7) (docs/EXPANSION-ITEMS.md section 9); DeathTreasureType 2121
-- (tier 8, loot quality 0.4: 3-5 magic items, 1-2 items, 2-3 mundane).
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 900400 AND type IN (6, 7, 35);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (900400, 35, 2121);

-- attributes: Str 300, End 380, Coord 280, Quick 260, Focus 150, Self 150
INSERT INTO weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_P_Spent) VALUES
  (900400, 1, 300, 0, 0), (900400, 2, 380, 0, 0), (900400, 3, 280, 0, 0), (900400, 4, 260, 0, 0), (900400, 5, 150, 0, 0), (900400, 6, 150, 0, 0);
-- vitals: Health 8,810 + End/2 = 9,000; Stamina 3,000; Mana 500
INSERT INTO weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_P_Spent, current_Level) VALUES
  (900400, 1, 8810, 0, 0, 9000), (900400, 3, 2620, 0, 0, 3000), (900400, 5, 350, 0, 0, 500);
-- skills (specialized): MeleeDefense 380, MissileDefense 360, MagicDefense 330, Run 330, Jump 150,
-- Perception 220, unarmed attack (LightWeapons, 45) 470. Measured against the owner's an admin character
-- (level 275, 290 Coord/Quick, Missile Weapons specialized, Melee Defense trained): see the doc.
INSERT INTO weenie_properties_skill (object_Id, type, level_From_P_P, s_a_c, p_p, init_Level, resistance_At_Last_Check, last_Used_Time) VALUES
  (900400, 6, 0, 3, 0, 380, 0, 0), (900400, 7, 0, 3, 0, 360, 0, 0), (900400, 15, 0, 3, 0, 330, 0, 0),
  (900400, 24, 0, 3, 0, 330, 0, 0), (900400, 22, 0, 3, 0, 150, 0, 0), (900400, 20, 0, 3, 0, 220, 0, 0),
  (900400, 45, 0, 3, 0, 470, 0, 0);

-- body parts. The Reedshark's four: 0 head (the bite), 10 and 13 the legs (claws), 16 the tail.
-- Bite: pierce 80, variance 0.5; claws: slash 45; tail: bludgeon 55. Hide armour 120 base, the
-- belly softer; weak to cold (he is cold-blooded), tough against fire and acid (swamp-hardened).
UPDATE weenie_properties_body_part SET d_Type = 2, d_Val = 80, d_Var = 0.5,  base_Armor = 130, armor_Vs_Slash = 130, armor_Vs_Pierce = 120, armor_Vs_Bludgeon = 110, armor_Vs_Cold = 80, armor_Vs_Fire = 170, armor_Vs_Acid = 170, armor_Vs_Electric = 120, armor_Vs_Nether = 120 WHERE object_Id = 900400 AND `key` = 0;
UPDATE weenie_properties_body_part SET d_Type = 1, d_Val = 45, d_Var = 0.5,  base_Armor = 110, armor_Vs_Slash = 110, armor_Vs_Pierce = 100, armor_Vs_Bludgeon = 95, armor_Vs_Cold = 70, armor_Vs_Fire = 150, armor_Vs_Acid = 150, armor_Vs_Electric = 105, armor_Vs_Nether = 105 WHERE object_Id = 900400 AND `key` IN (10, 13);
UPDATE weenie_properties_body_part SET d_Type = 4, d_Val = 55, d_Var = 0.5,  base_Armor = 125, armor_Vs_Slash = 125, armor_Vs_Pierce = 115, armor_Vs_Bludgeon = 105, armor_Vs_Cold = 80, armor_Vs_Fire = 165, armor_Vs_Acid = 165, armor_Vs_Electric = 115, armor_Vs_Nether = 115 WHERE object_Id = 900400 AND `key` = 16;

-- ===== 900401 Mounted Snapjaw Head: his trophy, the Tusker head's weenie with his name =====
-- Every row of 900060 (trophy-mounts.sql must be applied), then the name and the default text.
-- The model is expansion/models/w900401.glb, found through expansion/models/names.json.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 900401, 'actrophyoldsnapjaw', type FROM weenie WHERE class_Id = 900060;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900401, type, value FROM weenie_properties_int WHERE object_Id = 900060;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900401, type, value FROM weenie_properties_bool WHERE object_Id = 900060;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900401, type, value FROM weenie_properties_float WHERE object_Id = 900060;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900401, type, value FROM weenie_properties_d_i_d WHERE object_Id = 900060;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 900401, type, value FROM weenie_properties_string WHERE object_Id = 900060;
UPDATE weenie_properties_string SET value = 'Mounted Snapjaw Head' WHERE object_Id = 900401 AND type = 1;
DELETE FROM weenie_properties_string WHERE object_Id = 900401 AND type = 16;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900401, 16, 'The head of Old Snapjaw, the great alligator of the Blackmire, jaws set wide on a plaque. Hang it on a wall hook.');

-- ===== 900402 Old Snapjaw Gen: one at a time, 10 minutes after he dies =====
-- The golem generators' shape (new-content-baelzharon-golems.sql): Generic, hidden, ethereal, the
-- retail generator setup and icon. The profile places him at the lair (where_Create 4, Specific),
-- facing the south bank.
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900402, 'acoldsnapjawgen', 1);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (900402, 81, 1), (900402, 82, 1), (900402, 93, 1044);
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (900402, 1, 1), (900402, 11, 1), (900402, 18, 1);
INSERT INTO weenie_properties_float (object_Id, type, value) VALUES (900402, 41, 60), (900402, 43, 0);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900402, 1, 'Old Snapjaw Gen');
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (900402, 1, 33555051), (900402, 8, 100667494);
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES
  (900402, -1, 900400, 600, 1, 1, 1, 4, -1, 0, 0, 0xCB600027, 102.0, 146.0, 6.0, 0, 0, 0, 1);

-- ===== the lair: Blackmire Swamp, landblock 0xCB60, 24.6S 61.1E =====
-- A channel of standing water two vertices wide running east-west, marsh on both banks; he lies in
-- the water 12 m off its south bank (cell 0x27, water at all four corners, where the server stands
-- a body 0.9 m under the terrain: the lurk is authored for exactly that), facing the bank (south,
-- angles_Z 1 = 180 degrees). Moved from the shoreline cell 0x26 (y 134) after the first live look:
-- there he lay in mud, not water. docs/ALLIGATOR-BOSS.md, Placement.
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
  (0x7CB60C00, 900402, 0xCB600027, 102.0, 146.0, 6.0, 0, 0, 0, 1, 0);

COMMIT;
