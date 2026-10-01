CREATE TABLE #o (id INT, a INT, b INT, n INT, csv VARCHAR(20));
INSERT #o VALUES (1, 5, 9, 1, 'a,b'), (2, 7, 3, 2, 'a,b,c'), (3, 1, 2, 3, 'x');
CREATE TABLE #t2 (id INT);
INSERT #t2 VALUES (1), (2), (3), (4);

--# correlation through values, function arguments, top and offset
SELECT id, (SELECT MAX(v) FROM (VALUES (o.a), (o.b)) x(v)) AS mx FROM #o o ORDER BY id;
SELECT id, (SELECT COUNT(*) FROM STRING_SPLIT(o.csv, ',')) AS c FROM #o o ORDER BY id;
SELECT id, (SELECT COUNT(*) FROM (SELECT TOP (o.n) id FROM #t2 ORDER BY id) q) AS c FROM #o o ORDER BY id;
SELECT id FROM #o o WHERE EXISTS (SELECT 1 FROM (VALUES (o.a)) v(x) WHERE x > 4) ORDER BY id;
SELECT id, (SELECT SUM(q.id) FROM (SELECT id FROM #t2 ORDER BY id OFFSET o.n ROWS) q) AS s FROM #o o ORDER BY id;

--# correlation through an outer aggregate
SELECT n % 2 AS k, (SELECT COUNT(*) FROM #t2 WHERE #t2.id < SUM(o.a)) AS c FROM #o o GROUP BY n % 2 ORDER BY k;

--# assignment honours top and offset
DECLARE @x INT, @y INT, @z INT;
SELECT TOP 1 @x = a FROM #o ORDER BY a DESC;
SELECT TOP 2 @y = a FROM #o ORDER BY a;
SELECT @z = a FROM #o ORDER BY a DESC OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY;
SELECT @x AS x, @y AS y, @z AS z;

--# subquery branches keep their type
CREATE TABLE #p (id INT, p DECIMAL(10,2), d DATE);
INSERT #p VALUES (1, 12.50, '2020-01-01'), (2, 7.25, '2020-01-02');
SELECT o.id, COALESCE((SELECT SUM(p) FROM #p WHERE #p.id = o.id - 1), 0) AS c FROM #o o ORDER BY o.id;
SELECT o.id, CASE WHEN o.id = 1 THEN 0 ELSE (SELECT MAX(p) FROM #p) END AS c FROM #o o ORDER BY o.id;
SELECT o.id, COALESCE((SELECT MAX(d) FROM #p WHERE #p.id < o.id), '1999-12-31') AS c FROM #o o ORDER BY o.id;

--# implicit conversion of dates and money to text
CREATE TABLE #d (d DATE, d2 DATETIME2(3), t TIME(0), m MONEY, dt DATETIME);
INSERT #d VALUES ('2020-03-04', '2020-03-04 10:11:12.123', '10:11:12', 1234.5, '2020-03-04 10:11:12');
SELECT LEN(d) AS a, LEFT(d, 7) AS b, CASE WHEN d LIKE '2020%' THEN 1 ELSE 0 END AS c, REPLACE(d, '-', '') AS e, LEN(m) AS f, RTRIM(m) AS g, UPPER(d2) AS h, RIGHT(t, 2) AS i, LEFT(dt, 11) AS j FROM #d;
DECLARE @s VARCHAR(40), @s2 VARCHAR(40), @s3 VARCHAR(40), @s4 VARCHAR(40);
SELECT @s = d, @s2 = d2, @s3 = t, @s4 = m FROM #d;
SELECT @s AS s, @s2 AS s2, @s3 AS s3, @s4 AS s4;
SELECT ISNULL(CAST(NULL AS VARCHAR(30)), d) AS x FROM #d;
CREATE TABLE #txt (v VARCHAR(40));
INSERT #txt SELECT d FROM #d;
INSERT #txt SELECT m FROM #d;
INSERT #txt VALUES (CAST('2021-05-06' AS DATE));
SELECT v FROM #txt ORDER BY v;

--# strings compared with typed values
SELECT CASE WHEN CAST(1.23 AS DECIMAL(5,2)) = '1.234' THEN 1 ELSE 0 END AS a,
       CASE WHEN CAST('2020-01-01' AS DATE) = '2020-01-01 10:00' THEN 1 ELSE 0 END AS b,
       CASE WHEN CAST('2020-01-02' AS DATETIME) <= '2020-01-01 23:59:59.999' THEN 1 ELSE 0 END AS c,
       CASE WHEN CAST('2020-01-01' AS DATETIME) = '2020-01-01 00:00:00.001' THEN 1 ELSE 0 END AS d,
       CASE WHEN CAST(1.5 AS MONEY) = '$1.50' THEN 1 ELSE 0 END AS e;

--# long literals are max typed
DECLARE @long NVARCHAR(MAX) = REPLICATE(CAST(N'x' AS NVARCHAR(MAX)), 4500) + N'tail';
SELECT LEN(@long) AS a, RIGHT(@long, 4) AS b;

--# char values of different lengths
SELECT '[' + x + ']' AS v, DATALENGTH(x) AS n FROM (SELECT CAST('a' AS CHAR(3)) AS x UNION ALL SELECT CAST('b' AS CHAR(5))) q ORDER BY v;
SELECT '[' + CASE WHEN 1 = 1 THEN CAST('a' AS CHAR(3)) ELSE CAST('b' AS CHAR(5)) END + ']' AS v;

--# decimal multiplication beyond 38 digits
SELECT CAST(1.2345 AS DECIMAL(38,4)) * CAST(1.2345 AS DECIMAL(10,4)) AS a, CAST(1.2345 AS DECIMAL(19,4)) * CAST(1.2345 AS DECIMAL(19,4)) AS b;
SELECT SUM(p) * CAST(1.23456 AS DECIMAL(10,5)) AS c FROM #p;

--# values with mixed decimal scales
SELECT x FROM (VALUES (1.5), (2.25), (3)) v(x) ORDER BY x;

--# two recursive members
WITH c AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM c WHERE n < 3 UNION ALL SELECT n + 10 FROM c WHERE n = 1)
SELECT n FROM c ORDER BY n;

--# nvarchar punctuation order
SELECT v FROM (VALUES (N'_a'), (N'!a'), (N'[x'), (N'#a'), (N'~a'), (N'$a')) t(v) ORDER BY v;
SELECT CASE WHEN N'_a' < N'!a' THEN 1 ELSE 0 END AS lt, MIN(v) AS mn, MAX(v) AS mx FROM (VALUES (N'_a'), (N'!a'), (N'#a')) t(v);

--# distinct agrees with comparison for unusual letters
SELECT (SELECT COUNT(*) FROM (SELECT DISTINCT x FROM (VALUES (NCHAR(383)), (N's')) v(x)) q) AS longS,
       (SELECT COUNT(*) FROM (SELECT DISTINCT x FROM (VALUES (NCHAR(181)), (NCHAR(924))) v(x)) q) AS micro;

--# grouping sets and subqueries
SELECT n AS k, (SELECT COUNT(*) FROM #t2 WHERE #t2.id > o.n) AS c, GROUPING(n) AS g FROM #o o GROUP BY ROLLUP (n) ORDER BY g, k;
SELECT COUNT(*) AS n FROM #o WHERE 1 = 0 GROUP BY ();

--# binary values
SELECT CASE WHEN 0x01 = 0x0100 THEN 1 ELSE 0 END AS a, CASE WHEN 0x01 = 1 THEN 1 ELSE 0 END AS b, 5 & 0xFF AS c, 1 + 0x01 AS d,
       CAST(0xFFFFFFFF AS INT) AS e, CAST(0xFFFF AS SMALLINT) AS f;
SELECT COUNT(*) AS groups FROM (SELECT x FROM (VALUES (0x01), (0x0100), (0x02)) v(x) GROUP BY x) g;

--# bigint joined to float beyond 2^53
CREATE TABLE #big (x BIGINT);
CREATE TABLE #flt (y FLOAT);
INSERT #big VALUES (9007199254740993), (1), (2), (3), (4), (5), (6), (7), (8), (9);
INSERT #flt VALUES (9007199254740992), (1), (2), (3), (4), (5), (6), (7), (8), (9);
SELECT COUNT(*) AS n FROM #big a JOIN #flt b ON a.x = b.y WHERE a.x > 100;

--# sum of real
CREATE TABLE #r (v REAL);
INSERT #r VALUES (0.1), (0.2);
SELECT SUM(v) AS s, AVG(v) AS a FROM #r;

--# very large window frame offset
SELECT x, SUM(x) OVER (ORDER BY x ROWS BETWEEN CURRENT ROW AND 2147483647 FOLLOWING) AS s FROM (VALUES (1), (2), (3)) v(x) ORDER BY x;

--# dateadd keeps the argument type
SELECT DATEADD(ms, 1, CAST('2020-01-01' AS DATETIME)) AS a, DATEADD(ms, 2, CAST('2020-01-01' AS DATETIME)) AS b,
       DATEADD(second, 30, CAST('2020-01-01 00:00' AS SMALLDATETIME)) AS c;

--# dateadd time part on a date
SELECT DATEADD(hour, 1, CAST('2020-01-01' AS DATE)) AS a;

--# convert styles for date and time types
SELECT CONVERT(VARCHAR(30), CAST('2020-03-04' AS DATE), 100) AS a, CONVERT(VARCHAR(30), CAST('2020-03-04' AS DATE), 109) AS b, CONVERT(VARCHAR(30), CAST('2020-03-04' AS DATE), 113) AS c,
       CONVERT(VARCHAR(30), CAST('10:11:12.123' AS TIME), 108) AS d, CONVERT(VARCHAR(30), CAST('13:11:12.123' AS TIME(3)), 100) AS e,
       CONVERT(VARCHAR(30), CAST('2020-03-04 13:05:06.789' AS DATETIME2), 0) AS f, CONVERT(VARCHAR(30), CAST('2020-03-04 13:05:06.789' AS DATETIME), 22) AS g,
       CAST(CAST('2020-03-04 13:05:06.789 +02:00' AS DATETIMEOFFSET(2)) AS VARCHAR(40)) AS h;

--# money to integer rounds
DECLARE @i INT = $1.5;
SELECT CAST($1.5 AS INT) AS a, CAST($2.5 AS INT) AS b, CAST(CAST(-1.5 AS MONEY) AS INT) AS c, CAST(CAST(1.5 AS SMALLMONEY) AS TINYINT) AS d, @i AS e, CAST(1.5 AS INT) AS f;

--# function edge cases no longer crash
SELECT STR(1e0 / 3, 20, 16) AS a, UNICODE(NCHAR(55296)) AS b, -CAST(1 AS TINYINT) AS c, PARSENAME('a.b.c', 2) AS d, ISNUMERIC('-') AS e, ISNUMERIC('$') AS f,
       CAST('' AS FLOAT) AS g, CAST('' AS MONEY) AS h, CAST('-' AS INT) AS i;

--# exp overflow
SELECT EXP(1000) AS a;

--# log base one
SELECT LOG(1, 1) AS a;

--# nan is not a float
SELECT CAST('NaN' AS FLOAT) AS a;

--# json_query on invalid json
SELECT JSON_QUERY('not json', '$.a') AS a;

--# round with huge length
SELECT ROUND(1.5, 3000000000) AS a;

--# non reserved words as column names and odd syntax
CREATE TABLE #kw (offset INT, output INT, using INT);
INSERT #kw VALUES (1, 2, 3);
SELECT offset, output, using FROM #kw WHERE output = 2;
SELECT offset FROM #kw (NOLOCK) WHERE offset = 1;
SELECT k.offset FROM #kw k (NOLOCK);
SELECT 1 AS a WHERE 1 < = 2 AND 1 ! = 2 AND 2 > = 1 AND 1 < > 2;
SELECT {d '2020-01-02'} AS d, {ts '2020-01-02 03:04:05'} AS ts, {fn UCASE('a')} AS f;

--# datefirst and dateformat
SET DATEFIRST 1;
SELECT DATEPART(weekday, '2020-03-04') AS wd, @@DATEFIRST AS df, DATEPART(week, '2020-03-08') AS wk;
SET DATEFIRST 7;
SET DATEFORMAT dmy;
SELECT CAST('03/04/2020' AS DATETIME) AS d;
SET DATEFORMAT mdy;
SELECT CAST('03/04/2020' AS DATETIME) AS d;

--# set rowcount
SET ROWCOUNT 2;
SELECT i FROM (VALUES (1), (2), (3)) v(i) ORDER BY i;
SET ROWCOUNT 0;
SELECT COUNT(*) AS n FROM (VALUES (1), (2), (3)) v(i);

--# rand is constant within a statement
SELECT COUNT(DISTINCT r) AS n FROM (SELECT RAND() AS r FROM (VALUES (1), (2), (3)) v(i)) q;

--# guid conversions
SELECT CAST('6F9619FF-8B86-D011-B42D-00C04FC964FFextra' AS UNIQUEIDENTIFIER) AS a;

--# guid needs hyphens
SELECT CAST('6F9619FF8B86D011B42D00C04FC964FF' AS UNIQUEIDENTIFIER) AS a;

--# guid to short string
SELECT CAST(CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS UNIQUEIDENTIFIER) AS VARCHAR(10)) AS a;

--# like with unclosed bracket and md5 of unicode
SELECT CASE WHEN 'a[' LIKE 'a[' THEN 1 ELSE 0 END AS a, HASHBYTES('MD5', N'abc') AS b, HASHBYTES('MD5', 'abc') AS c,
       CAST(1e0 / 3 AS DECIMAL(20,18)) AS d;
