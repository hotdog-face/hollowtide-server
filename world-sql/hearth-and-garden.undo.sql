-- Undo of tools/gpubox-ace/hearth-and-garden.sql: the shop rows and weenies 921000-921050.
-- Hung or carried copies stay in players' houses and packs (ace_shard); this removes only the definitions and the shop rows.
START TRANSACTION;
DELETE FROM weenie_properties_create_list WHERE object_Id IN (12241, 12242, 12243) AND weenie_Class_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_int WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_int64 WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_bool WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_float WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_d_i_d WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_string WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie WHERE class_Id BETWEEN 921000 AND 921050;
COMMIT;
