-- Read-only: palette and shade of every item an admin character is wearing (for @ci <wcid> 1 <palette> <shade> copies).
-- Run: ssh ovh 'sudo -n mysql -N' < tools/gpubox-ace/owner-worn-colors.sql > ~/worn-colors.txt
USE ace_shard;
SELECT c.id INTO @me FROM `character` c WHERE c.name LIKE '%an admin character' AND c.is_Deleted = 0 LIMIT 1;
SELECT HEX(@me) AS me;
SELECT b.weenie_Class_Id AS wcid,
       n.value AS name,
       (SELECT value FROM biota_properties_int   WHERE object_Id = b.id AND type = 3)   AS palette,
       (SELECT value FROM biota_properties_float WHERE object_Id = b.id AND type = 12)  AS shade,
       (SELECT value FROM biota_properties_int   WHERE object_Id = b.id AND type = 105) AS workmanship
FROM biota b
JOIN biota_properties_string n ON n.object_Id = b.id AND n.type = 1
JOIN biota_properties_i_i_d  w ON w.object_Id = b.id AND w.type = 3 AND w.value = @me
ORDER BY n.value;
