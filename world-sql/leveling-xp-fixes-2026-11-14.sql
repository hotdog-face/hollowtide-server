-- Leveling guide XP check, 2026-11-14 (docs/LEVELING-GUIDE.md, the daily circuit's [?] rows). ACE-data fix, re-runnable.
-- Tested on a box extra test shard only. Live needs the owner's yes (evidence is the AC wiki only).
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/leveling-xp-fixes-2026-11-14.sql'
-- Undo: the same UPDATE with max_64 = 400000000.

-- L1. Emissary of Asheron (37042), Aerbax's Citadel: the FIRST reward (QuestFailure 'aerbaxshadowdefeatedrepeat')
-- is 100% of a level capped at 400,000,000, the repeat's cap copied onto it. The wiki gives the first reward as 100% of
-- a level, max 1,000,000,000, level cap 201 (a level at 200 costs 990,786,184); the repeat (40%, max 400M) is the
-- same cap 201 and stays. With the copy, a first kill from level 159 up paid 400M instead of up to 1B.
UPDATE weenie_properties_emote_action a
  JOIN weenie_properties_emote e ON e.id = a.emote_Id
   SET a.max_64 = 1000000000
 WHERE e.object_Id = 37042 AND e.category = 13 AND e.quest = 'aerbaxshadowdefeatedrepeat'
   AND a.type = 49 AND a.percent = 1 AND a.max_64 = 400000000;
