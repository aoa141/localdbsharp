CREATE TABLE Sale (Id INT IDENTITY PRIMARY KEY, Region VARCHAR(10) NOT NULL, Product VARCHAR(10) NOT NULL, Yr INT NOT NULL, Qty INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
INSERT INTO Sale (Region, Product, Yr, Qty, Amount) VALUES
    ('North', 'apple', 2023, 10, 100.00), ('North', 'apple', 2024, 12, 130.00), ('North', 'pear', 2023, 5, 40.00), ('South', 'apple', 2023, 7, 70.00),
    ('South', 'pear', 2024, 9, 81.00), ('South', 'plum', 2024, 2, 30.00), ('East', 'plum', 2023, 4, 60.00);

--# rollup
SELECT Region, Product, SUM(Qty) AS qty, GROUPING(Region) AS gr, GROUPING(Product) AS gp
FROM Sale GROUP BY ROLLUP (Region, Product) ORDER BY GROUPING(Region), Region, GROUPING(Product), Product;

--# with rollup syntax
SELECT Region, SUM(Amount) AS amt FROM Sale GROUP BY Region WITH ROLLUP ORDER BY GROUPING(Region), Region;

--# cube
SELECT Region, Yr, SUM(Amount) AS amt, GROUPING_ID(Region, Yr) AS gid
FROM Sale GROUP BY CUBE (Region, Yr) ORDER BY gid, Region, Yr;

--# grouping sets
SELECT Region, Product, Yr, COUNT(*) AS n, SUM(Qty) AS qty
FROM Sale GROUP BY GROUPING SETS ((Region, Yr), (Product), ()) ORDER BY GROUPING_ID(Region, Product, Yr), Region, Yr, Product;

--# rollup with having and expressions
SELECT ISNULL(Region, 'TOTAL') AS region, SUM(Amount) AS amt, COUNT(DISTINCT Product) AS products
FROM Sale GROUP BY ROLLUP (Region) HAVING SUM(Amount) > 100 ORDER BY GROUPING(Region), Region;

--# mixed plain column and rollup
SELECT Yr, Region, SUM(Qty) AS qty FROM Sale GROUP BY Yr, ROLLUP (Region) ORDER BY Yr, GROUPING(Region), Region;

--# rollup over expression
SELECT Yr % 2 AS parity, SUM(Qty) AS qty, GROUPING(Yr % 2) AS g FROM Sale GROUP BY ROLLUP (Yr % 2) ORDER BY g, parity;

--# rollup on empty input
SELECT Region, SUM(Qty) AS qty FROM Sale WHERE 1 = 0 GROUP BY ROLLUP (Region);

--# pivot
SELECT Region, [2023], [2024] FROM (SELECT Region, Yr, Amount FROM Sale) AS s
PIVOT (SUM(Amount) FOR Yr IN ([2023], [2024])) AS p ORDER BY Region;

--# pivot with count and strings
SELECT Yr, apple, pear, plum FROM (SELECT Yr, Product, Qty FROM Sale) AS s
PIVOT (COUNT(Qty) FOR Product IN (apple, pear, plum)) AS p ORDER BY Yr;

--# pivot groups by all remaining columns
SELECT * FROM (SELECT Region, Product, Yr, Qty FROM Sale WHERE Region <> 'East') AS s
PIVOT (MAX(Qty) FOR Yr IN ([2023], [2024])) AS p ORDER BY Region, Product;

--# unpivot
SELECT Id, Measure, Val FROM (SELECT Id, Qty, Yr FROM Sale WHERE Id <= 2) AS s
UNPIVOT (Val FOR Measure IN (Qty, Yr)) AS u ORDER BY Id, Measure;

--# unpivot skips nulls
SELECT k, col, v FROM (VALUES (1, 10, CAST(NULL AS INT), 30), (2, NULL, NULL, 5)) AS t(k, a, b, c)
UNPIVOT (v FOR col IN (a, b, c)) AS u ORDER BY k, col;

--# sequences
CREATE SEQUENCE dbo.OrderNo AS INT START WITH 100 INCREMENT BY 10;
GO
SELECT NEXT VALUE FOR dbo.OrderNo AS a;
SELECT NEXT VALUE FOR dbo.OrderNo AS b;
DECLARE @n INT = NEXT VALUE FOR dbo.OrderNo;
SELECT @n AS c;

--# sequence as default and in insert
CREATE TABLE Ticket (Id INT PRIMARY KEY DEFAULT (NEXT VALUE FOR dbo.OrderNo), Title VARCHAR(20));
INSERT INTO Ticket (Title) VALUES ('first'), ('second');
INSERT INTO Ticket (Id, Title) VALUES (NEXT VALUE FOR dbo.OrderNo, 'third');
SELECT Id, Title FROM Ticket ORDER BY Id;

--# sequence restart cycle and limits
CREATE SEQUENCE Small AS TINYINT START WITH 254 INCREMENT BY 1 MINVALUE 1 MAXVALUE 255 CYCLE;
CREATE SEQUENCE Tiny START WITH 1 INCREMENT BY 1 MAXVALUE 2;
GO
SELECT NEXT VALUE FOR Small AS a;
SELECT NEXT VALUE FOR Small AS b;
SELECT NEXT VALUE FOR Small AS c;
ALTER SEQUENCE Small RESTART WITH 7;
SELECT NEXT VALUE FOR Small AS d;
SELECT NEXT VALUE FOR Tiny AS e;
SELECT NEXT VALUE FOR Tiny AS f;

--# sequence exhausted
SELECT NEXT VALUE FOR Tiny AS g;

--# default sequence type and start
CREATE SEQUENCE Big;
GO
SELECT NEXT VALUE FOR Big AS a;
DROP SEQUENCE Big;
SELECT OBJECT_ID('Big') AS gone;

--# after triggers with inserted and deleted
CREATE TABLE Stock (Sku VARCHAR(10) PRIMARY KEY, Qty INT NOT NULL);
CREATE TABLE StockLog (Id INT IDENTITY PRIMARY KEY, Sku VARCHAR(10), Action CHAR(1), OldQty INT NULL, NewQty INT NULL);
GO
CREATE TRIGGER trg_Stock_Ins ON Stock AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO StockLog (Sku, Action, NewQty) SELECT Sku, 'I', Qty FROM inserted;
END
GO
CREATE TRIGGER trg_Stock_Upd ON Stock FOR UPDATE AS
    INSERT INTO StockLog (Sku, Action, OldQty, NewQty) SELECT i.Sku, 'U', d.Qty, i.Qty FROM inserted i JOIN deleted d ON d.Sku = i.Sku;
GO
CREATE TRIGGER trg_Stock_Del ON Stock AFTER DELETE AS
    INSERT INTO StockLog (Sku, Action, OldQty) SELECT Sku, 'D', Qty FROM deleted;
GO
INSERT INTO Stock VALUES ('a', 1), ('b', 2), ('c', 3);
UPDATE Stock SET Qty = Qty * 10 WHERE Sku <> 'c';
DELETE FROM Stock WHERE Sku = 'b';
SELECT Sku, Action, OldQty, NewQty FROM StockLog ORDER BY Action, Sku;

--# trigger fires once per statement even for zero rows
UPDATE Stock SET Qty = 0 WHERE Sku = 'nope';
SELECT COUNT(*) AS n FROM StockLog;
DELETE FROM StockLog;

--# trigger can reject changes
GO
CREATE TRIGGER trg_Stock_Check ON Stock AFTER INSERT, UPDATE AS
BEGIN
    IF EXISTS (SELECT 1 FROM inserted WHERE Qty < 0)
    BEGIN
        RAISERROR('Negative stock is not allowed', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END
GO
UPDATE Stock SET Qty = -5 WHERE Sku = 'a';

--# rejected change left no trace
SELECT Sku, Qty FROM Stock ORDER BY Sku;
SELECT COUNT(*) AS logged, @@TRANCOUNT AS tc FROM StockLog;

--# trigger with update function and multi action
GO
CREATE TABLE Flags (Msg VARCHAR(40));
GO
CREATE TRIGGER trg_Stock_Cols ON Stock AFTER UPDATE AS
BEGIN
    IF UPDATE(Qty) INSERT INTO Flags VALUES ('qty updated');
    IF UPDATE(Sku) INSERT INTO Flags VALUES ('sku updated');
END
GO
UPDATE Stock SET Qty = Qty + 1 WHERE Sku = 'a';
SELECT Msg FROM Flags ORDER BY Msg;

--# disable enable drop trigger
DISABLE TRIGGER trg_Stock_Upd ON Stock;
DELETE FROM StockLog;
UPDATE Stock SET Qty = 77 WHERE Sku = 'a';
SELECT COUNT(*) AS loggedWhileDisabled FROM StockLog;
ENABLE TRIGGER trg_Stock_Upd ON Stock;
UPDATE Stock SET Qty = 78 WHERE Sku = 'a';
SELECT COUNT(*) AS loggedWhenEnabled FROM StockLog;
DROP TRIGGER trg_Stock_Upd;
UPDATE Stock SET Qty = 79 WHERE Sku = 'a';
SELECT COUNT(*) AS loggedAfterDrop FROM StockLog;

--# instead of trigger
CREATE TABLE Person (Id INT IDENTITY PRIMARY KEY, Name VARCHAR(20), Deleted BIT NOT NULL DEFAULT 0);
GO
CREATE TRIGGER trg_Person_SoftDelete ON Person INSTEAD OF DELETE AS
    UPDATE Person SET Deleted = 1 WHERE Id IN (SELECT Id FROM deleted);
GO
INSERT INTO Person (Name) VALUES ('ann'), ('bob');
DELETE FROM Person WHERE Name = 'ann';
SELECT Id, Name, Deleted FROM Person ORDER BY Id;

--# nested triggers cascade across tables
CREATE TABLE A (x INT);
CREATE TABLE B (x INT);
CREATE TABLE C (x INT);
GO
CREATE TRIGGER trg_A ON A AFTER INSERT AS INSERT INTO B SELECT x + 1 FROM inserted;
GO
CREATE TRIGGER trg_B ON B AFTER INSERT AS INSERT INTO C SELECT x + 1 FROM inserted;
GO
INSERT INTO A VALUES (1), (10);
SELECT (SELECT SUM(x) FROM A) AS a, (SELECT SUM(x) FROM B) AS b, (SELECT SUM(x) FROM C) AS c;

--# trigger sees rowcount and merge fires triggers
CREATE TABLE M (Id INT PRIMARY KEY, V INT);
CREATE TABLE MLog (Kind VARCHAR(10), Cnt INT);
GO
CREATE TRIGGER trg_M ON M AFTER INSERT, UPDATE, DELETE AS
BEGIN
    DECLARE @i INT = (SELECT COUNT(*) FROM inserted), @d INT = (SELECT COUNT(*) FROM deleted);
    INSERT INTO MLog VALUES (CASE WHEN @i > 0 AND @d > 0 THEN 'update' WHEN @i > 0 THEN 'insert' WHEN @d > 0 THEN 'delete' ELSE 'none' END, @i + @d);
END
GO
INSERT INTO M VALUES (1, 1), (2, 2), (3, 3);
MERGE M AS t USING (VALUES (1, 10), (4, 40)) AS s(Id, V) ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET V = s.V
WHEN NOT MATCHED THEN INSERT (Id, V) VALUES (s.Id, s.V)
WHEN NOT MATCHED BY SOURCE THEN DELETE;
SELECT Kind, Cnt FROM MLog ORDER BY Kind, Cnt;

--# for json path
SELECT Id, Region, Amount FROM Sale WHERE Id <= 2 ORDER BY Id FOR JSON PATH;

--# for json nested paths nulls and root
SELECT Id AS [id], Region AS [loc.region], Product AS [loc.product], CAST(NULL AS INT) AS missing, CAST(1 AS BIT) AS flag, CAST('2024-03-05' AS DATE) AS d,
       CAST('2024-03-05 10:20:30.123' AS DATETIME) AS dt, 'quote" and \ slash' AS txt
FROM Sale WHERE Id = 1 FOR JSON PATH, ROOT('sales'), INCLUDE_NULL_VALUES;

--# for json without array wrapper and auto
SELECT Id, Product FROM Sale WHERE Id = 3 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
SELECT Id, Qty FROM Sale WHERE Id IN (4, 5) ORDER BY Id FOR JSON AUTO;

--# for json empty result
SELECT Id FROM Sale WHERE 1 = 0 FOR JSON PATH;

--# for json as nested subquery
SELECT r.Region, (SELECT s.Product, s.Qty FROM Sale s WHERE s.Region = r.Region ORDER BY s.Id FOR JSON PATH) AS items
FROM (SELECT DISTINCT Region FROM Sale WHERE Region <> 'North') r ORDER BY r.Region FOR JSON PATH;

--# for json into variable
DECLARE @j NVARCHAR(MAX) = (SELECT Id, Qty FROM Sale WHERE Id <= 2 ORDER BY Id FOR JSON PATH);
SELECT @j AS j, ISJSON(@j) AS ok, JSON_VALUE(@j, '$[1].Qty') AS q;

--# json_value json_query isjson
DECLARE @doc NVARCHAR(MAX) = N'{"name":"Ann","age":30,"tags":["a","b"],"addr":{"city":"Oslo","zip":null},"ok":true}';
SELECT JSON_VALUE(@doc, '$.name') AS name, JSON_VALUE(@doc, '$.age') AS age, JSON_VALUE(@doc, '$.tags[1]') AS tag, JSON_VALUE(@doc, '$.addr.city') AS city,
       JSON_VALUE(@doc, '$.addr.zip') AS zip, JSON_VALUE(@doc, '$.nope') AS nope, JSON_VALUE(@doc, '$.addr') AS obj, JSON_VALUE(@doc, '$.ok') AS ok,
       JSON_QUERY(@doc, '$.tags') AS tags, JSON_QUERY(@doc, '$.addr') AS addr, JSON_QUERY(@doc, '$.name') AS notObj, ISJSON(@doc) AS valid, ISJSON('nope') AS invalid;

--# openjson default schema
SELECT [key], value, type FROM OPENJSON(N'{"a":1,"b":"two","c":[1,2],"d":{"x":1},"e":null,"f":true}') ORDER BY [key];
SELECT [key], value, type FROM OPENJSON(N'[10,"x",null]') ORDER BY [key];
SELECT value FROM OPENJSON(N'{"list":[3,1,2]}', '$.list') ORDER BY value;

--# openjson with schema
DECLARE @json NVARCHAR(MAX) = N'[{"id":1,"name":"Ann","info":{"age":30,"tags":["x","y"]}},{"id":2,"name":"Bob","info":{"age":null}},{"id":3}]';
SELECT id, name, age, tags FROM OPENJSON(@json) WITH (id INT, name VARCHAR(20), age INT '$.info.age', tags NVARCHAR(MAX) '$.info.tags' AS JSON) ORDER BY id;
SELECT COUNT(*) AS n, SUM(j.id) AS total FROM OPENJSON(@json) WITH (id INT '$.id') AS j;

--# openjson to feed in list
DECLARE @ids NVARCHAR(100) = N'[1,3,5]';
SELECT Id, Product FROM Sale WHERE Id IN (SELECT CAST(value AS INT) FROM OPENJSON(@ids)) ORDER BY Id;
SELECT s.Id FROM Sale s JOIN OPENJSON(@ids) j ON CAST(j.value AS INT) = s.Id ORDER BY s.Id;

--# dbcc checkident
CREATE TABLE Seeded (Id INT IDENTITY(1,1) PRIMARY KEY, V INT);
INSERT INTO Seeded (V) VALUES (1), (2);
DBCC CHECKIDENT ('Seeded', RESEED, 100);
INSERT INTO Seeded (V) VALUES (3);
SELECT Id, V FROM Seeded ORDER BY Id;

--# no-op maintenance statements
CREATE INDEX IX_Sale_Region ON Sale (Region) INCLUDE (Amount);
ALTER INDEX IX_Sale_Region ON Sale REBUILD;
UPDATE STATISTICS Sale;
CHECKPOINT;
SELECT COUNT(*) AS n FROM Sale WITH (NOLOCK);
DROP INDEX IX_Sale_Region ON Sale;
