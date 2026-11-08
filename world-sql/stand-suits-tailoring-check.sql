-- READ-ONLY. Per piece on the Staff Base stands and in the Wardrobe Chest: what could block it as a tailoring look.
-- retained (Bool 91), attuned (Int 114), reduced (ValidLocations vs weenie), our-art (anim parts or 0x06FF icon).
SELECT HEX(b.id), b.weenie_Class_Id,
  (SELECT value FROM ace_shard.biota_properties_string WHERE object_Id=b.id AND type=1) name,
  IFNULL((SELECT value+0 FROM ace_shard.biota_properties_bool WHERE object_Id=b.id AND type=91),0) retained,
  IFNULL((SELECT value FROM ace_shard.biota_properties_int WHERE object_Id=b.id AND type=114),0) attuned,
  HEX((SELECT value FROM ace_shard.biota_properties_int WHERE object_Id=b.id AND type=9)) valid_now,
  HEX((SELECT value FROM ace_world.weenie_properties_int WHERE object_Id=b.weenie_Class_Id AND type=9)) valid_weenie,
  (SELECT COUNT(*) FROM ace_world.weenie_properties_anim_part WHERE object_Id=b.weenie_Class_Id) anim_parts,
  HEX((SELECT value FROM ace_shard.biota_properties_d_i_d WHERE object_Id=b.id AND type=8)) icon
FROM ace_shard.biota b JOIN ace_shard.biota_properties_i_i_d c ON c.object_Id=b.id AND c.type=2
WHERE c.value IN (0x80004DB3, 0x80004E02, 0x80007B51, 0x80007B52, 0x80007B53, 0x80007B54, 0x80007B55, 0x80007B56, 0x80007B57, 0x80007B58, 0x80007B59, 0x80007B5A, 0x7576B170) ORDER BY c.value, b.id;
