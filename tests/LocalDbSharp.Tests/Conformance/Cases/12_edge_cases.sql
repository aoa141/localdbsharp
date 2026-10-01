CREATE TABLE T (Id INT PRIMARY KEY, Grp CHAR(1) NULL, Val INT NULL, Txt VARCHAR(20) NULL);
INSERT INTO T VALUES (1, 'a', 10, 'apple'), (2, 'a', NULL, 'Banana'), (3, 'b', 30, 'cherry'), (4, 'b', 30, NULL), (5, NULL, 5, 'apple '), (6, NULL, NULL, 'APPLE');

--# sort order of mixed strings
SELECT v FROM (VALUES ('a'), ('B'), ('_x'), ('1'), ('-z'), (' y'), ('Z'), ('e'), ('E2'), ('10'), ('9'), ('a b'), ('ab'), ('a-b'), ('A'), (''), ('#'), ('~')) AS t(v) ORDER BY v, CAST(v AS VARBINARY(10));

--# sort with accents
SELECT v FROM (VALUES (N'e'), (N'é'), (N'f'), (N'E'), (N'ë'), (N'd')) AS t(v) ORDER BY v, CAST(v AS VARBINARY(10));

--# sort order of names in varchar and nvarchar
SELECT v FROM (VALUES ('O''Brien'), ('Obrien'), ('Oakes'), ('Smith-Jones'), ('Smithjones'), ('Smith'), ('Smith Jones'), ('de la Cruz'), ('Delacroix'), ('co-op'), ('coop'), ('coo'), ('Zoë'), ('Zoe'), ('Zof')) AS t(v) ORDER BY v, CAST(v AS VARBINARY(30));
SELECT v FROM (VALUES (N'O''Brien'), (N'Obrien'), (N'Oakes'), (N'Smith-Jones'), (N'Smithjones'), (N'Smith'), (N'Smith Jones'), (N'de la Cruz'), (N'Delacroix'), (N'co-op'), (N'coop'), (N'coo'), (N'Zoë'), (N'Zoe'), (N'Zof')) AS t(v) ORDER BY v, CAST(v AS VARBINARY(60));

--# comparison operators follow the same order
SELECT CASE WHEN 'a-b' < 'ab' THEN 1 ELSE 0 END AS a, CASE WHEN '_' < '1' THEN 1 ELSE 0 END AS b, CASE WHEN '9' < 'A' THEN 1 ELSE 0 END AS c, CASE WHEN 'Z' < 'a' THEN 1 ELSE 0 END AS d,
       CASE WHEN 'é' > 'e' THEN 1 ELSE 0 END AS e, CASE WHEN 'é' < 'ea' THEN 1 ELSE 0 END AS f, CASE WHEN 'B' BETWEEN 'a' AND 'c' THEN 1 ELSE 0 END AS g, CASE WHEN 'ss' = 'ß' THEN 1 ELSE 0 END AS h;

--# min max of strings
SELECT UPPER(RTRIM(MIN(Txt))) AS mn, MAX(Txt) AS mx, COUNT(DISTINCT Txt) AS d FROM T;

--# like and trailing spaces
SELECT CASE WHEN 'abc ' LIKE 'abc' THEN 1 ELSE 0 END AS a, CASE WHEN 'abc' LIKE 'abc ' THEN 1 ELSE 0 END AS b, CASE WHEN 'abc' = 'abc ' THEN 1 ELSE 0 END AS c,
       CASE WHEN N'abc ' LIKE N'abc' THEN 1 ELSE 0 END AS d, LEN('abc ') AS e, CASE WHEN 'a%c' LIKE 'a[%]c' THEN 1 ELSE 0 END AS f, CASE WHEN 'a_c' LIKE 'a[_]c' THEN 1 ELSE 0 END AS g,
       CASE WHEN 'abc' LIKE '' THEN 1 ELSE 0 END AS h, CASE WHEN '' LIKE '%' THEN 1 ELSE 0 END AS i, CASE WHEN 'a' LIKE '[]' THEN 1 ELSE 0 END AS j;

--# char column semantics
SELECT Id, Grp + '|' AS g, LEN(Grp) AS l, CASE WHEN Grp = 'A' THEN 1 ELSE 0 END AS isA FROM T WHERE Grp IS NOT NULL ORDER BY Id;

--# group by null and count variants
SELECT Grp, COUNT(*) AS n, COUNT(Val) AS nv, SUM(Val) AS s, AVG(Val) AS a, MIN(Val) AS mn, MAX(Txt) AS mx FROM T GROUP BY Grp ORDER BY Grp;

--# aggregates ignoring nulls
SELECT SUM(Val) AS s, AVG(Val) AS a, COUNT(Val) AS c, COUNT(*) AS n, AVG(CAST(Val AS DECIMAL(10,2))) AS ad, SUM(Val) / COUNT(*) AS wrongAvg FROM T;
SELECT SUM(Val) AS s, MAX(Val) AS m, COUNT(Val) AS c FROM T WHERE Val IS NULL;

--# distinct treats nulls as equal
SELECT DISTINCT Grp FROM T ORDER BY Grp;
SELECT COUNT(DISTINCT Grp) AS a, COUNT(DISTINCT Val) AS b FROM T;

--# order by nulls first and desc
SELECT Id FROM T ORDER BY Val, Id;
SELECT Id FROM T ORDER BY Val DESC, Id DESC;
SELECT Id FROM T ORDER BY CASE WHEN Val IS NULL THEN 1 ELSE 0 END, Val, Id;

--# alias and column name interplay in order by
SELECT Id AS Val, Val AS Id FROM T ORDER BY Val;
SELECT Val, Id FROM T ORDER BY T.Val, T.Id;
SELECT Id * -1 AS Id FROM T ORDER BY Id;
SELECT Id * -1 AS Id FROM T ORDER BY T.Id;

--# inner names shadow outer names in correlated subqueries
SELECT o.Id, (SELECT COUNT(*) FROM T WHERE Val = o.Val) AS sameVal, (SELECT MAX(Id) FROM T i WHERE i.Grp = o.Grp) AS maxInGrp FROM T o ORDER BY o.Id;
SELECT Id FROM T o WHERE EXISTS (SELECT 1 FROM T WHERE Id = o.Id + 1 AND Val = o.Val) ORDER BY Id;

--# subquery in on clause and join condition with or
SELECT a.Id, b.Id AS other FROM T a JOIN T b ON b.Id = (SELECT MAX(Id) FROM T c WHERE c.Val = a.Val) WHERE a.Val IS NOT NULL ORDER BY a.Id;
SELECT a.Id, b.Id AS other FROM T a JOIN T b ON (a.Id = b.Id - 1 OR a.Id = b.Id + 4) ORDER BY a.Id, b.Id;

--# in with nulls on both sides
SELECT Id FROM T WHERE Val IN (SELECT Val FROM T WHERE Id IN (2, 3)) ORDER BY Id;
SELECT Id FROM T WHERE Val NOT IN (SELECT Val FROM T WHERE Id IN (2, 3)) ORDER BY Id;
SELECT Id FROM T WHERE Val NOT IN (10, 5) ORDER BY Id;
SELECT Id FROM T WHERE NOT (Val = 10) ORDER BY Id;
SELECT Id FROM T WHERE Val <> 10 OR Val IS NULL ORDER BY Id;

--# intersect and except treat nulls as equal
SELECT Grp, Val FROM T WHERE Id IN (2, 6) INTERSECT SELECT Grp, Val FROM T WHERE Id IN (4, 6);
SELECT Val FROM T EXCEPT SELECT Val FROM T WHERE Val > 6 ORDER BY Val;

--# union order by and names
SELECT Id AS k, Txt FROM T WHERE Id <= 2 UNION ALL SELECT Val, Grp FROM T WHERE Id = 3 ORDER BY k DESC;
SELECT Id, Txt FROM T WHERE Id = 1 UNION SELECT Id, Txt FROM T WHERE Id = 1 UNION ALL SELECT Id, Txt FROM T WHERE Id = 1;

--# top with ties and percent edge cases
SELECT TOP 1 WITH TIES Id FROM T ORDER BY Val DESC;
SELECT TOP 34 PERCENT Id FROM T ORDER BY Id;
SELECT TOP 100 PERCENT Id FROM T ORDER BY Id DESC;
SELECT TOP (1) Id FROM T WHERE Val IS NOT NULL ORDER BY Val, Id;

--# short circuit evaluation
SELECT CASE WHEN Val IS NULL OR 100 / Val > 5 THEN 'y' ELSE 'n' END AS r FROM T ORDER BY Id;
SELECT Id FROM T WHERE Val <> 0 AND 100 / Val >= 10 ORDER BY Id;
SELECT COALESCE(Val, 1 / 0) AS v FROM T WHERE Id = 1;
SELECT IIF(1 = 1, 1, 1 / 0) AS a, CASE WHEN 1 = 0 THEN 1 / 0 ELSE 2 END AS b, ISNULL(5, 1 / 0) AS c;

--# bigint and decimal limits
SELECT CAST(9223372036854775807 AS BIGINT) AS a, CAST(-9223372036854775808 AS BIGINT) AS b, CAST(2147483647 AS BIGINT) + 1 AS c,
       CAST(99999999999999999999 AS DECIMAL(20,0)) AS d, 12345678901234567890.123456789 AS e;

--# bigint overflow
SELECT CAST(9223372036854775807 AS BIGINT) + 1 AS a;

--# decimal precision loss on large multiplications
SELECT CAST(123456789012345.123456 AS DECIMAL(21,6)) * CAST(1000.5 AS DECIMAL(10,4)) AS a, CAST(1 AS DECIMAL(38,10)) / 3 AS b, CAST(10 AS DECIMAL(38,0)) / 3 AS c;

--# integer division and modulo signs
SELECT 7 / -2 AS a, -7 / -2 AS b, 7 % -2 AS c, -7 % 2 AS d, 0 / 5 AS e, 5 / 10 * 10 AS f, 5 * 10 / 10 AS g, 7.0 / 2 AS h, 7 / 2.0 AS i, CAST(7 AS FLOAT) / 2 AS j;

--# functions inside aggregates and aggregates inside functions
SELECT SUM(ISNULL(Val, 0)) AS a, ISNULL(SUM(Val), 0) AS b, MAX(LEN(Txt)) AS c, UPPER(MAX(Txt)) AS d, SUM(CASE WHEN Grp = 'a' THEN 1 ELSE 0 END) AS e,
       COUNT(NULLIF(Val, 30)) AS f, ROUND(AVG(Val * 1.0), 2) AS g, CAST(SUM(Val) AS VARCHAR(10)) + '!' AS h FROM T;

--# string concatenation with numbers and nulls
SELECT Txt + ':' + CAST(Val AS VARCHAR(10)) AS a, CONCAT(Txt, ':', Val) AS b, ISNULL(Txt, '?') + '/' + ISNULL(CAST(Val AS VARCHAR(10)), '?') AS c FROM T ORDER BY Id;

--# updatable cte for dedupe delete
CREATE TABLE Dup (Id INT IDENTITY PRIMARY KEY, Email VARCHAR(30), Added DATE);
INSERT INTO Dup (Email, Added) VALUES ('a@x', '2024-01-01'), ('b@x', '2024-01-02'), ('a@x', '2024-01-03'), ('a@x', '2024-01-04'), ('b@x', '2024-01-05');
WITH d AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY Email ORDER BY Added DESC) AS rn FROM Dup)
DELETE FROM d WHERE rn > 1;
SELECT @@ROWCOUNT AS rc;
SELECT Id, Email FROM Dup ORDER BY Id;

--# updatable cte for update
WITH ranked AS (SELECT Id, Email, ROW_NUMBER() OVER (ORDER BY Id) AS rn FROM Dup)
UPDATE ranked SET Email = Email + CAST(rn AS VARCHAR(5));
SELECT Id, Email FROM Dup ORDER BY Id;
WITH firstOne AS (SELECT TOP 1 * FROM Dup ORDER BY Id DESC)
UPDATE firstOne SET Added = '2000-01-01';
SELECT Id, Added FROM Dup ORDER BY Id;

--# updatable view
GO
CREATE VIEW DupView AS SELECT Id, Email FROM Dup WHERE Id > 2;
GO
UPDATE DupView SET Email = 'via view';
DELETE FROM DupView WHERE Id = 99;
INSERT INTO DupView (Email) VALUES ('inserted via view');
SELECT Id, Email FROM Dup ORDER BY Id;

--# defaults checks and computed columns with functions
GO
CREATE FUNCTION dbo.Clamp (@v INT) RETURNS INT AS BEGIN RETURN CASE WHEN @v > 100 THEN 100 WHEN @v < 0 THEN 0 ELSE @v END END
GO
CREATE TABLE Fn (Id INT IDENTITY PRIMARY KEY, Raw INT NOT NULL, Clamped AS dbo.Clamp(Raw), Stamp VARCHAR(10) NOT NULL DEFAULT UPPER('new'), Code VARCHAR(5) NULL CHECK (Code IS NULL OR LEN(Code) = 3));
INSERT INTO Fn (Raw, Code) VALUES (150, 'abc'), (-5, NULL), (50, 'xyz');
SELECT Id, Raw, Clamped, Stamp, Code FROM Fn ORDER BY Id;

--# check constraint with function length
INSERT INTO Fn (Raw, Code) VALUES (1, 'toolong');

--# identity is consumed by failed inserts
INSERT INTO Fn (Raw, Code) VALUES (2, 'ok1');
SELECT MAX(Id) AS lastId FROM Fn;

--# multiple anchors and recursive members
WITH r AS (
    SELECT 1 AS n, 'a' AS src UNION ALL SELECT 100, 'b'
    UNION ALL
    SELECT n + 1, src FROM r WHERE n < 3 OR (n >= 100 AND n < 102))
SELECT n, src FROM r ORDER BY n;

--# recursive cte with join to table and depth limit
WITH chain AS (SELECT Id, Val, 1 AS depth FROM T WHERE Id = 1 UNION ALL SELECT t.Id, t.Val, c.depth + 1 FROM T t JOIN chain c ON t.Id = c.Id + 1 WHERE c.depth < 4)
SELECT Id, depth FROM chain ORDER BY Id;

--# merge with delete and output deleted
CREATE TABLE Tgt (Id INT PRIMARY KEY, V VARCHAR(5));
INSERT INTO Tgt VALUES (1, 'a'), (2, 'b'), (3, 'c');
MERGE Tgt AS t USING (SELECT 2 AS Id, 'B' AS V UNION ALL SELECT 4, 'D') AS s ON s.Id = t.Id
WHEN MATCHED THEN DELETE
WHEN NOT MATCHED BY TARGET THEN INSERT VALUES (s.Id, s.V)
WHEN NOT MATCHED BY SOURCE AND t.Id = 3 THEN UPDATE SET V = 'kept'
OUTPUT $action AS act, COALESCE(inserted.Id, deleted.Id) AS id, deleted.V AS oldV, inserted.V AS newV;
SELECT Id, V FROM Tgt ORDER BY Id;

--# delete and update with output and where on joined tables
DELETE t OUTPUT deleted.Id, deleted.V FROM Tgt t JOIN (VALUES (1), (4)) AS k(Id) ON k.Id = t.Id;
UPDATE t SET V = UPPER(V) OUTPUT inserted.V AS newV, deleted.V AS oldV FROM Tgt t WHERE t.Id = 3;

--# temp table visibility across procedures
GO
CREATE PROC FillTemp AS INSERT INTO #work VALUES (42);
GO
CREATE PROC MakeTemp AS BEGIN CREATE TABLE #inner (x INT); INSERT INTO #inner VALUES (1); SELECT COUNT(*) AS n FROM #inner; END
GO
CREATE TABLE #work (x INT);
EXEC FillTemp;
EXEC MakeTemp;
SELECT x FROM #work;
SELECT OBJECT_ID('tempdb..#inner') AS innerGone;
DROP TABLE #work;

--# variables in top offset and like
DECLARE @top INT = 2, @skip INT = 1, @pat VARCHAR(10) = 'a%';
SELECT TOP (@top) Id FROM T WHERE Txt LIKE @pat ORDER BY Id;
SELECT Id FROM T ORDER BY Id OFFSET @skip ROWS FETCH NEXT @top ROWS ONLY;
SELECT Id FROM T WHERE Txt LIKE '%' + 'an' + '%';

--# set and select assignment edge cases
DECLARE @a INT = 1, @b INT = 2, @c VARCHAR(20);
SELECT @a = @b, @b = @a;
SET @c = (SELECT Txt FROM T WHERE Id = 999);
SELECT @a AS a, @b AS b, @c AS c;
SELECT @a = Id FROM T ORDER BY Id DESC;
SELECT @a AS lastAssigned;
SELECT @a = COUNT(*), @b = MAX(Val) FROM T WHERE 1 = 0;
SELECT @a AS cnt, @b AS mx;

--# set from multi row subquery fails
DECLARE @x INT;
SET @x = (SELECT Id FROM T);

--# nested functions and string building
SELECT REPLACE(UPPER(LEFT(Txt, 1)) + LOWER(SUBSTRING(Txt, 2, 100)), ' ', '') AS proper, REVERSE(RTRIM(Txt)) AS rev,
       RIGHT('0000' + CAST(Id AS VARCHAR(4)), 4) AS padded, CHARINDEX('p', Txt) AS p, STUFF(Txt, 1, 1, '*') AS masked FROM T WHERE Txt IS NOT NULL ORDER BY Id;

--# string concatenation is capped at 8000 unless a max type takes part
DECLARE @a VARCHAR(8000) = REPLICATE('a', 5000), @m VARCHAR(MAX) = REPLICATE('m', 5000), @n NVARCHAR(4000) = REPLICATE(N'n', 3000);
SELECT LEN(@a + @a) AS capped, LEN(@m + @a) AS notCapped, LEN(@n + @n) AS unicodeCapped, LEN(@a + @n) AS mixedCapped, LEN(CAST(@a AS VARCHAR(MAX)) + @a) AS castMax,
       LEN(REPLICATE('x', 9000)) AS rep, LEN(REPLICATE(CAST('x' AS VARCHAR(MAX)), 9000)) AS repMax, LEN(CONCAT(@a, @a)) AS concatFn;

--# date arithmetic edge cases
SELECT DATEADD(month, 1, '2024-01-31') AS a, DATEADD(month, -1, '2024-03-31') AS b, DATEADD(year, -4, '2024-02-29') AS c, DATEDIFF(day, '2024-02-28', '2024-03-01') AS d,
       DATEDIFF(year, '2024-12-31 23:59:59', '2025-01-01 00:00:00') AS e, DATEADD(day, -1, '2024-01-01') AS f, DATEPART(weekday, '2024-01-07') AS g, DATEPART(week, '2024-01-07') AS h,
       DATEPART(week, '2024-12-31') AS i, DATEDIFF(week, '2024-01-06', '2024-01-07') AS j, EOMONTH('2024-02-01', -1) AS k, DATEDIFF(month, '2024-01-31', '2024-02-29') AS l;
