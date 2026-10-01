--# declare inside a loop
DECLARE @i INT = 0, @log VARCHAR(50) = '';
WHILE @i < 3
BEGIN
    DECLARE @x INT = 5, @keep INT;
    DECLARE @tv TABLE (v INT);
    INSERT INTO @tv VALUES (@i);
    SET @keep = ISNULL(@keep, 0) + 1;
    SET @x = @x + @i;
    SET @log = @log + CAST(@x AS VARCHAR(5)) + ',';
    SET @i += 1;
END
SELECT @i AS i, @log AS xs, @keep AS kept, (SELECT COUNT(*) FROM @tv) AS tvRows;

--# table variables are atomic per statement
DECLARE @t TABLE (a INT PRIMARY KEY, b INT CHECK (b < 10));
BEGIN TRY INSERT @t VALUES (1, 1), (2, 2), (1, 3); END TRY BEGIN CATCH PRINT 'x' END CATCH;
SELECT COUNT(*) AS afterDupInsert FROM @t;
BEGIN TRY INSERT @t VALUES (5, 5), (6, 60); END TRY BEGIN CATCH PRINT 'x' END CATCH;
SELECT COUNT(*) AS afterCheckInsert FROM @t;
INSERT @t VALUES (1, 1), (2, 2);
BEGIN TRY UPDATE @t SET b = b * 6; END TRY BEGIN CATCH PRINT 'x' END CATCH;
SELECT a, b FROM @t ORDER BY a;
BEGIN TRAN; INSERT @t VALUES (3, 3); ROLLBACK;
SELECT COUNT(*) AS afterRollback FROM @t;

--# raiserror under xact_abort keeps the transaction
CREATE TABLE #xa (id INT PRIMARY KEY);
SET XACT_ABORT ON;
BEGIN TRAN;
INSERT #xa VALUES (1);
RAISERROR('outside try', 16, 1);
GO
SELECT XACT_STATE() AS xs, @@TRANCOUNT AS tc;
BEGIN TRY RAISERROR('x', 16, 1); END TRY BEGIN CATCH SELECT XACT_STATE() AS xs, @@TRANCOUNT AS tc; ROLLBACK; END CATCH
SET XACT_ABORT OFF;
SELECT COUNT(*) AS n FROM #xa;

--# raiserror outside try under xact_abort
SET XACT_ABORT ON;
BEGIN TRAN;
RAISERROR('outside try', 16, 1);
GO
SELECT XACT_STATE() AS xs, @@TRANCOUNT AS tc;
ROLLBACK;
SET XACT_ABORT OFF;

--# conversion error aborts the batch and rolls back
BEGIN TRAN;
INSERT #xa VALUES (2);
GO
DECLARE @i INT = CAST('abc' AS INT);

--# conversion error left no transaction
SELECT @@TRANCOUNT AS tc, (SELECT COUNT(*) FROM #xa) AS n;

--# doomed transaction under xact_abort
SET XACT_ABORT ON;
BEGIN TRAN;
BEGIN TRY
    INSERT #xa VALUES (1);
END TRY
BEGIN CATCH
    SELECT XACT_STATE() AS xs, @@TRANCOUNT AS tc;
    BEGIN TRY COMMIT; END TRY BEGIN CATCH SELECT ERROR_NUMBER() AS commitErr; END CATCH
    ROLLBACK;
END CATCH
SET XACT_ABORT OFF;
SELECT @@TRANCOUNT AS tc, XACT_STATE() AS xs;

--# scope_identity is scoped
CREATE TABLE idA (id INT IDENTITY(1,1), v INT);
CREATE TABLE idB (id INT IDENTITY(100,1), v INT);
CREATE TABLE idC (id INT IDENTITY(5,5), v INT);
GO
CREATE TRIGGER trA ON idA AFTER INSERT AS INSERT idB (v) SELECT v FROM inserted;
GO
CREATE PROC insC AS INSERT idC (v) VALUES (1);
GO
INSERT idA (v) VALUES (7);
SELECT SCOPE_IDENTITY() AS si, @@IDENTITY AS ai;
EXEC insC;
SELECT SCOPE_IDENTITY() AS si, @@IDENTITY AS ai;
GO
SELECT SCOPE_IDENTITY() AS nextBatch;

--# procedure changing trancount
GO
CREATE PROC rollsBack AS ROLLBACK;
GO
BEGIN TRAN;
BEGIN TRY EXEC rollsBack; END TRY BEGIN CATCH SELECT ERROR_NUMBER() AS num; END CATCH
SELECT @@TRANCOUNT AS tc;

--# raiserror in a trigger does not undo the statement
CREATE TABLE tr1 (id INT PRIMARY KEY, v INT);
CREATE TABLE trlog (msg VARCHAR(50));
GO
CREATE TRIGGER trRaise ON tr1 AFTER INSERT AS BEGIN RAISERROR('trg', 16, 1); INSERT trlog VALUES ('continued'); END
GO
INSERT tr1 VALUES (1, 1);

--# raiserror in a trigger kept the row
SELECT (SELECT COUNT(*) FROM tr1) AS n, (SELECT COUNT(*) FROM trlog) AS logged;
DROP TRIGGER trRaise;

--# work after rollback in a trigger persists
GO
CREATE TRIGGER trRb ON tr1 AFTER INSERT AS BEGIN ROLLBACK; INSERT trlog VALUES ('after rollback'); END
GO
INSERT tr1 VALUES (2, 2);

--# work after rollback in a trigger persisted
SELECT (SELECT COUNT(*) FROM tr1) AS n, (SELECT COUNT(*) FROM trlog WHERE msg = 'after rollback') AS logged, @@TRANCOUNT AS tc;
DROP TRIGGER trRb;

--# runtime error in a trigger rolls back the transaction
GO
CREATE TRIGGER trDiv ON tr1 AFTER INSERT AS BEGIN DECLARE @x INT = 1 / 0; END
GO
BEGIN TRAN;
INSERT tr1 VALUES (3, 3);

--# runtime error in a trigger left no transaction
SELECT @@TRANCOUNT AS tc, (SELECT COUNT(*) FROM tr1) AS n;
DROP TRIGGER trDiv;

--# cascades fire child triggers
CREATE TABLE cpar (id INT PRIMARY KEY);
CREATE TABLE cchi (id INT PRIMARY KEY, pid INT REFERENCES cpar(id) ON DELETE CASCADE);
CREATE TABLE cnul (id INT PRIMARY KEY, pid INT NULL REFERENCES cpar(id) ON DELETE SET NULL);
CREATE TABLE clog (what VARCHAR(10), id INT);
GO
CREATE TRIGGER trCchi ON cchi AFTER DELETE AS INSERT clog SELECT 'del', id FROM deleted;
GO
CREATE TRIGGER trCnul ON cnul AFTER UPDATE AS INSERT clog SELECT 'upd', id FROM inserted WHERE pid IS NULL;
GO
INSERT cpar VALUES (1), (2);
INSERT cchi VALUES (10, 1), (11, 1), (12, 2);
INSERT cnul VALUES (20, 1), (21, 2);
DELETE cpar WHERE id = 1;
SELECT what, id FROM clog ORDER BY what, id;

--# output into sees values before triggers run
CREATE TABLE o1 (id INT, v INT);
GO
CREATE TRIGGER tro ON o1 AFTER INSERT AS UPDATE o1 SET v = o1.v * 10 FROM o1 JOIN inserted i ON i.id = o1.id;
GO
DECLARE @o TABLE (id INT, v INT);
INSERT o1 OUTPUT inserted.id, inserted.v INTO @o VALUES (1, 1);
SELECT id, v FROM @o;
SELECT id, v FROM o1;

--# instead of triggers do not chain on the same table
CREATE TABLE soft (id INT PRIMARY KEY, v INT, deleted BIT NOT NULL DEFAULT 0);
GO
CREATE TRIGGER trSoftDel ON soft INSTEAD OF DELETE AS UPDATE soft SET deleted = 1 WHERE id IN (SELECT id FROM deleted);
GO
CREATE TRIGGER trSoftUpd ON soft INSTEAD OF UPDATE AS UPDATE soft SET v = i.v + 1000 FROM soft JOIN inserted i ON i.id = soft.id;
GO
INSERT soft (id, v) VALUES (1, 1), (2, 2);
DELETE soft WHERE id = 1;
UPDATE soft SET v = 5 WHERE id = 2;
SELECT id, v, deleted FROM soft ORDER BY id;

--# merge applies deletes before updates
CREATE TABLE mg (id INT PRIMARY KEY, k INT UNIQUE);
INSERT mg VALUES (1, 1), (2, 2);
MERGE mg USING (VALUES (1, 'U', 2), (2, 'D', NULL)) AS s(id, op, nk) ON mg.id = s.id
WHEN MATCHED AND s.op = 'U' THEN UPDATE SET k = s.nk
WHEN MATCHED AND s.op = 'D' THEN DELETE;
SELECT id, k FROM mg;

--# output can reference joined tables
CREATE TABLE ot (id INT PRIMARY KEY, val VARCHAR(10));
INSERT ot VALUES (1, 'a'), (3, 'c'), (5, 'e');
CREATE TABLE os (id INT, nv VARCHAR(10));
INSERT os VALUES (1, 's1'), (3, 's3');
UPDATE t SET val = s.nv OUTPUT inserted.id, deleted.val AS old, s.nv AS src FROM ot t JOIN os s ON s.id = t.id;
DELETE t OUTPUT deleted.id, s.nv FROM ot t JOIN os s ON s.id = t.id WHERE s.id = 3;
SELECT id, val FROM ot ORDER BY id;

--# views with renamed columns are updatable
CREATE TABLE vt (id INT PRIMARY KEY, k INT NULL, val VARCHAR(10));
INSERT vt VALUES (1, 1, 'a'), (2, 9, 'b');
GO
CREATE VIEW vv AS SELECT id, val AS x FROM vt WHERE k < 3 OR k IS NULL;
GO
CREATE VIEW vw2 (ident, word) AS SELECT id, val FROM vt;
GO
UPDATE vv SET x = 'zz' WHERE id = 1;
INSERT vv VALUES (10, 'ins');
INSERT vv (id, x) VALUES (11, 'ins2');
UPDATE vw2 SET word = UPPER(word) WHERE ident >= 10;
DELETE FROM vv WHERE x = 'INS2';
SELECT id, k, val FROM vt ORDER BY id;
WITH c AS (SELECT id AS n, val AS w FROM vt) UPDATE c SET w = 'cte' WHERE n = 2;
SELECT val FROM vt WHERE id = 2;

--# on update cascade
CREATE TABLE ug (id INT PRIMARY KEY);
CREATE TABLE up (id INT PRIMARY KEY, gid INT REFERENCES ug(id) ON DELETE CASCADE ON UPDATE CASCADE);
INSERT ug VALUES (1), (2);
INSERT up VALUES (10, 1), (11, 1), (12, 2);
UPDATE ug SET id = id + 10;
SELECT id, gid FROM up ORDER BY id;

--# self referencing rows in one statement
CREATE TABLE emp (id INT PRIMARY KEY, mgr INT NULL REFERENCES emp(id));
INSERT emp VALUES (2, 1), (1, NULL), (3, 3);
SELECT id, mgr FROM emp ORDER BY id;

--# self reference still checked
INSERT emp VALUES (5, 99);

--# foreign keys on temp tables are not enforced
CREATE TABLE #fp (id INT PRIMARY KEY);
CREATE TABLE #fc (id INT REFERENCES #fp(id));
INSERT #fc VALUES (5);
SELECT COUNT(*) AS n FROM #fc;

--# drop column keeps keys intact
CREATE TABLE dc (a INT, b INT, c INT NOT NULL PRIMARY KEY);
INSERT dc VALUES (1, 1, 10), (2, 1, 20);
ALTER TABLE dc DROP COLUMN a;
GO
INSERT dc VALUES (1, 30);
SELECT b, c FROM dc ORDER BY c;

--# drop column kept the key
INSERT dc VALUES (5, 30);

--# primary key on nullable column via alter
CREATE TABLE np (a INT NULL, b INT);
GO
ALTER TABLE np ADD CONSTRAINT pk_np PRIMARY KEY (a);

--# failed primary key left column nullable
INSERT np VALUES (NULL, 1);
SELECT COUNT(*) AS n FROM np;

--# alter column resets nullability
CREATE TABLE ac (a INT NOT NULL, b VARCHAR(10) NOT NULL);
GO
ALTER TABLE ac ALTER COLUMN b VARCHAR(20);
GO
INSERT ac VALUES (1, NULL);
SELECT COUNT(*) AS n FROM ac;

--# drop column used by check constraint
CREATE TABLE dchk (a INT, b INT, CONSTRAINT ck_b CHECK (b > 0));
GO
ALTER TABLE dchk DROP COLUMN b;

--# drop column used by computed column
CREATE TABLE dcmp (a INT, b INT, c AS (b * 2));
GO
ALTER TABLE dcmp DROP COLUMN b;

--# alter primary key column
CREATE TABLE apk (k INT NOT NULL PRIMARY KEY, v INT);
GO
ALTER TABLE apk ALTER COLUMN k BIGINT NOT NULL;

--# drop referenced primary key
CREATE TABLE rp (id INT NOT NULL CONSTRAINT pk_rp PRIMARY KEY);
CREATE TABLE rc (id INT, pid INT REFERENCES rp(id));
GO
ALTER TABLE rp DROP CONSTRAINT pk_rp;

--# procedure temp tables shadow the caller's
GO
CREATE PROC shadow AS BEGIN CREATE TABLE #t (a INT); INSERT #t VALUES (99); SELECT a FROM #t; END
GO
CREATE TABLE #t (a INT);
INSERT #t VALUES (1);
EXEC shadow;
SELECT a FROM #t;
DROP TABLE #t;

--# add column with values
CREATE TABLE wv (id INT PRIMARY KEY);
INSERT wv VALUES (1);
ALTER TABLE wv ADD c1 INT NULL DEFAULT 8 WITH VALUES, c2 INT NULL DEFAULT 9;
GO
SELECT id, c1, c2 FROM wv;
