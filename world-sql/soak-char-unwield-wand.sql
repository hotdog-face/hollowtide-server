-- BOX TEST SHARD ONLY (gpubox). Never run on OVH.
-- The release soak logs in as M Five Fifteen (devbot15, 0x50000098). That character is wielding the
-- prototype Reveler's Ribbon Wand (922151, biota 0x80002294) whose model is only on an unmerged branch,
-- so the soak logs "server's tree has no model 0x01FF4301" and the 0.1.97 release refuses.
-- This moves the wand from its hand into its pack. Run with the character offline:
--   ssh gpubox 'sudo -n mysql -N' < tools/gpubox-ace/soak-char-unwield-wand.sql
-- Undo: tools/gpubox-ace/soak-char-unwield-wand.undo.sql
START TRANSACTION;
UPDATE ace_shard.biota_properties_i_i_d SET type=2 WHERE object_Id=2147492500 AND type=3 AND value=1342177432;
DELETE FROM ace_shard.biota_properties_int WHERE object_Id=2147492500 AND type=10;
COMMIT;
SELECT 'iid', object_Id, type, value FROM ace_shard.biota_properties_i_i_d WHERE object_Id=2147492500;
SELECT 'int', type, value FROM ace_shard.biota_properties_int WHERE object_Id=2147492500 AND type IN (9,10);
