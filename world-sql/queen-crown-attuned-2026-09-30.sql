-- Crown of the Unbound Queen (900813) attuned and bonded, as the First Brand is (owner 2026-09-30: fix the
-- exploit). The same two rows gen_sunken_lyceum.py now writes into sunken-lyceum.sql; applied on its own so
-- the Lyceum's placements are not reloaded.
START TRANSACTION;
DELETE FROM weenie_properties_int WHERE object_Id = 900813 AND type IN (33, 114);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (900813, 33, 1), (900813, 114, 1);
COMMIT;
