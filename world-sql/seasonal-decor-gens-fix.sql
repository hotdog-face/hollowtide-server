-- Seasonal decoration generators 900745-900747: give them their generator profile row (2026-09-30).
--
-- Owner, Holtburg, 2026-09-30: the Frostfell Garland Arch and the Spring Maypole were standing in the
-- square in September. The three gens were cloned from ace73174 (Linkable Fall Festival Gen) without
-- its weenie_properties_generator row, the placeholder (wcid 3666). ACE's IsGenerator is "has a
-- profile", so WorldObject.ActivateLinks took the plain-parent branch and created every
-- landblock_instance_link child at landblock load, ignoring GeneratorEvent (string 34) entirely
-- (server.log: "Linkable Spring Decorations Gen.SetLinkProperties(Spring Maypole) called for unknown
-- parent type: Generic"). With the row, AddGeneratorLinks turns each link into a profile and the
-- event gate (GeneratorTimeType 3, Event) decides: Off -> nothing spawns, On -> they appear, and
-- GeneratorEndDestructionType 2 removes them when the festival ends.
--
-- Idempotent: deletes its own rows first. gen_seasonal_costumes.py now emits the same rows.
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/seasonal-decor-gens-fix.sql A9B4 A1A4'
START TRANSACTION;
DELETE FROM weenie_properties_generator WHERE object_Id IN (900745, 900746, 900747);
INSERT INTO weenie_properties_generator (object_Id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z)
  SELECT g.id, probability, weenie_Class_Id, delay, init_Create, max_Create, when_Create, where_Create, stack_Size, palette_Id, shade, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z
  FROM weenie_properties_generator
  JOIN (SELECT 900745 AS id UNION ALL SELECT 900746 UNION ALL SELECT 900747) g
  WHERE object_Id = 73174;
COMMIT;
