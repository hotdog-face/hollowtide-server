-- HOTEL SWANK PORTAL ROOM: two dungeon slots now lead to their new content (2026-09-28).
-- The slots already pointed into these very dungeons as empty "unused DAT dungeons"
-- (swank-portal-room-2026-09-27.sql); the dungeons are now the Empyrean Vault and Bael'Zharon's lair,
-- so the same portals are renamed and their drops moved to each dungeon's proper arrival point.
-- Backup of the rows this replaces: swank-vault-lair-portals-backup-2026-09-28.sql.
-- Apply AFTER swank-portal-room-2026-09-27.sql (re-running that file puts the old slots back).
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/swank-vault-and-lair-portals-2026-09-28.sql
--   then in game: @clearcache weenie ; @reloadblock 018A   (AGENT-BRIEF trap 7)
--
-- 900137 (slot 0x7018A159, floor -12): Messenger's Sanctuary 0x00AE -> the Empyrean Vault. Its old drop,
--   0x00AE079B (130, -60, 0), is inside the Forge-Warden's room and would skip the whole vault; the new
--   one is the vault's own drop-in, the north-west tile of the first hall (empyrean-vault.sql, ENTRY).
-- 900142 (slot 0x7018A160, floor -12): the Shadow Breach 0x008D -> Bael'Zharon's lair, the arrival point
--   of the Arwic crater portal (new-content-baelzharon-golems.sql 900203, commit 158eea1b).
START TRANSACTION;
DELETE FROM weenie_properties_string WHERE object_Id IN (900137, 900142) AND type IN (1, 16);
DELETE FROM weenie_properties_position WHERE object_Id IN (900137, 900142) AND position_Type = 2;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES
  (900137, 1, 'Portal to the Empyrean Vault'),
  (900137, 16, 'The Empyrean Vault (dungeon 0x00AE): three halls of Aetherium golems, core-keyed seals and the Forge-Warden of Ispar. Season beat 4. Hotel Swank portal room, Bosses.'),
  (900142, 1, 'Portal to Bael''Zharon''s Lair'),
  (900142, 16, 'The Shadow Breach (dungeon 0x008D), where Bael''Zharon, the Hopeslayer, waits as a world boss. Season beat 5. Hotel Swank portal room, Bosses.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES
  (900137, 2, 0x00AE0590, 80, -10, -5.995, 0.70710678, 0, 0, -0.70710678),
  (900142, 2, 0x008D018E, 20, -20, -30, 0.382683, 0, 0, -0.92388);
COMMIT;
