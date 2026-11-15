-- HOUSING EXPANSION, content review batch 1: weenies 921000-921050. Sold by the three Apprentice Craftsmen.
-- Made by expansion/housing/batch1-housing/make_sql.py from manifest.json. Re-runnable: deletes its own rows first.
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/hearth-and-garden.sql
--   then in game: @clearcache weenie
START TRANSACTION;
DELETE FROM weenie_properties_create_list WHERE object_Id IN (12241, 12242, 12243) AND weenie_Class_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_int WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_int64 WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_bool WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_float WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_d_i_d WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie_properties_string WHERE object_Id BETWEEN 921000 AND 921050;
DELETE FROM weenie WHERE class_Id BETWEEN 921000 AND 921050;

-- 921000 Mounted Mattekar Head (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921000, 'b1hmountedmattekarhead', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921000, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921000, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921000, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921000, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921000, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921000, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921000, 19, 6452), (921000, 150, 103), (921000, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921000, 1, 33557262), (921000, 8, 117387264);   -- setup 0x02000B0E, icon 0x06FF3000
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921000, 1, 'Mounted Mattekar Head'), (921000, 16, 'The head of a Mattekar, mounted on a walnut hunter''s board.');

-- 921001 Mounted Monouga Head (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921001, 'b1hmountedmonougahead', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921001, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921001, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921001, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921001, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921001, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921001, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921001, 19, 6452), (921001, 150, 103), (921001, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921001, 1, 33557262), (921001, 8, 117387265);   -- setup 0x02000B0E, icon 0x06FF3001
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921001, 1, 'Mounted Monouga Head'), (921001, 16, 'The head of a Monouga, mounted on a walnut hunter''s board.');

-- 921002 Mounted Banderling Head (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921002, 'b1hmountedbanderlinghead', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921002, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921002, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921002, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921002, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921002, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921002, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921002, 19, 6452), (921002, 150, 103), (921002, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921002, 1, 33557262), (921002, 8, 117387266);   -- setup 0x02000B0E, icon 0x06FF3002
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921002, 1, 'Mounted Banderling Head'), (921002, 16, 'The head of a Banderling, mounted on a walnut hunter''s board.');

-- 921003 Mounted Mosswart Head (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921003, 'b1hmountedmosswarthead', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921003, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921003, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921003, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921003, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921003, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921003, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921003, 19, 6452), (921003, 150, 103), (921003, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921003, 1, 33557262), (921003, 8, 117387267);   -- setup 0x02000B0E, icon 0x06FF3003
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921003, 1, 'Mounted Mosswart Head'), (921003, 16, 'The head of a Mosswart, mounted on a walnut hunter''s board.');

-- 921004 Mounted Gromnie Head (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921004, 'b1hmountedgromniehead', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921004, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921004, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921004, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921004, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921004, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921004, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921004, 19, 6452), (921004, 150, 103), (921004, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921004, 1, 33557262), (921004, 8, 117387268);   -- setup 0x02000B0E, icon 0x06FF3004
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921004, 1, 'Mounted Gromnie Head'), (921004, 16, 'The head of a Gromnie, mounted on a walnut hunter''s board.');

-- 921005 Mounted Ursuin Head (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921005, 'b1hmountedursuinhead', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921005, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921005, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921005, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921005, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921005, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921005, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921005, 19, 6452), (921005, 150, 103), (921005, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921005, 1, 33557262), (921005, 8, 117387269);   -- setup 0x02000B0E, icon 0x06FF3005
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921005, 1, 'Mounted Ursuin Head'), (921005, 16, 'The head of an Ursuin, mounted on a walnut hunter''s board.');

-- 921006 Mounted Phyntos Wasp (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921006, 'b1hmountedphyntoswasp', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921006, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921006, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921006, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921006, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921006, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921006, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921006, 19, 6452), (921006, 150, 103), (921006, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921006, 1, 33557262), (921006, 8, 117387270);   -- setup 0x02000B0E, icon 0x06FF3006
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921006, 1, 'Mounted Phyntos Wasp'), (921006, 16, 'The head of a Phyntos Wasp, mounted on a walnut hunter''s board.');

-- 921007 Mounted Sclavus Head (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921007, 'b1hmountedsclavushead', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921007, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921007, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921007, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921007, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921007, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921007, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921007, 19, 6452), (921007, 150, 103), (921007, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921007, 1, 33557262), (921007, 8, 117387271);   -- setup 0x02000B0E, icon 0x06FF3007
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921007, 1, 'Mounted Sclavus Head'), (921007, 16, 'The head of a Sclavus, mounted on a walnut hunter''s board.');

-- 921010 Aluvian Tower Banner (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921010, 'b1haluviantowerbanner', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921010, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921010, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921010, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921010, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921010, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921010, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921010, 19, 9677), (921010, 150, 103), (921010, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921010, 1, 33557262), (921010, 8, 117387274);   -- setup 0x02000B0E, icon 0x06FF300A
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921010, 1, 'Aluvian Tower Banner'), (921010, 16, 'A blue banner of the Aluvian highlands, a gold tower and crown worked on it.');

-- 921011 Sho Ink Scroll (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921011, 'b1hshoinkscroll', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921011, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921011, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921011, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921011, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921011, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921011, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921011, 19, 6452), (921011, 150, 103), (921011, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921011, 1, 33557262), (921011, 8, 117387275);   -- setup 0x02000B0E, icon 0x06FF300B
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921011, 1, 'Sho Ink Scroll'), (921011, 16, 'A hanging scroll of mountains in mist, painted by a Sho hand in Hebian-to.');

-- 921012 Gharu'ndim Prayer Hanging (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921012, 'b1hgharundimprayerhanging', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921012, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921012, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921012, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921012, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921012, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921012, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921012, 19, 6452), (921012, 150, 103), (921012, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921012, 1, 33557262), (921012, 8, 117387276);   -- setup 0x02000B0E, icon 0x06FF300C
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921012, 1, 'Gharu''ndim Prayer Hanging'), (921012, 16, 'A knotted wool hanging from the looms of Zaikhal, a lamp-blue diamond at its heart.');

-- 921013 Iron Wall Sconce (wall hook), copied from retail weenie 25763, setup 0x02001005
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921013, 'b1hironwallsconce', type, NOW() FROM weenie WHERE class_Id = 25763;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921013, type, value FROM weenie_properties_int WHERE object_Id = 25763 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921013, type, value FROM weenie_properties_int64 WHERE object_Id = 25763;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921013, type, value FROM weenie_properties_bool WHERE object_Id = 25763;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921013, type, value FROM weenie_properties_float WHERE object_Id = 25763 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921013, type, value FROM weenie_properties_d_i_d WHERE object_Id = 25763 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921013, type, value FROM weenie_properties_string WHERE object_Id = 25763 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921013, 19, 3226), (921013, 150, 103), (921013, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921013, 1, 33558533), (921013, 8, 117387277);   -- setup 0x02001005, icon 0x06FF300D
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921013, 1, 'Iron Wall Sconce'), (921013, 16, 'A forged iron sconce with a drip pan and a tallow candle.');

-- 921014 Apothecary Wall Shelf (wall hook), copied from retail weenie 27525, setup 0x02000B0E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921014, 'b1hapothecarywallshelf', type, NOW() FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921014, type, value FROM weenie_properties_int WHERE object_Id = 27525 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921014, type, value FROM weenie_properties_int64 WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921014, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921014, type, value FROM weenie_properties_float WHERE object_Id = 27525 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921014, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921014, type, value FROM weenie_properties_string WHERE object_Id = 27525 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921014, 19, 6452), (921014, 150, 103), (921014, 151, 2);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921014, 1, 33557262), (921014, 8, 117387278);   -- setup 0x02000B0E, icon 0x06FF300E
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921014, 1, 'Apothecary Wall Shelf'), (921014, 16, 'Two oak shelves on iron brackets, crowded with stoppered jars and bottles.');

-- 921020 Wagon-Wheel Chandelier (ceiling hook), copied from retail weenie 241, setup 0x02000B50
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921020, 'b1hwagonwheelchandelier', type, NOW() FROM weenie WHERE class_Id = 241;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921020, type, value FROM weenie_properties_int WHERE object_Id = 241 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921020, type, value FROM weenie_properties_int64 WHERE object_Id = 241;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921020, type, value FROM weenie_properties_bool WHERE object_Id = 241;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921020, type, value FROM weenie_properties_float WHERE object_Id = 241 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921020, type, value FROM weenie_properties_d_i_d WHERE object_Id = 241 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921020, type, value FROM weenie_properties_string WHERE object_Id = 241 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921020, 19, 12903), (921020, 150, 103), (921020, 151, 4);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921020, 1, 33557328), (921020, 8, 117387284);   -- setup 0x02000B50, icon 0x06FF3014
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921020, 1, 'Wagon-Wheel Chandelier'), (921020, 16, 'An iron-shod cart wheel hung on chains, six candles set around its rim.');

-- 921021 Sho Lantern Cluster (ceiling hook), copied from retail weenie 241, setup 0x02000B50
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921021, 'b1hsholanterncluster', type, NOW() FROM weenie WHERE class_Id = 241;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921021, type, value FROM weenie_properties_int WHERE object_Id = 241 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921021, type, value FROM weenie_properties_int64 WHERE object_Id = 241;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921021, type, value FROM weenie_properties_bool WHERE object_Id = 241;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921021, type, value FROM weenie_properties_float WHERE object_Id = 241 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921021, type, value FROM weenie_properties_d_i_d WHERE object_Id = 241 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921021, type, value FROM weenie_properties_string WHERE object_Id = 241 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921021, 19, 6452), (921021, 150, 103), (921021, 151, 4);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921021, 1, 33557328), (921021, 8, 117387285);   -- setup 0x02000B50, icon 0x06FF3015
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921021, 1, 'Sho Lantern Cluster'), (921021, 16, 'Three red paper lanterns on silk cords, hung at different heights.');

-- 921022 Hanging Herb Rack (ceiling hook), copied from retail weenie 241, setup 0x02000B50
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921022, 'b1hhangingherbrack', type, NOW() FROM weenie WHERE class_Id = 241;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921022, type, value FROM weenie_properties_int WHERE object_Id = 241 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921022, type, value FROM weenie_properties_int64 WHERE object_Id = 241;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921022, type, value FROM weenie_properties_bool WHERE object_Id = 241;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921022, type, value FROM weenie_properties_float WHERE object_Id = 241 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921022, type, value FROM weenie_properties_d_i_d WHERE object_Id = 241 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921022, type, value FROM weenie_properties_string WHERE object_Id = 241 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921022, 19, 3226), (921022, 150, 103), (921022, 151, 4);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921022, 1, 33557328), (921022, 8, 117387286);   -- setup 0x02000B50, icon 0x06FF3016
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921022, 1, 'Hanging Herb Rack'), (921022, 16, 'A pine ladder rack hung from the rafters, bundles of drying herbs tied along it.');

-- 921030 Oak Wardrobe (floor hook), copied from retail weenie 25758, setup 0x02000183
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921030, 'b1hoakwardrobe', type, NOW() FROM weenie WHERE class_Id = 25758;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921030, type, value FROM weenie_properties_int WHERE object_Id = 25758 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921030, type, value FROM weenie_properties_int64 WHERE object_Id = 25758;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921030, type, value FROM weenie_properties_bool WHERE object_Id = 25758;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921030, type, value FROM weenie_properties_float WHERE object_Id = 25758 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921030, type, value FROM weenie_properties_d_i_d WHERE object_Id = 25758 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921030, type, value FROM weenie_properties_string WHERE object_Id = 25758 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921030, 19, 16129), (921030, 150, 103), (921030, 151, 1);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921030, 1, 33554819), (921030, 8, 117387294);   -- setup 0x02000183, icon 0x06FF301E
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921030, 1, 'Oak Wardrobe'), (921030, 16, 'A tall oak wardrobe with iron strap hinges and a carved crown.');

-- 921031 Iron-Bound Strongbox (floor hook), copied from retail weenie 22854, setup 0x02000369
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921031, 'b1hironboundstrongbox', type, NOW() FROM weenie WHERE class_Id = 22854;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921031, type, value FROM weenie_properties_int WHERE object_Id = 22854 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921031, type, value FROM weenie_properties_int64 WHERE object_Id = 22854;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921031, type, value FROM weenie_properties_bool WHERE object_Id = 22854;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921031, type, value FROM weenie_properties_float WHERE object_Id = 22854 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921031, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22854 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921031, type, value FROM weenie_properties_string WHERE object_Id = 22854 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921031, 19, 6452), (921031, 150, 103), (921031, 151, 1);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921031, 1, 33555305), (921031, 8, 117387295);   -- setup 0x02000369, icon 0x06FF301F
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921031, 1, 'Iron-Bound Strongbox'), (921031, 16, 'A squat oak chest banded in iron, with a heavy hasp and lock.');

-- 921033 Sho Low Table (floor hook), copied from retail weenie 22854, setup 0x02000369
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921033, 'b1hsholowtable', type, NOW() FROM weenie WHERE class_Id = 22854;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921033, type, value FROM weenie_properties_int WHERE object_Id = 22854 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921033, type, value FROM weenie_properties_int64 WHERE object_Id = 22854;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921033, type, value FROM weenie_properties_bool WHERE object_Id = 22854;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921033, type, value FROM weenie_properties_float WHERE object_Id = 22854 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921033, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22854 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921033, type, value FROM weenie_properties_string WHERE object_Id = 22854 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921033, 19, 6452), (921033, 150, 103), (921033, 151, 1);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921033, 1, 33555305), (921033, 8, 117387297);   -- setup 0x02000369, icon 0x06FF3021
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921033, 1, 'Sho Low Table'), (921033, 16, 'A low red-lacquered tea table with curled feet, made for sitting on the floor.');

-- 921034 Gharu'ndim Brass Brazier (floor hook), copied from retail weenie 22844, setup 0x02000E86
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921034, 'b1hgharundimbrassbrazier', type, NOW() FROM weenie WHERE class_Id = 22844;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921034, type, value FROM weenie_properties_int WHERE object_Id = 22844 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921034, type, value FROM weenie_properties_int64 WHERE object_Id = 22844;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921034, type, value FROM weenie_properties_bool WHERE object_Id = 22844;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921034, type, value FROM weenie_properties_float WHERE object_Id = 22844 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921034, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22844 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921034, type, value FROM weenie_properties_string WHERE object_Id = 22844 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921034, 19, 9677), (921034, 150, 103), (921034, 151, 1);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921034, 1, 33558150), (921034, 8, 117387298);   -- setup 0x02000E86, icon 0x06FF3022
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921034, 1, 'Gharu''ndim Brass Brazier'), (921034, 16, 'A pierced brass bowl of glowing coals on three lion-paw legs.');

-- 921035 Braided Aluvian Rug (floor hook), copied from retail weenie 22254, setup 0x02000E8A
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921035, 'b1hbraidedaluvianrug', type, NOW() FROM weenie WHERE class_Id = 22254;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921035, type, value FROM weenie_properties_int WHERE object_Id = 22254 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921035, type, value FROM weenie_properties_int64 WHERE object_Id = 22254;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921035, type, value FROM weenie_properties_bool WHERE object_Id = 22254;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921035, type, value FROM weenie_properties_float WHERE object_Id = 22254 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921035, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22254 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921035, type, value FROM weenie_properties_string WHERE object_Id = 22254 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921035, 19, 3226), (921035, 150, 103), (921035, 151, 1);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921035, 1, 33558154), (921035, 8, 117387299);   -- setup 0x02000E8A, icon 0x06FF3023
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921035, 1, 'Braided Aluvian Rug'), (921035, 16, 'A round rag rug, braided from old cloaks in rust, wheat and moss.');

-- 921036 Sho Reed Mat (floor hook), copied from retail weenie 22254, setup 0x02000E8A
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921036, 'b1hshoreedmat', type, NOW() FROM weenie WHERE class_Id = 22254;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921036, type, value FROM weenie_properties_int WHERE object_Id = 22254 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921036, type, value FROM weenie_properties_int64 WHERE object_Id = 22254;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921036, type, value FROM weenie_properties_bool WHERE object_Id = 22254;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921036, type, value FROM weenie_properties_float WHERE object_Id = 22254 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921036, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22254 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921036, type, value FROM weenie_properties_string WHERE object_Id = 22254 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921036, 19, 3226), (921036, 150, 103), (921036, 151, 1);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921036, 1, 33558154), (921036, 8, 117387300);   -- setup 0x02000E8A, icon 0x06FF3024
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921036, 1, 'Sho Reed Mat'), (921036, 16, 'A woven reed floor mat edged in dark cloth, as laid in every Sho house.');

-- 921037 Fireside Armchair (floor hook), copied from retail weenie 22874, setup 0x02000E7E
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921037, 'b1hfiresidearmchair', type, NOW() FROM weenie WHERE class_Id = 22874;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921037, type, value FROM weenie_properties_int WHERE object_Id = 22874 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921037, type, value FROM weenie_properties_int64 WHERE object_Id = 22874;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921037, type, value FROM weenie_properties_bool WHERE object_Id = 22874;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921037, type, value FROM weenie_properties_float WHERE object_Id = 22874 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921037, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22874 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921037, type, value FROM weenie_properties_string WHERE object_Id = 22874 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921037, 19, 9677), (921037, 150, 103), (921037, 151, 1);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921037, 1, 33558142), (921037, 8, 117387301);   -- setup 0x02000E7E, icon 0x06FF3025
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921037, 1, 'Fireside Armchair'), (921037, 16, 'A deep oak armchair with a wine velvet seat, worn smooth by long winter evenings.');

-- 921040 Raised Herb Planter (yard hook), copied from retail weenie 22854, setup 0x02000369
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921040, 'b1hraisedherbplanter', type, NOW() FROM weenie WHERE class_Id = 22854;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921040, type, value FROM weenie_properties_int WHERE object_Id = 22854 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921040, type, value FROM weenie_properties_int64 WHERE object_Id = 22854;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921040, type, value FROM weenie_properties_bool WHERE object_Id = 22854;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921040, type, value FROM weenie_properties_float WHERE object_Id = 22854 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921040, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22854 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921040, type, value FROM weenie_properties_string WHERE object_Id = 22854 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921040, 19, 6452), (921040, 150, 103), (921040, 151, 9);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921040, 1, 33555305), (921040, 8, 117387304);   -- setup 0x02000369, icon 0x06FF3028
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921040, 1, 'Raised Herb Planter'), (921040, 16, 'A plank planter box of dark soil, thick with sage, thyme and fennel.');

-- 921041 Stone Garden Bench (yard hook), copied from retail weenie 268, setup 0x02000120
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921041, 'b1hstonegardenbench', type, NOW() FROM weenie WHERE class_Id = 268;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921041, type, value FROM weenie_properties_int WHERE object_Id = 268 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921041, type, value FROM weenie_properties_int64 WHERE object_Id = 268;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921041, type, value FROM weenie_properties_bool WHERE object_Id = 268;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921041, type, value FROM weenie_properties_float WHERE object_Id = 268 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921041, type, value FROM weenie_properties_d_i_d WHERE object_Id = 268 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921041, type, value FROM weenie_properties_string WHERE object_Id = 268 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921041, 19, 9677), (921041, 150, 103), (921041, 151, 9);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921041, 1, 33554720), (921041, 8, 117387305);   -- setup 0x02000120, icon 0x06FF3029
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921041, 1, 'Stone Garden Bench'), (921041, 16, 'A slab of grey stone on two carved legs, mottled with moss.');

-- 921042 Stone Birdbath (yard hook), copied from retail weenie 15826, setup 0x02000C84
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921042, 'b1hstonebirdbath', type, NOW() FROM weenie WHERE class_Id = 15826;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921042, type, value FROM weenie_properties_int WHERE object_Id = 15826 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921042, type, value FROM weenie_properties_int64 WHERE object_Id = 15826;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921042, type, value FROM weenie_properties_bool WHERE object_Id = 15826;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921042, type, value FROM weenie_properties_float WHERE object_Id = 15826 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921042, type, value FROM weenie_properties_d_i_d WHERE object_Id = 15826 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921042, type, value FROM weenie_properties_string WHERE object_Id = 15826 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921042, 19, 9677), (921042, 150, 103), (921042, 151, 9);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921042, 1, 33557636), (921042, 8, 117387306);   -- setup 0x02000C84, icon 0x06FF302A
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921042, 1, 'Stone Birdbath'), (921042, 16, 'A carved stone basin on a fluted column, a little rainwater still in it.');

-- 921043 Field Scarecrow (yard hook), copied from retail weenie 22853, setup 0x02000719
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921043, 'b1hfieldscarecrow', type, NOW() FROM weenie WHERE class_Id = 22853;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921043, type, value FROM weenie_properties_int WHERE object_Id = 22853 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921043, type, value FROM weenie_properties_int64 WHERE object_Id = 22853;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921043, type, value FROM weenie_properties_bool WHERE object_Id = 22853;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921043, type, value FROM weenie_properties_float WHERE object_Id = 22853 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921043, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22853 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921043, type, value FROM weenie_properties_string WHERE object_Id = 22853 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921043, 19, 3226), (921043, 150, 103), (921043, 151, 9);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921043, 1, 33556249), (921043, 8, 117387307);   -- setup 0x02000719, icon 0x06FF302B
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921043, 1, 'Field Scarecrow'), (921043, 16, 'A sackcloth scarecrow in a patched coat and a straw hat, guarding nothing in particular.');

-- 921044 Sho Stone Lantern (yard hook), copied from retail weenie 22853, setup 0x02000719
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921044, 'b1hshostonelantern', type, NOW() FROM weenie WHERE class_Id = 22853;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921044, type, value FROM weenie_properties_int WHERE object_Id = 22853 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921044, type, value FROM weenie_properties_int64 WHERE object_Id = 22853;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921044, type, value FROM weenie_properties_bool WHERE object_Id = 22853;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921044, type, value FROM weenie_properties_float WHERE object_Id = 22853 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921044, type, value FROM weenie_properties_d_i_d WHERE object_Id = 22853 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921044, type, value FROM weenie_properties_string WHERE object_Id = 22853 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921044, 19, 9677), (921044, 150, 103), (921044, 151, 9);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921044, 1, 33556249), (921044, 8, 117387308);   -- setup 0x02000719, icon 0x06FF302C
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921044, 1, 'Sho Stone Lantern'), (921044, 16, 'A squat grey stone lantern with a pagoda cap, as set beside the paths of Shoushi gardens.');

-- 921045 Flowering Shrub in an Urn (yard hook), copied from retail weenie 25776, setup 0x02001008
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921045, 'b1hfloweringshrubinanurn', type, NOW() FROM weenie WHERE class_Id = 25776;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921045, type, value FROM weenie_properties_int WHERE object_Id = 25776 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921045, type, value FROM weenie_properties_int64 WHERE object_Id = 25776;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921045, type, value FROM weenie_properties_bool WHERE object_Id = 25776;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921045, type, value FROM weenie_properties_float WHERE object_Id = 25776 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921045, type, value FROM weenie_properties_d_i_d WHERE object_Id = 25776 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921045, type, value FROM weenie_properties_string WHERE object_Id = 25776 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921045, 19, 6452), (921045, 150, 103), (921045, 151, 9);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921045, 1, 33558536), (921045, 8, 117387309);   -- setup 0x02001008, icon 0x06FF302D
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921045, 1, 'Flowering Shrub in an Urn'), (921045, 16, 'A terracotta urn holding a clipped shrub dotted with pale blossom.');

-- 921050 Iron Weathervane (roof hook), copied from retail weenie 16920, setup 0x02000CDB
INSERT INTO weenie (class_Id, class_Name, type, last_Modified) SELECT 921050, 'b1hironweathervane', type, NOW() FROM weenie WHERE class_Id = 16920;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 921050, type, value FROM weenie_properties_int WHERE object_Id = 16920 AND type NOT IN (19, 150, 151, 114, 33);
INSERT INTO weenie_properties_int64 (object_Id, type, value) SELECT 921050, type, value FROM weenie_properties_int64 WHERE object_Id = 16920;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 921050, type, value FROM weenie_properties_bool WHERE object_Id = 16920;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 921050, type, value FROM weenie_properties_float WHERE object_Id = 16920 AND type NOT IN (39);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 921050, type, value FROM weenie_properties_d_i_d WHERE object_Id = 16920 AND type NOT IN (1, 8);
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 921050, type, value FROM weenie_properties_string WHERE object_Id = 16920 AND type NOT IN (1, 14, 15, 16);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (921050, 19, 9677), (921050, 150, 103), (921050, 151, 24);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (921050, 1, 33557723), (921050, 8, 117387314);   -- setup 0x02000CDB, icon 0x06FF3032
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (921050, 1, 'Iron Weathervane'), (921050, 16, 'A wrought iron weathervane, a running mattekar over the four winds.');

-- the shop rows (destination 4 = Shop), in the manifest's order
INSERT INTO weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES
  (12241, 4, 921000, -1, 0, 0, 0),
  (12241, 4, 921001, -1, 0, 0, 0),
  (12241, 4, 921002, -1, 0, 0, 0),
  (12241, 4, 921003, -1, 0, 0, 0),
  (12241, 4, 921004, -1, 0, 0, 0),
  (12241, 4, 921005, -1, 0, 0, 0),
  (12241, 4, 921006, -1, 0, 0, 0),
  (12241, 4, 921007, -1, 0, 0, 0),
  (12241, 4, 921010, -1, 0, 0, 0),
  (12241, 4, 921011, -1, 0, 0, 0),
  (12241, 4, 921012, -1, 0, 0, 0),
  (12241, 4, 921013, -1, 0, 0, 0),
  (12241, 4, 921014, -1, 0, 0, 0),
  (12241, 4, 921020, -1, 0, 0, 0),
  (12241, 4, 921021, -1, 0, 0, 0),
  (12241, 4, 921022, -1, 0, 0, 0),
  (12241, 4, 921030, -1, 0, 0, 0),
  (12241, 4, 921031, -1, 0, 0, 0),
  (12241, 4, 921033, -1, 0, 0, 0),
  (12241, 4, 921034, -1, 0, 0, 0),
  (12241, 4, 921035, -1, 0, 0, 0),
  (12241, 4, 921036, -1, 0, 0, 0),
  (12241, 4, 921037, -1, 0, 0, 0),
  (12241, 4, 921040, -1, 0, 0, 0),
  (12241, 4, 921041, -1, 0, 0, 0),
  (12241, 4, 921042, -1, 0, 0, 0),
  (12241, 4, 921043, -1, 0, 0, 0),
  (12241, 4, 921044, -1, 0, 0, 0),
  (12241, 4, 921045, -1, 0, 0, 0),
  (12241, 4, 921050, -1, 0, 0, 0),
  (12242, 4, 921000, -1, 0, 0, 0),
  (12242, 4, 921001, -1, 0, 0, 0),
  (12242, 4, 921002, -1, 0, 0, 0),
  (12242, 4, 921003, -1, 0, 0, 0),
  (12242, 4, 921004, -1, 0, 0, 0),
  (12242, 4, 921005, -1, 0, 0, 0),
  (12242, 4, 921006, -1, 0, 0, 0),
  (12242, 4, 921007, -1, 0, 0, 0),
  (12242, 4, 921010, -1, 0, 0, 0),
  (12242, 4, 921011, -1, 0, 0, 0),
  (12242, 4, 921012, -1, 0, 0, 0),
  (12242, 4, 921013, -1, 0, 0, 0),
  (12242, 4, 921014, -1, 0, 0, 0),
  (12242, 4, 921020, -1, 0, 0, 0),
  (12242, 4, 921021, -1, 0, 0, 0),
  (12242, 4, 921022, -1, 0, 0, 0),
  (12242, 4, 921030, -1, 0, 0, 0),
  (12242, 4, 921031, -1, 0, 0, 0),
  (12242, 4, 921033, -1, 0, 0, 0),
  (12242, 4, 921034, -1, 0, 0, 0),
  (12242, 4, 921035, -1, 0, 0, 0),
  (12242, 4, 921036, -1, 0, 0, 0),
  (12242, 4, 921037, -1, 0, 0, 0),
  (12242, 4, 921040, -1, 0, 0, 0),
  (12242, 4, 921041, -1, 0, 0, 0),
  (12242, 4, 921042, -1, 0, 0, 0),
  (12242, 4, 921043, -1, 0, 0, 0),
  (12242, 4, 921044, -1, 0, 0, 0),
  (12242, 4, 921045, -1, 0, 0, 0),
  (12242, 4, 921050, -1, 0, 0, 0),
  (12243, 4, 921000, -1, 0, 0, 0),
  (12243, 4, 921001, -1, 0, 0, 0),
  (12243, 4, 921002, -1, 0, 0, 0),
  (12243, 4, 921003, -1, 0, 0, 0),
  (12243, 4, 921004, -1, 0, 0, 0),
  (12243, 4, 921005, -1, 0, 0, 0),
  (12243, 4, 921006, -1, 0, 0, 0),
  (12243, 4, 921007, -1, 0, 0, 0),
  (12243, 4, 921010, -1, 0, 0, 0),
  (12243, 4, 921011, -1, 0, 0, 0),
  (12243, 4, 921012, -1, 0, 0, 0),
  (12243, 4, 921013, -1, 0, 0, 0),
  (12243, 4, 921014, -1, 0, 0, 0),
  (12243, 4, 921020, -1, 0, 0, 0),
  (12243, 4, 921021, -1, 0, 0, 0),
  (12243, 4, 921022, -1, 0, 0, 0),
  (12243, 4, 921030, -1, 0, 0, 0),
  (12243, 4, 921031, -1, 0, 0, 0),
  (12243, 4, 921033, -1, 0, 0, 0),
  (12243, 4, 921034, -1, 0, 0, 0),
  (12243, 4, 921035, -1, 0, 0, 0),
  (12243, 4, 921036, -1, 0, 0, 0),
  (12243, 4, 921037, -1, 0, 0, 0),
  (12243, 4, 921040, -1, 0, 0, 0),
  (12243, 4, 921041, -1, 0, 0, 0),
  (12243, 4, 921042, -1, 0, 0, 0),
  (12243, 4, 921043, -1, 0, 0, 0),
  (12243, 4, 921044, -1, 0, 0, 0),
  (12243, 4, 921045, -1, 0, 0, 0),
  (12243, 4, 921050, -1, 0, 0, 0);

COMMIT;
