--# integer arithmetic
SELECT 1 + 2 AS a, 7 - 10 AS b, 6 * 7 AS c, 7 / 2 AS d, -7 / 2 AS e, 7 % 3 AS f, -7 % 3 AS g, 2147483647 AS h;

--# integer overflow
SELECT 2147483647 + 1 AS x;

--# bigint literal arithmetic
SELECT 3000000000 AS a, 3000000000 * 2 AS b, CAST(3000000000 AS bigint) * 2 AS c, 9223372036854775807 AS d;

--# divide by zero
SELECT 1 / 0 AS x;

--# decimal arithmetic scale
SELECT 1.5 + 2.25 AS a, 1.5 * 2.25 AS b, 10 / 4.0 AS c, 1 / 3.0 AS d, 2 / 3.0 AS e, 1.0 / 3 AS f, 10.00 - 0.5 AS g;

--# decimal division typed
SELECT CAST(10 AS decimal(10,2)) / CAST(3 AS decimal(10,2)) AS a, CAST(1 AS decimal(18,4)) / 7 AS b, CAST(100 AS decimal(5,0)) / 3 AS c,
       CAST(7 AS int) / CAST(2 AS decimal(4,1)) AS d;

--# decimal multiplication typed
SELECT CAST(1.25 AS decimal(5,2)) * CAST(2.5 AS decimal(3,1)) AS a, CAST(3 AS decimal(10,2)) * 2 AS b, CAST(2.5 AS decimal(10,2)) + 1 AS c;

--# float arithmetic
SELECT 1e0 / 3 AS a, CAST(1 AS float) + 0.5 AS b, 2e2 * 1.5 AS c, CAST(10 AS real) / 4 AS d, 5e-1 AS e;

--# mixed int and string
SELECT 1 + '2' AS a, '10' * 2 AS b, '3' + 4 AS c;

--# string to int conversion failure
SELECT 1 + 'abc' AS x;

--# string concatenation
SELECT 'abc' + 'def' AS a, 'a' + NULL AS b, N'ünï' + 'x' AS c, 'x' + CAST(5 AS varchar(10)) AS d;

--# unary and bitwise
SELECT -(-5) AS a, +3 AS b, ~5 AS c, 6 & 3 AS d, 6 | 3 AS e, 6 ^ 3 AS f, -2.5 AS g;

--# comparison and three valued logic
SELECT CASE WHEN 1 = 1 THEN 'T' ELSE 'F' END AS a,
       CASE WHEN NULL = NULL THEN 'T' ELSE 'F' END AS b,
       CASE WHEN NULL <> 1 THEN 'T' ELSE 'F' END AS c,
       CASE WHEN NOT (NULL = 1) THEN 'T' ELSE 'F' END AS d,
       CASE WHEN NULL IS NULL THEN 'T' ELSE 'F' END AS e,
       CASE WHEN 1 = 1 OR NULL = 1 THEN 'T' ELSE 'F' END AS f,
       CASE WHEN 1 = 2 AND NULL = 1 THEN 'T' ELSE 'F' END AS g,
       CASE WHEN 1 = 1 AND NULL = 1 THEN 'T' ELSE 'F' END AS h;

--# string comparison is case and trailing space insensitive
SELECT CASE WHEN 'abc' = 'ABC' THEN 1 ELSE 0 END AS a, CASE WHEN 'abc' = 'abc   ' THEN 1 ELSE 0 END AS b,
       CASE WHEN 'a' < 'B' THEN 1 ELSE 0 END AS c, CASE WHEN 'abc' = ' abc' THEN 1 ELSE 0 END AS d,
       CASE WHEN 'é' = 'e' THEN 1 ELSE 0 END AS e;

--# between in like
SELECT CASE WHEN 5 BETWEEN 1 AND 10 THEN 1 ELSE 0 END AS a, CASE WHEN 5 NOT BETWEEN 1 AND 4 THEN 1 ELSE 0 END AS b,
       CASE WHEN 3 IN (1, 2, 3) THEN 1 ELSE 0 END AS c, CASE WHEN 4 NOT IN (1, 2, 3) THEN 1 ELSE 0 END AS d,
       CASE WHEN 4 NOT IN (1, NULL) THEN 1 ELSE 0 END AS e, CASE WHEN 1 IN (1, NULL) THEN 1 ELSE 0 END AS f,
       CASE WHEN NULL IN (1, 2) THEN 1 ELSE 0 END AS g;

--# like patterns
SELECT CASE WHEN 'hello' LIKE 'h%' THEN 1 ELSE 0 END AS a, CASE WHEN 'hello' LIKE '_ello' THEN 1 ELSE 0 END AS b,
       CASE WHEN 'hello' LIKE 'H%O' THEN 1 ELSE 0 END AS c, CASE WHEN 'hello' LIKE '[a-h]ello' THEN 1 ELSE 0 END AS d,
       CASE WHEN 'hello' LIKE '[^a-g]ello' THEN 1 ELSE 0 END AS e, CASE WHEN '50%' LIKE '50!%' ESCAPE '!' THEN 1 ELSE 0 END AS f,
       CASE WHEN '500' LIKE '50!%' ESCAPE '!' THEN 1 ELSE 0 END AS g, CASE WHEN 'a.b' LIKE 'a.b' THEN 1 ELSE 0 END AS h,
       CASE WHEN 'axb' LIKE 'a.b' THEN 1 ELSE 0 END AS i, CASE WHEN 'hello' NOT LIKE '%z%' THEN 1 ELSE 0 END AS j,
       CASE WHEN 'a[b' LIKE 'a[[]b' THEN 1 ELSE 0 END AS k;

--# case expressions
SELECT CASE 2 WHEN 1 THEN 'one' WHEN 2 THEN 'two' ELSE 'many' END AS a,
       CASE WHEN 1 > 2 THEN 'x' END AS b,
       CASE WHEN 1 = 1 THEN 1 ELSE 2.5 END AS c,
       CASE WHEN 1 = 0 THEN 1 ELSE 2.5 END AS d,
       CASE WHEN 1 = 1 THEN 'short' ELSE 'much longer' END AS e,
       CASE NULL WHEN NULL THEN 'null' ELSE 'not' END AS f;

--# case mixed types take highest precedence
SELECT CASE WHEN 1 = 1 THEN 1 ELSE CAST(2 AS bigint) END AS a, CASE WHEN 1 = 1 THEN 1 ELSE 1e0 END AS b,
       CASE WHEN 1 = 1 THEN CAST(1 AS tinyint) ELSE CAST(2 AS smallint) END AS c;

--# coalesce isnull nullif iif choose
SELECT COALESCE(NULL, NULL, 3) AS a, COALESCE(NULL, 'x') AS b, ISNULL(NULL, 5) AS c, ISNULL(4, 5) AS d,
       IIF(1 < 2, 'yes', 'no') AS g, CHOOSE(2, 'a', 'b', 'c') AS h, CHOOSE(5, 'a', 'b') AS i,
       COALESCE(NULL, 1, 2.5) AS j, ISNULL(CAST(NULL AS varchar(3)), 'abcdef') AS k, COALESCE(CAST(NULL AS varchar(3)), 'abcdef') AS l;

--# nullif
SELECT NULLIF(a, 1) AS x, NULLIF(a, 2) AS y, NULLIF(s, 'A') AS z FROM (VALUES (CAST(1 AS int), 'a')) AS t(a, s);

--# money division rounding
SELECT CAST(2 AS money) / 3 AS a, CAST(1 AS money) / 7 AS b, CAST(10 AS money) / CAST(3 AS money) AS c, CAST(2.5 AS money) * CAST(1.5 AS money) AS d,
       CAST(10 AS money) / 3.0 AS e, CAST(10 AS money) * 1.5 AS f;

--# null propagation
SELECT 1 + NULL AS a, NULL * 2 AS b, 'x' + NULL AS c, UPPER(NULL) AS d, LEN(NULL) AS e, ABS(NULL) AS f;

--# literal types
SELECT 1 AS i, 1.0 AS d1, 1.50 AS d2, 1e0 AS f, 'a' AS s, N'a' AS n, 0x0A0B AS b, NULL AS z, 123456789012 AS big, .5 AS half;

--# no column name
SELECT 1, 'a', 1 + 1;

--# select without from using where
SELECT 1 AS x WHERE 1 = 0;

--# bit values
SELECT CAST(1 AS bit) AS a, CAST(0 AS bit) AS b, CAST(5 AS bit) AS c, CAST('true' AS bit) AS d, CAST(NULL AS bit) AS e;

--# operator precedence
SELECT 2 + 3 * 4 AS a, (2 + 3) * 4 AS b, 10 - 2 - 3 AS c, 2 * 3 % 4 AS d, 1 + 2 & 3 AS e,
       CASE WHEN 1 = 1 OR 1 = 0 AND 1 = 0 THEN 1 ELSE 0 END AS f, CASE WHEN NOT 1 = 0 AND 1 = 1 THEN 1 ELSE 0 END AS g;

--# tinyint smallint arithmetic
SELECT CAST(200 AS tinyint) + CAST(50 AS tinyint) AS a, CAST(100 AS smallint) * CAST(2 AS smallint) AS b, CAST(1 AS tinyint) + 1 AS c;

--# tinyint overflow
SELECT CAST(200 AS tinyint) + CAST(100 AS tinyint) AS a;

--# money arithmetic
SELECT CAST(10.5 AS money) AS a, CAST(10.5 AS money) * 2 AS b, CAST(10 AS money) / 3 AS c, CAST(1.23456 AS money) AS d, CAST(5 AS smallmoney) + 1.5 AS e;

--# modulo decimals and negatives
SELECT 10.5 % 3 AS a, -10 % 3 AS b, 10 % -3 AS c, 5.5 % 2.5 AS d;

--# unicode and escapes
SELECT 'it''s' AS a, N'日本語' AS b, LEN(N'日本語') AS c, 'tab	x' AS d, '' AS e;
