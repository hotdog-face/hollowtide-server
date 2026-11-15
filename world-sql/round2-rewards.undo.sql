-- INERT: content review, not applied anywhere.
-- Undo of round2-rewards.sql: deletes weenies 924300-924305 and every property row it wrote. Touches nothing else.
START TRANSACTION;
DELETE FROM weenie_properties_int WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_int64 WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_bool WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_float WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_d_i_d WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_i_i_d WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_string WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_palette WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_texture_map WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_spell_book WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie_properties_anim_part WHERE object_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
DELETE FROM weenie WHERE class_Id IN (924300, 924301, 924302, 924303, 924304, 924305);
COMMIT;
