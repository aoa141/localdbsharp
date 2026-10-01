--# cast to integers
SELECT CAST(3.99 AS INT) AS a, CAST(-3.99 AS INT) AS b, CAST('  42 ' AS INT) AS c, CAST(1e2 AS INT) AS d, CAST(255 AS TINYINT) AS e,
       CAST(32767 AS SMALLINT) AS f, CAST(9999999999 AS BIGINT) AS g, CAST('' AS INT) AS h, CAST(CAST(7 AS BIT) AS INT) AS i, CAST('-5' AS INT) AS j;

--# cast int overflow
SELECT CAST(3000000000 AS INT) AS a;

--# cast decimal string to int fails
SELECT CAST('3.5' AS INT) AS a;

--# cast to decimal
SELECT CAST(3.14159 AS DECIMAL(5,2)) AS a, CAST(2.5 AS DECIMAL(5,0)) AS b, CAST(3.5 AS DECIMAL(5,0)) AS c, CAST(-2.5 AS DECIMAL(5,0)) AS d,
       CAST('12.345' AS DECIMAL(6,2)) AS e, CAST(7 AS DECIMAL(5,2)) AS f, CAST(1e0/3 AS DECIMAL(10,4)) AS g, CAST(123.456 AS NUMERIC(10,1)) AS h,
       CAST(0.5 AS DECIMAL(3,0)) AS i, CAST(1.005 AS DECIMAL(4,2)) AS j;

--# decimal overflow
SELECT CAST(12345.6 AS DECIMAL(5,2)) AS a;

--# bad decimal string
SELECT CAST('abc' AS DECIMAL(5,2)) AS a;

--# cast to float
SELECT CAST('1.5' AS FLOAT) AS a, CAST(1 AS FLOAT) / 3 AS b, CAST(3 AS REAL) AS c, CAST('1e3' AS FLOAT) AS d, CAST(0.1 AS FLOAT) + CAST(0.2 AS FLOAT) AS e;

--# cast numbers to strings
SELECT CAST(42 AS VARCHAR(10)) AS a, CAST(-1.50 AS VARCHAR(10)) AS b, CAST(1e0 AS VARCHAR(20)) AS c, CAST(123456789e0 AS VARCHAR(20)) AS d,
       CAST(0.1e0 AS VARCHAR(20)) AS e, CAST(1.5e-7 AS VARCHAR(20)) AS f, CAST(CAST(1 AS BIT) AS VARCHAR(5)) AS g, CAST(12.3456 AS MONEY) AS h,
       CAST(CAST(12.3456 AS MONEY) AS VARCHAR(20)) AS i, CAST(1234567.891 AS VARCHAR(20)) AS j, CAST(CAST(2 AS FLOAT) / 3 AS VARCHAR(30)) AS k,
       CAST(CAST(123456 AS FLOAT) AS VARCHAR(30)) AS l, CAST(CAST(1234567 AS FLOAT) AS VARCHAR(30)) AS m, CAST(CAST(0.00001 AS FLOAT) AS VARCHAR(30)) AS n;

--# float to string styles
SELECT CONVERT(VARCHAR(30), CAST(1234.5678 AS FLOAT), 0) AS s0, CONVERT(VARCHAR(30), CAST(1234.5678 AS FLOAT), 1) AS s1, CONVERT(VARCHAR(30), CAST(1234.5678 AS FLOAT), 2) AS s2,
       CONVERT(VARCHAR(30), CAST(1234.5678 AS MONEY), 1) AS m1, CONVERT(VARCHAR(30), CAST(1234.5678 AS MONEY), 2) AS m2, CONVERT(VARCHAR(30), CAST(1234.5678 AS MONEY), 0) AS m0;

--# string truncation in cast
SELECT CAST('abcdef' AS VARCHAR(3)) AS a, CAST('abc' AS CHAR(5)) + '|' AS b, CAST(12345 AS VARCHAR(3)) AS c, CONVERT(VARCHAR, 'this string is longer than thirty characters in total') AS d,
       CAST('x' AS NCHAR(3)) + '|' AS e, LEN(CAST('ab' AS CHAR(10))) AS f, DATALENGTH(CAST('ab' AS CHAR(10))) AS g;

--# cast int to short varchar yields star
SELECT CAST(12345 AS VARCHAR(2)) AS a, CAST(1.5 AS VARCHAR(10)) AS b;

--# try_cast and try_convert
SELECT TRY_CAST('12' AS INT) AS a, TRY_CAST('x' AS INT) AS b, TRY_CONVERT(DATE, '2024-02-30') AS c, TRY_CONVERT(DATE, '2024-02-28') AS d,
       TRY_CAST('1.5' AS DECIMAL(3,1)) AS e, TRY_CAST(1000 AS TINYINT) AS f, TRY_CAST('abc' AS FLOAT) AS g, TRY_CONVERT(INT, 3.7) AS h;

--# uniqueidentifier
SELECT CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS UNIQUEIDENTIFIER) AS a, CAST(CAST('6f9619ff-8b86-d011-b42d-00c04fc964ff' AS UNIQUEIDENTIFIER) AS VARCHAR(36)) AS b,
       CASE WHEN CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS UNIQUEIDENTIFIER) = '6f9619ff-8b86-d011-b42d-00c04fc964ff' THEN 1 ELSE 0 END AS c,
       LEN(CAST(NEWID() AS VARCHAR(36))) AS d;

--# bad uniqueidentifier
SELECT CAST('not-a-guid' AS UNIQUEIDENTIFIER) AS a;

--# guid ordering
SELECT g FROM (VALUES (CAST('00000000-0000-0000-0000-000000000001' AS UNIQUEIDENTIFIER)), (CAST('01000000-0000-0000-0000-000000000000' AS UNIQUEIDENTIFIER)),
                     (CAST('00000000-0000-0000-0100-000000000000' AS UNIQUEIDENTIFIER)), (CAST('00000000-0100-0000-0000-000000000000' AS UNIQUEIDENTIFIER))) AS t(g) ORDER BY g;

--# binary
SELECT 0x0102 AS a, CAST('AB' AS VARBINARY(4)) AS b, CAST(0x4142 AS VARCHAR(4)) AS c, CAST(1 AS VARBINARY(4)) AS d, CAST(258 AS BINARY(2)) AS e,
       CAST(0x01 AS BINARY(3)) AS f, CONVERT(VARCHAR(10), 0x0AFF, 1) AS g, CONVERT(VARCHAR(10), 0x0AFF, 2) AS h, CONVERT(VARBINARY(4), '0x0AFF', 1) AS i,
       DATALENGTH(0x010203) AS j, CAST(0x00000102 AS INT) AS k, 0x01 + 0x02 AS l, CAST(N'A' AS VARBINARY(4)) AS m;

--# hashbytes
SELECT HASHBYTES('MD5', 'abc') AS md5, HASHBYTES('SHA2_256', 'abc') AS sha256, HASHBYTES('SHA1', 'abc') AS sha1;

--# implicit conversion in comparisons
SELECT CASE WHEN '10' = 10 THEN 1 ELSE 0 END AS a, CASE WHEN '2' < 10 THEN 1 ELSE 0 END AS b, CASE WHEN '2' < '10' THEN 1 ELSE 0 END AS c,
       CASE WHEN 1.0 = 1 THEN 1 ELSE 0 END AS d, CASE WHEN CAST(1 AS BIT) = 1 THEN 1 ELSE 0 END AS e, CASE WHEN '2024-01-01' = CAST('2024-01-01' AS DATE) THEN 1 ELSE 0 END AS f,
       CASE WHEN 0.1e0 + 0.2e0 = 0.3e0 THEN 1 ELSE 0 END AS g, CASE WHEN CAST(0.1 AS DECIMAL(3,1)) + 0.2 = 0.3 THEN 1 ELSE 0 END AS h;

--# implicit conversion on insert
CREATE TABLE Conv (i INT, d DECIMAL(6,2), f FLOAT, s VARCHAR(10), dt DATETIME, b BIT, m MONEY, dd DATE, t TIME(0), si SMALLINT, r REAL, u UNIQUEIDENTIFIER, bin VARBINARY(4), n NVARCHAR(5), d2 DATETIME2(2));
INSERT INTO Conv VALUES ('12', '3.456', '1.5', 123, '2024-03-05 10:11:12.127', 'true', '12.34567', '2024-03-05 23:59', '10:11:12.9', 12.9, 1.1, '6F9619FF-8B86-D011-B42D-00C04FC964FF', 0x01, 12.5, '2024-03-05 10:11:12.127');
INSERT INTO Conv (i, d, f, s, b) VALUES (3.99, 7, 2, 4.5, 2);
SELECT * FROM Conv ORDER BY i DESC;

--# column metadata types
SELECT TOP 1 i, d, f, s, dt, b, m, dd, t, si, r, u, bin, n, d2 FROM Conv ORDER BY i DESC;

--# insert conversion failures
INSERT INTO Conv (i) VALUES ('abc');

--# insert decimal overflow
INSERT INTO Conv (d) VALUES (123456.789);

--# decimal column rounding and precision
CREATE TABLE Dec (a DECIMAL(5,2), b DECIMAL(18,6), c NUMERIC(3,0), d DECIMAL(38,10));
INSERT INTO Dec VALUES (1.005, 1.0000005, 99.5, 1234567890.123456789);
INSERT INTO Dec VALUES (999.994, 2, -0.5, 1);
SELECT a, b, c, d, a + b AS ab, a * c AS ac, b / 3 AS b3 FROM Dec ORDER BY a;
SELECT SUM(a) AS sa, AVG(a) AS aa, SUM(b) AS sb, AVG(c) AS ac, MAX(d) AS md FROM Dec;

--# integer division and averages keep integer type
CREATE TABLE Ints (t TINYINT, s SMALLINT, i INT, b BIGINT);
INSERT INTO Ints VALUES (1, 1, 1, 1), (2, 2, 2, 2), (4, 4, 4, 4);
SELECT AVG(t) AS at, AVG(s) AS avs, AVG(i) AS ai, AVG(b) AS ab, SUM(t) AS st, SUM(s) AS ss, SUM(i) AS si, SUM(b) AS sb, MAX(t) AS mt, MIN(s) AS ms,
       AVG(i * 1.0) AS aid, SUM(i) / COUNT(*) AS q, SUM(i) * 1.0 / COUNT(*) AS q2 FROM Ints;

--# sum int overflow
SELECT SUM(v) AS s FROM (VALUES (2147483647), (1)) AS t(v);

--# sum bigint ok
SELECT SUM(CAST(v AS BIGINT)) AS s FROM (VALUES (2147483647), (1)) AS t(v);

--# bit columns
CREATE TABLE Bits (id INT, flag BIT NULL);
INSERT INTO Bits VALUES (1, 1), (2, 0), (3, NULL), (5, 17);
INSERT INTO Bits VALUES (4, 'false');
SELECT id, flag FROM Bits WHERE flag = 1 ORDER BY id;
SELECT id FROM Bits WHERE flag = 'true' ORDER BY id;
SELECT COUNT(flag) AS n, SUM(CAST(flag AS INT)) AS s, MAX(CAST(flag AS TINYINT)) AS m FROM Bits;
SELECT id, CASE flag WHEN 1 THEN 'yes' WHEN 0 THEN 'no' ELSE 'unknown' END AS txt FROM Bits ORDER BY id;

--# multi row values are type unified
INSERT INTO Bits VALUES (10, 1), (11, 'false');

--# multi row values unified to decimal
CREATE TABLE Uni (v VARCHAR(10));
INSERT INTO Uni VALUES (1), (2.50), ('3');
SELECT v FROM Uni ORDER BY v;

--# nvarchar and varchar columns
CREATE TABLE Str (a VARCHAR(5), b NVARCHAR(5), c CHAR(5), d NCHAR(5), e VARCHAR(MAX), f NVARCHAR(MAX));
INSERT INTO Str VALUES ('ab', N'ab', 'ab', N'ab', REPLICATE('x', 100), REPLICATE(N'y', 100));
SELECT a + '|' AS a, b + '|' AS b, c + '|' AS c, d + '|' AS d, LEN(e) AS le, LEN(f) AS lf, DATALENGTH(a) AS da, DATALENGTH(b) AS db, DATALENGTH(c) AS dc, DATALENGTH(d) AS dd FROM Str;
SELECT CASE WHEN c = 'ab' THEN 1 ELSE 0 END AS eq, CASE WHEN c LIKE 'ab' THEN 1 ELSE 0 END AS lk, CASE WHEN a = c THEN 1 ELSE 0 END AS ac FROM Str;

--# datetime columns round trip
CREATE TABLE Dates (d DATE, dt DATETIME, d2 DATETIME2, d23 DATETIME2(3), sd SMALLDATETIME, t TIME, t0 TIME(0), dto DATETIMEOFFSET);
INSERT INTO Dates VALUES ('2024-02-29', '2024-02-29 23:59:59.997', '2024-02-29 23:59:59.9999999', '2024-02-29 23:59:59.9994', '2024-02-29 23:59:29', '23:59:59.1234567', '23:59:59.6', '2024-02-29 12:00:00 +05:30');
SELECT * FROM Dates;
SELECT DATEDIFF(day, d, dt) AS dd, DATEADD(day, 1, d) AS next, CAST(dt AS DATE) AS asDate, CAST(d AS DATETIME) AS asDt, YEAR(d2) AS y, CAST(dto AS DATETIME2) AS dtoLocal FROM Dates;

--# sql_variant and type precedence in coalesce
SELECT COALESCE(CAST(NULL AS INT), 1.5) AS a, COALESCE(CAST(NULL AS VARCHAR(5)), 5) AS b, ISNULL(CAST(NULL AS INT), 1.9) AS c, COALESCE(1, 'x') AS d;

--# isnull takes the exact type of its first argument
SELECT ISNULL(LEFT(c, 1), 'none') AS a, ISNULL(c + c, 'none') + '|' AS b, ISNULL(SUBSTRING(v, 1, 2), 'none') AS d, ISNULL(RIGHT(v, 3), 'none') AS e,
       ISNULL(CAST(NULL AS CHAR(2)), 'x') + '|' AS f, ISNULL(UPPER(c), 'lower case') AS g, ISNULL(n + c, 'unicode fixed') + '|' AS h, ISNULL(v + c, 'variable') + '|' AS i,
       ISNULL(REPLACE(c, 'a', 'b'), REPLICATE('x', 20)) AS j, ISNULL(LTRIM(v), 'abcdefgh') AS k
FROM (VALUES (CAST(NULL AS CHAR(5)), CAST(NULL AS VARCHAR(6)), CAST(NULL AS NCHAR(4)))) AS t(c, v, n);

--# stuff result length
SELECT ISNULL(STUFF(CAST(NULL AS VARCHAR(1)), 1, 0, ''), 'none') AS a, ISNULL(STUFF(CAST(NULL AS VARCHAR(3)), 1, 0, 'xy'), 'nonexistent') AS b;

--# coalesce conversion failure
SELECT COALESCE(CAST(NULL AS INT), 'x') AS a;

--# case conversion failure only when branch taken
SELECT CASE WHEN 1 = 1 THEN 1 ELSE 'x' END AS a;

--# case branch conversion failure
SELECT CASE WHEN 1 = 0 THEN 1 ELSE 'x' END AS a;

--# union conversions
SELECT 1 AS v UNION ALL SELECT '2' ORDER BY v;

--# iif and choose types
SELECT IIF(1 = 1, 1, 2.50) AS a, IIF(1 = 0, 'a', N'b') AS b, CHOOSE(1, 1, 2.5, 3e0) AS c, IIF(1 = 1, NULL, 5) AS d;

--# parse numeric literal forms
SELECT 1. AS a, .5 AS b, 1.e2 AS c, 0x AS d, -0.0 AS e, 1E-2 AS f, +5 AS g, - 5 AS h, $5.25 AS i;
