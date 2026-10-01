CREATE TABLE Dept (DeptId INT PRIMARY KEY, Name VARCHAR(30) NOT NULL);
CREATE TABLE Emp (EmpId INT PRIMARY KEY, Name VARCHAR(40) NOT NULL, DeptId INT NULL, Salary DECIMAL(10,2) NOT NULL, Hired DATE NOT NULL, ManagerId INT NULL);
CREATE TABLE Sales (SaleId INT PRIMARY KEY, EmpId INT NOT NULL, Amount MONEY NOT NULL, Qty INT NOT NULL, SoldOn DATETIME NOT NULL, Region VARCHAR(10) NULL);
INSERT INTO Dept VALUES (10, 'Sales'), (20, 'Tech'), (30, 'Ops'), (40, 'Empty');
INSERT INTO Emp VALUES
    (1, 'Ann', 10, 3000, '2019-03-01', NULL), (2, 'Bob', 10, 2500.50, '2020-06-15', 1), (3, 'Cid', 20, 5000, '2018-01-20', 1),
    (4, 'Dee', 20, 4200, '2021-11-05', 3), (5, 'Eve', NULL, 1500, '2022-02-02', 3), (6, 'Fay', 30, 2500.50, '2020-06-15', 1),
    (7, 'Gus', 20, 5000, '2023-07-07', 3);
INSERT INTO Sales VALUES
    (1, 1, 100.00, 2, '2024-01-05 10:00', 'North'), (2, 1, 250.50, 1, '2024-01-20 12:30', 'South'), (3, 2, 75.25, 5, '2024-02-01 09:15', 'North'),
    (4, 2, 300.00, 3, '2024-02-14 16:45', NULL), (5, 3, 999.99, 1, '2024-03-03 08:00', 'East'), (6, 6, 50.00, 10, '2024-03-10 11:11', 'North'),
    (7, 1, 20.00, 1, '2024-03-15 14:00', 'East'), (8, 6, 410.10, 4, '2024-04-01 13:00', 'South');

--# where and order by
SELECT Name, Salary FROM Emp WHERE Salary > 2500 AND DeptId IS NOT NULL ORDER BY Salary DESC, Name;

--# order by nulls and multiple keys
SELECT Name, DeptId FROM Emp ORDER BY DeptId, Name DESC;
SELECT Name, DeptId FROM Emp ORDER BY DeptId DESC, Name;

--# order by ordinal alias and expression
SELECT Name, Salary * 12 AS Yearly FROM Emp ORDER BY 2 DESC, 1;
SELECT Name, Salary * 12 AS Yearly FROM Emp ORDER BY Yearly, Name;
SELECT Name FROM Emp ORDER BY LEN(Name) + EmpId DESC;
SELECT Name FROM Emp ORDER BY Hired, EmpId;

--# order by column not in select list
SELECT Name FROM Emp ORDER BY Salary, EmpId;

--# top variants
SELECT TOP 3 Name FROM Emp ORDER BY EmpId;
SELECT TOP (2) Name, Salary FROM Emp ORDER BY Salary DESC, EmpId;
SELECT TOP 2 WITH TIES Name, Salary FROM Emp ORDER BY Salary DESC;
SELECT TOP 50 PERCENT EmpId FROM Emp ORDER BY EmpId;
SELECT TOP 0 Name FROM Emp;
DECLARE @n INT = 2;
SELECT TOP (@n) EmpId FROM Emp ORDER BY EmpId DESC;

--# offset fetch
SELECT EmpId FROM Emp ORDER BY EmpId OFFSET 2 ROWS;
SELECT EmpId FROM Emp ORDER BY EmpId OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY;
SELECT EmpId FROM Emp ORDER BY EmpId OFFSET 0 ROWS FETCH FIRST 1 ROW ONLY;
SELECT EmpId FROM Emp ORDER BY EmpId OFFSET 100 ROWS FETCH NEXT 3 ROWS ONLY;

--# distinct
SELECT DISTINCT DeptId FROM Emp ORDER BY DeptId;
SELECT DISTINCT Salary, Hired FROM Emp ORDER BY Salary, Hired;
SELECT COUNT(DISTINCT Salary) AS a, COUNT(DISTINCT DeptId) AS b, SUM(DISTINCT Salary) AS c FROM Emp;

--# aggregates without group by
SELECT COUNT(*) AS cnt, COUNT(DeptId) AS cntDept, SUM(Salary) AS total, AVG(Salary) AS avgSal, MIN(Salary) AS minSal, MAX(Salary) AS maxSal,
       MIN(Name) AS firstName, MAX(Hired) AS lastHired FROM Emp;

--# aggregates over empty set
SELECT COUNT(*) AS cnt, SUM(Salary) AS total, AVG(Salary) AS a, MIN(Name) AS mn, MAX(Salary) AS mx FROM Emp WHERE 1 = 0;

--# aggregate types
SELECT SUM(Qty) AS sumInt, AVG(Qty) AS avgInt, SUM(Amount) AS sumMoney, AVG(Amount) AS avgMoney, SUM(CAST(Qty AS BIGINT)) AS sumBig,
       AVG(CAST(Qty AS FLOAT)) AS avgFloat, SUM(CAST(Qty AS DECIMAL(5,1))) AS sumDec, AVG(CAST(Qty AS DECIMAL(5,1))) AS avgDec,
       COUNT_BIG(*) AS cb, SUM(CAST(Qty AS TINYINT)) AS sumTiny, AVG(CAST(Qty AS SMALLINT)) AS avgSmall FROM Sales;

--# statistical aggregates
SELECT STDEV(Qty) AS sd, STDEVP(Qty) AS sdp, VAR(Qty) AS v, VARP(Qty) AS vp FROM Sales;

--# group by
SELECT DeptId, COUNT(*) AS n, SUM(Salary) AS total, AVG(Salary) AS average FROM Emp GROUP BY DeptId ORDER BY DeptId;

--# group by expression and having
SELECT YEAR(Hired) AS yr, COUNT(*) AS n FROM Emp GROUP BY YEAR(Hired) HAVING COUNT(*) > 1 ORDER BY yr;
SELECT DeptId FROM Emp GROUP BY DeptId HAVING SUM(Salary) > 5000 AND DeptId IS NOT NULL ORDER BY DeptId;

--# group by multiple columns
SELECT Region, EmpId, SUM(Amount) AS amt, SUM(Qty) AS qty FROM Sales GROUP BY Region, EmpId ORDER BY Region, EmpId;

--# group by with expression on aggregates
SELECT EmpId, SUM(Amount) / SUM(Qty) AS unit, MAX(Amount) - MIN(Amount) AS spread, COUNT(*) * 1.0 / 8 AS share FROM Sales GROUP BY EmpId ORDER BY EmpId;

--# group by case insensitive strings
SELECT UPPER(v) AS v, COUNT(*) AS n FROM (VALUES ('a'), ('A'), ('b'), ('a ')) AS t(v) GROUP BY v ORDER BY 1;

--# having without group by
SELECT COUNT(*) AS n FROM Emp HAVING COUNT(*) > 100;
SELECT COUNT(*) AS n FROM Emp HAVING COUNT(*) > 1;

--# inner join
SELECT e.Name, d.Name AS Dept FROM Emp e INNER JOIN Dept d ON d.DeptId = e.DeptId ORDER BY e.EmpId;

--# left join
SELECT e.Name, d.Name AS Dept FROM Emp e LEFT JOIN Dept d ON d.DeptId = e.DeptId ORDER BY e.EmpId;
SELECT d.Name, COUNT(e.EmpId) AS n FROM Dept d LEFT OUTER JOIN Emp e ON e.DeptId = d.DeptId GROUP BY d.Name ORDER BY d.Name;

--# right and full join
SELECT e.Name, d.Name AS Dept FROM Emp e RIGHT JOIN Dept d ON d.DeptId = e.DeptId ORDER BY d.DeptId, e.EmpId;
SELECT e.Name, d.Name AS Dept FROM Emp e FULL OUTER JOIN Dept d ON d.DeptId = e.DeptId ORDER BY COALESCE(e.EmpId, 99), d.DeptId;

--# cross join and comma join
SELECT COUNT(*) AS n FROM Emp CROSS JOIN Dept;
SELECT e.Name, d.Name AS Dept FROM Emp e, Dept d WHERE e.DeptId = d.DeptId AND d.Name = 'Tech' ORDER BY e.EmpId;

--# self join
SELECT e.Name, m.Name AS Manager FROM Emp e LEFT JOIN Emp m ON m.EmpId = e.ManagerId ORDER BY e.EmpId;

--# join with non equi and compound conditions
SELECT e.Name, s.SaleId FROM Emp e JOIN Sales s ON s.EmpId = e.EmpId AND s.Amount > 100 ORDER BY s.SaleId;
SELECT a.EmpId AS a, b.EmpId AS b FROM Emp a JOIN Emp b ON a.Salary < b.Salary AND a.DeptId = b.DeptId ORDER BY a.EmpId, b.EmpId;
SELECT e.Name, s.SaleId FROM Emp e LEFT JOIN Sales s ON s.EmpId = e.EmpId AND s.Region = 'North' ORDER BY e.EmpId, s.SaleId;

--# three table join with aggregation
SELECT d.Name, COUNT(s.SaleId) AS sales, ISNULL(SUM(s.Amount), 0) AS amount
FROM Dept d LEFT JOIN Emp e ON e.DeptId = d.DeptId LEFT JOIN Sales s ON s.EmpId = e.EmpId
GROUP BY d.Name ORDER BY amount DESC, d.Name;

--# join on mismatched types
SELECT e.EmpId, v.code FROM Emp e JOIN (VALUES ('1'), ('3'), ('07')) AS v(code) ON e.EmpId = v.code ORDER BY e.EmpId;

--# join with null keys never matches
SELECT COUNT(*) AS n FROM Emp a JOIN Emp b ON a.DeptId = b.DeptId WHERE a.EmpId = 5;

--# star expansion
SELECT d.*, e.Name AS EmpName FROM Dept d JOIN Emp e ON e.DeptId = d.DeptId WHERE e.EmpId = 1;
SELECT e.EmpId, d.* FROM Emp e JOIN Dept d ON e.DeptId = d.DeptId WHERE e.EmpId = 3;

--# derived tables
SELECT x.DeptId, x.total FROM (SELECT DeptId, SUM(Salary) AS total FROM Emp GROUP BY DeptId) AS x WHERE x.total > 3000 ORDER BY x.DeptId;
SELECT t.a + t.b AS s FROM (SELECT 1 AS a, 2 AS b UNION ALL SELECT 3, 4) AS t ORDER BY s;
SELECT n FROM (SELECT EmpId FROM Emp) AS t(n) WHERE n < 3 ORDER BY n;

--# values constructor
SELECT a, b FROM (VALUES (1, 'x'), (2, 'y'), (3, NULL)) AS v(a, b) ORDER BY a DESC;
SELECT a FROM (VALUES (1), (2.5), (NULL)) AS v(a) ORDER BY a;

--# scalar subqueries
SELECT Name, (SELECT COUNT(*) FROM Sales s WHERE s.EmpId = e.EmpId) AS sales, (SELECT MAX(Amount) FROM Sales s WHERE s.EmpId = e.EmpId) AS best
FROM Emp e ORDER BY EmpId;
SELECT Name FROM Emp WHERE Salary = (SELECT MAX(Salary) FROM Emp) ORDER BY Name;
SELECT (SELECT Name FROM Dept WHERE DeptId = 999) AS missing;

--# scalar subquery returning many rows
SELECT (SELECT Name FROM Emp) AS x;

--# in and not in subqueries
SELECT Name FROM Emp WHERE EmpId IN (SELECT EmpId FROM Sales) ORDER BY Name;
SELECT Name FROM Emp WHERE EmpId NOT IN (SELECT EmpId FROM Sales) ORDER BY Name;
SELECT Name FROM Dept WHERE DeptId NOT IN (SELECT DeptId FROM Emp) ORDER BY Name;
SELECT Name FROM Dept WHERE DeptId NOT IN (SELECT DeptId FROM Emp WHERE DeptId IS NOT NULL) ORDER BY Name;

--# exists and not exists
SELECT Name FROM Dept d WHERE EXISTS (SELECT 1 FROM Emp e WHERE e.DeptId = d.DeptId) ORDER BY Name;
SELECT Name FROM Dept d WHERE NOT EXISTS (SELECT * FROM Emp e WHERE e.DeptId = d.DeptId AND e.Salary > 4500) ORDER BY Name;

--# correlated subquery in where
SELECT Name FROM Emp e WHERE Salary > (SELECT AVG(Salary) FROM Emp x WHERE x.DeptId = e.DeptId) ORDER BY Name;
SELECT Name FROM Emp e WHERE Salary >= ALL (SELECT Salary FROM Emp x WHERE x.DeptId = e.DeptId) ORDER BY Name;
SELECT Name FROM Emp WHERE Salary > ANY (SELECT Salary FROM Emp WHERE DeptId = 20) ORDER BY Name;
SELECT Name FROM Emp WHERE Salary < ALL (SELECT Salary FROM Emp WHERE DeptId = 999) ORDER BY Name;

--# nested correlated subqueries
SELECT d.Name FROM Dept d WHERE EXISTS (SELECT 1 FROM Emp e WHERE e.DeptId = d.DeptId AND EXISTS (SELECT 1 FROM Sales s WHERE s.EmpId = e.EmpId AND s.Amount > 400)) ORDER BY d.Name;

--# subquery in select with outer aggregate
SELECT DeptId, COUNT(*) AS n, (SELECT COUNT(*) FROM Emp) AS total FROM Emp GROUP BY DeptId ORDER BY DeptId;

--# cross apply and outer apply
SELECT e.Name, t.SaleId, t.Amount FROM Emp e CROSS APPLY (SELECT TOP 1 SaleId, Amount FROM Sales s WHERE s.EmpId = e.EmpId ORDER BY Amount DESC) AS t ORDER BY e.EmpId;
SELECT e.Name, t.SaleId FROM Emp e OUTER APPLY (SELECT TOP 1 SaleId FROM Sales s WHERE s.EmpId = e.EmpId ORDER BY SoldOn) AS t ORDER BY e.EmpId;
SELECT d.Name, v.n FROM Dept d CROSS APPLY (VALUES (d.DeptId * 2), (d.DeptId * 3)) AS v(n) WHERE d.DeptId <= 20 ORDER BY v.n;

--# union intersect except
SELECT DeptId FROM Emp UNION SELECT DeptId FROM Dept ORDER BY DeptId;
SELECT DeptId FROM Emp UNION ALL SELECT DeptId FROM Dept ORDER BY DeptId;
SELECT DeptId FROM Dept INTERSECT SELECT DeptId FROM Emp ORDER BY DeptId;
SELECT DeptId FROM Dept EXCEPT SELECT DeptId FROM Emp ORDER BY DeptId;
SELECT DeptId FROM Emp EXCEPT SELECT DeptId FROM Dept;

--# set operation precedence and types
SELECT 1 AS x UNION SELECT 2 INTERSECT SELECT 2 ORDER BY x;
SELECT 1 AS x UNION ALL SELECT 2.5 UNION ALL SELECT NULL ORDER BY x;
SELECT COUNT(*) AS n FROM (SELECT 'a' AS x UNION SELECT 'A' UNION SELECT 'b ' UNION SELECT 'b') AS u;
SELECT Name, 'emp' AS kind FROM Emp WHERE EmpId = 1 UNION ALL SELECT Name, 'dept' FROM Dept WHERE DeptId = 10 ORDER BY kind;

--# union column count mismatch
SELECT 1, 2 UNION SELECT 1;

--# simple cte
WITH big AS (SELECT * FROM Emp WHERE Salary >= 4200)
SELECT Name FROM big ORDER BY Name;

--# multiple ctes and column lists
WITH d (id, cnt) AS (SELECT DeptId, COUNT(*) FROM Emp GROUP BY DeptId), top1 AS (SELECT MAX(cnt) AS m FROM d)
SELECT d.id, d.cnt FROM d JOIN top1 ON top1.m = d.cnt ORDER BY d.id;

--# cte referenced twice
WITH s AS (SELECT EmpId, SUM(Amount) AS amt FROM Sales GROUP BY EmpId)
SELECT a.EmpId, b.EmpId AS other FROM s a JOIN s b ON a.amt < b.amt ORDER BY a.EmpId, b.EmpId;

--# recursive cte numbers
WITH n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < 10)
SELECT SUM(i) AS total, COUNT(*) AS cnt FROM n;

--# recursive cte hierarchy
WITH tree AS (
    SELECT EmpId, Name, 0 AS lvl, CAST(Name AS VARCHAR(200)) AS path FROM Emp WHERE ManagerId IS NULL
    UNION ALL
    SELECT e.EmpId, e.Name, t.lvl + 1, CAST(t.path + '>' + e.Name AS VARCHAR(200)) FROM Emp e JOIN tree t ON e.ManagerId = t.EmpId)
SELECT EmpId, lvl, path FROM tree ORDER BY path;

--# recursion limit
WITH n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < 200)
SELECT COUNT(*) AS cnt FROM n;

--# recursion with maxrecursion option
WITH n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < 200)
SELECT COUNT(*) AS cnt FROM n OPTION (MAXRECURSION 500);

--# cte with dml
WITH low AS (SELECT * FROM Sales WHERE Amount < 60)
SELECT COUNT(*) AS n FROM low;

--# case in where group and order
SELECT CASE WHEN Salary >= 4000 THEN 'high' WHEN Salary >= 2500 THEN 'mid' ELSE 'low' END AS band, COUNT(*) AS n
FROM Emp GROUP BY CASE WHEN Salary >= 4000 THEN 'high' WHEN Salary >= 2500 THEN 'mid' ELSE 'low' END ORDER BY n DESC, band;

--# like and in filters on tables
SELECT Name FROM Emp WHERE Name LIKE '[A-C]%' OR Name LIKE '%e' ORDER BY Name;
SELECT Name FROM Emp WHERE DeptId IN (10, 30) AND Name NOT LIKE 'B%' ORDER BY Name;
SELECT Name FROM Emp WHERE Hired BETWEEN '2020-01-01' AND '2021-12-31' ORDER BY Name;

--# date filters and grouping
SELECT DATEPART(month, SoldOn) AS m, COUNT(*) AS n, SUM(Amount) AS amt FROM Sales GROUP BY DATEPART(month, SoldOn) ORDER BY m;
SELECT SaleId FROM Sales WHERE SoldOn >= '2024-03-01' AND SoldOn < '2024-04-01' ORDER BY SaleId;
SELECT CAST(SoldOn AS DATE) AS d, Amount FROM Sales WHERE CAST(SoldOn AS DATE) = '2024-02-14';

--# views
GO
CREATE VIEW dbo.DeptTotals AS SELECT d.DeptId, d.Name, COUNT(e.EmpId) AS Staff, ISNULL(SUM(e.Salary), 0) AS Payroll FROM Dept d LEFT JOIN Emp e ON e.DeptId = d.DeptId GROUP BY d.DeptId, d.Name;
GO
SELECT * FROM DeptTotals ORDER BY DeptId;
SELECT v.Name FROM dbo.DeptTotals v WHERE v.Staff = 0;
GO
CREATE VIEW V2 (a, b) AS SELECT EmpId, Name FROM Emp WHERE DeptId = 10;
GO
SELECT a, b FROM V2 ORDER BY a;
DROP VIEW V2;

--# select variable assignment
DECLARE @name VARCHAR(40), @cnt INT, @list VARCHAR(200) = '';
SELECT @name = Name FROM Emp WHERE EmpId = 3;
SELECT @cnt = COUNT(*) FROM Emp;
SELECT @list = @list + Name + ',' FROM Emp WHERE DeptId = 20 ORDER BY EmpId;
SELECT @name AS name, @cnt AS cnt, @list AS list;
SELECT @name = Name FROM Emp WHERE EmpId = 999;
SELECT @name AS unchanged, @@ROWCOUNT AS rc;
