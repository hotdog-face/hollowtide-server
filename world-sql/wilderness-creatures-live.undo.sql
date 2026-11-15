-- Undo of wilderness-creatures-live.sql.
START TRANSACTION;
DELETE FROM landblock_instance WHERE weenie_Class_Id BETWEEN 923100 AND 923199;
DELETE FROM weenie WHERE class_Id BETWEEN 923100 AND 923199;
COMMIT;
