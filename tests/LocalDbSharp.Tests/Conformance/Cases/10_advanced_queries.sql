CREATE TABLE Customer (CustomerId INT IDENTITY(1,1) PRIMARY KEY, Name NVARCHAR(50) NOT NULL, Country CHAR(2) NOT NULL, Joined DATE NOT NULL, Tier TINYINT NOT NULL DEFAULT 1);
CREATE TABLE Product (ProductId INT PRIMARY KEY, Name VARCHAR(40) NOT NULL, Category VARCHAR(20) NULL, Price DECIMAL(9,2) NOT NULL, Discontinued BIT NOT NULL DEFAULT 0);
CREATE TABLE [Order] (OrderId INT IDENTITY(100,1) PRIMARY KEY, CustomerId INT NOT NULL REFERENCES Customer(CustomerId), OrderDate DATETIME NOT NULL, Status VARCHAR(10) NOT NULL);
CREATE TABLE OrderLine (OrderId INT NOT NULL REFERENCES [Order](OrderId), LineNum INT NOT NULL, ProductId INT NOT NULL REFERENCES Product(ProductId), Qty INT NOT NULL, UnitPrice DECIMAL(9,2) NOT NULL,
    PRIMARY KEY (OrderId, LineNum));
INSERT INTO Customer (Name, Country, Joined, Tier) VALUES (N'Acme', 'US', '2020-01-15', 3), (N'Bolt', 'DE', '2021-06-01', 1), (N'Cyan', 'US', '2022-03-20', 2), (N'Dune', 'FR', '2022-11-11', 1), (N'Echo', 'DE', '2023-05-05', 2);
INSERT INTO Product VALUES (1, 'Widget', 'Hardware', 9.99, 0), (2, 'Gadget', 'Hardware', 24.50, 0), (3, 'Manual', 'Books', 5.00, 0), (4, 'Legacy', NULL, 99.00, 1), (5, 'Cable', 'Hardware', 3.25, 0);
INSERT INTO [Order] (CustomerId, OrderDate, Status) VALUES (1, '2024-01-10 09:00', 'shipped'), (1, '2024-02-11 10:30', 'shipped'), (2, '2024-02-12 11:00', 'open'),
    (3, '2024-03-01 15:45', 'cancelled'), (3, '2024-03-02 08:20', 'shipped'), (5, '2024-03-15 12:00', 'open');
INSERT INTO OrderLine VALUES (100, 1, 1, 10, 9.99), (100, 2, 3, 1, 5.00), (101, 1, 2, 2, 24.50), (102, 1, 1, 5, 9.50), (102, 2, 5, 20, 3.25), (102, 3, 2, 1, 24.50),
    (103, 1, 4, 1, 99.00), (104, 1, 3, 3, 4.50), (104, 2, 5, 2, 3.25), (105, 1, 2, 4, 22.00);

--# order totals with join and group
SELECT o.OrderId, c.Name, COUNT(*) AS lines, SUM(l.Qty * l.UnitPrice) AS total
FROM [Order] o JOIN Customer c ON c.CustomerId = o.CustomerId JOIN OrderLine l ON l.OrderId = o.OrderId
GROUP BY o.OrderId, c.Name ORDER BY total DESC;

--# customers without orders
SELECT c.Name FROM Customer c LEFT JOIN [Order] o ON o.CustomerId = c.CustomerId WHERE o.OrderId IS NULL ORDER BY c.Name;
SELECT c.Name FROM Customer c WHERE NOT EXISTS (SELECT 1 FROM [Order] o WHERE o.CustomerId = c.CustomerId) ORDER BY c.Name;
SELECT Name FROM Customer WHERE CustomerId NOT IN (SELECT CustomerId FROM [Order]) ORDER BY Name;

--# revenue per country with rollup style union
SELECT Country, SUM(total) AS revenue FROM (
    SELECT c.Country, l.Qty * l.UnitPrice AS total FROM Customer c JOIN [Order] o ON o.CustomerId = c.CustomerId JOIN OrderLine l ON l.OrderId = o.OrderId WHERE o.Status <> 'cancelled') x
GROUP BY Country
UNION ALL
SELECT 'ALL', SUM(l.Qty * l.UnitPrice) FROM [Order] o JOIN OrderLine l ON l.OrderId = o.OrderId WHERE o.Status <> 'cancelled'
ORDER BY revenue;

--# top product per category
SELECT Category, Name, Price FROM (
    SELECT Category, Name, Price, ROW_NUMBER() OVER (PARTITION BY Category ORDER BY Price DESC) AS rn FROM Product WHERE Category IS NOT NULL) t
WHERE rn = 1 ORDER BY Category;

--# products never ordered and ordered more than once
SELECT p.Name FROM Product p WHERE p.ProductId NOT IN (SELECT ProductId FROM OrderLine) ORDER BY p.Name;
SELECT p.Name, COUNT(DISTINCT l.OrderId) AS orders FROM Product p JOIN OrderLine l ON l.ProductId = p.ProductId GROUP BY p.Name HAVING COUNT(DISTINCT l.OrderId) > 1 ORDER BY orders DESC, p.Name;

--# correlated aggregates in select list
SELECT c.Name,
       (SELECT COUNT(*) FROM [Order] o WHERE o.CustomerId = c.CustomerId) AS orders,
       (SELECT MAX(o.OrderDate) FROM [Order] o WHERE o.CustomerId = c.CustomerId) AS lastOrder,
       ISNULL((SELECT SUM(l.Qty * l.UnitPrice) FROM [Order] o JOIN OrderLine l ON l.OrderId = o.OrderId WHERE o.CustomerId = c.CustomerId), 0) AS spent
FROM Customer c ORDER BY c.CustomerId;

--# running total per customer by date
SELECT o.CustomerId, o.OrderId, t.total, SUM(t.total) OVER (PARTITION BY o.CustomerId ORDER BY o.OrderDate) AS running
FROM [Order] o CROSS APPLY (SELECT SUM(Qty * UnitPrice) AS total FROM OrderLine l WHERE l.OrderId = o.OrderId) t ORDER BY o.CustomerId, o.OrderDate;

--# month over month with lag
WITH m AS (SELECT MONTH(o.OrderDate) AS mon, SUM(l.Qty * l.UnitPrice) AS rev FROM [Order] o JOIN OrderLine l ON l.OrderId = o.OrderId GROUP BY MONTH(o.OrderDate))
SELECT mon, rev, LAG(rev) OVER (ORDER BY mon) AS prev, rev - LAG(rev, 1, 0) OVER (ORDER BY mon) AS change FROM m ORDER BY mon;

--# paging
SELECT ProductId, Name FROM Product ORDER BY Price DESC, ProductId OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY;
WITH p AS (SELECT ProductId, Name, ROW_NUMBER() OVER (ORDER BY Name) AS rn FROM Product) SELECT ProductId, Name FROM p WHERE rn BETWEEN 2 AND 3 ORDER BY rn;

--# conditional aggregation pivot
SELECT c.Country,
       SUM(CASE WHEN o.Status = 'shipped' THEN 1 ELSE 0 END) AS shipped,
       SUM(CASE WHEN o.Status = 'open' THEN 1 ELSE 0 END) AS [open],
       COUNT(CASE WHEN o.Status = 'cancelled' THEN 1 END) AS cancelled,
       COUNT(o.OrderId) AS total
FROM Customer c LEFT JOIN [Order] o ON o.CustomerId = c.CustomerId GROUP BY c.Country ORDER BY c.Country;

--# group by null category
SELECT Category, COUNT(*) AS n, MIN(Price) AS lo, MAX(Price) AS hi, AVG(Price) AS mean FROM Product GROUP BY Category ORDER BY Category;

--# having with subquery
SELECT ProductId, SUM(Qty) AS qty FROM OrderLine GROUP BY ProductId HAVING SUM(Qty) > (SELECT AVG(Qty) FROM OrderLine) ORDER BY ProductId;

--# in list with expressions and strings
SELECT Name FROM Customer WHERE Country IN ('us', 'FR') AND YEAR(Joined) IN (2020, 2021 + 1) ORDER BY Name;

--# case in order by
SELECT Name, Status FROM [Order] o JOIN Customer c ON c.CustomerId = o.CustomerId
ORDER BY CASE Status WHEN 'open' THEN 1 WHEN 'shipped' THEN 2 ELSE 3 END, OrderId;

--# distinct with order by and expressions
SELECT DISTINCT c.Country, c.Tier FROM Customer c ORDER BY c.Tier DESC, c.Country;
SELECT DISTINCT UPPER(LEFT(Name, 1)) AS initial FROM Product ORDER BY initial;
SELECT DISTINCT TOP 2 Category FROM Product ORDER BY Category DESC;

--# update with correlated subquery and join
UPDATE c SET Tier = CASE WHEN x.spent > 100 THEN 3 WHEN x.spent > 50 THEN 2 ELSE 1 END
FROM Customer c JOIN (SELECT o.CustomerId, SUM(l.Qty * l.UnitPrice) AS spent FROM [Order] o JOIN OrderLine l ON l.OrderId = o.OrderId GROUP BY o.CustomerId) x ON x.CustomerId = c.CustomerId;
SELECT @@ROWCOUNT AS rc;
SELECT Name, Tier FROM Customer ORDER BY CustomerId;
UPDATE Product SET Price = Price * 1.10 WHERE EXISTS (SELECT 1 FROM OrderLine l WHERE l.ProductId = Product.ProductId AND l.Qty >= 10);
SELECT ProductId, Price FROM Product ORDER BY ProductId;

--# delete with exists and join
DELETE l FROM OrderLine l JOIN [Order] o ON o.OrderId = l.OrderId WHERE o.Status = 'cancelled';
SELECT @@ROWCOUNT AS rc;
DELETE FROM [Order] WHERE Status = 'cancelled' AND NOT EXISTS (SELECT 1 FROM OrderLine l WHERE l.OrderId = [Order].OrderId);
SELECT COUNT(*) AS orders FROM [Order];

--# insert select with top order and identity
INSERT INTO [Order] (CustomerId, OrderDate, Status)
SELECT TOP 2 CustomerId, '2024-04-01', 'new' FROM Customer WHERE Country = 'US' ORDER BY Joined DESC;
SELECT OrderId, CustomerId, Status FROM [Order] WHERE Status = 'new' ORDER BY OrderId;
SELECT SCOPE_IDENTITY() AS lastId, IDENT_CURRENT('Order') AS cur;

--# nested derived tables and aliases
SELECT z.c, z.total FROM (SELECT y.c, SUM(y.v) AS total FROM (SELECT Country AS c, Tier * 10 AS v FROM Customer) AS y GROUP BY y.c) AS z WHERE z.total >= 30 ORDER BY z.c;

--# alias visibility rules
SELECT Price * 2 AS dbl FROM Product WHERE dbl > 10;

--# cte chains with aggregates and joins
WITH spend AS (SELECT o.CustomerId, SUM(l.Qty * l.UnitPrice) AS total FROM [Order] o JOIN OrderLine l ON l.OrderId = o.OrderId GROUP BY o.CustomerId),
     ranked AS (SELECT CustomerId, total, RANK() OVER (ORDER BY total DESC) AS rk FROM spend),
     named AS (SELECT c.Name, r.total, r.rk FROM ranked r JOIN Customer c ON c.CustomerId = r.CustomerId)
SELECT Name, total, rk FROM named WHERE rk <= 2 ORDER BY rk;

--# cte inside subquery and derived table
SELECT Name FROM Customer WHERE CustomerId IN (SELECT CustomerId FROM (SELECT CustomerId, COUNT(*) AS n FROM [Order] GROUP BY CustomerId) t WHERE n > 1) ORDER BY Name;

--# exists in case and select list
SELECT Name, CASE WHEN EXISTS (SELECT 1 FROM [Order] o WHERE o.CustomerId = c.CustomerId AND o.Status = 'open') THEN 'has open' ELSE 'none' END AS st FROM Customer c ORDER BY CustomerId;

--# full join reconciliation
SELECT COALESCE(a.k, b.k) AS k, a.v AS left_v, b.v AS right_v
FROM (VALUES (1, 'a'), (2, 'b'), (3, 'c')) AS a(k, v) FULL JOIN (VALUES (2, 'B'), (3, 'C'), (4, 'D')) AS b(k, v) ON a.k = b.k ORDER BY k;

--# multiple joins to same table
SELECT l.OrderId, p.Name AS product, c.Name AS customer FROM OrderLine l JOIN Product p ON p.ProductId = l.ProductId JOIN [Order] o ON o.OrderId = l.OrderId JOIN Customer c ON c.CustomerId = o.CustomerId
WHERE p.Category = 'Hardware' AND c.Country = 'DE' ORDER BY l.OrderId, l.LineNum;

--# string aggregation per group
SELECT c.Country, STRING_AGG(c.Name, ', ') WITHIN GROUP (ORDER BY c.Name) AS names FROM Customer c GROUP BY c.Country ORDER BY c.Country;

--# date bucketing
SELECT DATEFROMPARTS(YEAR(OrderDate), MONTH(OrderDate), 1) AS monthStart, COUNT(*) AS n FROM [Order] GROUP BY DATEFROMPARTS(YEAR(OrderDate), MONTH(OrderDate), 1) ORDER BY monthStart;
SELECT DATENAME(weekday, OrderDate) AS dow, COUNT(*) AS n FROM [Order] GROUP BY DATENAME(weekday, OrderDate), DATEPART(weekday, OrderDate) ORDER BY DATEPART(weekday, OrderDate);

--# catalog helpers valid
SELECT CASE WHEN OBJECT_ID('dbo.Customer') IS NOT NULL THEN 1 ELSE 0 END AS a, CASE WHEN OBJECT_ID('dbo.Customer', 'U') IS NOT NULL THEN 1 ELSE 0 END AS b,
       OBJECT_ID('dbo.Customer', 'V') AS c, OBJECT_ID('Nope') AS d, OBJECT_NAME(OBJECT_ID('[dbo].[Order]')) AS e, SCHEMA_NAME() AS f, USER_NAME() AS g;

--# information_schema
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME;
SELECT COLUMN_NAME, ORDINAL_POSITION, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Product' ORDER BY ORDINAL_POSITION;

--# sys catalog views
SELECT name, type, type_desc FROM sys.tables ORDER BY name;
SELECT t.name AS tbl, c.name AS col, c.column_id, c.is_nullable, c.is_identity FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.name = 'Customer' ORDER BY c.column_id;
SELECT name FROM sys.objects WHERE type = 'U' AND name LIKE 'Order%' ORDER BY name;
SELECT s.name FROM sys.schemas s WHERE s.name IN ('dbo', 'sys') ORDER BY s.name;

--# if exists drop patterns
IF OBJECT_ID('dbo.Scratch', 'U') IS NOT NULL DROP TABLE dbo.Scratch;
CREATE TABLE dbo.Scratch (x INT);
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'Scratch') DROP TABLE dbo.Scratch;
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Scratch') SELECT 'gone' AS state;

--# comments and odd formatting
SELECT /* inline */ 1 AS a -- trailing
     , 2 /* nested /* comment */ still */ AS b
;SELECT
3
AS
c;

--# semicolons and empty statements
;;SELECT 1 AS a;;;SELECT 2 AS b;

--# keywords as identifiers when bracketed
SELECT [o].[Status] AS [from], [o].OrderId AS [select] FROM [dbo].[Order] AS [o] WHERE [o].[OrderId] = 100;

--# three part names
SELECT COUNT(*) AS n FROM dbo.Customer;
DECLARE @db SYSNAME = DB_NAME();
DECLARE @sql NVARCHAR(200) = N'SELECT COUNT(*) AS n FROM ' + QUOTENAME(@db) + N'.dbo.Customer';
EXEC (@sql);
