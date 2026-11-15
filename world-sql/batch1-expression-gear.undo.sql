-- Undo of batch1-expression-gear.sql: removes weenies 922100-922149 (this batch's block) and nothing else.
START TRANSACTION;
DELETE FROM weenie_properties_int WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_int64 WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_bool WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_float WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_d_i_d WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_i_i_d WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_string WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_spell_book WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie_properties_anim_part WHERE object_Id BETWEEN 922100 AND 922149;
DELETE FROM weenie WHERE class_Id BETWEEN 922100 AND 922149;
COMMIT;
