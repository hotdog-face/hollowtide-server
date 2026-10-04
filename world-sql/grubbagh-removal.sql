-- GRUBBAGH REMOVAL (owner 2026-09-29: "i want to cancel the Grubbagh content. he's not approved. lets remove
-- him and all things related. chest, adds, etc."). docs/GRUBBAGH-REMOVAL.md has the full inventory.
--
-- Deletes, from ace_world only:
--   * every landblock_instance row of weenies 900950-900999 and the lair guids 0x74443000-0x744430FF
--     (East Direlands Swamp 0x4443: Grubbagh Gen 0x74443000, throne, bone heap, mireling pen, two Pools of
--     Bile, sacks, meat rack, trough, two egg clutches, and Grubbagh's Larder 0x7444300B);
--   * every weenie 900950-900999 (the boss 900950, Mireling 900951, Swill-Thrall 900952, Brood Sac 900953,
--     Grubbagh Gen 900954, lair props 900960-900967, Grubbagh's Larder 900968). Their property, generator
--     and create-list rows go with the weenie (ON DELETE CASCADE).
-- Retail weenies are not touched (the larder's meats are retail foods 27669/5270/5217/34864 and stay).
-- ace_shard is not touched here (see the doc for the leftover world objects there).
--
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/grubbagh-removal.sql 4443'
-- REVERT: restore the ace_world backup apply-live.sh names, or re-run the archived creation SQL.
START TRANSACTION;

DELETE FROM landblock_instance
 WHERE weenie_Class_Id BETWEEN 900950 AND 900999
    OR guid BETWEEN 0x74443000 AND 0x744430FF;
DELETE FROM weenie_properties_generator WHERE weenie_Class_Id BETWEEN 900950 AND 900999;
DELETE FROM weenie_properties_create_list WHERE weenie_Class_Id BETWEEN 900950 AND 900999;
DELETE FROM weenie WHERE class_Id BETWEEN 900950 AND 900999;

COMMIT;

SELECT COUNT(*) AS grubbagh_weenies_left FROM weenie WHERE class_Id BETWEEN 900950 AND 900999;
SELECT COUNT(*) AS grubbagh_placements_left FROM landblock_instance
 WHERE weenie_Class_Id BETWEEN 900950 AND 900999 OR guid BETWEEN 0x74443000 AND 0x744430FF;
