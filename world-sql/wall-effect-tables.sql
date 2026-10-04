-- Shockwave Wall (7281) and Whirling Blade Wall (7282) projectiles: restore their retail
-- PhysicsEffectTable (DID 22). ACE's data has none, so the Launch and Explode PlayScripts the
-- server sends had nothing to resolve against and the walls flew without particles.
-- Evidence, 2017 retail captures (AC-Mac-Revival/Conformance/pcaps, 1485671265): the CreateObject
-- PhysicsDesc reads PeTable, then Setup, beside the same SoundTable ACE has:
--   setup 0x020003FA, stable 0x2000003B -> petable 0x34000009 (7281, 36 creates)
--   setup 0x020003FC, stable 0x2000003C -> petable 0x34000008 (7282, 45 creates)
-- The ring projectiles (7269-7275) were checked the same way: retail sent them with no PeTable,
-- so their plain flight is retail. 7278 and 7307 have no capture evidence and are left alone.
-- Spell sweep 2026-09-29, docs/SPELL-SWEEP-2026-09-29.md section 6.
DELETE FROM weenie_properties_d_i_d WHERE object_Id IN (7281, 7282) AND type = 22;
INSERT INTO weenie_properties_d_i_d (object_Id, type, value) VALUES (7281, 22, 872415241), (7282, 22, 872415240);
