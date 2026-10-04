-- Bael'Zharon moves (owner playtest 2026-09-29: "Bael doesn't move enough. He stood still the whole
-- time."). He was built with AiImmobile (bool 52) = 1 so he held his platform in the Shadow Breach;
-- off now, so he chases and closes like any boss. MonsterAi's leash (80 m, home heal) still brings
-- him back. Same change as new-content-baelzharon-golems.sql line 59, applied alone.
UPDATE weenie_properties_bool SET value = 0 WHERE object_Id = 900200 AND type = 52;
