-- MANSION FURNITURE, weenies 900500-900503 and the armor stand's figure 900510 (owner-approved
-- 2026-09-27: "useful pieces plus trophy displays"). docs/MANSION-FURNITURE.md.
-- New rows only, plus new SHOP rows on four vendors; nothing retail is changed. The vendors' rows as
-- they stood are in mansion-furniture-backup-2026-09-27.sql. Re-runnable (deletes its own rows first).
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/mansion-furniture.sql
--   (and ace_world_staging for the staging server), then in game: @clearcache weenie
--
-- THE FOUR PIECES are Hookers (WeenieType 64), the type of the retail portal devices, so ACE's hook
-- code redirects a use through the hook to them (Hook.CheckUseRequirements / ActOnUse) and checks
-- the house's permission (Hooker.CheckUseRequirements). What they DO is RevivalGuard
-- MansionFurniture.cs. Setup 0x02000B0E (the retail Plaque's, no collision volume, as the trophy
-- mounts use), so a hooked piece blocks nothing; the client draws expansion/models/w9005xx.glb,
-- found by NAME through expansion/models/names.json because a hooked item reaches it as the hook.
-- Properties are the portal device's (26588): ItemType 128, ItemUseable 32, PhysicsState 1044,
-- HookPlacement 103, Ethereal and Inscribable; HookType 2 (wall) or 1 (floor); no HookGroup, so no
-- per-house limit beyond the hook count. Icons 0x06FF0500-0x06FF0503 (expansion/textures).
--
-- THE FIGURE (900510) is spawned by the armor stand; nobody buys it (see its section below).
START TRANSACTION;

DELETE FROM weenie_properties_create_list WHERE object_Id IN (6826, 12241, 12242, 12243) AND weenie_Class_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_int           WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_int64         WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_bool          WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_float         WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_d_i_d         WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_i_i_d         WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_string        WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_attribute     WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_attribute_2nd WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_body_part     WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie_properties_skill         WHERE object_Id BETWEEN 900500 AND 900549;
DELETE FROM weenie WHERE class_Id BETWEEN 900500 AND 900549;

-- ---------------------------------------------------------------- the four pieces
INSERT INTO weenie (class_Id, class_Name, type) VALUES
  (900500, 'ace900500-weaponrack', 64),
  (900501, 'ace900501-armorstand', 64),
  (900502, 'ace900502-portalgemshelf', 64),
  (900503, 'ace900503-trophywall', 64);

-- int: ItemType 128, Mass 25, ValidLocations 0, ItemUseable 32, PhysicsState 1044, HookPlacement 103
INSERT INTO weenie_properties_int (object_Id, type, value)
  SELECT w.class_Id, t.type, t.value FROM weenie w JOIN (
    SELECT 1 AS type, 128 AS value UNION ALL SELECT 8, 25 UNION ALL SELECT 9, 0 UNION ALL
    SELECT 16, 32 UNION ALL SELECT 93, 1044 UNION ALL SELECT 150, 103) t
  WHERE w.class_Id BETWEEN 900500 AND 900503;
-- EncumbranceVal (5), Value (19), HookType (151: 2 wall, 1 floor). Priced like retail's best
-- furniture (the Arcane Pedestal and Dereth Map are 100,000).
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES
  (900500, 5,  800), (900500, 19, 50000), (900500, 151, 2),
  (900501, 5, 1500), (900501, 19, 75000), (900501, 151, 1),
  (900502, 5,  700), (900502, 19, 60000), (900502, 151, 2),
  (900503, 5, 1000), (900503, 19, 50000), (900503, 151, 2);

-- bool: Ethereal (13), Inscribable (22), as the portal device
INSERT INTO weenie_properties_bool (object_Id, type, value)
  SELECT w.class_Id, t.type, 1 FROM weenie w JOIN (SELECT 13 AS type UNION ALL SELECT 22) t
  WHERE w.class_Id BETWEEN 900500 AND 900503;

-- float: UseRadius 3
INSERT INTO weenie_properties_float (object_Id, type, value)
  SELECT class_Id, 54, 3 FROM weenie WHERE class_Id BETWEEN 900500 AND 900503;

-- d_i_d: Setup 0x02000B0E, SoundTable 0x20000014, PhysicsEffectTable 0x3400002B, Icon 0x06FF050n
INSERT INTO weenie_properties_d_i_d (object_Id, type, value)
  SELECT w.class_Id, t.type, t.value FROM weenie w JOIN (
    SELECT 1 AS type, 33557262 AS value UNION ALL SELECT 3, 536870932 UNION ALL SELECT 22, 872415275) t
  WHERE w.class_Id BETWEEN 900500 AND 900503;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES
  (900500, 8, 117376256), (900501, 8, 117376257), (900502, 8, 117376258), (900503, 8, 117376259);

INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (900500, 1, 'Weapon Rack'),
  (900501, 1, 'Armor Stand'),
  (900502, 1, 'Portal Gem Shelf'),
  (900503, 1, 'Trophy Wall'),
  (900500, 14, 'Hang it on a wall hook in your house. Then drag a weapon, bow, wand or shield from your pack onto it.'),
  (900501, 14, 'Stand it on a floor hook in your house. Then drag armor or clothing from your pack onto it.'),
  (900502, 14, 'Hang it on a wall hook in your house. Then drag portal gems from your pack onto it.'),
  (900503, 14, 'Hang it on a wall hook in your house. Then drag trophies from your pack onto it.'),
  (900500, 16, 'A plank rack with brass pegs for four weapons. Hung in a house, it shows the weapons put on it, each one as it really is: visitors can appraise them. Double-click one to take it down again.'),
  (900501, 16, 'A round plinth with a wooden figure. Stood on a floor hook, the figure wears the armor and clothing put on it, up to a whole suit, and anyone may appraise it to see what it wears.'),
  (900502, 16, 'Two shelves for portal gems. Hung in a house, it holds eight stacks, and anyone welcome in the house can double-click a gem to use it. Stock it for your guests and your allegiance.'),
  (900503, 16, 'A framed velvet board for four trophies: mounted heads, fish, idols and other wall hangings, shown two by two on a single hook.');

-- ---------------------------------------------------------------- the figure
-- A Generic object on the human setup with NO motion table, so it stands still in the rest pose
-- (the statue table 0x090000F3 it first had is the full human table and played the breathing idle;
-- armor-stand-figure-still-2026-09-30.sql). An
-- object and not a creature: a statue CREATURE was tried first and ACE's physics walked it a metre
-- off the plinth, then stopped sending it after a teleport. The mod dresses it (FigureDress).
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900510, 'ace900510-displayfigure', 1);
-- int: ItemType 128, Encumbrance 0, Mass 0, ItemUseable 32, Value 0, PhysicsState 20 (Ethereal and
-- IgnoreCollisions, no Gravity: with gravity ACE set the figure down on the floor, inside the plinth)
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES
  (900510, 1, 128), (900510, 5, 0), (900510, 8, 0), (900510, 16, 32), (900510, 19, 0), (900510, 93, 20);
INSERT INTO weenie_properties_bool (object_Id, type, value) VALUES (900510, 1, 1), (900510, 13, 1);   -- Stuck, Ethereal
-- d_i_d: Setup 0x02000001 (NO MotionTable: see armor-stand-figure-still-2026-09-30.sql), SoundTable 0x2000008C, PaletteBase 0x0400007E,
-- Icon 0x06FF0501 (the stand's), PhysicsEffectTable 0x34000075 (all as the statue)
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES
  (900510, 1, 33554433), (900510, 3, 536871052), (900510, 6, 67108990),
  (900510, 8, 117376257), (900510, 22, 872415349);
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (900510, 1, 'Wooden Figure'),
  (900510, 16, 'A wooden figure on an armor stand, waiting to be dressed.');

-- ---------------------------------------------------------------- where to get them
-- Jeeves (6826) in Hotel Swank for the owner, and the three retail furniture sellers every player
-- can reach: Steiner's (12241, 0xBA9E), Jordan's (12242, 0x7F8F) and Jubei's (12243, 0xE64E)
-- Apprentice Craftsman, who already sell the Bed, Book Shelf, Pedestal and the rest.
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
  SELECT v.id, 4, f.id, -1, 0, 0, 0 FROM
    (SELECT 6826 AS id UNION ALL SELECT 12241 UNION ALL SELECT 12242 UNION ALL SELECT 12243) v
    JOIN (SELECT 900500 AS id UNION ALL SELECT 900501 UNION ALL SELECT 900502 UNION ALL SELECT 900503) f;

COMMIT;
