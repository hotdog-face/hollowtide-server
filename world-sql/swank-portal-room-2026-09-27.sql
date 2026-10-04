-- HOTEL SWANK PORTAL ROOM, REBUILT (owner 2026-09-27: "empty the portal room out except for interesting
-- points around the map that are on the tour and elsewhere. replace all portals in hotel swank except
-- for the one to my mansion."). Backup of what this removes: swank-portals-backup-2026-09-27.sql.
--
--   ssh gpubox sudo -n mysql ace_world < tools/gpubox-ace/swank-portal-room-2026-09-27.sql
--   then in game: @clearcache weenie ; @clearcache landblock ; @reloadblock 0x018A   (AGENT-BRIEF trap 7)
--
-- Every portal in landblock 0x018A (weenie type 7 = Portal) except 0x7018A151 (wcid 500010, the Obsidian
-- Span mansion) is deleted, and new portal weenies 900100-9001xx (copies of 500010's shape: ItemType
-- Portal, PortalBitmask 1 = unrestricted, no level or quest limits) are placed in the old slots (each new
-- guid is the old slot's guid + 0x100, same cell, position and rotation), grouped by floor:
--   ground floor (z 0):  the Tour, around the mansion portal
--   floor z -6:          waterfalls (west), towns (north), bosses (south and east)
--   floor z -12:         DAT dungeons nothing in ACE uses (docs/UNUSED-ASSETS.md section 1)
-- Destinations marked "copy" take retail's own portal destination row; the rest were read from a bot's
-- WHERE after @tele/@teleloc (it stood there). Re-runnable: deletes its own rows first.
START TRANSACTION;
DELETE FROM landblock_instance WHERE (guid >> 12) = 0x7018A AND weenie_Class_Id IN (SELECT class_Id FROM weenie WHERE type = 7) AND guid <> 0x7018A151;
DELETE FROM weenie_properties_int      WHERE object_Id BETWEEN 900100 AND 900199;
DELETE FROM weenie_properties_bool     WHERE object_Id BETWEEN 900100 AND 900199;
DELETE FROM weenie_properties_float    WHERE object_Id BETWEEN 900100 AND 900199;
DELETE FROM weenie_properties_d_i_d    WHERE object_Id BETWEEN 900100 AND 900199;
DELETE FROM weenie_properties_string   WHERE object_Id BETWEEN 900100 AND 900199;
DELETE FROM weenie_properties_position WHERE object_Id BETWEEN 900100 AND 900199;
DELETE FROM weenie WHERE class_Id BETWEEN 900100 AND 900199;

-- 900100 [Tour] Portal to the Top of the Deru Tree
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900100, 'acportalthetopofthederutree', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900100, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900100, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900100, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900100, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900100, 1, 'Portal to the Top of the Deru Tree'), (900100, 16, 'The top of the Viridian Deru Tree. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900100, 2, 3041525780, 56, 80, 201.275, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662427, 900100, 25821701, 83.09, -20.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900101 [Tour] Portal to the Advocate Wing
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900101, 'acportaltheadvocatewing', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900101, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900101, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900101, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900101, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900101, 1, 'Portal to the Advocate Wing'), (900101, 16, 'The Advocate wing of Hotel Swank, one floor down and east. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900101, 2, 25821567, 130, -100, -12, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662428, 900101, 25821702, 83.09, -30.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900102 [Tour] Portal to Aerbax's Citadel
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900102, 'acportalaerbaxscitadel', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900102, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900102, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900102, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900102, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900102, 1, 'Portal to Aerbax\'s Citadel'), (900102, 16, 'The south gate of Aerbax\'s Citadel, 65.0S 63.8W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900102, 2, 791543850, 132, 36, 12.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662429, 900102, 25821703, 83.09, -40.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900103 [Tour] Portal to the Tower Guardian
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900103, 'acportalthetowerguardian', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900103, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900103, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900103, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900103, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900103, 1, 'Portal to the Tower Guardian'), (900103, 16, 'The courtyard of Asheron\'s Castle where the Tower Guardian stands. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900103, 2, 3583574079, 180, 155, 374, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662430, 900103, 25821704, 83.09, -50.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900104 [Tour] Portal to Issk
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900104, 'acportalissk', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900104, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900104, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900104, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900104, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900104, 1, 'Portal to Issk'), (900104, 16, 'Issk, where the Harbinger fight begins, 12.9S 46.5E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900104, 2, 3111059491, 108, 60, 10.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662431, 900104, 25821705, 83.09, -60.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900105 [Tour] Portal to Asheron's Castle
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900105, 'acportalasheronscastle', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900105, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900105, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900105, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900105, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900105, 1, 'Portal to Asheron\'s Castle'), (900105, 16, 'Asheron\'s Castle on its mountain, 21.1N 69.3E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900105, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 35293 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662432, 900105, 25821712, 89.9928, -13.8072, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900106 [Tour] Portal to Asheron's Island
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900106, 'acportalasheronsisland', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900106, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900106, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900106, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900106, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900106, 1, 'Portal to Asheron\'s Island'), (900106, 16, 'The arrival point on Asheron\'s Island, 16.1N 69.3E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900106, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 33558 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662451, 900106, 25821726, 99.9928, -13.8072, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900107 [Tour] Portal to Obsidian Span
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900107, 'acportalobsidianspan', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900107, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900107, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900107, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900107, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900107, 1, 'Portal to Obsidian Span'), (900107, 16, 'The Empyrean ruin at the Obsidian Span, 34.5N 42.1E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900107, 2, 3031040005, 12, 108, 51.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662476, 900107, 25821768, 119.993, -13.8072, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900108 [Tour] Portal to the Valley of Death
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900108, 'acportalthevalleyofdeath', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900108, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900108, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900108, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900108, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900108, 1, 'Portal to the Valley of Death'), (900108, 16, 'The lifestone at the southern edge of the Valley of Death (retail\'s Obsidian Rim drop). Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900108, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 7210 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662483, 900108, 25821786, 129.993, -13.8072, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900109 [Tour] Portal to Ayan Baqur
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900109, 'acportalayanbaqur', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900109, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900109, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900109, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900109, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900109, 1, 'Portal to Ayan Baqur'), (900109, 16, 'Ayan Baqur, the far western Direlands city, 60.5S 88.0W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900109, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 7194 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662435, 900109, 25821718, 89.9928, -66.9105, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900110 [Tour] Portal to Candeth Keep
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900110, 'acportalcandethkeep', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900110, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900110, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900110, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900110, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900110, 1, 'Portal to Candeth Keep'), (900110, 16, 'Candeth Keep, 87.5S 67.1W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900110, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 24579 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662452, 900110, 25821732, 99.9365, -66.9763, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900111 [Tour] Portal to Singularity Caul
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900111, 'acportalsingularitycaul', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900111, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900111, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900111, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900111, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900111, 1, 'Portal to Singularity Caul'), (900111, 16, 'The Virindi island of Singularity Caul, 94.5S 94.7W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900111, 2, 151584771, 12, 60, 14.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662477, 900111, 25821774, 119.993, -66.9105, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900112 [Tour] Portal to Timaru
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900112, 'acportaltimaru', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900112, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900112, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900112, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900112, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900112, 1, 'Portal to Timaru'), (900112, 16, 'Timaru on the Marescent Plateau, 44.2N 78.0W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900112, 2, 498467078, 180, 132, 119.733, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662486, 900112, 25821792, 129.993, -66.9105, 0.005, 1, 0, 0, 0, 0, NOW());
-- 900113 [Tour] Portal to Sanamar
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900113, 'acportalsanamar', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900113, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900113, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900113, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900113, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900113, 1, 'Portal to Sanamar'), (900113, 16, 'Sanamar, the Viamontian capital, 72.1N 60.9W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900113, 2, 869859349, 60, 108, 52.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662507, 900113, 25821804, 136.193, -20.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900114 [Tour] Portal to the Floating City
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900114, 'acportalthefloatingcity', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900114, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900114, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900114, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900114, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900114, 1, 'Portal to the Floating City'), (900114, 16, 'Inside the Floating City (retail\'s portal destination). Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900114, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 8190 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662508, 900114, 25821805, 136.193, -30.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900115 [Tour] Portal to the Colosseum
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900115, 'acportalthecolosseum', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900115, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900115, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900115, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900115, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900115, 1, 'Portal to the Colosseum'), (900115, 16, 'The Colosseum west of Yanshi, 11.2S 37.5E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900115, 2, 2926641172, 60, 84, 20.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662509, 900115, 25821806, 136.193, -40.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900116 [Tour] Portal to Fort Tethana
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900116, 'acportalforttethana', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900116, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900116, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900116, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900116, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900116, 1, 'Portal to Fort Tethana'), (900116, 16, 'Fort Tethana, 1.7N 71.2W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900116, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 4040 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662510, 900116, 25821807, 136.193, -50.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900117 [Tour] Portal to Mhoire Castle
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900117, 'acportalmhoirecastle', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900117, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900117, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900117, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900117, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900117, 1, 'Portal to Mhoire Castle'), (900117, 16, 'Mhoire Castle, 64.7S 45.2W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900117, 2, 1177419837, 180, 108, 3.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662511, 900117, 25821808, 136.193, -60.007, 0.005, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900118 [Tour] Portal to the Royal Tent
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900118, 'acportaltheroyaltent', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900118, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900118, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900118, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900118, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900118, 1, 'Portal to the Royal Tent'), (900118, 16, 'The Royal Tent at Mareeno Donn, where the Samurai Titan fight begins, 80.7N 43.0W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900118, 2, 1239679233, 132, 60.891, 11.71, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662433, 900118, 25821713, 94.3815, -24.6313, 0.005, 0.449595, 0, 0, -0.893232, 0, NOW());
-- 900119 [Tour] Portal to Bur
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900119, 'acportalbur', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900119, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900119, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900119, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900119, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900119, 1, 'Portal to Bur'), (900119, 16, 'The entrance to Bur, 67.4N 30.5E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900119, 2, 2782068774, 108, 132, 404.065, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662434, 900119, 25821717, 94.6209, -55.4297, 0.005, 0.930508, 0, 0, -0.366273, 0, NOW());
-- 900120 [Tour] Portal to Mount Lethe
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900120, 'acportalmountlethe', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900120, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900120, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900120, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900120, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900120, 1, 'Portal to Mount Lethe'), (900120, 16, 'Mount Lethe, 34.4S 84.9W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900120, 2, 357826580, 60, 84, 122.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662484, 900120, 25821787, 125.989, -23.9892, 0.005, 0.361522, 0, 0, 0.932364, 0, NOW());
-- 900121 [Tour] Portal to Mount Esper
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900121, 'acportalmountesper', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900121, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900121, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900121, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900121, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900121, 1, 'Portal to Mount Esper'), (900121, 16, 'Mount Esper, 66.0N 13.0E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900121, 2, 2412838960, 132, 180, 280.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662485, 900121, 25821791, 125.33, -55.8435, 0.005, 0.909165, 0, 0, 0.416436, 0, NOW());
-- 900122 [Tour] Portal to the Cataracts of Sabella
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900122, 'acportalthecataractsofsabella', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900122, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900122, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900122, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900122, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900122, 1, 'Portal to the Cataracts of Sabella'), (900122, 16, 'The Cataracts of Sabella waterfall, 82.6N 75.0W. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900122, 2, 568721454, 132, 132, 79.11, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662392, 900122, 25821667, 93.09, -30.007, -5.995, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900123 [Tour] Portal to Ithaenc Falls
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900123, 'acportalithaencfalls', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900123, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900123, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900123, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900123, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900123, 1, 'Portal to Ithaenc Falls'), (900123, 16, 'The grass beside the top of Ithaenc Falls, 81.9S 94.0E (the pool below is deep water and hostile). Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900123, 2, 4095279161, 180, 12, 99.56, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662393, 900123, 25821668, 93.09, -40.007, -5.995, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900124 [Tour] Portal to the Xi Ru Sea Cliff
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900124, 'acportalthexiruseacliff', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900124, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900124, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900124, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900124, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900124, 1, 'Portal to the Xi Ru Sea Cliff'), (900124, 16, 'The sea cliff by Xi Ru\'s Chapel, 90.2S 87.4E. Hotel Swank portal room, Tour.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900124, 2, 3960340525, 132, 120, -0.09, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662394, 900124, 25821669, 93.09, -50.007, -5.995, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900125 [Towns] Portal to Holtburg
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900125, 'acportalholtburg', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900125, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900125, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900125, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900125, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900125, 1, 'Portal to Holtburg'), (900125, 16, 'Holtburg. Hotel Swank portal room, Towns.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900125, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 42820 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662395, 900125, 25821670, 99.9928, -23.8072, -5.995, 1, 0, 0, 0, 0, NOW());
-- 900126 [Towns] Portal to Shoushi
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900126, 'acportalshoushi', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900126, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900126, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900126, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900126, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900126, 1, 'Portal to Shoushi'), (900126, 16, 'Shoushi. Hotel Swank portal room, Towns.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900126, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 42840 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662400, 900126, 25821676, 109.993, -23.8072, -5.995, 1, 0, 0, 0, 0, NOW());
-- 900127 [Towns] Portal to Yaraq
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900127, 'acportalyaraq', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900127, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900127, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900127, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900127, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900127, 1, 'Portal to Yaraq'), (900127, 16, 'Yaraq. Hotel Swank portal room, Towns.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900127, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 42824 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662403, 900127, 25821681, 119.993, -23.8072, -5.995, 1, 0, 0, 0, 0, NOW());
-- 900128 [Towns] Portal to Cragstone
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900128, 'acportalcragstone', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900128, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900128, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900128, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900128, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900128, 1, 'Portal to Cragstone'), (900128, 16, 'Cragstone. Hotel Swank portal room, Towns.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900128, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 42818 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662396, 900128, 25821671, 96.4269, -26.426, -5.995, 0.944725, 0, 0, 0.327865, 0, NOW());
-- 900129 [Towns] Portal to Hebian-To
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900129, 'acportalhebianto', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900129, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900129, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900129, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900129, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900129, 1, 'Portal to Hebian-To'), (900129, 16, 'Hebian-To. Hotel Swank portal room, Towns.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900129, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 42846 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662401, 900129, 25821677, 105.057, -26.357, -5.995, 0.999953, 0, 0, 0.009678, 0, NOW());
-- 900130 [Towns] Portal to Zaikhal
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900130, 'acportalzaikhal', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900130, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900130, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900130, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900130, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900130, 1, 'Portal to Zaikhal'), (900130, 16, 'Zaikhal. Hotel Swank portal room, Towns.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900130, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 42831 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662404, 900130, 25821682, 123.082, -27.0069, -5.995, 0.921061, 0, 0, -0.389418, 0, NOW());
-- 900131 [Bosses] Portal to the Gardens of Menilesh
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900131, 'acportalthegardensofmenilesh', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900131, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900131, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900131, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900131, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900131, 1, 'Portal to the Gardens of Menilesh'), (900131, 16, 'The Gardens of Menilesh, home of Lady Aerfalle (retail\'s portal destination). Hotel Swank portal room, Bosses.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900131, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 38086 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662399, 900131, 25821674, 99.9928, -56.9105, -5.995, 1, 0, 0, 0, 0, NOW());
-- 900132 [Bosses] Portal to the Twisted Refuge
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900132, 'acportalthetwistedrefuge', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900132, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900132, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900132, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900132, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900132, 1, 'Portal to the Twisted Refuge'), (900132, 16, 'The Twisted Refuge, where Geraine is fought (retail\'s portal destination). Hotel Swank portal room, Bosses.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900132, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 45725 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662402, 900132, 25821680, 109.993, -56.9105, -5.995, 1, 0, 0, 0, 0, NOW());
-- 900133 [Bosses] Portal to Hoshino Fortress
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900133, 'acportalhoshinofortress', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900133, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900133, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900133, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900133, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900133, 1, 'Portal to Hoshino Fortress'), (900133, 16, 'The Private Chambers of Hoshino Fortress, where Hoshino Kei is fought (retail\'s portal destination). Hotel Swank portal room, Bosses.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900133, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 72527 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662406, 900133, 25821685, 119.993, -56.9105, -5.995, 1, 0, 0, 0, 0, NOW());
-- 900134 [Bosses] Portal to the Olthoi Hive Queen
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900134, 'acportaltheolthoihivequeen', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900134, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900134, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900134, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900134, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900134, 1, 'Portal to the Olthoi Hive Queen'), (900134, 16, 'The Queen\'s Quarters of the Olthoi hive (retail\'s portal destination). Hotel Swank portal room, Bosses.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900134, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 72454 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662407, 900134, 25821686, 126.193, -30.007, -5.995, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900135 [Bosses] Portal to Rynthid Genesis
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900135, 'acportalrynthidgenesis', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900135, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900135, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900135, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900135, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900135, 1, 'Portal to Rynthid Genesis'), (900135, 16, 'Rynthid Genesis, where the Aspect of Avarice is fought (retail\'s portal destination). Hotel Swank portal room, Bosses.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900135, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 51615 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662408, 900135, 25821687, 126.193, -40.007, -5.995, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900136 [Bosses] Portal to the Catacombs of Torment
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900136, 'acportalthecatacombsoftorment', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900136, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900136, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900136, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900136, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900136, 1, 'Portal to the Catacombs of Torment'), (900136, 16, 'The Catacombs of Torment, where the Curator of Torment is fought (retail\'s portal destination). Hotel Swank portal room, Bosses.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) SELECT 900136, 2, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z FROM weenie_properties_position WHERE object_Id = 52012 AND position_Type = 2;
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662409, 900136, 25821688, 126.193, -50.007, -5.995, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900137 [Dungeons] Portal to Messenger's Sanctuary (0x00AE)
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900137, 'acportalmessengerssanctuary0x00ae', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900137, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900137, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900137, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900137, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900137, 1, 'Portal to Messenger\'s Sanctuary (0x00AE)'), (900137, 16, 'An unused dungeon: 1,701 cells, a large grid hall. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900137, 2, 11405211, 130, -60, 0.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662361, 900137, 25821440, 93.4792, -29.7542, -11.995, 0.714421, 0, 0, -0.699716, 0, NOW());
-- 900138 [Dungeons] Portal to Unused Dungeon 0x003F
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900138, 'acportalunuseddungeon0x003f', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900138, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900138, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900138, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900138, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900138, 1, 'Portal to Unused Dungeon 0x003F'), (900138, 16, 'An unused dungeon: 914 cells over 81 m of height. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900138, 2, 4129667, 130, -210, -5.995, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662362, 900138, 25821441, 93.154, -39.7525, -11.995, 0.682746, 0, 0, -0.730656, 0, NOW());
-- 900139 [Dungeons] Portal to Unused Dungeon 0x5950
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900139, 'acportalunuseddungeon0x5950', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900139, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900139, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900139, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900139, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900139, 1, 'Portal to Unused Dungeon 0x5950'), (900139, 16, 'An unused dungeon: 608 cells of halls and corridors. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900139, 2, 1498415559, 170, -90, -11.995, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662363, 900139, 25821442, 93.1449, -50.2271, -11.995, 0.714421, 0, 0, -0.699716, 0, NOW());
-- 900140 [Dungeons] Portal to Unused Dungeon 0x00BD
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900140, 'acportalunuseddungeon0x00bd', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900140, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900140, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900140, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900140, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900140, 1, 'Portal to Unused Dungeon 0x00BD'), (900140, 16, 'An unused dungeon: a diamond-shaped maze of 344 cells. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900140, 2, 12386735, 90, -110, 0.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662364, 900140, 25821443, 99.898, -23.064, -11.995, 1, 0, 0, 0, 0, NOW());
-- 900141 [Dungeons] Portal to the Tactical Arena (0x00E8)
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900141, 'acportalthetacticalarena0x00e8', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900141, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900141, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900141, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900141, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900141, 1, 'Portal to the Tactical Arena (0x00E8)'), (900141, 16, 'An unused dungeon: three separate arenas. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900141, 2, 15204693, 130, -160, 6.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662366, 900141, 25821455, 109.791, -23.1045, -11.995, 1, 0, 0, 0, 0, NOW());
-- 900142 [Dungeons] Portal to the Shadow Breach (0x008D)
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900142, 'acportaltheshadowbreach0x008d', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900142, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900142, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900142, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900142, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900142, 1, 'Portal to the Shadow Breach (0x008D)'), (900142, 16, 'An unused dungeon: one great hall with side passages. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900142, 2, 9240993, 40, -50, -29.995, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662368, 900142, 25821473, 120.073, -23.0238, -11.995, 1, 0, 0, 0, 0, NOW());
-- 900143 [Dungeons] Portal to Unused Dungeon 0x576B
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900143, 'acportalunuseddungeon0x576b', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900143, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900143, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900143, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900143, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900143, 1, 'Portal to Unused Dungeon 0x576B'), (900143, 16, 'An unused dungeon: an octagonal arena with eight alcoves. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900143, 2, 1466630440, 70, -50, 0.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662365, 900143, 25821452, 99.7103, -56.8842, -11.995, 1, 0, 0, 0, 0, NOW());
-- 900144 [Dungeons] Portal to Unused Dungeon 0x0069
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900144, 'acportalunuseddungeon0x0069', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900144, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900144, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900144, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900144, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900144, 1, 'Portal to Unused Dungeon 0x0069'), (900144, 16, 'An unused dungeon: a small two-level cave. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900144, 2, 6881598, 40, -40, -29.995, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662367, 900144, 25821463, 109.742, -56.9163, -11.995, 1, 0, 0, 0, 0, NOW());
-- 900145 [Dungeons] Portal to Unused Dungeon 0x008E
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900145, 'acportalunuseddungeon0x008e', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900145, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900145, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900145, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900145, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900145, 1, 'Portal to Unused Dungeon 0x008E'), (900145, 16, 'An unused dungeon: a small two-level cave (another layout from 0x0069). Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900145, 2, 9306430, 40, -40, -29.995, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662369, 900145, 25821483, 119.906, -56.9696, -11.995, 1, 0, 0, 0, 0, NOW());
-- 900146 [Dungeons] Portal to the Fowl Basement (0x5956)
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900146, 'acportalthefowlbasement0x5956', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900146, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900146, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900146, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900146, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900146, 1, 'Portal to the Fowl Basement (0x5956)'), (900146, 16, 'An unused dungeon: a small linear basement. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900146, 2, 1498808596, 80, -40, 0.005, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662374, 900146, 25821543, 126.969, -30.1143, -11.995, 0.707107, 0, 0, -0.707107, 0, NOW());
-- 900147 [Dungeons] Portal to a Spare Training Academy (0x9204)
INSERT INTO weenie (class_Id, class_Name, type) VALUES (900147, 'acportalasparetrainingacademy0x9204', 7);
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 900147, type, value FROM weenie_properties_int WHERE object_Id = 500010;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 900147, type, value FROM weenie_properties_bool WHERE object_Id = 500010;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 900147, type, value FROM weenie_properties_float WHERE object_Id = 500010;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 900147, type, value FROM weenie_properties_d_i_d WHERE object_Id = 500010;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (900147, 1, 'Portal to a Spare Training Academy (0x9204)'), (900147, 16, 'One of the 61 unused copies of the Training Academy layout. Empty: no monsters and no way out but recall. Hotel Swank portal room, Dungeons.');
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (900147, 2, 2449735996, 130, -140, -5.995, 1, 0, 0, 0);
INSERT INTO landblock_instance (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child, last_Modified) VALUES (1880662375, 900147, 25821544, 126.97, -40.2784, -11.995, 0.707107, 0, 0, -0.707107, 0, NOW());
COMMIT;
