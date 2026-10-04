-- Retail focused quest sweep, 2026-09-30 (docs/SWEEP-RETAIL-QUESTS-2026-09-30.md). ACE-data fixes, re-runnable.
-- Apply: ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/retail-quest-sweep-fixes-2026-09-30.sql'

-- R1. Arcanum Researcher (16892): the Reedshark's Bronze Gear from a Statue (19218) asked InqQuest
-- 'GearMosswartGiven@2', a key with no QuestSuccess/QuestFailure set, so the gear was taken with no word
-- and no XP. Her 'GearReedsharkGiven' sets (stamp + 2,000 XP; or "come back tomorrow" and the gear back)
-- exist and nothing else reaches them. Point the ask at them.
UPDATE weenie_properties_emote_action a
  JOIN weenie_properties_emote e ON e.id = a.emote_Id
   SET a.message = 'GearReedsharkGiven'
 WHERE e.object_Id = 16892 AND e.category = 6 AND e.weenie_Class_Id = 19218
   AND a.type = 21 AND a.message = 'GearMosswartGiven@2';

-- R2. Halvor (44989), the Frozen Wight Lair: a Wardley belonging asks InqQuest 'FrozenWightLairComplete1111_2'
-- (boots, shirt) or '_3' (necklace). '_2'/'_3' are not quest names (ACE strips only an '@' suffix), have no
-- quest row, so the ask always fails and the reward (100,000,000 XP + 10,000 luminance) paid on every hand-in,
-- even inside the 20 h wait his Use reports. Their success sets already carry his "again in %tqt" line, so the
-- intent is the timed 'FrozenWightLairComplete1111' row: rename to '@2'/'@3' and give the success sets'
-- empty Tell his existing "Thank you again" line.
UPDATE weenie_properties_emote_action a
  JOIN weenie_properties_emote e ON e.id = a.emote_Id
   SET a.message = REPLACE(a.message, 'FrozenWightLairComplete1111_', 'FrozenWightLairComplete1111@')
 WHERE e.object_Id = 44989 AND a.type = 21 AND a.message IN ('FrozenWightLairComplete1111_2', 'FrozenWightLairComplete1111_3');
UPDATE weenie_properties_emote
   SET quest = REPLACE(quest, 'FrozenWightLairComplete1111_', 'FrozenWightLairComplete1111@')
 WHERE object_Id = 44989 AND quest IN ('FrozenWightLairComplete1111_2', 'FrozenWightLairComplete1111_3');
UPDATE weenie_properties_emote_action a
  JOIN weenie_properties_emote e ON e.id = a.emote_Id
   SET a.message = 'Thank you again for returning Wardley''s things to me.'
 WHERE e.object_Id = 44989 AND e.category = 12 AND e.quest IN ('FrozenWightLairComplete1111@2', 'FrozenWightLairComplete1111@3')
   AND a.type = 10 AND a.message IS NULL;
