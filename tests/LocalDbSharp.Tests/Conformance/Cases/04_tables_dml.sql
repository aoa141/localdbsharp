CREATE TABLE dbo.Dept (
    DeptId INT NOT NULL PRIMARY KEY,
    Name VARCHAR(30) NOT NULL UNIQUE,
    Budget DECIMAL(12,2) NULL
);
CREATE TABLE dbo.Emp (
    EmpId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(40) NOT NULL,
    DeptId INT NULL REFERENCES dbo.Dept(DeptId),
    Salary DECIMAL(10,2) NOT NULL CONSTRAINT DF_Emp_Salary DEFAULT 1000,
    Hired DATE NOT NULL DEFAULT '2020-01-01',
    Active BIT NOT NULL DEFAULT 1,
    Code CHAR(3) NULL,
    CONSTRAINT CK_Emp_Salary CHECK (Salary >= 0)
);
INSERT INTO Dept (DeptId, Name, Budget) VALUES (10, 'Sales', 1000.50), (20, 'Tech', 25000), (30, 'Ops', NULL);
INSERT INTO Emp (Name, DeptId, Salary, Hired, Code) VALUES
    (N'Ann', 10, 3000, '2019-03-01', 'A1'),
    (N'Bob', 10, 2500.5, '2020-06-15', 'B2'),
    (N'Cid', 20, 5000, '2018-01-20', NULL),
    (N'Dee', 20, 4200, '2021-11-05', 'D4'),
    (N'Eve', NULL, 1500, '2022-02-02', 'E5');

--# select all
SELECT * FROM Emp ORDER BY EmpId;

--# column types and char padding
SELECT Name, Salary, Hired, Active, Code, DATALENGTH(Code) AS codeLen, Code + '|' AS padded FROM Emp WHERE EmpId <= 2 ORDER BY EmpId;

--# insert with defaults and scope_identity
INSERT INTO Emp (Name) VALUES (N'Fay');
SELECT SCOPE_IDENTITY() AS id, @@IDENTITY AS id2, @@ROWCOUNT AS rc;
SELECT EmpId, Name, DeptId, Salary, Hired, Active, Code FROM Emp WHERE Name = N'Fay';

--# insert default keyword and default values
CREATE TABLE T1 (Id INT IDENTITY(5, 5) PRIMARY KEY, A INT DEFAULT 7, B VARCHAR(10) DEFAULT 'x', C INT NULL);
INSERT INTO T1 DEFAULT VALUES;
INSERT INTO T1 (A, B) VALUES (DEFAULT, 'y'), (1, DEFAULT);
INSERT INTO T1 VALUES (2, 'z', 3);
SELECT * FROM T1 ORDER BY Id;

--# insert select
CREATE TABLE T2 (Id INT, Name NVARCHAR(40));
INSERT INTO T2 (Id, Name) SELECT EmpId, UPPER(Name) FROM Emp WHERE DeptId = 10;
INSERT T2 SELECT EmpId + 100, Name FROM Emp WHERE DeptId = 20;
SELECT * FROM T2 ORDER BY Id;
SELECT @@ROWCOUNT AS rc;

--# update simple
UPDATE Emp SET Salary = Salary * 1.1, Code = 'ZZ' WHERE DeptId = 10;
SELECT @@ROWCOUNT AS rc;
SELECT EmpId, Salary, Code FROM Emp WHERE DeptId = 10 ORDER BY EmpId;

--# update with compound operators
UPDATE Emp SET Salary += 100, Name += N'!' WHERE EmpId = 3;
UPDATE Emp SET Salary -= 50 WHERE EmpId = 3;
UPDATE Emp SET Salary *= 2 WHERE EmpId = 3;
SELECT Name, Salary FROM Emp WHERE EmpId = 3;

--# update uses old values on the right side
CREATE TABLE Swap (a INT, b INT);
INSERT INTO Swap VALUES (1, 2), (3, 4);
UPDATE Swap SET a = b, b = a;
SELECT * FROM Swap ORDER BY a;

--# update from join
UPDATE e SET e.Salary = e.Salary + d.Budget / 1000
FROM Emp e JOIN Dept d ON d.DeptId = e.DeptId WHERE d.Name = 'Tech';
SELECT EmpId, Salary FROM Emp WHERE DeptId = 20 ORDER BY EmpId;

--# update with subquery and top
UPDATE Dept SET Budget = (SELECT SUM(Salary) FROM Emp WHERE Emp.DeptId = Dept.DeptId) WHERE DeptId IN (10, 30);
SELECT DeptId, Budget FROM Dept ORDER BY DeptId;
UPDATE TOP (1) T2 SET Name = N'top';
SELECT COUNT(*) AS n FROM T2 WHERE Name = N'top';

--# update variable assignment
DECLARE @total DECIMAL(12,2) = 0, @last INT;
UPDATE Emp SET @total = @total + Salary, @last = EmpId WHERE DeptId = 10;
SELECT @total AS total, CASE WHEN @last IN (1, 2) THEN 1 ELSE 0 END AS lastOk;

--# delete
DELETE FROM T2 WHERE Id > 100;
SELECT @@ROWCOUNT AS rc;
DELETE T2;
SELECT COUNT(*) AS n FROM T2;

--# delete from join
INSERT INTO T2 SELECT EmpId, Name FROM Emp;
DELETE t FROM T2 t JOIN Emp e ON e.EmpId = t.Id WHERE e.DeptId = 20;
SELECT Id FROM T2 ORDER BY Id;
DELETE TOP (2) FROM T2;
SELECT COUNT(*) AS n FROM T2;

--# truncate resets identity
CREATE TABLE T3 (Id INT IDENTITY PRIMARY KEY, V INT);
INSERT INTO T3 (V) VALUES (1), (2), (3);
DELETE FROM T3;
INSERT INTO T3 (V) VALUES (4);
SELECT Id, V FROM T3;
TRUNCATE TABLE T3;
INSERT INTO T3 (V) VALUES (5);
SELECT Id, V FROM T3;

--# identity_insert
SET IDENTITY_INSERT T3 ON;
INSERT INTO T3 (Id, V) VALUES (50, 6);
SET IDENTITY_INSERT T3 OFF;
INSERT INTO T3 (V) VALUES (7);
SELECT Id, V FROM T3 ORDER BY Id;
SELECT IDENT_CURRENT('T3') AS cur;

--# explicit identity without identity_insert
INSERT INTO T3 (Id, V) VALUES (99, 1);

--# primary key violation
INSERT INTO Dept (DeptId, Name) VALUES (10, 'Dup');

--# unique violation
INSERT INTO Dept (DeptId, Name) VALUES (40, 'sales');

--# not null violation
INSERT INTO Dept (DeptId, Name) VALUES (41, NULL);

--# check violation
INSERT INTO Emp (Name, Salary) VALUES (N'Neg', -1);

--# foreign key violation on insert
INSERT INTO Emp (Name, DeptId) VALUES (N'Orphan', 999);

--# foreign key violation on delete
DELETE FROM Dept WHERE DeptId = 10;

--# foreign key violation on update
UPDATE Emp SET DeptId = 12345 WHERE EmpId = 1;

--# string truncation
INSERT INTO Dept (DeptId, Name) VALUES (42, 'This department name is far too long for the column');

--# failed statements left no rows behind
SELECT COUNT(*) AS depts FROM Dept;
SELECT COUNT(*) AS emps FROM Emp;

--# multi row insert is atomic
INSERT INTO Dept (DeptId, Name) VALUES (50, 'New'), (10, 'Dup again'), (51, 'Other');
GO
SELECT COUNT(*) AS n FROM Dept WHERE DeptId >= 50;

--# statement error does not stop the batch
INSERT INTO Dept (DeptId, Name) VALUES (10, 'Dup');
INSERT INTO Dept (DeptId, Name) VALUES (60, 'After error');
GO
SELECT Name FROM Dept WHERE DeptId = 60;

--# column count mismatch
INSERT INTO Dept VALUES (1, 'x');

--# invalid column in insert
INSERT INTO Dept (DeptId, Nope) VALUES (1, 'x');

--# invalid table
SELECT * FROM NoSuchTable;

--# invalid column
SELECT Nope FROM Dept;

--# ambiguous column
SELECT DeptId FROM Emp e JOIN Dept d ON d.DeptId = e.DeptId;

--# cascade delete and set null
CREATE TABLE P (Id INT PRIMARY KEY);
CREATE TABLE C1 (Id INT PRIMARY KEY, Pid INT REFERENCES P(Id) ON DELETE CASCADE);
CREATE TABLE C2 (Id INT PRIMARY KEY, Pid INT NULL, CONSTRAINT FK_C2 FOREIGN KEY (Pid) REFERENCES P(Id) ON DELETE SET NULL);
INSERT INTO P VALUES (1), (2);
INSERT INTO C1 VALUES (1, 1), (2, 1), (3, 2);
INSERT INTO C2 VALUES (1, 1), (2, 2);
DELETE FROM P WHERE Id = 1;
SELECT Id, Pid FROM C1 ORDER BY Id;
SELECT Id, Pid FROM C2 ORDER BY Id;

--# composite keys
CREATE TABLE CK (a INT NOT NULL, b VARCHAR(5) NOT NULL, v INT, CONSTRAINT PK_CK PRIMARY KEY (a, b));
INSERT INTO CK VALUES (1, 'x', 1), (1, 'y', 2), (2, 'x', 3);
SELECT COUNT(*) AS n FROM CK;
INSERT INTO CK VALUES (1, 'X', 9);

--# computed columns
CREATE TABLE Comp (Qty INT, Price DECIMAL(8,2), Total AS (Qty * Price), Label AS ('#' + CAST(Qty AS VARCHAR(10))));
INSERT INTO Comp (Qty, Price) VALUES (2, 9.99), (3, 1.5);
UPDATE Comp SET Qty = 10 WHERE Qty = 3;
SELECT * FROM Comp ORDER BY Qty;

--# alter table add and drop column
ALTER TABLE T1 ADD D INT NULL, E VARCHAR(5) NOT NULL DEFAULT 'def';
GO
SELECT TOP 1 Id, D, E FROM T1 ORDER BY Id;
ALTER TABLE T1 DROP COLUMN C;
ALTER TABLE T1 ALTER COLUMN B VARCHAR(50) NOT NULL;
GO
INSERT INTO T1 (A, B, D) VALUES (9, 'a longer value than ten', 1);
SELECT A, B, D, E FROM T1 WHERE A = 9;

--# alter table add constraint
ALTER TABLE T1 ADD CONSTRAINT UQ_T1_B UNIQUE (B);
INSERT INTO T1 (A, B) VALUES (1, 'a longer value than ten');

--# drop constraint then insert
ALTER TABLE T1 DROP CONSTRAINT UQ_T1_B;
INSERT INTO T1 (A, B) VALUES (1, 'a longer value than ten');
SELECT COUNT(*) AS n FROM T1 WHERE B = 'a longer value than ten';

--# unique index
CREATE UNIQUE INDEX IX_T2_Id ON T2 (Id);
INSERT INTO T2 VALUES (1000, N'a');
INSERT INTO T2 VALUES (1000, N'b');

--# scans return rows in clustered key order
CREATE TABLE Ord1 (Id INT PRIMARY KEY, V VARCHAR(5));
INSERT INTO Ord1 VALUES (5, 'e'), (1, 'a'), (3, 'c');
INSERT INTO Ord1 VALUES (4, 'd');
INSERT INTO Ord1 VALUES (2, 'b');
SELECT Id, V FROM Ord1;
UPDATE Ord1 SET Id = 10 WHERE Id = 1;
DELETE FROM Ord1 WHERE Id = 3;
SELECT Id FROM Ord1;
SELECT TOP 2 Id FROM Ord1;
CREATE TABLE Ord2 (Code VARCHAR(5) NOT NULL, N INT NOT NULL, PRIMARY KEY (Code, N));
INSERT INTO Ord2 VALUES ('b', 2), ('a', 9), ('B', 1), ('a', 1), ('c', 0);
SELECT Code, N FROM Ord2;
CREATE TABLE Heap1 (Id INT PRIMARY KEY NONCLUSTERED, V INT);
INSERT INTO Heap1 VALUES (3, 1), (1, 2), (2, 3);
SELECT Id, V FROM Heap1;
CREATE TABLE Heap2 (Id INT, V INT);
INSERT INTO Heap2 VALUES (3, 1), (1, 2), (2, 3);
SELECT Id FROM Heap2;
CREATE CLUSTERED INDEX CIX_Heap2 ON Heap2 (V DESC, Id);
SELECT Id FROM Heap2;
CREATE TABLE Desc1 (A INT NOT NULL, B INT NOT NULL, CONSTRAINT PK_Desc1 PRIMARY KEY (A DESC, B));
INSERT INTO Desc1 VALUES (1, 2), (2, 1), (1, 1), (2, 2);
SELECT A, B FROM Desc1;

--# identity values follow the order of the source scan
CREATE TABLE Src1 (K INT PRIMARY KEY);
INSERT INTO Src1 VALUES (30), (10), (20);
CREATE TABLE Dst1 (Id INT IDENTITY PRIMARY KEY, K INT);
INSERT INTO Dst1 (K) SELECT K FROM Src1;
SELECT Id, K FROM Dst1;

--# key violations are reported before check violations
CREATE TABLE Prec (Id INT PRIMARY KEY, Q INT NOT NULL CHECK (Q >= 0), R INT NULL REFERENCES Ord1(Id));
INSERT INTO Prec VALUES (1, 1, NULL);
INSERT INTO Prec VALUES (1, -1, 999);

--# check violations are reported when the key is fine
INSERT INTO Prec VALUES (2, -1, 999);

--# null violations are reported first
INSERT INTO Prec VALUES (1, NULL, 999);

--# insert reports the first problem in column order
CREATE TABLE Prec2 (a DECIMAL(4,2) NULL, b INT NOT NULL, c VARCHAR(3) NULL, d INT NULL);
INSERT INTO Prec2 VALUES (1, 1, 'x', 1);
INSERT INTO Prec2 VALUES (99999, NULL, 'x', 1);

--# insert null before later truncation
INSERT INTO Prec2 VALUES (1, NULL, 'toolong', 1);

--# insert null before later conversion error
INSERT INTO Prec2 VALUES (1, NULL, 'x', 'abc');

--# insert overflow before later truncation
INSERT INTO Prec2 VALUES (99999, 1, 'toolong', 1);

--# update converts every column before checking nulls
UPDATE Prec2 SET b = NULL, c = 'toolong';

--# update overflow before null
UPDATE Prec2 SET a = 99999, b = NULL;

--# update null
UPDATE Prec2 SET b = NULL, c = 'ok';

--# drop table if exists
DROP TABLE IF EXISTS NoSuchTable;
DROP TABLE IF EXISTS Swap;
SELECT OBJECT_ID('Swap') AS id;

--# drop missing table
DROP TABLE NoSuchTable;

--# create existing table
CREATE TABLE Dept (x INT);

--# select into
SELECT EmpId, Name, Salary * 2 AS Dbl INTO EmpCopy FROM Emp WHERE DeptId = 10;
SELECT @@ROWCOUNT AS rc;
SELECT * FROM EmpCopy ORDER BY EmpId;

--# temp tables
CREATE TABLE #tmp (Id INT PRIMARY KEY, V VARCHAR(10));
INSERT INTO #tmp VALUES (1, 'a'), (2, 'b');
SELECT * INTO #tmp2 FROM #tmp WHERE Id = 2;
SELECT t.V AS v1, u.V AS v2 FROM #tmp t CROSS JOIN #tmp2 u ORDER BY t.Id;
SELECT CASE WHEN OBJECT_ID('tempdb..#tmp') IS NOT NULL THEN 1 ELSE 0 END AS present;
DROP TABLE #tmp, #tmp2;

--# table variables
DECLARE @t TABLE (Id INT IDENTITY PRIMARY KEY, Name VARCHAR(20) NOT NULL, Qty INT DEFAULT 1);
INSERT INTO @t (Name) VALUES ('a'), ('b');
INSERT INTO @t (Name, Qty) SELECT 'c', 5;
UPDATE @t SET Qty = Qty + 1 WHERE Name <> 'a';
DELETE FROM @t WHERE Name = 'b';
SELECT * FROM @t ORDER BY Id;

--# schemas
CREATE SCHEMA app;
GO
CREATE TABLE app.Items (Id INT PRIMARY KEY, Name VARCHAR(10));
INSERT INTO app.Items VALUES (1, 'one');
SELECT i.Name FROM app.Items AS i;
SELECT [Name] FROM [app].[Items];

--# bracketed and quoted identifiers
CREATE TABLE [My Table] ([my col] INT, [select] VARCHAR(5), "quoted" INT);
INSERT INTO [My Table] ([my col], [select], "quoted") VALUES (1, 'x', 2);
SELECT [my col], [select], "quoted", [my col] + "quoted" AS [the sum] FROM [My Table];
