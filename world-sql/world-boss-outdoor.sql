-- WORLD BOSS OUTDOORS (owner, 2026-10-01: "the world boss needs to summon Bael in a outdoor location.
-- and to get there i want it to be harder. players have to journey to get there a bit. and show it on
-- the map but dont tell people the route"). docs/WORLD-BOSS-SYSTEM.md, "Where he rises".
--
-- Bael'Zharon no longer rises in the Shadow Breach (0x008D) behind a portal by Arwic. His lair
-- generator (900202) and a Landblock KeepAlive (ACE's 80007) stand on a Direlands summit instead:
-- 1.5S 60.0W, cell 0x347D001D (84, 108, 280), a flat top 147 m above its surroundings with one way
-- up, no spawns of its own, and at least 1.6 km of walking from any portal drop, lifestone, recall
-- or house (the analysis is in the doc). The Shadow Breach portal in the Arwic crater (900203,
-- guid 0x7C6A9BA0) is removed: nothing leads to him. The lair's own exit portal stays, so an admin
-- touring the empty lair can still leave. The broadcasts that named the Breach beneath Arwic now
-- name no place. RevivalGuard WorldBoss.cs carries the same site (Lair 0x347D, the map marker).
--
-- Apply (the RevivalGuard deploy's restart reloads everything; without one, reload these):
--   ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/world-boss-outdoor.sql 008D C6A9 347D'
-- Same rows as new_content_baelzharon_golems.py (regenerating gives this result).
START TRANSACTION;

-- the old lair: generator and KeepAlive out (the exit portal 0x7008DBA2 stays); the crater portal out
DELETE FROM landblock_instance WHERE guid IN (0x7008DBA0, 0x7008DBA1, 0x7C6A9BA0, 0x7347DBA0, 0x7347DBA1);

-- the site: generator 900202 and a KeepAlive, so the block stays loaded and he can rise unvisited
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
  (0x7347DBA0, 900202, 0x347D001D, 84.0, 108.0, 280.0, 1, 0, 0, 0, 0),
  (0x7347DBA1, 80007, 0x347D001D, 90.0, 108.0, 280.0, 1, 0, 0, 0, 0);

-- his profile spawns at a Specific place (where_Create 4): the summit, not the lair floor
UPDATE weenie_properties_generator SET obj_Cell_Id = 0x347D001D, origin_X = 84.0, origin_Y = 108.0, origin_Z = 280.0,
  angles_W = 1, angles_X = 0, angles_Y = 0, angles_Z = 0
  WHERE object_Id = 900202 AND weenie_Class_Id = 900200;

-- broadcasts: no place named (the Generation line keeps its "has torn open" prefix, which our client's
-- arrival effect listens for; AcVfxMoments.OnChat)
UPDATE weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
  SET a.message = 'Bael''Zharon, the Hopeslayer, has torn open the sky and walks the wilds of Dereth. Hope itself is hunted again.'
  WHERE e.object_Id = 900200 AND a.message LIKE 'Bael''Zharon, the Hopeslayer, has torn open the Shadow Breach%';
UPDATE weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
  SET a.message = '%tn has struck down Bael''Zharon, the Hopeslayer! The Shadows scatter, for now.'
  WHERE e.object_Id = 900200 AND a.message LIKE '%tn has struck down Bael''Zharon%';
UPDATE weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
  SET a.message = 'Bael''Zharon roars, and the earth itself answers his fury!'
  WHERE e.object_Id = 900200 AND a.message = 'Bael''Zharon roars, and the Breach itself answers his fury!';

COMMIT;

SELECT hex(guid), weenie_Class_Id, hex(obj_Cell_Id), origin_X, origin_Y, origin_Z FROM landblock_instance WHERE guid IN (0x7008DBA0, 0x7008DBA1, 0x7008DBA2, 0x7C6A9BA0, 0x7347DBA0, 0x7347DBA1);
SELECT object_Id, hex(obj_Cell_Id), origin_X, origin_Y, origin_Z FROM weenie_properties_generator WHERE object_Id = 900202;
SELECT a.message FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id WHERE e.object_Id = 900200 AND a.message IS NOT NULL;
