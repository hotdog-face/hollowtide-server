-- LAUNCH DATA SWEEP, PORTALS (docs/SWEEP-DATA-2026-09-29.md section 1): the two OUR portals whose
-- destination is under the terrain. Found by tools/sweepdata (SweepData portals), which checks every
-- destination against ACE's own physics: Landblock.GetZ for the ground, EnvCell.point_in_cell indoors.
--
--   900110 [Tour] Portal to Candeth Keep (Hotel Swank, 0x018A). swank-portal-room-2026-09-27.sql copied
--          retail portalcandethkeep's (24579) destination, 0x2B120029 (120.64, 1.55) at z 10.11, and the
--          ground there is 48.00: 37.9 m underground. Retail's own spell 4214 "Return to the Keep" names
--          the same cell and x,y at z 48.01, so that z is used. (24579 itself is placed nowhere.)
--   900204 Arwic (Bael'Zharon's lair -> the Shadow Breach crater, 0x008D). new-content-baelzharon-golems.sql
--          set 0xC6A9000F (42, 150, 32); the crater floor is z 32 only at the crater portal (36, 156), and
--          at (42, 150) the ground is 37.00: 5 m inside the crater wall. z 37.01 lands on it.
--
-- Weenie rows only; the placed portals copy Destination when their landblock loads, so after applying:
--   ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/sweepdata-portal-fixes-2026-09-29.sql 018A 008D'
-- (apply-live clears the weenie cache and reloads those two landblocks). Re-runnable.
UPDATE weenie_properties_position SET origin_Z = 48.01
 WHERE object_Id = 900110 AND position_Type = 2 AND obj_Cell_Id = 0x2B120029;
UPDATE weenie_properties_position SET origin_Z = 37.01
 WHERE object_Id = 900204 AND position_Type = 2 AND obj_Cell_Id = 0xC6A9000F;
