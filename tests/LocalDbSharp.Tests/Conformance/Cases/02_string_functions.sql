--# len and datalength
SELECT LEN('abc') AS a, LEN('abc  ') AS b, LEN('  abc') AS c, LEN('') AS d, DATALENGTH('abc  ') AS e, DATALENGTH(N'abc') AS f,
       DATALENGTH(1) AS g, DATALENGTH(CAST(1 AS bigint)) AS h, DATALENGTH(NULL) AS i;

--# case and trim
SELECT UPPER('aBc') AS a, LOWER('aBc') AS b, LTRIM('  x ') + '|' AS c, RTRIM(' x  ') + '|' AS d, TRIM('  x  ') + '|' AS e,
       TRIM('xy' FROM 'xyhixy') AS f;

--# substring left right
SELECT SUBSTRING('abcdef', 2, 3) AS a, SUBSTRING('abcdef', 0, 3) AS b, SUBSTRING('abcdef', -1, 4) AS c, SUBSTRING('abcdef', 5, 100) AS d,
       SUBSTRING('abcdef', 10, 2) AS e, LEFT('abcdef', 2) AS f, RIGHT('abcdef', 2) AS g, LEFT('ab', 10) AS h, RIGHT('ab', 0) AS i;

--# substring negative length
SELECT SUBSTRING('abc', 1, -1) AS a;

--# charindex patindex
SELECT CHARINDEX('c', 'abcabc') AS a, CHARINDEX('c', 'abcabc', 4) AS b, CHARINDEX('z', 'abc') AS c, CHARINDEX('BC', 'abc') AS d,
       PATINDEX('%b_d%', 'abcde') AS e, PATINDEX('%[0-9]%', 'abc123') AS f, PATINDEX('abc%', 'abcde') AS g, PATINDEX('%z%', 'abc') AS h,
       CHARINDEX('', 'abc') AS i;

--# patindex edge cases
SELECT PATINDEX('%', 'abc') AS a, PATINDEX('%', '') AS b, PATINDEX('%c', 'abc') AS c, PATINDEX('a%c', 'abc') AS d, PATINDEX('%[^a-z]%', 'abc1') AS e,
       PATINDEX('%B%', 'abc') AS f, PATINDEX('b%', 'abc') AS g, PATINDEX('%[0-9][0-9]%', 'a1b22c') AS h, PATINDEX('_b_', 'abc') AS i, PATINDEX('', 'abc') AS j, PATINDEX('%%', 'abc') AS k;

--# like ranges follow the collation
SELECT CASE WHEN 'ë' LIKE '[a-y]' THEN 1 ELSE 0 END AS a, CASE WHEN 'E' LIKE '[a-f]' THEN 1 ELSE 0 END AS b, CASE WHEN 'é' LIKE 'e' THEN 1 ELSE 0 END AS c,
       CASE WHEN 'Z' LIKE '[a-y]' THEN 1 ELSE 0 END AS d, CASE WHEN '5' LIKE '[0-9]' THEN 1 ELSE 0 END AS e, CASE WHEN 'a' LIKE '[^a-y]' THEN 1 ELSE 0 END AS f,
       CASE WHEN '_' LIKE '[a-z]' THEN 1 ELSE 0 END AS g, CASE WHEN 'b' LIKE '[abc]' THEN 1 ELSE 0 END AS h, CASE WHEN 'B' LIKE '[abc]' THEN 1 ELSE 0 END AS i,
       CASE WHEN 'ß' LIKE '[r-t]' THEN 1 ELSE 0 END AS j, CASE WHEN '-' LIKE '[a-]' THEN 1 ELSE 0 END AS k, CASE WHEN 'abcabc' LIKE '%b%b%' THEN 1 ELSE 0 END AS l,
       CASE WHEN 'abc' LIKE 'a%b%c%d' THEN 1 ELSE 0 END AS m, CASE WHEN 'aXbXc' LIKE 'a_b_c' THEN 1 ELSE 0 END AS n;

--# replace replicate reverse stuff space
SELECT REPLACE('abcabc', 'b', 'XY') AS a, REPLACE('abc', 'B', '') AS b, REPLICATE('ab', 3) AS c, REVERSE('abc') AS d,
       STUFF('abcdef', 2, 3, 'XY') AS e, STUFF('abc', 1, 0, 'X') AS f, STUFF('abc', 5, 1, 'X') AS g, 'a' + SPACE(3) + 'b' AS h,
       REPLICATE('x', 0) AS i, STUFF('abc', 2, 10, '') AS j;

--# concat functions
SELECT CONCAT('a', 1, NULL, 2.5) AS a, CONCAT_WS('-', 'a', NULL, 'b', 3) AS b, CONCAT('x', CAST('2024-03-05' AS date)) AS c,
       CONCAT(1, 2) AS d;

--# ascii char unicode nchar
SELECT ASCII('A') AS a, CHAR(66) AS b, UNICODE(N'€') AS c, NCHAR(8364) AS d, ASCII('') AS e, CHAR(300) AS f;

--# quotename
SELECT QUOTENAME('abc') AS a, QUOTENAME('a]b') AS b, QUOTENAME('abc', '''') AS c, QUOTENAME('abc', '"') AS d, QUOTENAME('abc', '(') AS e;

--# str function
SELECT STR(123.456) AS a, STR(123.456, 8, 2) AS b, STR(1234567, 3) AS c, STR(5) AS d, STR(-1.5, 6, 1) AS e;

--# str shrinks decimals to fit
SELECT STR(10, 4, 2) AS a, STR(125.25, 4, 2) AS b, STR(-1.75, 4, 2) AS c, STR(0, 4, 2) AS d, STR(12345, 4, 2) AS e, STR(9.99, 3, 1) AS f, STR(9.99, 4, 1) AS g,
       STR(0.5, 1, 1) AS h, STR(-0.5, 2) AS i, STR(1234.5678, 10, 3) AS j, STR(99.5) AS k, STR(1.5, 3, 5) AS l, STR(123, 3, 2) AS m, STR(-12, 3, 1) AS n;

--# translate and string_escape
SELECT TRANSLATE('2*[3+4]/{7-2}', '[]{}', '()()') AS a, STRING_ESCAPE('a"b\c', 'json') AS b;

--# format numbers and dates
SELECT FORMAT(1234.5678, 'N2') AS a, FORMAT(1234, '00000') AS b, FORMAT(CAST('2024-03-05' AS date), 'yyyy/MM/dd') AS c,
       FORMAT(0.256, 'P1') AS d, FORMAT(42, 'X') AS e, FORMAT(CAST('2024-03-05 14:30:00' AS datetime), 'HH:mm') AS f;

--# string_agg
SELECT STRING_AGG(v, ',') WITHIN GROUP (ORDER BY v) AS a FROM (VALUES ('b'), ('a'), ('c'), (NULL)) AS t(v);
SELECT STRING_AGG(v, '; ') WITHIN GROUP (ORDER BY v DESC) AS b FROM (VALUES ('b'), ('a'), ('c'), (NULL)) AS t(v);
SELECT STRING_AGG(v, ',') AS c FROM (VALUES ('only')) AS t(v);
SELECT STRING_AGG(v, ',') AS d FROM (VALUES (CAST(NULL AS varchar(5)))) AS t(v);

--# string_agg grouped
SELECT g, STRING_AGG(CAST(n AS varchar(10)), '+') WITHIN GROUP (ORDER BY n) AS s
FROM (VALUES (1, 3), (1, 1), (2, 5), (1, 2)) AS t(g, n) GROUP BY g ORDER BY g;

--# string_split
SELECT value FROM STRING_SPLIT('a,b,,c', ',') ORDER BY value;

--# string_split with ordinal
SELECT value, ordinal FROM STRING_SPLIT('x y z', ' ', 1) ORDER BY ordinal;

--# soundex
SELECT SOUNDEX('Robert') AS a, SOUNDEX('Rupert') AS b, SOUNDEX('Tymczak') AS c;

--# string functions on numbers
SELECT LEN(12345) AS a, LEFT(12345, 2) AS b, REPLACE(12345, 3, 'x') AS c, UPPER(1) AS d, REVERSE(123) AS e;
