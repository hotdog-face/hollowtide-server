-- HOLTBURG GLOW-UP: the two SERVER objects of docs/HOLTBURG-GLOWUP.md, weenies 901100-901101.
--
--   *** NOT APPLIED. MADE, NOT ADDED (2026-09-28 content round). Do not run this until the owner
--   *** approves the Holtburg proposals. Applying it changes what every player sees in Holtburg.
--
-- New rows only: nothing retail is changed, so there is nothing to back up. Re-runnable (deletes
-- its own rows first). To apply, once approved:
--
--   cp expansion/holtburg/models/foresters_memorial.glb expansion/models/w901100.glb
--   cp expansion/holtburg/models/notice_board.glb       expansion/models/w901101.glb
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/holtburg-glowup.sql
--   then in game: @clearcache weenie ; @reloadblock A9B4
--
-- The glbs must be in expansion/models/ BEFORE anyone logs in near Holtburg: without them the
-- client draws each object's base setup (a lifestone-training signpost and a mine head).
--
-- BOTH ARE BOOKS (WeenieType 8) cloned from 5108 'trainlifestonesign', the shape docs/EXPANSION-ITEMS.md
-- section 10 settled for a world object: usable and readable, the way AC's own memorials are (the
-- Shard Vigil Memorial is a Book; the Advocate and Sentinel Monument's page says "You can use the
-- statues to read upon the history").
--
-- THE SETUP IS CHOSEN FOR ITS COLLISION VOLUME, NOT ITS LOOK: a non-creature entity blocks with its
-- setup's DAT volume (WorldColliders.AddRetailColliders), never with the mesh we draw.
--   901100 Foresters' Memorial  0x02000BD7 (3.01 m tall, 2.02 m wide; the Sealed Mine Head's) around
--                               a 1.85 m plinth. The statue above 3 m has no volume, which nobody
--                               can tell from the ground.
--   901101 Holtburg Notice Board 0x0200062E (2.16 m tall, 1.17 m wide; 5108's own, and the Arwic
--                               stele's) inside a 1.8 m board: the posts at the ends do not block.
--
-- LORE (the AC Community Wiki, pages Holtburg, Historical Marker, Eternal Flame 40.4N 34.5E,
-- Brogord the Forester, Celcynd the Dour, Flinrala Ryndmad): Holtburg was raised in 2 PY by settlers
-- out of Cragstone who came to log the Tiofor Wood. In PY 6 Tumeroks came; the townsfolk held
-- Holtburg Redoubt while Celcynd the Dour opened a portal and carried the children away (Flinrala,
-- Hardunna, Worcer). Their parents did not come back: Brogord the Forester and Ryndya (Flinrala's),
-- June (Worcer's), Hope and Roland (Hardunna's). Flinrala Ryndmad and Worcer still stand in
-- Holtburg (A9B40169). The existing memorial is out at the Redoubt; this one is in the town square.
START TRANSACTION;

DELETE FROM weenie_properties_book_page_data WHERE object_Id IN (901100, 901101);
DELETE FROM weenie_properties_book           WHERE object_Id IN (901100, 901101);
DELETE FROM weenie_properties_int    WHERE object_Id IN (901100, 901101);
DELETE FROM weenie_properties_bool   WHERE object_Id IN (901100, 901101);
DELETE FROM weenie_properties_float  WHERE object_Id IN (901100, 901101);
DELETE FROM weenie_properties_d_i_d  WHERE object_Id IN (901100, 901101);
DELETE FROM weenie_properties_string WHERE object_Id IN (901100, 901101);
DELETE FROM weenie WHERE class_Id IN (901100, 901101);

INSERT INTO weenie (class_Id, class_Name, type)
  SELECT 901100, 'acholtburgforestersmemorial', type FROM weenie WHERE class_Id = 5108;
INSERT INTO weenie (class_Id, class_Name, type)
  SELECT 901101, 'acholtburgnoticeboard', type FROM weenie WHERE class_Id = 5108;

INSERT INTO weenie_properties_int    (object_Id, type, value) SELECT 901100, type, value FROM weenie_properties_int    WHERE object_Id = 5108;
INSERT INTO weenie_properties_bool   (object_Id, type, value) SELECT 901100, type, value FROM weenie_properties_bool   WHERE object_Id = 5108;
INSERT INTO weenie_properties_float  (object_Id, type, value) SELECT 901100, type, value FROM weenie_properties_float  WHERE object_Id = 5108;
INSERT INTO weenie_properties_d_i_d  (object_Id, type, value) SELECT 901100, type, value FROM weenie_properties_d_i_d  WHERE object_Id = 5108;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 901100, type, value FROM weenie_properties_string WHERE object_Id = 5108;

INSERT INTO weenie_properties_int    (object_Id, type, value) SELECT 901101, type, value FROM weenie_properties_int    WHERE object_Id = 5108;
INSERT INTO weenie_properties_bool   (object_Id, type, value) SELECT 901101, type, value FROM weenie_properties_bool   WHERE object_Id = 5108;
INSERT INTO weenie_properties_float  (object_Id, type, value) SELECT 901101, type, value FROM weenie_properties_float  WHERE object_Id = 5108;
INSERT INTO weenie_properties_d_i_d  (object_Id, type, value) SELECT 901101, type, value FROM weenie_properties_d_i_d  WHERE object_Id = 5108;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 901101, type, value FROM weenie_properties_string WHERE object_Id = 5108;

UPDATE weenie_properties_string SET value = "Foresters' Memorial"   WHERE object_Id = 901100 AND type = 1;
UPDATE weenie_properties_string SET value = "Holtburg Notice Board" WHERE object_Id = 901101 AND type = 1;
UPDATE weenie_properties_d_i_d  SET value = 33557463 WHERE object_Id = 901100 AND type = 1;  -- 0x02000BD7
-- 901101 keeps 5108's setup 0x0200062E.

INSERT INTO weenie_properties_book (object_Id, max_Num_Pages, max_Num_Chars_Per_Page) VALUES
  (901100, 2, 1000), (901101, 2, 1000);

INSERT INTO weenie_properties_book_page_data (object_Id, page_Id, author_Id, author_Name, author_Account, ignore_Author, page_Text) VALUES
 (901100, 0, 0, "Foresters' Memorial", "prewritten", 0,
  "\nTO THE FORESTERS OF HOLTBURG\n\nwho came out of Cragstone in the second year of the Portal to fell the Tiofor Wood, and raised this town on the hill above the River Prosper.\n\nAnd to those of them who held the Redoubt in the sixth year, when the Tumeroks came, so that the children could be carried away.\n"),
 (901100, 1, 0, "Foresters' Memorial", "prewritten", 0,
  "\nBrogord the Forester. Ryndya his wife.\nJune. Hope. Roland.\nAnd the others, whose names the Redoubt kept.\n\nCelcynd the Dour held the portal open until the last child was through. Flinrala, Hardunna and Worcer grew up in this town. Ask them about their parents, if you are kind.\n"),
 (901101, 0, 0, "Holtburg Notice Board", "prewritten", 0,
  "\nNOTICES\n\nDRUDGES seen again south-west of the town, out by their hideout. Travellers go armed.\n\nMANA: the stones of the menhir ring south-west of the lifestone will restore it to any who stand within.\n\nFISH: the Tackle Master keeps his shop by the water north-east of town.\n"),
 (901101, 1, 0, "Holtburg Notice Board", "prewritten", 0,
  "\nWANTED: strong backs for the Tiofor timber. Carts leave from the square at first light.\n\nLOST: a forester's axe, very old, roughly hewn. Reward from Flinrala Ryndmad in the upper town, no questions asked.\n\nThe Pathwarden asks that new arrivals speak with him before they leave the town.\n");

-- PLACEMENT (expansion/holtburg/placements.json holds the same numbers, and the client-only props).
-- Landblock 0xA9B4 is Holtburg. obj_Cell_Id <landblock>0000 as every outdoor instance there uses;
-- ACE resolves the cell from the origin. z read off a bot standing on each spot (2026-09-28):
-- the lower square is flat at 66.0, the drop terrace at 94.0.
--   Memorial: the lower square, 8 m north-east of the well, facing north across the open grass
--   (heading 180: angles_W 0, angles_Z 1).
--   Notice board: beside the lifestone at the drop point, facing the arrivals (heading 0).
DELETE FROM landblock_instance WHERE weenie_Class_Id IN (901100, 901101);
INSERT INTO landblock_instance
  (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child) VALUES
  (0x7A9B4E01, 901100, 0xA9B40000, 97.5, 167.0, 66.0, 0.0, 0, 0, 1.0, 0),
  (0x7A9B4E02, 901101, 0xA9B40000, 89.5, 16.5, 94.0, 1.0, 0, 0, 0.0, 0);

COMMIT;
