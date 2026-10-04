-- TROPHY MOUNTS, weenies 900060-900063 (owner-approved 2026-09-27). New rows only: nothing retail
-- is changed, so there is nothing to back up. Re-runnable (deletes its own rows first).
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/trophy-mounts.sql
--   then in game: @clearcache weenie ; @reloadblock 018A   (AGENT-BRIEF trap 7; the vendor rows
--   are weenie data, so the cache clear is what makes Jeeves stock them)
--
-- THE RETAIL SHAPE. Cloned from the Burun Idol (27525): a Generic, wall-hook item that came off a
-- corpse with a LongDesc ("A strange idol, taken from the corpse of a Burun Ruuk raider."). Same
-- ItemType (Misc 128), HookType wall (2) and PhysicsState 1044, so ACE's hooks and our
-- HookTypeGuard accept it exactly as they accept the idol. Changed from the idol:
--   * Setup 0x02000B0E, the retail Plaque's (weenie 11970, the Allegiance Hall Board, which already
--     hangs on mansion walls). Its volume is empty, so a trophy on a hook blocks nothing. The boss's
--     own setup was tried on paper and refused: Hook.OnAddItem copies the item's setup onto the
--     hook and makes it solid, so a Tusker's two-metre collision spheres would stand on the wall.
--   * HookPlacement 103 (Hook), as the Plaque and the Mounted Fish carry.
--   * Icon 0x06002925, the Plaque's. No ObjScale (the Plaque's 0.5 would halve the head too).
--   * Name, and a default LongDesc for the copies Jeeves sells; a KILL's trophy gets
--     "Slain by <name>." from RevivalGuard TrophyMounts instead.
-- The models are expansion/models/w900060..63.glb (tools/img/trophy_mounts.py), found through
-- expansion/models/names.json because a hooked item reaches the client as the HOOK, by name.
START TRANSACTION;

DELETE FROM weenie_properties_create_list WHERE object_Id = 6826 AND weenie_Class_Id BETWEEN 900060 AND 900063;
DELETE FROM weenie_properties_int    WHERE object_Id BETWEEN 900060 AND 900063;
DELETE FROM weenie_properties_bool   WHERE object_Id BETWEEN 900060 AND 900063;
DELETE FROM weenie_properties_float  WHERE object_Id BETWEEN 900060 AND 900063;
DELETE FROM weenie_properties_d_i_d  WHERE object_Id BETWEEN 900060 AND 900063;
DELETE FROM weenie_properties_string WHERE object_Id BETWEEN 900060 AND 900063;
DELETE FROM weenie WHERE class_Id BETWEEN 900060 AND 900063;

INSERT INTO weenie (class_Id, class_Name, type) VALUES
  (900060, 'actrophytuskerguard', 1),
  (900061, 'actrophylugianwarlord', 1),
  (900062, 'actrophyolthoiqueen', 1),
  (900063, 'actrophyoverlordsword', 1);

-- int: ItemType 128, Encumbrance 150, Mass 150, ValidLocations 0, ItemUseable 1 (No), Value 250,
-- PhysicsState 1044, HookPlacement 103, HookType 2 (wall)
INSERT INTO weenie_properties_int (object_Id, type, value)
  SELECT w.class_Id, t.type, t.value FROM weenie w JOIN (
    SELECT 1 AS type, 128 AS value UNION ALL SELECT 5, 150 UNION ALL SELECT 8, 150 UNION ALL
    SELECT 9, 0 UNION ALL SELECT 16, 1 UNION ALL SELECT 19, 250 UNION ALL SELECT 93, 1044 UNION ALL
    SELECT 150, 103 UNION ALL SELECT 151, 2) t
  WHERE w.class_Id BETWEEN 900060 AND 900063;

INSERT INTO weenie_properties_bool (object_Id, type, value)
  SELECT w.class_Id, b.type, b.value FROM weenie w JOIN weenie_properties_bool b ON b.object_Id = 27525
  WHERE w.class_Id BETWEEN 900060 AND 900063;

-- d_i_d: Setup 0x02000B0E, SoundTable 0x20000014, Icon 0x06002925, PhysicsEffectTable 0x3400002B
INSERT INTO weenie_properties_d_i_d (object_Id, type, value)
  SELECT w.class_Id, t.type, t.value FROM weenie w JOIN (
    SELECT 1 AS type, 33557262 AS value UNION ALL SELECT 3, 536870932 UNION ALL
    SELECT 8, 100673829 UNION ALL SELECT 22, 872415275) t
  WHERE w.class_Id BETWEEN 900060 AND 900063;

INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (900060, 1, 'Mounted Tusker Guard Head'),
  (900061, 1, 'Mounted Lugian Warlord Head'),
  (900062, 1, 'Mounted Olthoi Queen Head'),
  (900063, 1, 'Mounted Overlord''s Sword'),
  (900060, 16, 'The head of a Tusker Guard, mounted on a board. No hunter''s name has been cut into it yet.'),
  (900061, 16, 'The head of a Lugian Warlord, mounted on a board. No hunter''s name has been cut into it yet.'),
  (900062, 16, 'The head of an Olthoi Queen, mounted on a board. No hunter''s name has been cut into it yet.'),
  (900063, 16, 'A Tumerok Overlord''s sword, mounted on a board. No hunter''s name has been cut into it yet.'),
  (900060, 14, 'This item can be hung on a wall hook.'),
  (900061, 14, 'This item can be hung on a wall hook.'),
  (900062, 14, 'This item can be hung on a wall hook.'),
  (900063, 14, 'This item can be hung on a wall hook.');

-- Jeeves (6826, Hotel Swank 0x018A0225) sells all four, destination 4 = Shop, so the owner can hang
-- one without hunting a boss. Nothing else stocks or drops them: no loot table, no other vendor.
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (6826, 4, 900060, -1, 0, 0, 0),
  (6826, 4, 900061, -1, 0, 0, 0),
  (6826, 4, 900062, -1, 0, 0, 0),
  (6826, 4, 900063, -1, 0, 0, 0);

COMMIT;
