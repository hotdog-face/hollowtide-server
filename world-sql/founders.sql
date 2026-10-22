-- THE FOUNDERS' GIFTS (owner, 2026-10-11: "one time only bragging rights that only people who were early supporters get
-- and they can keep for life ... 'i was there' when the game started"). Given once per account, to every account made
-- by 31 October 2026, by RevivalGuard Founders.cs, with the title "Founder" (ACE CharacterTitle.HollowtideFounder).
--
--   901913  Founder's Shirt   retail's anniversary tradition (the Night Club Shirt, ace69980, inscribed for each
--                             year's party), in dark-yellow dye: a gold one exists nowhere else
--   901914  Founder's Banner  the wall-hook Falcon Banner (23626), for a house
--
-- Both Bonded (int 33 = 1: never lost on death). The shirt is also Attuned (int 114 = 1: cannot be given, traded,
-- sold or dropped). THE BANNER IS NOT ATTUNED (player report 261021-0046, 2026-10-21: "will not go on my wall hooks"):
-- ACE refuses any attuned item from a pack onto a hook (Player_Inventory.cs, the "target on the landscape" check
-- returns WeenieError.AttunedItem before the hook rules are read), so an attuned banner can never be hung, which is all
-- a banner is for. Its other properties are the Falcon Banner's (HookType 2 wall, ItemType 1024, HookItemType -1 on the
-- hooks) and were never the problem. Existing banners: tools/public-shard/datafixes/2026-10-21-founders-banner-hookable.sql.
-- Idempotent: the two ids are removed and written again.
DELETE FROM ace_world.weenie_properties_int WHERE object_Id IN (901913, 901914);
DELETE FROM ace_world.weenie_properties_float WHERE object_Id IN (901913, 901914);
DELETE FROM ace_world.weenie_properties_d_i_d WHERE object_Id IN (901913, 901914);
DELETE FROM ace_world.weenie_properties_bool WHERE object_Id IN (901913, 901914);
DELETE FROM ace_world.weenie_properties_string WHERE object_Id IN (901913, 901914);
DELETE FROM ace_world.weenie WHERE class_Id IN (901913, 901914);

-- ---------------------------------------------------------------- Founder's Shirt (from 69980)
INSERT INTO ace_world.weenie (class_Id, class_Name, type, last_Modified) VALUES (901913, 'ace901913-foundersshirt', 2, NOW());
INSERT INTO ace_world.weenie_properties_int (object_Id, type, value)
  SELECT 901913, type, value FROM ace_world.weenie_properties_int WHERE object_Id = 69980 AND type NOT IN (3, 19);
INSERT INTO ace_world.weenie_properties_int (object_Id, type, value) VALUES
  (901913, 3, 86),      -- PaletteTemplate: DyeDarkYellow (gold); the shirt's clothing base offers it, no other shirt uses it
  (901913, 19, 0),      -- Value: priceless, not for sale
  (901913, 33, 1),      -- Bonded
  (901913, 114, 1);     -- Attuned
INSERT INTO ace_world.weenie_properties_float (object_Id, type, value)
  SELECT 901913, type, value FROM ace_world.weenie_properties_float WHERE object_Id = 69980 AND type <> 12;
INSERT INTO ace_world.weenie_properties_float (object_Id, type, value) VALUES
  (901913, 12, 1.0);    -- Shade: the bright end of the dye (the Night Club shirt's 0 is its darkest)
INSERT INTO ace_world.weenie_properties_d_i_d (object_Id, type, value)
  SELECT 901913, type, value FROM ace_world.weenie_properties_d_i_d WHERE object_Id = 69980;
INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value)
  SELECT 901913, type, value FROM ace_world.weenie_properties_bool WHERE object_Id = 69980;
INSERT INTO ace_world.weenie_properties_string (object_Id, type, value) VALUES
  (901913, 1, 'Founder''s Shirt'),
  (901913, 7, 'I was there when Hollowtide began, in the autumn of 2026.'),
  (901913, 8, 'Hollowtide'),
  (901913, 16, 'A shirt of gold given to the founders of Hollowtide: the adventurers who were in Dereth before the end of October 2026, when the revival began. It cannot be given away, and it is never lost.');

-- ---------------------------------------------------------------- Founder's Banner (from 23626)
INSERT INTO ace_world.weenie (class_Id, class_Name, type, last_Modified) VALUES (901914, 'ace901914-foundersbanner', 1, NOW());
INSERT INTO ace_world.weenie_properties_int (object_Id, type, value)
  SELECT 901914, type, value FROM ace_world.weenie_properties_int WHERE object_Id = 23626;
INSERT INTO ace_world.weenie_properties_int (object_Id, type, value) VALUES
  (901914, 33, 1);      -- Bonded. NOT Attuned (114): ACE will not hang an attuned item on a hook (see the top).
INSERT INTO ace_world.weenie_properties_d_i_d (object_Id, type, value)
  SELECT 901914, type, value FROM ace_world.weenie_properties_d_i_d WHERE object_Id = 23626;
INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value)
  SELECT 901914, type, value FROM ace_world.weenie_properties_bool WHERE object_Id = 23626;
INSERT INTO ace_world.weenie_properties_string (object_Id, type, value) VALUES
  (901914, 1, 'Founder''s Banner'),
  (901914, 14, 'This item can be used on wall hooks.'),
  (901914, 16, 'A banner for the hall of a Hollowtide founder: one of the adventurers who were in Dereth before the end of October 2026, when the revival began. It is never lost.');
