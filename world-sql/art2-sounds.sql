-- Art pass 2 (owner playtest 2026-09-29): creature voices.
--
-- "the shrike birds are roaring, fix their sound": the Crag Shrike (901256) and Snowcrest Shrike
-- (901257) were cloned from the Ash/Sable Gromnie and kept its sound table 0x20000009, a beast's
-- roar. They get the Siraluun table 0x2000007A instead: the retail wiki calls the Siraluun
-- "omnivorous native fowl", "the larger versions ... capable of cracking the skulls of Carenzi
-- pouchlings", which is the Crag Shrike's niche. It carries every sound type the Gromnie motion
-- table's hooks name (Attack1, Death1, Speak1, Swoosh1, Walk1, Wound1-3) plus Attack2, Scratch1,
-- Smash1, Thump1, Knockdown3.
--
-- "a sister died and she groaned like a man": the Blood-Sister (901300) and the Deru-Bound Keeper
-- (901340, also a Falatacot sister) kept the Falatacot Consort's table 0x20000016, the male
-- Falatacot/Tumerok voice. They get 0x20000002, the human FEMALE table: ace_shard's own female
-- player (Gender 2) carries it, the male player 0x20000001, and ace_world gives it to the female
-- NPCs (Aaminah, Adara al-Rajin, Aethelswith ...). It has Death1, Wound1-3, Attack1 and Swoosh1-3.
--
-- PropertyDataId.SoundTable = 3. Apply with apply-live.sh (clears the weenie cache); creatures
-- already spawned keep the old table until they respawn or their landblock reloads.
UPDATE weenie_properties_d_i_d SET value = 0x2000007A WHERE object_Id IN (901256, 901257) AND type = 3;
UPDATE weenie_properties_d_i_d SET value = 0x20000002 WHERE object_Id IN (901300, 901340) AND type = 3;
