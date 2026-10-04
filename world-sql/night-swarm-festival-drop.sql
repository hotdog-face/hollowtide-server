-- The Necklace of the Night Swarm (901700) as a rare Festival Season drop (owner 2026-10-01): the Pumpkin
-- Lord (32186) and Rand's festival scarecrows, Vile (28881), Vicious (28880) and Villainous (28882).
-- docs/NIGHT-SWARM.md "Where players get it".
--
-- 1 in 100 per kill, each its own treasure set (necklace 0.01, nothing 0.99), placed after the creature's
-- existing rows (all closed at 1.0, checked 2026-10-01; the Pumpkin Lord has none), so no other drop changes.
-- Out of season RevivalGuard SeasonalDrops removes a rolled necklace (901700 is in its table under
-- festivalseason), the same gate as the Mask Maker trophies. These creatures only spawn in the festival
-- anyway; the gate also covers an @create'd or left-over one.
--
-- IDEMPOTENT: rows 9310001-9310008 are this file's and are deleted first.
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/night-swarm-festival-drop.sql'
-- Undo:  DELETE FROM weenie_properties_create_list WHERE id BETWEEN 9310001 AND 9310008;
START TRANSACTION;
DELETE FROM weenie_properties_create_list WHERE id BETWEEN 9310001 AND 9310008;
INSERT INTO weenie_properties_create_list (id, object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (9310001, 32186, 9, 901700, 1, 0, 0.01, 0), -- Necklace of the Night Swarm on Pumpkin Lord
  (9310002, 32186, 9, 0, 0, 0, 0.99, 0),      -- nothing (closes the set)
  (9310003, 28881, 9, 901700, 1, 0, 0.01, 0), -- on Vile Scarecrow
  (9310004, 28881, 9, 0, 0, 0, 0.99, 0),
  (9310005, 28880, 9, 901700, 1, 0, 0.01, 0), -- on Vicious Scarecrow
  (9310006, 28880, 9, 0, 0, 0, 0.99, 0),
  (9310007, 28882, 9, 901700, 1, 0, 0.01, 0), -- on Villainous Scarecrow
  (9310008, 28882, 9, 0, 0, 0, 0.99, 0);
COMMIT;
