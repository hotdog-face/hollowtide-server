-- Remove Tumble, the Jester's Apprentice (wcid 901660) from the world (owner 2026-10-20: "remove tumble the jesters
-- apprentace"; new NPCs need his approval). DML only, re-runnable. The weenie row stays (inert, not placed).
DELETE FROM ace_world.landblock_instance WHERE guid = 2056998752 OR weenie_Class_Id = 901660;
