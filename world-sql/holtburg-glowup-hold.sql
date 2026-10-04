-- HOLTBURG GLOW-UP ON HOLD (owner 2026-09-29: "hold off on those until we build a world editor tool").
--
-- Takes the two server placements holtburg-glowup.sql made (0x7A9B4E01 Foresters' Memorial,
-- 0x7A9B4E02 Holtburg Notice Board) out of the live world. The weenies 901100/901101 stay, so
-- re-running holtburg-glowup.sql later puts both back exactly where they were.
--
-- The client-only props (stalls, banners, cart, garden, lamps) are not server rows: AcTownDressing
-- draws them from expansion/holtburg/placements.json, and the client session turned that loader off
-- by default on 2026-09-29 (new pref key, Config row removed); placements.json stays for the editor.
--
-- NOT part of the glow-up and left alone: the seasonal costumers and decorations round the Holtburg
-- lifestone (0x7A9B4F00-FF, seasonal-placement.sql, docs/SEASONAL-UPGRADE.md).
--
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/holtburg-glowup-hold.sql A9B4'
START TRANSACTION;
DELETE FROM landblock_instance_link WHERE parent_GUID IN (0x7A9B4E01, 0x7A9B4E02) OR child_GUID IN (0x7A9B4E01, 0x7A9B4E02);
DELETE FROM landblock_instance WHERE guid IN (0x7A9B4E01, 0x7A9B4E02) OR weenie_Class_Id IN (901100, 901101);
COMMIT;
