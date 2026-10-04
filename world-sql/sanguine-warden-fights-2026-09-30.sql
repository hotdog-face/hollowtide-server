-- THE SANGUINE WARDEN FIGHTS BACK (launch sweep #6, docs/SWEEP-BOTHALVES-2026-09-30.md).
--
-- 901301 was cloned from the Platinum Golem (7097) and then given the temple Guardian statue's setup
-- and motion table (0x02001055 / 0x090000CB, tools/gpubox-ace/lore-factions.sql). On the server that
-- pair does not fight: @create'd beside a fleet bot and watched 25 s at 5 m and 20 s at 1.5 m, the
-- Warden took HandCombat stance and then never moved, swung or cast (no "You resist the spell
-- cast by" line, no attack motion, vitals unchanged), while the Platinum Golem it was cloned from,
-- watched the same way, cast at once. Retail's own Guardian of Fury (26550, the same setup and
-- table) never even takes a stance. So the Warden could be killed from anywhere without answering.
--
-- The server half goes back to the Platinum Golem's own setup and motion table (0x020007CA /
-- 0x09000081), with the golem's combat table 0x30000008 and sound table 0x2000009A (both already
-- the golem's). The client draws the Warden from its sculpted glb by weenie (AcGlbCreature,
-- ignoreServerScale), so only collision and the motions the server sends change; the manifest maps
-- the golem table's extra attacks (AttackHigh2, AttackMed2, AttackMed3) onto Slam and Sweep.
--
-- Apply: tools/gpubox-ace/apply-live.sh tools/gpubox-ace/sanguine-warden-fights-2026-09-30.sql 0E49
-- Undo: type 1 back to 33558613 (0x02001055), type 2 back to 150995147 (0x090000CB).
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 901301 AND type IN (1, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (901301, 1, 33556426), (901301, 2, 150995073);
