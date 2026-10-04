-- NETHER RIFT, void spell 60001 (docs/NEW-ANIMATIONS.md, part 3). MADE, NOT ADDED: NOT APPLIED TO
-- THE LIVE ace_world. It does nothing on its own: the spell's base (school, level, mana, range,
-- components, flags) is not in the DAT, and RevivalGuard NetherRift.cs puts it in the loaded spell
-- table only while server property rg_nether_rift is true. Re-runnable (deletes its own row first).
--
--   staging only:  ssh gpubox sudo -n mysql ace_world_<copy> < tools/gpubox-ace/nether-rift.sql
--
-- The spell, in retail's spell-table shape (SpellBase, set by NetherRift.Inject from Nether Bolt VII 5355):
--   name        Nether Rift
--   school      Void Magic (5)                 level VII (Platinum Scarab), Power 310
--   mana        50                             (Nether Bolt VII 35, Nether Streak VII 70)
--   range       30 m + 0.2/level, like every void bolt VII (BaseRange 30, RangeMod 0.2)
--   components  Platinum Scarab (112), Indigo Taper (70), Soulweed (195), Bottled Rage (197),
--               Birch Talisman (55): words "Traku Bor", which no retail void spell uses. Birch is every
--               void bolt's talisman, so the cast gesture is retail's MagicRecoilMissile and ACE's cast
--               timing is unchanged; the windup is the Platinum Scarab's purple MagicPowerUp08.
--   target      a creature (NonComponentTarget 16), Resistable | PKSensitive | NotResearchable (0x83)
--   icon        0x06FF0600 (expansion/spells/nether_rift/icon), shown only by our client
--
-- The damage row below. A STRIKE (the projectile is born at the target; NetherRift.cs makes ACE's
-- GetProjectileSpellType answer Strike for 60001, as it does for categories 236-242): one nether
-- projectile, 150-240 nether damage (base 150 + variance 90). In line with retail void VII:
--   Nether Bolt VII 168-262 (avg 215), Incantation of Nether Bolt 252-325, Nether Streak VII 84-130.
-- Retail's war strikes out-hit their incantation bolts (Incendiary Strike 150-300 against
-- Incantation of Flame Bolt 142-204); this one is kept about 10% UNDER Nether Bolt VII instead, the
-- price of a bolt that cannot be side-stepped, since it is a scribe-level VII rather than a quest spell.
--
-- The projectile is retail's own Nether Bolt (weenie 43230, setup 0x02001A28), so a retail client
-- watching sees a nether bolt appear beside the target and strike it. create_Offset (0, 0.7, 0.5)
-- puts it, after ACE's own radius and 2/3-height offsets (WorldObject_Magic.CalculateProjectileOrigins),
-- about 1.5 m in front of the target toward the caster and 1.7 m up: where our client opens the tear.
START TRANSACTION;

DELETE FROM spell WHERE id = 60001;

INSERT INTO spell (id, name, e_Type, base_Intensity, variance, wcid, num_Projectiles, spread_Angle, non_Tracking,
                   create_Offset_Origin_X, create_Offset_Origin_Y, create_Offset_Origin_Z,
                   padding_Origin_X, padding_Origin_Y, padding_Origin_Z,
                   dims_Origin_X, dims_Origin_Y, dims_Origin_Z, last_Modified)
VALUES (60001, 'Nether Rift', 1024, 150, 90, 43230, 1, 0, 0,
        0, 0.7, 0.5,
        1, 1, 1,
        1, 1, 1, '2026-09-28 00:00:00');

COMMIT;

-- No scroll weenie, on purpose: an item carrying spell 60001 can be appraised by a retail client,
-- which would then be sent a spell id its own SpellTable does not have. How a player learns it is the
-- owner's call (docs/NEW-ANIMATIONS.md); the test path is the admin's @netherrift teach.
