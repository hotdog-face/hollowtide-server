-- WORLD BOSS SCHEDULE (2026-09-30, docs/WORLD-BOSS-SYSTEM.md). Bael'Zharon's lair generator (900202)
-- stops respawning him by itself: its profile Delay goes from 86,400 s (24 h) to 604,800 s (7 days).
-- RevivalGuard WorldBoss now decides when he rises: on the schedule (rg_world_boss_schedule on) or
-- 24 h after his last death (off, the old rule, which the mod now enforces itself). Without this,
-- nothing double-spawns (the profile's MaxCreate is 1), but a tick of the mod that failed would hand
-- the respawn back to ACE's 24 h; with it, only the mod raises him.
--
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/world-boss-schedule.sql'
-- (no landblock reload: the running generator keeps its old in-memory Delay until the next restart,
-- which is the RevivalGuard deploy). Undo: the same UPDATE with 86400.
-- The same value is in new_content_baelzharon_golems.py and new-content-baelzharon-golems.sql.
UPDATE weenie_properties_generator SET delay = 604800 WHERE object_Id = 900202 AND weenie_Class_Id = 900200;
SELECT object_Id, weenie_Class_Id, delay FROM weenie_properties_generator WHERE object_Id = 900202;
