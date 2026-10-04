-- LAUNCH SWEEP #1 (quests), docs/SWEEP-QUESTS-2026-09-30.md: fixes to OUR quest data found by the bots.
--
-- Q1  Chosen of Asheron (900324), the Empyrean Vault attunement. After a player was attuned, every
--     further Cracked Golem Core was still counted ("Bring me more; three are needed to attune you") and
--     every third one paid the 10%-of-a-level reward again (50,000,000 XP at level 275, seen twice by the
--     bot) and re-granted the title. The attunement is meant once (EmpyreanVaultAttuned, max 1 solve).
--     Now the Give set first asks InqQuest 'EmpyreanVaultAttuned@Core' (success = stamped and at its cap):
--     attuned -> his existing "The Forge Gate knows you now..." line and the core handed back (it still
--     opens the First Seal); not attuned -> the old count (InqQuestSolves EmpyreanVaultCoresGiven 2-999).
--     No new dialogue. The generator (gen_empyrean_vault.py -> empyrean-vault.sql) carries the same change.
--
-- Q2  Aldra Venn (900816), the Sunken Lyceum. After the Brood Seal was stamped, every further three
--     Lyceum Record Shards paid 5% of a level again (30,000,000 XP at level 275) with the first-time
--     "Three. Listen..." speech (bot, 15:59). Shards keep coming (four record cases every 10 minutes, two
--     from each First Branded). Now the Give set asks InqQuest 'SunkenLyceumBroodSeal@Shard': stamped ->
--     she answers exactly as her Use does (InqQuest SunkenLyceumWellspring) and pays nothing; not
--     stamped -> the old count.
-- Q3  Aldra Venn, the First Brand. Every further brand paid 8% of a level again (60,000,000 XP, bot,
--     15:59), and the First Branded drops six. Now InqQuest 'SunkenLyceumWellspring@Brand': stamped ->
--     her existing Wellspring line; not stamped -> the old speech, stamp and reward.
--     The crown's repeat (5% of a level for another crown) is the design and is unchanged.
--     The generator (gen_sunken_lyceum.py -> sunken-lyceum.sql) carries Q2 and Q3.
-- Q4  The First Brand (900812) is a clone of the retail Olthoi Carapace (3678) and kept its Quest string
--     'InvasionQuest10' (a 20 hour pickup timer). ACE checks that timer when an item is taken from a
--     corpse, so a player could loot one brand a day ("You have solved this quest too recently!", bot,
--     17:04) and anyone who had picked up an invasion-quest carapace in the last 20 hours could not loot a
--     brand at all. The brand's own quest is SunkenLyceumWellspring, stamped at the hand-in. The string is
--     removed (the Lyceum Record Shard's clone already had its own removed). Generator carries it (Q4).
--
-- Apply (after syncing the repo to the box):
--   ssh gpubox '~/Dereth-Unity/tools/gpubox-ace/apply-live.sh tools/gpubox-ace/quest-sweep-fixes-2026-09-30.sql 2372 003F'
-- Re-runnable.
START TRANSACTION;

-- Q1: the Give set's branch
UPDATE weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
   SET a.type = 21, a.message = 'EmpyreanVaultAttuned@Core', a.min = NULL, a.max = NULL
 WHERE e.object_Id = 900324 AND e.category = 6 AND e.weenie_Class_Id = 900301 AND a.`order` = 1;
-- Q1: its two branches (deleted first, so the file can run again)
DELETE a FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
 WHERE e.object_Id = 900324 AND e.quest = 'EmpyreanVaultAttuned@Core';
DELETE FROM weenie_properties_emote WHERE object_Id = 900324 AND quest = 'EmpyreanVaultAttuned@Core';
INSERT INTO weenie_properties_emote (object_Id, category, probability, weenie_Class_Id, quest) VALUES (900324, 12, 1.0, NULL, 'EmpyreanVaultAttuned@Core');
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (@e, 0, 10, 1, 1, 'The Forge Gate knows you now. Go down through the vault and end the Warden before the Hopeslayer''s shadow reaches its forge.');
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, weenie_Class_Id, stack_Size) VALUES (@e, 1, 3, 0, 1, 900301, 1);
INSERT INTO weenie_properties_emote (object_Id, category, probability, weenie_Class_Id, quest) VALUES (900324, 13, 1.0, NULL, 'EmpyreanVaultAttuned@Core');
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message, min, max) VALUES (@e, 0, 30, 0, 1, 'EmpyreanVaultCoresGiven', 2, 999);

-- Q2: the shard Give set's branch, and its two branches
UPDATE weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
   SET a.type = 21, a.message = 'SunkenLyceumBroodSeal@Shard', a.min = NULL, a.max = NULL
 WHERE e.object_Id = 900816 AND e.category = 6 AND e.weenie_Class_Id = 900811 AND a.`order` = 1;
DELETE a FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
 WHERE e.object_Id = 900816 AND e.quest = 'SunkenLyceumBroodSeal@Shard';
DELETE FROM weenie_properties_emote WHERE object_Id = 900816 AND quest = 'SunkenLyceumBroodSeal@Shard';
INSERT INTO weenie_properties_emote (object_Id, category, probability, weenie_Class_Id, quest) VALUES (900816, 12, 1.0, NULL, 'SunkenLyceumBroodSeal@Shard');
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (@e, 0, 21, 0, 1, 'SunkenLyceumWellspring');
INSERT INTO weenie_properties_emote (object_Id, category, probability, weenie_Class_Id, quest) VALUES (900816, 13, 1.0, NULL, 'SunkenLyceumBroodSeal@Shard');
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message, min, max) VALUES (@e, 0, 30, 0, 1, 'SunkenLyceumShards', 2, 999);

-- Q3: the brand Give set keeps its TurnToTarget and branches; the old speech and reward move to the branch
DELETE a FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
 WHERE e.object_Id = 900816 AND e.category = 6 AND e.weenie_Class_Id = 900812 AND a.`order` >= 1;
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message)
SELECT e.id, 1, 21, 0, 1, 'SunkenLyceumWellspring@Brand' FROM weenie_properties_emote e
 WHERE e.object_Id = 900816 AND e.category = 6 AND e.weenie_Class_Id = 900812;
DELETE a FROM weenie_properties_emote_action a JOIN weenie_properties_emote e ON e.id = a.emote_Id
 WHERE e.object_Id = 900816 AND e.quest = 'SunkenLyceumWellspring@Brand';
DELETE FROM weenie_properties_emote WHERE object_Id = 900816 AND quest = 'SunkenLyceumWellspring@Brand';
INSERT INTO weenie_properties_emote (object_Id, category, probability, weenie_Class_Id, quest, min_Health, max_Health) VALUES (900816, 12, 1.0, NULL, 'SunkenLyceumWellspring@Brand', NULL, NULL);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (@e, 0, 10, 1, 1, 'The Wellspring Gate knows the First Brand on you. It runs down the old well-shaft from the Branding Pit into the great hall of the hive. She is there, and she is awake. Bring me her crown.');
INSERT INTO weenie_properties_emote (object_Id, category, probability, weenie_Class_Id, quest, min_Health, max_Health) VALUES (900816, 13, 1.0, NULL, 'SunkenLyceumWellspring@Brand', NULL, NULL);
SET @e = LAST_INSERT_ID();
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (@e, 0, 10, 1, 1, 'The First Brand. Feel how warm it still is. They burned this into the first of her children six hundred years ago, to make it obey, and it never has.');
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (@e, 1, 22, 0, 1, 'SunkenLyceumWellspring');
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, percent, min_64, max_64) VALUES (@e, 2, 49, 0, 1, 0.08, 0, 60000000);
INSERT INTO weenie_properties_emote_action (emote_Id, `order`, type, delay, extent, message) VALUES (@e, 3, 10, 1, 1, 'The Wellspring Gate in the Branding Pit will know its mark on you now. The Empyreans called her the Bound Queen. When the Harbinger drank the world''s mana, the well ran dry and her bindings broke.');

-- Q4: the brand's inherited pickup timer
DELETE FROM weenie_properties_string WHERE object_Id = 900812 AND type = 33;

COMMIT;
