-- THE NIGHT CLUB OPENS EVERY NOVEMBER (owner-approved 2026-09-28, "Yes fix night club").
-- Backup: nightclub-backup-2026-09-28.sql. Apply: ssh gpubox sudo -n mysql ace_world < nightclub-calendar.sql,
-- then clearcache; the new event row loads at the next restart (EventManager reads ace_world.event once).
--
-- ace69997-nightclubattendantgenerator (placed 4 times: 0x33D9, 0x8090, 0xBC9F, 0xE74E) spawned the Night
-- Club Attendants on its own RealTime window, GeneratorTimeType 1 with Start/End 1793595660/1796187540:
-- 2026-11-02 00:01 to 2026-12-01 23:59 at UTC-5, THIS YEAR ONLY. It becomes an Event generator (142 = 3)
-- gated on a new row, AnniversaryNightClub, which RevivalGuard SeasonalCalendar turns on Nov 2 - Dec 1 every
-- year, the same shape as every other festival switch. Re-runnable.
START TRANSACTION;
INSERT INTO event (name, start_Time, end_Time, state)
  SELECT 'AnniversaryNightClub', -1, -1, 3 FROM DUAL
  WHERE NOT EXISTS (SELECT 1 FROM event WHERE name = 'AnniversaryNightClub');
UPDATE weenie_properties_int SET value = 3 WHERE object_Id = 69997 AND type = 142;           -- GeneratorTimeType Event
DELETE FROM weenie_properties_int WHERE object_Id = 69997 AND type IN (143, 144);            -- the 2026-only window
DELETE FROM weenie_properties_string WHERE object_Id = 69997 AND type = 34;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (69997, 34, 'AnniversaryNightClub');  -- GeneratorEvent
COMMIT;
