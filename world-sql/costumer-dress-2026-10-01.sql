-- COSTUMERS WEAR A BODY PIECE (2026-10-01). Each seasonal costumer (900730-900733, clones of the
-- Festival Vendor 70263 without its create list) wore only a head item and a cloak, so ACE sent an
-- ObjDesc with no body coverage and the NPC stood in its underwear. Retail's Festival Vendor wears a
-- full costume (22018 Mummy Costume + 25554 Knath mask). Same change is in gen_seasonal_costumes.py.
--   900730 Festival Costumer  + 70269 Festival Robe (Black)
--   900731 Frostfell Costumer + 32187 Festival Robe
--   900732 Spring Costumer    + 32187 Festival Robe
--   900733 Anniversary Costumer + 127 Pants, 132 Shoes (under its Anniversary Shirt)
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/costumer-dress-2026-10-01.sql A1A4 A9B4'
-- Idempotent. Takes effect when the costumer next spawns (the reload respawns the placed ones).
DELETE FROM weenie_properties_create_list WHERE destination_Type = 2 AND
  ((object_Id = 900730 AND weenie_Class_Id = 70269) OR (object_Id = 900731 AND weenie_Class_Id = 32187)
   OR (object_Id = 900732 AND weenie_Class_Id = 32187) OR (object_Id = 900733 AND weenie_Class_Id IN (127, 132)));
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (900730, 2, 70269, 1, 0, 0, 0),
  (900731, 2, 32187, 1, 0, 0, 0),
  (900732, 2, 32187, 1, 0, 0, 0),
  (900733, 2, 127, 1, 0, 0, 0),
  (900733, 2, 132, 1, 0, 0, 0);
