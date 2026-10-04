-- Old Snapjaw's Skull (900710) moves from part 16 to part 22 (owner 2026-09-28: "its better but still
-- odd. Make the character face show below."). Part 16 IS the head, so a mesh there replaces the
-- wearer's face and hair; part 22 is the empty attachment in the head's frame that retail's crowns use,
-- so the new cap (expansion/seasonal/build_snapjaw.py) sits on the wearer's own head.
-- ACE's ApplyOwnAnimParts only swaps a part the item's ClothingBase already changes, so the weenie also
-- takes the Circlet's ClothingBase 0x1000063D (part 22 on every heritage the Skull Mask's table covers).
-- Everything else stays the Skull Mask's (head slot, armour level). Same change in gen_seasonal_costumes.py.
-- World only: items already created keep their own copies (tools/gpubox-ace/snapjaw-part22-instances.sql).
UPDATE weenie_properties_anim_part SET `index` = 22 WHERE object_Id = 900710 AND `index` = 16 AND animation_Id = 33490704;
DELETE FROM weenie_properties_d_i_d WHERE object_Id = 900710 AND type = 7;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (900710, 7, 268437053);  -- ClothingBase 0x1000063D (Circlet)
