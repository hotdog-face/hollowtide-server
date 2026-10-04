-- THE FESTIVAL STONES AND THE MAD COWS COME BACK EVERY YEAR (owner-approved 2026-09-28, "Yes fix those too").
-- Backup: stones-madcow-backup-2026-09-28.sql. Apply: ssh gpubox sudo -n mysql ace_world < stones-madcow-calendar.sql,
-- then clearcache; the new event rows load at the next restart (EventManager reads ace_world.event once).
-- Same fix as nightclub-calendar.sql: each carried its own RealTime window (GeneratorTimeType 142 = 1 with
-- 143/144 start/end) for ONE year, and becomes an Event generator (142 = 3) gated on a new row that
-- RevivalGuard SeasonalCalendar turns on every year. Re-runnable.
--   Festival Stones 5376-5395 (placed once each; "Rejoice! The Hopeslayer has been defeated"):
--     1793595660-1793941140 = 2026-11-02 00:01 to 11-05 23:59 at UTC-5 -> row AnniversaryFestivalStones,
--     on with the anniversary (calendar key 'anniversary', Nov 2-5).
--   April 2003 Raining Mad Cows Gen 23631 (placed 22 times, 10 cowmad profiles):
--     1775019660-1775105940 = 2026-04-01 00:01 to 23:59 at UTC-5 -> row AprilFoolsMadCows,
--     calendar key 'aprilfools', Apr 1 (the wiki: "Invaded towns on April 1st").
START TRANSACTION;
INSERT INTO event (name, start_Time, end_Time, state)
  SELECT 'AnniversaryFestivalStones', -1, -1, 3 FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM event WHERE name = 'AnniversaryFestivalStones');
INSERT INTO event (name, start_Time, end_Time, state)
  SELECT 'AprilFoolsMadCows', -1, -1, 3 FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM event WHERE name = 'AprilFoolsMadCows');

UPDATE weenie_properties_int SET value = 3 WHERE type = 142 AND (object_Id BETWEEN 5376 AND 5395 OR object_Id = 23631);
DELETE FROM weenie_properties_int WHERE type IN (143, 144) AND (object_Id BETWEEN 5376 AND 5395 OR object_Id = 23631);
DELETE FROM weenie_properties_string WHERE type = 34 AND (object_Id BETWEEN 5376 AND 5395 OR object_Id = 23631);
INSERT INTO weenie_properties_string (object_Id, type, value)
  SELECT class_Id, 34, 'AnniversaryFestivalStones' FROM weenie WHERE class_Id BETWEEN 5376 AND 5395;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (23631, 34, 'AprilFoolsMadCows');
COMMIT;
