-- OBTAINABLE ITEMS (owner 2026-09-30: "there are likely many items that players can never get because they
-- exist but have no way to get them ... Let's make those receivable by players in various ways").
-- Audit, counts and the list left unreachable on purpose: docs/OBTAINABLE-ITEMS-2026-09-30.md.
-- Audit tool: tools/gpubox-ace/obtainable_audit.py.
--
-- Everything here adds an item to an EXISTING source (content freeze: no new NPCs, dialogue, quests or zones):
--   * our Heartseed of the Deru (900862): Doriathazaar's existing first-talk reward, plus a 3% roll on the
--     four Viridian Rise bosses that already drop Heartwood Sap (it was only on the admin-only Keeper);
--   * our Replica Nexus Blade (901055): sold by Pang Sin-Xiang the Weaponsmith in Wai Jhou, the weaponsmith
--     nearest Varrow's arena (it was only on the admin-only Keeper);
--   * twelve retail Mask Maker trophies: the Mask Makers (Tsua Kagemata, Janda Sulifiya, Alexander the Deft)
--     take them for masks, but no creature in this data dropped them. Each goes on the creatures the wiki
--     names as its droppers, as its own treasure set (item p, nothing 1-p). Retail dropped them in festival
--     season only, and so do these: RevivalGuard's SeasonalDrops removes a rolled trophy outside Festival
--     Season (owner 2026-09-30; docs/OBTAINABLE-ITEMS-2026-09-30.md section 6).
--
-- IDEMPOTENT: every row this file owns has an explicit id in 9300001-9300999 and is deleted first, so a
-- re-run replaces them exactly. Explicit high ids also keep each new treasure set AFTER the creature's
-- existing rows (ACE rolls create-list sets in row order and chunks them at a total of 1.0; every target's
-- existing sets were checked closed on 2026-09-30, so a new set never merges into an old one).
--
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/obtainable-items-2026-09-30.sql'
--   (it clears the weenie cache; creatures already spawned keep their old create list until they respawn,
--   a vendor's shop is read from the weenie when the shop is opened after the cache clear)
-- Undo: DELETE FROM weenie_properties_create_list WHERE id BETWEEN 9300001 AND 9300999;
--       DELETE FROM weenie_properties_emote_action WHERE id BETWEEN 9300001 AND 9300999;
START TRANSACTION;
DELETE FROM weenie_properties_create_list WHERE id BETWEEN 9300001 AND 9300999;
DELETE FROM weenie_properties_emote_action WHERE id BETWEEN 9300001 AND 9300999;

-- ===== creature drops (destination 9 = contain + treasure; shade = chance) =====
INSERT INTO weenie_properties_create_list (id, object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
-- Undead Sailor's Head: retail Mask Maker trophy, the wiki says Undead Sailors drop it, Tsua Kagemata, Janda Sulifiya and Alexander the Deft trade it for an Undead Sailor Mask
  (9300001, 24323, 9, 36361, 1, 0, 0.05, 0), -- Undead Sailor's Head on Undead Sailor (24323)
  (9300002, 24323, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300003, 24324, 9, 36361, 1, 0, 0.05, 0), -- Undead Sailor's Head on Undead Sailor (24324)
  (9300004, 24324, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Undead Captain's Head: retail Mask Maker trophy dropped by the Undead Captain, the Mask Makers trade it for an Undead Captain Mask
  (9300005, 24321, 9, 70324, 1, 0, 0.05, 0), -- Undead Captain's Head on Undead Captain (24321)
  (9300006, 24321, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Ruschk Head: retail Mask Maker trophy dropped by the Ruschk (wiki: Barbaric Ruschk, Laktar, Sadist, Slayer, Warlord)
  (9300007, 28669, 9, 70279, 1, 0, 0.05, 0), -- Ruschk Head on Barbaric Ruschk (28669)
  (9300008, 28669, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300009, 29342, 9, 70279, 1, 0, 0.05, 0), -- Ruschk Head on Ruschk Laktar (29342)
  (9300010, 29342, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300011, 29344, 9, 70279, 1, 0, 0.05, 0), -- Ruschk Head on Ruschk Sadist (29344)
  (9300012, 29344, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300013, 28666, 9, 70279, 1, 0, 0.05, 0), -- Ruschk Head on Ruschk Slayer (28666)
  (9300014, 28666, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300015, 28668, 9, 70279, 1, 0, 0.05, 0), -- Ruschk Head on Ruschk Warlord (28668)
  (9300016, 28668, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Snowman Head: retail Mask Maker trophy dropped by Snowmen
  (9300017, 5761, 9, 70280, 1, 0, 0.05, 0), -- Snowman Head on Snowman (5761)
  (9300018, 5761, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300019, 5766, 9, 70280, 1, 0, 0.05, 0), -- Snowman Head on Snowman (5766)
  (9300020, 5766, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Two Headed Snowman Head: retail Mask Maker trophy dropped by the Two Headed Snowman
  (9300021, 14466, 9, 32185, 1, 0, 0.05, 0), -- Two Headed Snowman Head on Two Headed Snowman (14466)
  (9300022, 14466, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Uber Penguin Head: retail Mask Maker trophy dropped by Uber Penguins
  (9300023, 28659, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (28659)
  (9300024, 28659, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300025, 28660, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (28660)
  (9300026, 28660, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300027, 28661, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (28661)
  (9300028, 28661, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300029, 35152, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (35152)
  (9300030, 35152, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300031, 88385, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (88385)
  (9300032, 88385, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300033, 88388, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (88388)
  (9300034, 88388, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300035, 88391, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (88391)
  (9300036, 88391, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300037, 88394, 9, 70278, 1, 0, 0.05, 0), -- Uber Penguin Head on Uber Penguin (88394)
  (9300038, 88394, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Penguin Head: retail Mask Maker trophy dropped by Penguins (wiki: Penguin, Arrogant, Sycophantic, Augmented Penguin is not in the data)
  (9300039, 28662, 9, 70275, 1, 0, 0.05, 0), -- Penguin Head on Penguin (28662)
  (9300040, 28662, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300041, 28663, 9, 70275, 1, 0, 0.05, 0), -- Penguin Head on Arrogant Penguin (28663)
  (9300042, 28663, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300043, 28658, 9, 70275, 1, 0, 0.05, 0), -- Penguin Head on Sycophantic Penguin (28658)
  (9300044, 28658, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Ghostly Shroud: retail Mask Maker trophy (Ghost Guise) dropped by Doomed Spirits, Specters and Spirits
  (9300045, 31948, 9, 32181, 1, 0, 0.05, 0), -- Ghostly Shroud on Doomed Spirit (31948)
  (9300046, 31948, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300047, 28048, 9, 32181, 1, 0, 0.05, 0), -- Ghostly Shroud on Specter (28048)
  (9300048, 28048, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300049, 28246, 9, 32181, 1, 0, 0.05, 0), -- Ghostly Shroud on Spirit (28246)
  (9300050, 28246, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300051, 30713, 9, 32181, 1, 0, 0.05, 0), -- Ghostly Shroud on Spirit (30713)
  (9300052, 30713, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Knath Husk: retail Mask Maker trophy (Knath Head) dropped by the six K'nath the wiki names
  (9300053, 23556, 9, 25741, 1, 0, 0.05, 0), -- Knath Husk on K'nath An'dras (23556)
  (9300054, 23556, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300055, 25659, 9, 25741, 1, 0, 0.05, 0), -- Knath Husk on K'nath I'km (25659)
  (9300056, 25659, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300057, 23558, 9, 25741, 1, 0, 0.05, 0), -- Knath Husk on K'nath La'nal (23558)
  (9300058, 23558, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300059, 23559, 9, 25741, 1, 0, 0.05, 0), -- Knath Husk on K'nath N'aes (23559)
  (9300060, 23559, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300061, 25292, 9, 25741, 1, 0, 0.05, 0), -- Knath Husk on K'nath Thea'reh (25292)
  (9300062, 25292, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
  (9300063, 25293, 9, 25741, 1, 0, 0.05, 0), -- Knath Husk on K'nath X'ela (25293)
  (9300064, 25293, 9, 0, 0, 0, 0.95, 0), -- nothing (closes the set)
-- Ursuin parts: retail Mask Maker trophy parts: Arm + Torso + Legs combine into an Ursuin Body, one roll per kill, 4% each
  (9300065, 38181, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Blighted Dire Ursuin (38181)
  (9300066, 38181, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Blighted Dire Ursuin (38181)
  (9300067, 38181, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Blighted Dire Ursuin (38181)
  (9300068, 38181, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300069, 7994, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Dire Ursuin (7994)
  (9300070, 7994, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Dire Ursuin (7994)
  (9300071, 7994, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Dire Ursuin (7994)
  (9300072, 7994, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300073, 23568, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Dreadful Ursuin (23568)
  (9300074, 23568, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Dreadful Ursuin (23568)
  (9300075, 23568, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Dreadful Ursuin (23568)
  (9300076, 23568, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300077, 27715, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Ferocious Ursuin (27715)
  (9300078, 27715, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Ferocious Ursuin (27715)
  (9300079, 27715, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Ferocious Ursuin (27715)
  (9300080, 27715, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300081, 7990, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Field Ursuin (7990)
  (9300082, 7990, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Field Ursuin (7990)
  (9300083, 7990, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Field Ursuin (7990)
  (9300084, 7990, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300085, 7993, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Linvak Ursuin (7993)
  (9300086, 7993, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Linvak Ursuin (7993)
  (9300087, 7993, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Linvak Ursuin (7993)
  (9300088, 7993, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300089, 27716, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Raging Ursuin (27716)
  (9300090, 27716, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Raging Ursuin (27716)
  (9300091, 27716, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Raging Ursuin (27716)
  (9300092, 27716, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300093, 7989, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Scavenger Ursuin (7989)
  (9300094, 7989, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Scavenger Ursuin (7989)
  (9300095, 7989, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Scavenger Ursuin (7989)
  (9300096, 7989, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
  (9300097, 7991, 9, 32170, 1, 0, 0.04, 0), -- Ursuin Arm on Tiofor Ursuin (7991)
  (9300098, 7991, 9, 32171, 1, 0, 0.04, 0), -- Ursuin Legs on Tiofor Ursuin (7991)
  (9300099, 7991, 9, 32174, 1, 0, 0.04, 0), -- Ursuin Torso on Tiofor Ursuin (7991)
  (9300100, 7991, 9, 0, 0, 0, 0.88, 0), -- nothing (closes the set)
-- Heartseed of the Deru: OURS: the Viridian Grove set's trinket had no player source (only the admin Keeper), VIRIDIAN-RISE-UPGRADE.md wanted a small boss chance, the four Viridian Rise bosses that already drop Heartwood Sap
  (9300101, 53365, 9, 900862, 1, 0, 0.03, 0), -- Heartseed of the Deru on Wind Fury (53365)
  (9300102, 53365, 9, 0, 0, 0, 0.97, 0), -- nothing (closes the set)
  (9300103, 72000, 9, 900862, 1, 0, 0.03, 0), -- Heartseed of the Deru on NightBrier (72000)
  (9300104, 72000, 9, 0, 0, 0, 0.97, 0), -- nothing (closes the set)
  (9300105, 72001, 9, 900862, 1, 0, 0.03, 0), -- Heartseed of the Deru on Norshuntyr (72001)
  (9300106, 72001, 9, 0, 0, 0, 0.97, 0), -- nothing (closes the set)
  (9300107, 72002, 9, 900862, 1, 0, 0.03, 0), -- Heartseed of the Deru on Zerzelikyr (72002)
  (9300108, 72002, 9, 0, 0, 0, 0.97, 0); -- nothing (closes the set)
-- ===== vendor =====
-- Replica Nexus Blade (901055): OURS, the costume copy of Varrow's blade (1 damage, no spells, value 500).
-- Made for the Keeper of Lost Things, who stands in admin-only Hotel Swank. Pang Sin-Xiang the Weaponsmith
-- (24220, Wai Jhou 61.9S 51.5W) is the weaponsmith on the arena's own latitude (Varrow: 61.6S 69.6W); his
-- sell rate 1.9 makes it 950 pyreals. The real Nexus Blade of the Unbowed (901054) still only drops from Varrow.
INSERT INTO weenie_properties_create_list (id, object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (9300201, 24220, 4, 901055, -1, 0, 0, 0); -- Replica Nexus Blade, Pang Sin-Xiang's shop

-- ===== quest reward =====
-- Heartseed of the Deru (900862): OURS, the Viridian Grove set's trinket. docs/VIRIDIAN-RISE-UPGRADE.md
-- section "Rewards": "Doriathazaar's first talk also gives the Heartseed of the Deru". That talk is his
-- existing QuestFailure emote on ClimbedViridianDeru@2 (max_Solves 1, so once per character); it already
-- gives 100 Infused Amber (52968) and stamps the quest. This adds one Give action after the stamp, copied
-- from that Give row. No new words: the player sees the ordinary "Doriathazaar gives you" line.
INSERT INTO weenie_properties_emote_action (id, emote_Id, `order`, type, delay, extent, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
SELECT 9300301, a.emote_Id, 100, 3, a.delay, a.extent, a.destination_Type, 900862, 1, 0, 0, 0
FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
WHERE e.object_Id = 53272 AND e.category = 13 AND e.quest = 'ClimbedViridianDeru@2' AND a.type = 3 AND a.weenie_Class_Id = 52968
LIMIT 1;
COMMIT;
