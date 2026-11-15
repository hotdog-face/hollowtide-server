-- RIFTWATCHER YSANNE SELLS CORROSION VII AND CORRUPTION VII (owner 2026-11-14). Retail had both scrolls (43284 Scroll of
-- Corrosion VII, 43292 Scroll of Corruption VII) but nothing sold or dropped them. Ysanne (901420, nether-rift-trainer.sql)
-- becomes a vendor with the Grand Master Scrivener of Void Magic's (43353) shop terms: SellPrice 50, so each scroll (value
-- 2,000, retail's level-7 value) costs 100,000 pyreals, the next step on the scriveners' ladder (level 6: 1,000 x 50 =
-- 50,000). ACE vendors cannot gate a sale on a skill: RevivalGuard VoidScrollGate.cs refuses the purchase without Void
-- Magic trained, and runs her Nether Rift lesson beside the shop (ACE's Vendor.ActOnUse replaces Creature's). NEEDS that
-- RevivalGuard: with an older mod anyone could buy them and her lesson is silent.
-- Apply: tools/public-shard/apply-live-ovh.sh tools/gpubox-ace/ysanne-void-scrolls.sql 3032 (her landblock: the type
-- change needs her respawned). Undo: ysanne-void-scrolls.undo.sql (same landblock). DML only, re-runnable.
UPDATE ace_world.weenie SET type = 12 WHERE class_Id = 901420;   -- Vendor (was 10, Creature)
DELETE FROM ace_world.weenie_properties_int WHERE object_Id = 901420 AND type IN (74, 75, 76, 126, 127);
INSERT INTO ace_world.weenie_properties_int (object_Id, type, value)
  SELECT 901420, type, value FROM ace_world.weenie_properties_int WHERE object_Id = 43353 AND type IN (74, 75, 76, 126, 127)
  AND EXISTS (SELECT 1 FROM ace_world.weenie WHERE class_Id = 901420);   -- MerchandiseItemTypes/Min/MaxValue, VendorHappyMean/Variance
DELETE FROM ace_world.weenie_properties_float WHERE object_Id = 901420 AND type IN (37, 38);
INSERT INTO ace_world.weenie_properties_float (object_Id, type, value) SELECT 901420, 37, 0.5 FROM DUAL
  WHERE EXISTS (SELECT 1 FROM ace_world.weenie WHERE class_Id = 901420);   -- BuyPrice, as the scriveners
INSERT INTO ace_world.weenie_properties_float (object_Id, type, value) SELECT 901420, 38, 50 FROM DUAL
  WHERE EXISTS (SELECT 1 FROM ace_world.weenie WHERE class_Id = 901420);   -- SellPrice, as the scriveners
DELETE FROM ace_world.weenie_properties_bool WHERE object_Id = 901420 AND type IN (39, 52);
INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value) SELECT 901420, 39, 1 FROM DUAL
  WHERE EXISTS (SELECT 1 FROM ace_world.weenie WHERE class_Id = 901420);   -- DealMagicalItems
INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value) SELECT 901420, 52, 1 FROM DUAL
  WHERE EXISTS (SELECT 1 FROM ace_world.weenie WHERE class_Id = 901420);   -- AiImmobile: a shopkeeper stays at her post
DELETE FROM ace_world.weenie_properties_create_list WHERE object_Id = 901420 AND destination_Type = 4 AND weenie_Class_Id IN (43284, 43292);
INSERT INTO ace_world.weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
  SELECT 901420, 4, i.class_Id, -1, 0, 0, 0 FROM ace_world.weenie i WHERE i.class_Id IN (43284, 43292)
  AND EXISTS (SELECT 1 FROM ace_world.weenie WHERE class_Id = 901420) ORDER BY i.class_Id;
