-- Read-only: which account owns a character (edit the name), and when it last logged in.
-- Run: ssh ovh 'sudo -n mysql' < tools/gpubox-ace/account-for-character.sql
SET @who = 'Mastacho';
SELECT c.name AS character_name, a.accountName AS account, a.last_Login_Time, c.is_Deleted
FROM ace_shard.`character` c
JOIN ace_auth.account a ON a.accountId = c.account_Id
WHERE c.name LIKE CONCAT('%', @who, '%');
SELECT a.accountName AS account, a.create_Time,
       (SELECT COUNT(*) FROM ace_shard.`character` c WHERE c.account_Id = a.accountId AND c.is_Deleted = 0) AS characters
FROM ace_auth.account a WHERE a.accountName = @who;
