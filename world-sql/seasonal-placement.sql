-- SEASONAL COSTUMERS AND TOWN DECORATIONS, PLACED (2026-09-28, integration round; docs/SEASONAL-UPGRADE.md).
-- Needs seasonal-costumes.sql first (the weenies). Every generator here is gated on its season's own event
-- row, so nothing appears out of season; Festival Season is forced on at the moment, so its pieces spawn now.
-- Backup: none needed, these guids were measured empty (0x7A9B4F00-FF, 0x7A1A4F00-FF, 0x7BC9FF00-FF) and no
-- landblock_instance row names a 9007xx weenie; to undo, delete the guids below and their links.
-- Heights are where a bot (++Sysprobe) landed on each spot with @teleloc and read WHERE. Re-runnable.
START TRANSACTION;
-- Holtburg's Festival Season pieces (0x7A9B4F02/05, 0x7A9B4F10/11, 0x7A9B4F20-9F) belong to holtburg-festival.sql
-- (gen_holtburg_festival.py, 2026-10-02), so this file names its own Holtburg guids instead of the whole range.
DELETE FROM landblock_instance_link WHERE parent_GUID IN (0x7A9B4F00, 0x7A9B4F01, 0x7A9B4F03, 0x7A9B4F04, 0x7A1A4F00, 0x7A1A4F01, 0x7BC9FF00);
DELETE FROM landblock_instance WHERE guid IN (0x7A9B4F00, 0x7A9B4F01, 0x7A9B4F03, 0x7A9B4F04, 0x7A9B4F12, 0x7A9B4F13) OR guid BETWEEN 0x7A1A4F00 AND 0x7A1A4FFF OR guid BETWEEN 0x7BC9FF00 AND 0x7BC9FFFF;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
-- Holtburg (0xA9B4), round the lifestone square
  (0x7A9B4F00, 900736, 0xA9B40019, 88.0, 14.0, 94.0, 1, 0, 0, 0, 0),     -- Frostfell Costumer Generator (hiddenpresentsevent)
  (0x7A9B4F01, 900737, 0xA9B40019, 74.0, 16.0, 94.0, 1, 0, 0, 0, 0),     -- Spring Costumer Generator (springbunnyevent)
  -- (the Harvest Decorations Gen and its lantern posts moved to holtburg-festival.sql, 2026-10-02)
  (0x7A9B4F03, 900746, 0xA9B40019, 84.0, 22.0, 94.0, 1, 0, 0, 0, 0),     -- Frostfell Decorations Gen (hiddenpresentsevent)
  (0x7A9B4F12, 900741, 0xA9B4001A, 81.0, 28.0, 94.0, 1, 0, 0, 0, 1),     --   Frostfell Garland Arch
  (0x7A9B4F04, 900747, 0xA9B40019, 86.0, 22.0, 94.0, 1, 0, 0, 0, 0),     -- Spring Decorations Gen (springbunnyevent)
  (0x7A9B4F13, 900742, 0xA9B40021, 98.0, 20.0, 94.0, 1, 0, 0, 0, 1),     --   Spring Maypole
-- Glenden Wood (0xA1A4), beside the retail Festival Vendor (its generator 0x7A1A402B at 118.9, 139.1)
  (0x7A1A4F00, 900735, 0xA1A40026, 112.0, 134.0, 50.0, 1, 0, 0, 0, 0),   -- Festival Costumer Generator (EventFallFestival)
  (0x7A1A4F01, 900745, 0xA1A40026, 108.0, 138.0, 50.0, 1, 0, 0, 0, 0),   -- Harvest Decorations Gen
  (0x7A1A4F10, 900740, 0xA1A40026, 104.0, 140.0, 50.0, 1, 0, 0, 0, 1),   --   Harvest Lantern Post
-- Cragstone (0xBC9F), beside the Night Club Attendant's generator (0x7BC9F035 at 180, 84)
  (0x7BC9FF00, 900738, 0xBC9F003C, 175.613, 80.223, 32.005, 1, 0, 0, 0, 0);  -- Anniversary Costumer Generator (Fireworks)
INSERT INTO landblock_instance_link (parent_GUID, child_GUID) VALUES
  (0x7A9B4F03, 0x7A9B4F12), (0x7A9B4F04, 0x7A9B4F13),
  (0x7A1A4F01, 0x7A1A4F10);
COMMIT;
-- then: @clearcache landblock, and @reloadblock A9B4 / A1A4 / BC9F (refused while anyone is in the block).
