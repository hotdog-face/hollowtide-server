-- READ-ONLY. What every Staff Base armor stand (and the Wardrobe Chest) holds, for a before/after check around
-- 2026-11-06-staff-base-stand-gallery.sql (owner live ticket 261106-0024: "make sure it doesnt mess up the armor sets ive
-- set up on my stands"). Run it before the restart and after it, and diff the two outputs: the rows must match, item
-- for item. Only the "hook position" rows change (the stands move).
--   sudo -n mysql -N < tools/gpubox-ace/staff-base-stand-contents.sql > before.txt   (and after.txt; then diff)
-- Not a datafix: it is kept out of tools/public-shard/datafixes/ so nothing runs it on a restart.

-- 1. what each hook holds (its Armor Stand, 900501) and what each stand holds (the suit: ContainerId = the stand), plus
--    the Wardrobe Chest's contents. hook, stand, item, weenie, name.
SELECT 'item', HEX(h.value) AS hook, HEX(s1.id) AS on_hook, HEX(b.id) AS item, b.weenie_Class_Id,
       (SELECT s.value FROM ace_shard.biota_properties_string s WHERE s.object_Id = b.id AND s.type = 1) AS name
FROM ace_shard.biota s1
JOIN ace_shard.biota_properties_i_i_d h ON h.object_Id = s1.id AND h.type = 2          -- the stand's ContainerId = the hook
JOIN ace_shard.biota_properties_i_i_d c ON c.type = 2 AND c.value = s1.id              -- the suit's ContainerId = the stand
JOIN ace_shard.biota b ON b.id = c.object_Id
WHERE h.value IN (0x7576B202, 0x7576B203) OR h.value BETWEEN 0x7576B230 AND 0x7576B239
ORDER BY h.value, b.id;
SELECT 'chest item', HEX(b.id), b.weenie_Class_Id,
       (SELECT s.value FROM ace_shard.biota_properties_string s WHERE s.object_Id = b.id AND s.type = 1)
FROM ace_shard.biota b JOIN ace_shard.biota_properties_i_i_d c ON c.object_Id = b.id AND c.type = 2 AND c.value = 0x7576B170
ORDER BY b.id;

-- 2. the counts: per hook, its stand and how many pieces the stand holds
SELECT 'count', HEX(h.value) AS hook, HEX(s1.id) AS stand, s1.weenie_Class_Id,
       (SELECT COUNT(*) FROM ace_shard.biota_properties_i_i_d c WHERE c.type = 2 AND c.value = s1.id) AS pieces
FROM ace_shard.biota s1 JOIN ace_shard.biota_properties_i_i_d h ON h.object_Id = s1.id AND h.type = 2
WHERE h.value IN (0x7576B202, 0x7576B203) OR h.value BETWEEN 0x7576B230 AND 0x7576B239
ORDER BY h.value;

-- 3. where each hook stands (the only thing the gallery file changes): world row, then saved copy
SELECT 'hook position', HEX(guid), weenie_Class_Id, origin_X, origin_Y FROM ace_world.landblock_instance
WHERE guid IN (0x7576B202, 0x7576B203, 0x7576B170) OR guid BETWEEN 0x7576B230 AND 0x7576B239 ORDER BY guid;
SELECT 'hook saved position', HEX(object_Id), origin_X, origin_Y FROM ace_shard.biota_properties_position
WHERE position_Type = 1 AND (object_Id IN (0x7576B202, 0x7576B203, 0x7576B170) OR object_Id BETWEEN 0x7576B230 AND 0x7576B239)
ORDER BY object_Id;

-- 4. decor-10 preconditions: weenie 903410 and guid 0x7576B170 free, or already the Wardrobe Chest
SELECT 'decor-10 weenie', class_Id, class_Name FROM ace_world.weenie WHERE class_Id = 903410;
SELECT 'decor-10 guid', HEX(guid), weenie_Class_Id FROM ace_world.landblock_instance WHERE guid = 0x7576B170;
SELECT 'gallery light guids in use', HEX(guid), weenie_Class_Id FROM ace_world.landblock_instance WHERE guid BETWEEN 0x7576B180 AND 0x7576B189;
