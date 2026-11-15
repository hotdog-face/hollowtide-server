-- INERT: content review, not applied anywhere. Content review batch 1, quest line (docs/content-review/quest-line.md).

-- Quest items and props, weenies 920200, 920201, 920202, 920203, 920204, 920205, 920206, 920207, 920208, 920209, 920210. docs/content-review/quest-line/props.md has the renders,
-- what each one serves and the ids. Defines weenies only: no placement (batch1-quest-placement-PROPOSED.sql proposes
-- it), no generator, no NPC emote. Re-runnable: it deletes its own rows first (every property, book and position
-- table cascades from weenie). Undo: batch1-quest-props.undo.sql.
-- Written by tools/gpubox-ace/content-review/gen_batch1_quest_props.py; edit that and re-run it.
START TRANSACTION;

DELETE FROM weenie WHERE class_Id IN (920200, 920201, 920202, 920203, 920204, 920205, 920206, 920207, 920208, 920209, 920210);

-- ===== 920200: Stitched Mask, quest drop (step 1: bring Ilsira five). Base 43742 'Snow Tusker Blood Sample', a retail stackable
-- (WeenieType 51) quest drop. MaxStackSize 5, the number the step asks for. Setup 0x0200095B, the retail Virindi Mask's
-- (a mask-sized item on the ground; the look without expansion/ is a Virindi mask). DefaultScale (float 39, 0.5 on the
-- base) dropped. Drops from the Strung Drudge (920001, batch1-quest-monsters.sql) via its create list: that is the
-- monster file's to add, not this one.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920200, 'ace920200-stitchedmask', type FROM weenie WHERE class_Id = 43742;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920200, type, value FROM weenie_properties_int WHERE object_Id = 43742;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920200, type, value FROM weenie_properties_bool WHERE object_Id = 43742;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920200, type, value FROM weenie_properties_float WHERE object_Id = 43742;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920200, type, value FROM weenie_properties_string WHERE object_Id = 43742;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920200, type, value FROM weenie_properties_d_i_d WHERE object_Id = 43742;
DELETE FROM weenie_properties_string WHERE object_Id = 920200 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920200, 1, 'Stitched Mask');
DELETE FROM weenie_properties_string WHERE object_Id = 920200 AND type = 15;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920200, 15, 'A Virindi mask sewn onto a scrap of dark hide with coarse thread, its strings cut.');
DELETE FROM weenie_properties_string WHERE object_Id = 920200 AND type = 16;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920200, 16, 'The face is a Virindi''s, or made to look like one. Someone sewed it onto this hide with a careful, even stitch, pinned it in three places, and tied strings to it. The strings have been cut. The Exploration Society in Ayan Baqur is asking for these.');
DELETE FROM weenie_properties_int WHERE object_Id = 920200 AND type = 11;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (920200, 11, 5);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920200 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920200, 1, 33556827);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920200 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920200, 8, 117383680);
DELETE FROM weenie_properties_float WHERE object_Id = 920200 AND type IN (39);

-- ===== 920201: Tension Pin, quest item (step 2: three, from Tusker Marionettes). Same base (43742, stackable), MaxStackSize 3.
-- Setup 0x02000181, the retail armoredillo spine's (a long thin pick-up; base already uses it). The three are handed to
-- Tama Shoen, whose emote takes them and gives 920202 (the NPC file's job; see props.md, "How the pins become the key").
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920201, 'ace920201-tensionpin', type FROM weenie WHERE class_Id = 43742;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920201, type, value FROM weenie_properties_int WHERE object_Id = 43742;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920201, type, value FROM weenie_properties_bool WHERE object_Id = 43742;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920201, type, value FROM weenie_properties_float WHERE object_Id = 43742;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920201, type, value FROM weenie_properties_string WHERE object_Id = 43742;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920201, type, value FROM weenie_properties_d_i_d WHERE object_Id = 43742;
DELETE FROM weenie_properties_string WHERE object_Id = 920201 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920201, 1, 'Tension Pin');
DELETE FROM weenie_properties_string WHERE object_Id = 920201 AND type = 15;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920201, 15, 'A brass pin with a spindle head, wound with pale thread. A cut cord is still tied to it.');
DELETE FROM weenie_properties_string WHERE object_Id = 920201 AND type = 16;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920201, 16, 'A loom''s tension pin, pulled from the back of a strung tusker. The thread wound on its head is fine and pale and very strong, and it was pulled taut enough to sing. Tama Shoen in Ayan Baqur reads threads.');
DELETE FROM weenie_properties_int WHERE object_Id = 920201 AND type = 11;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (920201, 11, 3);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920201 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920201, 8, 117383681);
DELETE FROM weenie_properties_float WHERE object_Id = 920201 AND type IN (39);

-- ===== 920202: Loom-Warden's Key (step 3). Base 28772 'Torture Chamber Key', a retail Key (WeenieType 22). KeyCode
-- 'hollowweaverstitchworks' (so a locked door in the Stitchworks could take it later). Structure / MaxStructure (91/92,
-- three uses on the base) dropped: the key is shown, not spent. Setup kept (0x02001276, a key on the ground).
-- It opens the lair portal (920209) the retail permission way: shown to Ser Orrin Vasco at the camp (a Refuse emote,
-- so he hands it back) he stamps HollowWeaverKeyTurned, the portal's QuestRestriction. See props.md.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920202, 'ace920202-loomwardenskey', type FROM weenie WHERE class_Id = 28772;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920202, type, value FROM weenie_properties_int WHERE object_Id = 28772;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920202, type, value FROM weenie_properties_bool WHERE object_Id = 28772;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920202, type, value FROM weenie_properties_float WHERE object_Id = 28772;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920202, type, value FROM weenie_properties_string WHERE object_Id = 28772;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920202, type, value FROM weenie_properties_d_i_d WHERE object_Id = 28772;
DELETE FROM weenie_properties_string WHERE object_Id = 920202 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920202, 1, 'Loom-Warden''s Key');
DELETE FROM weenie_properties_string WHERE object_Id = 920202 AND type = 13;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920202, 13, 'hollowweaverstitchworks');
DELETE FROM weenie_properties_string WHERE object_Id = 920202 AND type = 16;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920202, 16, 'Three tension pins bound together in stitched leather, their points splayed into wards, with a Virindi face set in the bow. Show it to the warden at the Exploration Society camp east of Ayan Baqur.');
DELETE FROM weenie_properties_int WHERE object_Id = 920202 AND type IN (91, 92);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920202 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920202, 8, 117383682);

-- ===== 920203: Heartstring, relic (step 4 drop, laid on the altar in step 5). Base 30506 'Mad Star Marionette's Head', a retail
-- quest relic taken off a marionette (Generic). Its Quest string (33), stamped on pick-up, becomes HollowWeaverGutbrace.
-- Palette and ClothingBase (d_i_d 6, 7) dropped: the glb paints itself. Setup 0x02000CCF, the Spool of Silk's.
-- Drops from Gutbrace the Strung (batch1-quest-monsters.sql's create list).
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920203, 'ace920203-heartstring', type FROM weenie WHERE class_Id = 30506;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920203, type, value FROM weenie_properties_int WHERE object_Id = 30506;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920203, type, value FROM weenie_properties_bool WHERE object_Id = 30506;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920203, type, value FROM weenie_properties_float WHERE object_Id = 30506;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920203, type, value FROM weenie_properties_string WHERE object_Id = 30506;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920203, type, value FROM weenie_properties_d_i_d WHERE object_Id = 30506;
DELETE FROM weenie_properties_string WHERE object_Id = 920203 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920203, 1, 'Heartstring');
DELETE FROM weenie_properties_string WHERE object_Id = 920203 AND type = 16;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920203, 16, 'The string that held Gutbrace up, wound into a knot the shape of a heart about a brass spindle. It is warm, and it still twitches when nothing is touching it. It belongs on the Altar of Cut Strings.');
DELETE FROM weenie_properties_string WHERE object_Id = 920203 AND type = 33;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920203, 33, 'HollowWeaverGutbrace');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920203 AND type IN (6, 7);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920203 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920203, 1, 33557711);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920203 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920203, 8, 117383683);

-- ===== 920204: Silence Bell, world object x3 in the lair (step 3: ring all three). A Book, cloned from 5108 'trainlifestonesign' like
-- the Arwic memorial (docs/EXPANSION-ITEMS.md section 10). Setup 0x02000BD7, the mine head's: one cylinder r 0.83 m,
-- h 2.94 m, which holds the 1.76 m wide, 2.83 m tall gallows. Ringing it is an emote (Use: stamp HollowWeaverBells
-- once per bell, by a per-bell flag) that this file does not write: see props.md, "Server work".
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920204, 'ace920204-silencebell', type FROM weenie WHERE class_Id = 5108;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920204, type, value FROM weenie_properties_int WHERE object_Id = 5108;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920204, type, value FROM weenie_properties_bool WHERE object_Id = 5108;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920204, type, value FROM weenie_properties_float WHERE object_Id = 5108;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920204, type, value FROM weenie_properties_string WHERE object_Id = 5108;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920204, type, value FROM weenie_properties_d_i_d WHERE object_Id = 5108;
DELETE FROM weenie_properties_string WHERE object_Id = 920204 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920204, 1, 'Silence Bell');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920204 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920204, 1, 33557463);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920204 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920204, 8, 117383684);
DELETE FROM weenie_properties_string WHERE object_Id = 920204 AND type = 14;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920204, 14, 'Use this to cut the bindings and ring the bell.');
INSERT INTO weenie_properties_book (object_Id, max_Num_Pages, max_Num_Chars_Per_Page) VALUES (920204, 1, 1000);
INSERT INTO weenie_properties_book_page_data (object_Id, page_Id, author_Id, author_Name, author_Account, ignore_Author, page_Text) VALUES (920204, 0, 0, 'Silence Bell', 'prewritten', 0, '
The bell has been muffled: a band of hide sewn tight round its waist, the clapper bound in thread, a Virindi face stitched to its shoulder.

While it hangs silent the strings in this place stay taut. Cut the bindings and ring it.
');

-- ===== 920205: Altar of Cut Strings, world object, the Stitchworks' centre room (step 5: lay the Heartstring on it and the Hollow
-- Weaver comes down). Setup 0x02001762, the retail Altar's own (36691): its two cylinders (r 0.5 m, h 1.0 m at x
-- +-0.49) are the stone the model draws (the stone IS the retail gfxobj 0x01000BB7); the brass uprights stand inside
-- the slab's footprint. Laying the Heartstring is a Give / UseWith emote (take 920203, spawn 920010): props.md.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920205, 'ace920205-altarofcutstrings', type FROM weenie WHERE class_Id = 5108;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920205, type, value FROM weenie_properties_int WHERE object_Id = 5108;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920205, type, value FROM weenie_properties_bool WHERE object_Id = 5108;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920205, type, value FROM weenie_properties_float WHERE object_Id = 5108;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920205, type, value FROM weenie_properties_string WHERE object_Id = 5108;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920205, type, value FROM weenie_properties_d_i_d WHERE object_Id = 5108;
DELETE FROM weenie_properties_string WHERE object_Id = 920205 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920205, 1, 'Altar of Cut Strings');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920205 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920205, 1, 33560418);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920205 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920205, 8, 117383685);
DELETE FROM weenie_properties_string WHERE object_Id = 920205 AND type = 14;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920205, 14, 'Lay the Heartstring in the cradle.');
INSERT INTO weenie_properties_book (object_Id, max_Num_Pages, max_Num_Chars_Per_Page) VALUES (920205, 1, 1000);
INSERT INTO weenie_properties_book_page_data (object_Id, page_Id, author_Id, author_Name, author_Account, ignore_Author, page_Text) VALUES (920205, 0, 0, 'Altar of Cut Strings', 'prewritten', 0, '
A stone altar strung like a loom. Most of the strings have been cut, and the cut ends still move.

In the middle is a shallow cradle of stitched hide, empty, the size of a heart.
');

-- ===== 920206: Mounted Weaver's Crossbar, boss trophy (step 5: on the Hollow Weaver's corpse). The trophy-mount pattern, cloned from
-- the Burun Idol 27525 exactly as tools/gpubox-ace/trophy-mounts.sql builds 900060-900063: a Generic wall-hook item,
-- ItemType Misc, HookType wall, HookPlacement 103, Setup 0x02000B0E (the Plaque, no volume). expansion/models/names.json
-- maps the name to 920206 (a hooked item reaches the client as the HOOK). RevivalGuard TrophyMounts puts it in a
-- player-killed boss's corpse with "Slain by <name>.": adding 920010 -> 920206 to its table is server work (props.md).
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920206, 'ace920206-trophyweaverscrossbar', type FROM weenie WHERE class_Id = 27525;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920206, type, value FROM weenie_properties_int WHERE object_Id = 27525;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920206, type, value FROM weenie_properties_bool WHERE object_Id = 27525;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920206, type, value FROM weenie_properties_float WHERE object_Id = 27525;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920206, type, value FROM weenie_properties_string WHERE object_Id = 27525;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920206, type, value FROM weenie_properties_d_i_d WHERE object_Id = 27525;
DELETE FROM weenie_properties_string WHERE object_Id = 920206 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920206, 1, 'Mounted Weaver''s Crossbar');
DELETE FROM weenie_properties_string WHERE object_Id = 920206 AND type = 16;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920206, 16, 'The Hollow Weaver''s own control, mounted on a board. Its strings have all been cut.');
DELETE FROM weenie_properties_string WHERE object_Id = 920206 AND type = 14;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920206, 14, 'This item can be hung on a wall hook.');
DELETE FROM weenie_properties_int WHERE object_Id = 920206 AND type IN (1, 5, 8, 9, 16, 19, 93, 150, 151);
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (920206, 1, 128), (920206, 5, 150), (920206, 8, 150), (920206, 9, 0), (920206, 16, 1), (920206, 19, 250), (920206, 93, 1044), (920206, 150, 103), (920206, 151, 2);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920206 AND type IN (1, 3, 8, 22);
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920206, 1, 33557262), (920206, 3, 536870932), (920206, 8, 117383686), (920206, 22, 872415275);

-- ===== 920207: Puppet Loom, dungeon dressing (corridors and Gutbrace's inner loom). Setup 0x02000BD7 (cylinder r 0.83, h 2.94):
-- the frame is 2.1 m wide, so its outer posts stand a little outside the cylinder; the works and the puppet are inside.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920207, 'ace920207-puppetloom', type FROM weenie WHERE class_Id = 5108;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920207, type, value FROM weenie_properties_int WHERE object_Id = 5108;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920207, type, value FROM weenie_properties_bool WHERE object_Id = 5108;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920207, type, value FROM weenie_properties_float WHERE object_Id = 5108;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920207, type, value FROM weenie_properties_string WHERE object_Id = 5108;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920207, type, value FROM weenie_properties_d_i_d WHERE object_Id = 5108;
DELETE FROM weenie_properties_string WHERE object_Id = 920207 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920207, 1, 'Puppet Loom');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920207 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920207, 1, 33557463);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920207 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920207, 8, 117383687);
INSERT INTO weenie_properties_book (object_Id, max_Num_Pages, max_Num_Chars_Per_Page) VALUES (920207, 1, 1000);
INSERT INTO weenie_properties_book_page_data (object_Id, page_Id, author_Id, author_Name, author_Account, ignore_Author, page_Text) VALUES (920207, 0, 0, 'Puppet Loom', 'prewritten', 0, '
A loom built for something other than cloth. The warp is pale thread, finer than silk, and on the beam a half-made figure hangs by its wrists, waiting for a face.
');

-- ===== 920208: Strung Puppet (hanging), dungeon dressing. Setup 0x02000B0E, the retail Plaque's, which has NO volume: it hangs
-- over the corridor and nothing should bump it. Its cord runs to 4.85 m; the Stitchworks' corridors are 5 m high.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920208, 'ace920208-strungpuppet', type FROM weenie WHERE class_Id = 5108;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920208, type, value FROM weenie_properties_int WHERE object_Id = 5108;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920208, type, value FROM weenie_properties_bool WHERE object_Id = 5108;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920208, type, value FROM weenie_properties_float WHERE object_Id = 5108;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920208, type, value FROM weenie_properties_string WHERE object_Id = 5108;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920208, type, value FROM weenie_properties_d_i_d WHERE object_Id = 5108;
DELETE FROM weenie_properties_string WHERE object_Id = 920208 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920208, 1, 'Strung Puppet');
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920208 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920208, 1, 33557262);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920208 AND type = 8;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920208, 8, 117383688);
INSERT INTO weenie_properties_book (object_Id, max_Num_Pages, max_Num_Chars_Per_Page) VALUES (920208, 1, 1000);
INSERT INTO weenie_properties_book_page_data (object_Id, page_Id, author_Id, author_Name, author_Account, ignore_Author, page_Text) VALUES (920208, 0, 0, 'Strung Puppet', 'prewritten', 0, '
A marionette hangs from a cord in the dark, a Virindi face sewn over its own. One of its strings has been cut, and its right hand has dropped.
');

-- ===== 920209: Stitchworks Portal, sealed, keyed to the Loom-Warden's Key (920202). Cloned from 2095 'Witshire Dungeon Portal',
-- the retail permission-gated portal: QuestRestriction (string 37) = HollowWeaverKeyTurned, the flag Ser Orrin stamps
-- when shown the key. MinLevel 70 (the step's level). Setup 0x020001B3, the retail purple portal (Gaerlan's Inner
-- Sanctum, 21422, wears it; the base's 0x020005D2 is a lesser swirl). No model of ours: the retail portal look.
-- Destination: the Stitchworks' raised north hall, cell 0x00BD0242 (env 0x0D0001AD, five exits; the only z 6 room with
-- a stair cell, 0x00BD0257, above it), 3 m south of the room's centre stab, facing south into the maze.
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920209, 'ace920209-portalstitchworks', type FROM weenie WHERE class_Id = 2095;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920209, type, value FROM weenie_properties_int WHERE object_Id = 2095;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920209, type, value FROM weenie_properties_bool WHERE object_Id = 2095;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920209, type, value FROM weenie_properties_float WHERE object_Id = 2095;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920209, type, value FROM weenie_properties_string WHERE object_Id = 2095;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920209, type, value FROM weenie_properties_d_i_d WHERE object_Id = 2095;
DELETE FROM weenie_properties_string WHERE object_Id = 920209 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920209, 1, 'Stitchworks Portal');
DELETE FROM weenie_properties_string WHERE object_Id = 920209 AND type IN (37);
DELETE FROM weenie_properties_string WHERE object_Id = 920209 AND type = 37;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920209, 37, 'HollowWeaverKeyTurned');
DELETE FROM weenie_properties_int WHERE object_Id = 920209 AND type IN (86);
DELETE FROM weenie_properties_int WHERE object_Id = 920209 AND type = 86;
INSERT INTO weenie_properties_int (object_Id, type, value) VALUES (920209, 86, 70);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920209 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920209, 1, 33554867);
DELETE FROM weenie_properties_position WHERE object_Id = 920209 AND position_Type = 2;
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (920209, 2, 12386882, 80.0, -13.0, 6.005, 0.0, 0, 0, 1.0);

-- ===== 920210: Stitchworks Exit, the way back to the camp (the lair has none: 0x00BD has no exits and nothing placed).
-- Same clone, no restriction. Destination: the PROPOSED Society camp east of Ayan Baqur (0x1233, see the placement file;
-- terrain there read at z 12.0 off extracted/world/landblocks/1233, to be checked in game before anything is placed).
INSERT INTO weenie (class_Id, class_Name, type) SELECT 920210, 'ace920210-portalstitchworksexit', type FROM weenie WHERE class_Id = 2095;
INSERT INTO weenie_properties_int (object_Id, type, value) SELECT 920210, type, value FROM weenie_properties_int WHERE object_Id = 2095;
INSERT INTO weenie_properties_bool (object_Id, type, value) SELECT 920210, type, value FROM weenie_properties_bool WHERE object_Id = 2095;
INSERT INTO weenie_properties_float (object_Id, type, value) SELECT 920210, type, value FROM weenie_properties_float WHERE object_Id = 2095;
INSERT INTO weenie_properties_string (object_Id, type, value) SELECT 920210, type, value FROM weenie_properties_string WHERE object_Id = 2095;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) SELECT 920210, type, value FROM weenie_properties_d_i_d WHERE object_Id = 2095;
DELETE FROM weenie_properties_string WHERE object_Id = 920210 AND type = 1;
INSERT INTO weenie_properties_string (object_Id, type, value) VALUES (920210, 1, 'Stitchworks Exit');
DELETE FROM weenie_properties_string WHERE object_Id = 920210 AND type IN (37);
DELETE FROM weenie_properties_int WHERE object_Id = 920210 AND type IN (86);
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 920210 AND type = 1;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (920210, 1, 33554867);
DELETE FROM weenie_properties_position WHERE object_Id = 920210 AND position_Type = 2;
INSERT INTO weenie_properties_position (object_Id, position_Type, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z) VALUES (920210, 2, 305332224, 60.0, 132.0, 12.0, 1.0, 0, 0, 0.0);

COMMIT;
