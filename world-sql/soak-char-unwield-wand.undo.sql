-- BOX TEST SHARD ONLY: put the Ribbon Wand back in M Five Fifteen's hand (undoes soak-char-unwield-wand.sql)
START TRANSACTION;
UPDATE ace_shard.biota_properties_i_i_d SET type=3 WHERE object_Id=2147492500 AND type=2 AND value=1342177432;
INSERT INTO ace_shard.biota_properties_int (object_Id,type,value) VALUES (2147492500,10,16777216);
COMMIT;
