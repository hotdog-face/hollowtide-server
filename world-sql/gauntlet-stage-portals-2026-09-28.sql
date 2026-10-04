-- THE GAUNTLET: three stage portals brought into line with their siblings (2026-09-28).
-- Found by diffing the six arena chains (CH/EW/RB x Arena One/Two) under each master generator;
-- the only differences were these, and in each case the other two societies agree with each other.
-- Walk: docs/QUESTS-HUBS.md "The Gauntlet". Backup: gauntlet-backup-2026-09-28.sql.
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/gauntlet-stage-portals-2026-09-28.sql
--   then in game: @clearcache   (objects already spawned keep their old emotes until respawn)
--
-- 52611 CH Stage 4 exit portal (made by CH Stage3 Exit Controller 87967): had no emotes, so stage 3
--   ended silently in the Celestial Hand (seen on the walk) while every other CH stage prints the
--   officer's line. EW 88002 and RB 52906 carry Portal: erase the Stage 5 room flags, and
--   Generation: "<Society> Officer says, Continue on to the next stage, warriors."
-- 87946 CH Stage 4 (the master generator's stage object): EW 88014 and RB 52919 erase the Stage 5
--   room flags on use; CH did not.
-- 53380 RB Stage 6 exit portal, room a: EW 88023 and CH 87975 erase the Stage 5 room flags on use;
--   RB's room-a portal only broadcast (its room-b twin 52908 does both).
-- Without the erase, a GauntletStage5a/5b_Flag left over from an unfinished run sends the player to
--   the same Stage 5 room next time instead of being assigned one (Gauntlet Stage 5, 87954 et al.).
START TRANSACTION;
DELETE a FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
  WHERE e.object_Id IN (52611, 87946) OR (e.object_Id = 53380 AND e.category = 4);
DELETE FROM weenie_properties_emote WHERE object_Id IN (52611, 87946) OR (object_Id = 53380 AND category = 4);

-- category 4 = Portal (EraseQuest x2), category 9 = Generation (LocalBroadcast), as 88002 / 52908
INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (52611, 4, 1);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES
  (@e, 0, 31, 0, 1, 'GauntletStage5a_Flag'),
  (@e, 1, 31, 0, 1, 'GauntletStage5b_Flag');
INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (52611, 9, 1);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES
  (@e, 0, 17, 0, 1, 'Celestial Hand Officer says, "Continue on to the next stage, warriors."');

INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (87946, 4, 1);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES
  (@e, 0, 31, 0, 1, 'GauntletStage5a_Flag'),
  (@e, 1, 31, 0, 1, 'GauntletStage5b_Flag');

INSERT INTO weenie_properties_emote (object_Id, category, probability) VALUES (53380, 4, 1);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES
  (@e, 0, 31, 0, 1, 'GauntletStage5a_Flag'),
  (@e, 1, 31, 0, 1, 'GauntletStage5b_Flag');
COMMIT;
