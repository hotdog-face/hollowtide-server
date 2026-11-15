-- Undo ysanne-void-scrolls.sql: Riftwatcher Ysanne a plain Creature again (her Nether Rift lesson through NetherRift's own
-- Creature.ActOnUse hook), no shop. Scrolls already bought stay. Apply with apply-live-ovh.sh ... 3032. DML only.
DELETE FROM ace_world.weenie_properties_create_list WHERE object_Id = 901420 AND destination_Type = 4 AND weenie_Class_Id IN (43284, 43292);
DELETE FROM ace_world.weenie_properties_int WHERE object_Id = 901420 AND type IN (74, 75, 76, 126, 127);
DELETE FROM ace_world.weenie_properties_float WHERE object_Id = 901420 AND type IN (37, 38);
DELETE FROM ace_world.weenie_properties_bool WHERE object_Id = 901420 AND type IN (39, 52);
UPDATE ace_world.weenie SET type = 10 WHERE class_Id = 901420;   -- Creature
