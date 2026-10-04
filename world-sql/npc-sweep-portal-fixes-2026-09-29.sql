-- LAUNCH SWEEP #2 portal spot-checks (docs/SWEEP-NPC-2026-09-29.md section 4): the six retail portal
-- destinations the data sweep found under the ground or in no cell (docs/SWEEP-DATA-2026-09-29.md 1.5).
-- Each was re-checked with ACE's own physics (tools/sweepdata SweepData portals: Landblock.GetZ outdoors,
-- EnvCell.point_in_cell indoors) and walked with a fleet bot before and after.
--
--   4911  portalmattekarcaveexit (Mattekar cave, 0x0169) -> 0x94D10012 (63, 43.4) at z 21.6; the ground
--         there is 216.00 to the centimetre, so 21.6 is a dropped digit. z 216.01.
--   8886  portalshadestrongholdescapelower (Shade Stronghold lower, 0x02B2) -> 0x02B10138 (220, -150) at
--         z -240: in no EnvCell (216 m below its cell). Its twin 8885 (upper escape) lands at z -24 in
--         the neighbouring cell 0x02B10135, and z -24 is inside 0x02B10138. z -24.
--   6115  portalmountainfortressexit (0x011C) -> 0x98180001 (69.9, -95.2, 340.1): y is negative on an
--         outdoor cell, so ACE resolved it into 0x98170015, 50 m under. With y +95.2 the given z 340.1
--         is 0.03 m above the ground, beside the entrance portal 6114 (52.1, 87, 340.76). The cell for
--         (69.9, 95.2) is 0x98180014.
--   87641 ace87641-surface (Tanada, 0x007B; ACE-authored) -> 0xB82A0013 (60.54, 57.13) at z 0; the
--         ground is 310.19, beside its own entrance 87640 (66.1, 57, 309.68). z 310.20.
--   11441 portalpalenqualexit-xp (0x028E) -> 0x24BC003C (173.2, 94.7) at z 22.1, ground 40.91, and
--   1096  portalshoushigrottoexit (0x01F7) -> 0xDA54002A (135.1, 42.7) at z 34, ground 42.37. No retail
--         source gives another landing, so each gets its ground: z 40.92 and 42.38.
--
-- The bot (devbot3, before this file): 8886 used for real left the player at z -240 in no cell, in the
-- void under the dungeon, until a teleport. 6115 held portal space for 60-90 s and then dropped the player
-- on a mountain top in 0x9817, 190 m from the entrance. 4911, 1096 and 11441 held portal space for 20-60 s
-- before the client settled onto the ground; 87641 settled at once. Nobody should wait a minute in portal
-- space or land in the wrong landblock, so all six are fixed.
--
-- Weenie rows only; placed portals copy Destination when their landblock loads. After syncing:
--   ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/npc-sweep-portal-fixes-2026-09-29.sql 0169 02B2 011C 007B 01F7 028E'
-- (clears the weenie cache and reloads the landblocks the portals stand in). Re-runnable.
UPDATE weenie_properties_position SET origin_Z = 216.01
 WHERE object_Id = 4911 AND position_Type = 2 AND obj_Cell_Id = 0x94D10012;
UPDATE weenie_properties_position SET origin_Z = -24
 WHERE object_Id = 8886 AND position_Type = 2 AND obj_Cell_Id = 0x02B10138;
UPDATE weenie_properties_position SET obj_Cell_Id = 0x98180014, origin_Y = 95.2
 WHERE object_Id = 6115 AND position_Type = 2 AND obj_Cell_Id = 0x98180001;
UPDATE weenie_properties_position SET origin_Z = 310.20
 WHERE object_Id = 87641 AND position_Type = 2 AND obj_Cell_Id = 0xB82A0013;
UPDATE weenie_properties_position SET origin_Z = 40.92
 WHERE object_Id = 11441 AND position_Type = 2 AND obj_Cell_Id = 0x24BC003C;
UPDATE weenie_properties_position SET origin_Z = 42.38
 WHERE object_Id = 1096 AND position_Type = 2 AND obj_Cell_Id = 0xDA54002A;
