CREATE TABLE Item (Id INT PRIMARY KEY, Name VARCHAR(20) NOT NULL, Qty INT NOT NULL, Price DECIMAL(8,2) NULL);
INSERT INTO Item VALUES (1, 'bolt', 10, 0.25), (2, 'nut', 20, 0.10), (3, 'gear', 5, 12.00);
CREATE TABLE Audit (Id INT IDENTITY PRIMARY KEY, Action VARCHAR(10), ItemId INT, OldQty INT NULL, NewQty INT NULL);

--# commit keeps changes
BEGIN TRANSACTION;
UPDATE Item SET Qty = Qty + 1 WHERE Id = 1;
SELECT @@TRANCOUNT AS tc;
COMMIT TRANSACTION;
SELECT Qty, @@TRANCOUNT AS tc FROM Item WHERE Id = 1;

--# rollback undoes insert update delete
BEGIN TRAN;
INSERT INTO Item VALUES (4, 'temp', 1, 1);
UPDATE Item SET Qty = 999, Name = 'changed' WHERE Id = 2;
DELETE FROM Item WHERE Id = 3;
SELECT COUNT(*) AS n, SUM(Qty) AS q FROM Item;
ROLLBACK TRAN;
SELECT Id, Name, Qty FROM Item ORDER BY Id;

--# rollback restores unique index state
BEGIN TRAN;
DELETE FROM Item WHERE Id = 1;
INSERT INTO Item VALUES (1, 'replacement', 0, 0);
UPDATE Item SET Id = 10 WHERE Id = 2;
ROLLBACK;
INSERT INTO Item VALUES (10, 'ten', 1, 1);
SELECT Id, Name FROM Item ORDER BY Id;
DELETE FROM Item WHERE Id = 10;

--# nested transactions
BEGIN TRAN;
BEGIN TRAN;
UPDATE Item SET Qty = 0 WHERE Id = 3;
SELECT @@TRANCOUNT AS tc;
COMMIT;
SELECT @@TRANCOUNT AS tc;
ROLLBACK;
SELECT @@TRANCOUNT AS tc, Qty FROM Item WHERE Id = 3;

--# savepoints
BEGIN TRAN;
UPDATE Item SET Qty = 100 WHERE Id = 1;
SAVE TRANSACTION sp1;
UPDATE Item SET Qty = 200 WHERE Id = 2;
DELETE FROM Item WHERE Id = 3;
ROLLBACK TRANSACTION sp1;
SELECT @@TRANCOUNT AS tc;
COMMIT;
SELECT Id, Qty FROM Item ORDER BY Id;
UPDATE Item SET Qty = 11 WHERE Id = 1;

--# rolling back to a savepoint releases it
BEGIN TRAN;
SAVE TRAN a;
UPDATE Item SET Qty = 500 WHERE Id = 1;
SAVE TRAN b;
ROLLBACK TRAN a;
SELECT Qty, @@TRANCOUNT AS tc FROM Item WHERE Id = 1;
BEGIN TRY
    ROLLBACK TRAN b;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS num, @@TRANCOUNT AS tc;
END CATCH
BEGIN TRY
    ROLLBACK TRAN a;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS num, @@TRANCOUNT AS tc;
END CATCH
COMMIT;

--# named transactions
BEGIN TRAN outerTran;
UPDATE Item SET Qty = 600 WHERE Id = 1;
BEGIN TRAN innerTran;
BEGIN TRY
    ROLLBACK TRAN innerTran;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS num, @@TRANCOUNT AS tc;
END CATCH
ROLLBACK TRAN outerTran;
SELECT Qty, @@TRANCOUNT AS tc FROM Item WHERE Id = 1;

--# rollback in catch
BEGIN TRY
    BEGIN TRAN;
    UPDATE Item SET Qty = Qty - 1 WHERE Id = 1;
    INSERT INTO Item VALUES (1, 'dup', 0, 0);
    COMMIT;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    SELECT ERROR_NUMBER() AS num, XACT_STATE() AS xs;
END CATCH
SELECT Qty, @@TRANCOUNT AS tc FROM Item WHERE Id = 1;

--# commit without transaction
COMMIT;

--# rollback without transaction
ROLLBACK;

--# xact_abort rolls back on error
SET XACT_ABORT ON;
BEGIN TRAN;
UPDATE Item SET Qty = 12345 WHERE Id = 1;
INSERT INTO Item VALUES (1, 'dup', 0, 0);
COMMIT;
GO
SET XACT_ABORT OFF;
SELECT Qty, @@TRANCOUNT AS tc FROM Item WHERE Id = 1;

--# transaction with ddl rollback
BEGIN TRAN;
CREATE TABLE Temp1 (x INT);
INSERT INTO Temp1 VALUES (1);
ROLLBACK;
SELECT OBJECT_ID('Temp1') AS id;

--# table variables survive rollback
DECLARE @t TABLE (v INT);
BEGIN TRAN;
INSERT INTO @t VALUES (1), (2);
INSERT INTO Item VALUES (50, 'x', 1, 1);
ROLLBACK;
SELECT (SELECT COUNT(*) FROM @t) AS inVar, (SELECT COUNT(*) FROM Item WHERE Id = 50) AS inTable;

--# output inserted
INSERT INTO Item (Id, Name, Qty, Price) OUTPUT inserted.Id, inserted.Name, inserted.Qty * 2 AS dbl VALUES (5, 'cog', 7, 3.5), (6, 'pin', 9, NULL);

--# output deleted
DELETE FROM Item OUTPUT deleted.* WHERE Id IN (5, 6);

--# output on update
UPDATE Item SET Qty = Qty + 5 OUTPUT inserted.Id, deleted.Qty AS oldQty, inserted.Qty AS newQty, inserted.Qty - deleted.Qty AS delta WHERE Id <= 2;

--# output into table
UPDATE Item SET Qty = Qty - 5 OUTPUT 'UPD', inserted.Id, deleted.Qty, inserted.Qty INTO Audit (Action, ItemId, OldQty, NewQty) WHERE Id <= 2;
DELETE FROM Item OUTPUT 'DEL', deleted.Id, deleted.Qty INTO Audit (Action, ItemId, OldQty) WHERE Id = 99;
INSERT INTO Item OUTPUT 'INS', inserted.Id, inserted.Qty INTO Audit (Action, ItemId, NewQty) VALUES (7, 'hook', 3, 1.0);
SELECT Action, ItemId, OldQty, NewQty FROM Audit ORDER BY Id;
DELETE FROM Item WHERE Id = 7;

--# output into table variable
DECLARE @ids TABLE (Id INT, Name VARCHAR(20));
INSERT INTO Item OUTPUT inserted.Id, inserted.Name INTO @ids VALUES (8, 'a', 1, 1), (9, 'b', 2, 2);
SELECT * FROM @ids ORDER BY Id;
DELETE FROM Item WHERE Id IN (8, 9);

--# merge insert update delete
CREATE TABLE Src (Id INT PRIMARY KEY, Name VARCHAR(20), Qty INT);
INSERT INTO Src VALUES (1, 'bolt', 50), (2, 'nut', 20), (4, 'washer', 70);
MERGE INTO Item AS t
USING Src AS s ON t.Id = s.Id
WHEN MATCHED AND t.Qty <> s.Qty THEN UPDATE SET t.Qty = s.Qty
WHEN NOT MATCHED BY TARGET THEN INSERT (Id, Name, Qty) VALUES (s.Id, s.Name, s.Qty)
WHEN NOT MATCHED BY SOURCE THEN DELETE;
SELECT @@ROWCOUNT AS rc;
SELECT Id, Name, Qty, Price FROM Item ORDER BY Id;

--# merge with output and action
MERGE Item AS t
USING (VALUES (1, 'bolt', 51), (3, 'gear', 5), (4, 'washer', 70)) AS s(Id, Name, Qty) ON t.Id = s.Id
WHEN MATCHED AND t.Qty <> s.Qty THEN UPDATE SET Qty = s.Qty, Name = UPPER(s.Name)
WHEN NOT MATCHED THEN INSERT (Id, Name, Qty, Price) VALUES (s.Id, s.Name, s.Qty, 9.99)
OUTPUT $action AS act, inserted.Id, deleted.Qty AS oldQty, inserted.Qty AS newQty, s.Name AS srcName;
SELECT Id, Name, Qty, Price FROM Item ORDER BY Id;

--# merge duplicate source rows
MERGE Item AS t
USING (VALUES (1, 5), (1, 6)) AS s(Id, Qty) ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Qty = s.Qty;

--# merge upsert with variables
DECLARE @id INT = 20, @name VARCHAR(20) = 'spring', @qty INT = 4;
MERGE Item AS t USING (SELECT @id AS Id, @name AS Name, @qty AS Qty) AS s ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Qty = t.Qty + s.Qty
WHEN NOT MATCHED THEN INSERT (Id, Name, Qty) VALUES (s.Id, s.Name, s.Qty);
MERGE Item AS t USING (SELECT @id AS Id, @name AS Name, @qty AS Qty) AS s ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Qty = t.Qty + s.Qty
WHEN NOT MATCHED THEN INSERT (Id, Name, Qty) VALUES (s.Id, s.Name, s.Qty);
SELECT Id, Name, Qty FROM Item WHERE Id = 20;

--# cte with update and delete
WITH low AS (SELECT Id FROM Item WHERE Qty < 10)
UPDATE Item SET Price = 1 WHERE Id IN (SELECT Id FROM low);
SELECT Id, Price FROM Item ORDER BY Id;
WITH gone AS (SELECT Id FROM Item WHERE Id >= 20)
DELETE FROM Item WHERE Id IN (SELECT Id FROM gone);
SELECT COUNT(*) AS n FROM Item;

--# insert from cte
WITH more AS (SELECT Id + 100 AS Id, Name, Qty FROM Item WHERE Id <= 2)
INSERT INTO Item (Id, Name, Qty) SELECT Id, Name, Qty FROM more;
SELECT Id FROM Item WHERE Id > 100 ORDER BY Id;
