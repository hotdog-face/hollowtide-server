-- Zefir Charms (900760-900768) for sale at Dev Vault Curios (901604) in Hotel Swank (owner 2026-10-20:
-- "the zefir pet gem items - please add them to one of the vendors in the hotel swank. im not finding them").
-- DML only, idempotent: shop rows (destination_Type 2, as the vendor's other rows).
DELETE FROM ace_world.weenie_properties_create_list WHERE object_Id = 901604 AND weenie_Class_Id BETWEEN 900760 AND 900768;
INSERT INTO ace_world.weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
SELECT 901604, 2, class_Id, 0, 0, 0, 0 FROM ace_world.weenie WHERE class_Id BETWEEN 900760 AND 900768;
