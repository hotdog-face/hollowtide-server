-- THE SHADOW SHRINES, weenies 900900-900902 (docs/PK-EXPANSION.md, idea 1; RevivalGuard PkShrines).
-- MADE, NOT ADDED: this has been applied only to the private staging copy (ace_world_pk, 2026-09-28).
-- Do not apply it to ace_world until the owner says so. New rows only: nothing retail is changed, so
-- there is nothing to back up. Re-runnable (deletes its own rows first).
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/pk-shrines.sql
--   then in game: @clearcache weenie ; @reloadblock 56E5 ; @reloadblock B046 ; @reloadblock 1940
--
-- Without `pk_shrines_enabled` true the shrines are only scenery: a Generic with no emotes, whose use
-- does nothing (ACE's own handling). With it on, RevivalGuard PkShrines takes the use.
--
-- THE LOOK: the Altar of Bael'Zharon (854, the retail PK altar, setup 0x02000459), re-used from the
-- DATs. Cloned as a Generic (type 1) instead of a PKModifier (27), and without PropertyInt 99
-- (PkLevelModifier), so using one never changes anyone's PK status. Its bool, float and data-id rows
-- (Stuck, UseRadius 5, setup, motion and sound tables, icon) are copied from 854 as they are.
--
-- WHERE (all measured with a bot on the staging copy, 2026-09-28: open ground, 6 m beside where
-- @tele lands, z the ground height the client settled on). Far from every town (10+ map units):
--   900900 Shadow Shrine of the Empyrean Ruin  81.4N 32.9W  0x56E50012 (66, 36, 50)   north
--   900901 Shadow Shrine of Zombie Castle      45.2S 39.1E  0xB0460018 (66, 180, 26)  south-east
--   900902 Shadow Shrine of the Tumerok Fort   50.7S 81.8W  0x19400009 (42, 12, 80)   south-west
-- The names and places must match PkShrines.SHRINES in server-mods/RevivalGuard/PkShrines.cs.
START TRANSACTION;

DELETE FROM landblock_instance WHERE weenie_Class_Id BETWEEN 900900 AND 900902;
DELETE FROM weenie_properties_int    WHERE object_Id BETWEEN 900900 AND 900902;
DELETE FROM weenie_properties_bool   WHERE object_Id BETWEEN 900900 AND 900902;
DELETE FROM weenie_properties_float  WHERE object_Id BETWEEN 900900 AND 900902;
DELETE FROM weenie_properties_d_i_d  WHERE object_Id BETWEEN 900900 AND 900902;
DELETE FROM weenie_properties_string WHERE object_Id BETWEEN 900900 AND 900902;
DELETE FROM weenie WHERE class_Id BETWEEN 900900 AND 900902;

INSERT INTO weenie (class_Id, class_Name, type) VALUES
  (900900, 'acshadowshrineempyrean', 1),
  (900901, 'acshadowshrinezombie', 1),
  (900902, 'acshadowshrinetumerok', 1);

-- int: ItemType 128 (Misc), Encumbrance 50, Mass 25, ItemUseable 32 (Remote), Value 0, PhysicsState 1040
INSERT INTO weenie_properties_int (object_Id, type, value)
  SELECT w.class_Id, t.type, t.value FROM weenie w JOIN (
    SELECT 1 AS type, 128 AS value UNION ALL SELECT 5, 50 UNION ALL SELECT 8, 25 UNION ALL
    SELECT 16, 32 UNION ALL SELECT 19, 0 UNION ALL SELECT 93, 1040) t
  WHERE w.class_Id BETWEEN 900900 AND 900902;

INSERT INTO weenie_properties_bool (object_Id, type, value)
  SELECT w.class_Id, b.type, b.value FROM weenie w JOIN weenie_properties_bool b ON b.object_Id = 854
  WHERE w.class_Id BETWEEN 900900 AND 900902;

INSERT INTO weenie_properties_float (object_Id, type, value)
  SELECT w.class_Id, f.type, f.value FROM weenie w JOIN weenie_properties_float f ON f.object_Id = 854
  WHERE w.class_Id BETWEEN 900900 AND 900902;

INSERT INTO weenie_properties_d_i_d (object_Id, type, value)
  SELECT w.class_Id, d.type, d.value FROM weenie w JOIN weenie_properties_d_i_d d ON d.object_Id = 854
  WHERE w.class_Id BETWEEN 900900 AND 900902;

INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (900900, 1, 'Shadow Shrine of the Empyrean Ruin'),
  (900901, 1, 'Shadow Shrine of Zombie Castle'),
  (900902, 1, 'Shadow Shrine of the Tumerok Fort'),
  (900900, 16, 'An altar raised in Bael''Zharon''s name among the Empyrean stones. A player killer who holds it with no enemy near binds it to their allegiance, and the Hopeslayer''s favour follows them into every hunt.'),
  (900901, 16, 'An altar raised in Bael''Zharon''s name under the walls of Zombie Castle. A player killer who holds it with no enemy near binds it to their allegiance, and the Hopeslayer''s favour follows them into every hunt.'),
  (900902, 16, 'An altar raised in Bael''Zharon''s name before the Tumerok fort. A player killer who holds it with no enemy near binds it to their allegiance, and the Hopeslayer''s favour follows them into every hunt.');

-- One of each in the world. Guids 0x7LLLL001: none of the three landblocks had an instance before.
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
  (0x756E5001, 900900, 0x56E50012, 66, 36, 50, 1, 0, 0, 0, 0),
  (0x7B046001, 900901, 0xB0460018, 66, 180, 26, 1, 0, 0, 0, 0),
  (0x71940001, 900902, 0x19400009, 42, 12, 80, 1, 0, 0, 0, 0);

COMMIT;
