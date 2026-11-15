-- Undo of expression-wear-vendors.sql: its shop rows.
START TRANSACTION;
DELETE FROM weenie_properties_create_list WHERE destination_Type = 4 AND weenie_Class_Id BETWEEN 922100 AND 922199 AND object_Id IN (658, 667, 703, 719, 821, 843, 870, 983, 994, 1047, 1058, 1080, 1145, 1823, 1834, 2233, 2260, 2301, 2502, 4445, 4556, 4686, 4703, 11399, 30034, 30069, 656, 734, 865, 4555, 5835, 8438, 8495, 24219);
COMMIT;
