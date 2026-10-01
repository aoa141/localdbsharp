--# abs ceiling floor sign
SELECT ABS(-5) AS a, ABS(-2.5) AS b, CEILING(2.1) AS c, CEILING(-2.1) AS d, FLOOR(2.9) AS e, FLOOR(-2.1) AS f, SIGN(-3) AS g, SIGN(0.0) AS h,
       CEILING(5) AS i, FLOOR(CAST(2.5 AS float)) AS j, ABS(CAST(-1.5 AS float)) AS k, SIGN(2.5) AS l;

--# round
SELECT ROUND(2.567, 2) AS a, ROUND(2.5, 0) AS b, ROUND(-2.5, 0) AS c, ROUND(1234.567, -2) AS d, ROUND(2.567, 1, 1) AS e, ROUND(15, -1) AS f,
       ROUND(CAST(2.675 AS float), 2) AS g, ROUND(3.5, 0) AS h, ROUND(149, -2) AS i, ROUND(150, -2) AS j;

--# float rounding and negative zero
SELECT ROUND(CAST(1000 AS FLOAT), 1) AS a, ROUND(CAST(-2.25 AS FLOAT), 1) AS b, ROUND(CAST(3.14159 AS FLOAT), 1) AS c, ROUND(CAST(0.1 AS FLOAT), 1) AS d,
       CEILING(CAST(-0.5 AS FLOAT)) AS e, FLOOR(CAST(0.5 AS FLOAT)) * -1 AS f, ROUND(CAST(-0.04 AS FLOAT), 1) AS g, CAST(0 AS FLOAT) * -5 AS h,
       CAST(CEILING(CAST(-0.5 AS FLOAT)) AS VARCHAR(10)) AS i, ROUND(CAST(2.5 AS FLOAT), 0) AS j, ROUND(CAST(1234.5678 AS REAL), 2) AS k;

--# datalength of decimals depends on the value
SELECT DATALENGTH(CAST(10.5 AS DECIMAL(12,4))) AS a, DATALENGTH(CAST(1 AS DECIMAL(38,0))) AS b, DATALENGTH(CAST(123456789012 AS DECIMAL(20,0))) AS c,
       DATALENGTH(CAST(12345678901234567890123 AS DECIMAL(38,0))) AS d, DATALENGTH(1.5) AS e, DATALENGTH(CAST(0 AS DECIMAL(5,2))) AS f, DATALENGTH(CAST(1 AS MONEY)) AS g;

--# datetime2 with long styles
SELECT CONVERT(VARCHAR(40), CAST('2024-03-10 10:10:10.1234567' AS DATETIME2(3)), 109) AS a, CONVERT(VARCHAR(40), CAST('2024-03-10 22:10:10.1234567' AS DATETIME2), 109) AS b,
       CONVERT(VARCHAR(40), CAST('2024-03-10 10:10:10.1234567' AS DATETIME2(3)), 113) AS c, CONVERT(VARCHAR(40), CAST('2024-03-10 10:10:10.1234567' AS DATETIME2(2)), 114) AS d,
       CONVERT(VARCHAR(40), CAST('2024-03-10 10:10:10.1234567' AS DATETIME2(0)), 109) AS e, CONVERT(VARCHAR(40), CAST('2024-03-10 10:10:10.1234567' AS DATETIME2(3)), 100) AS f,
       CONVERT(VARCHAR(40), CAST('2024-03-10 10:10:10.1234567' AS DATETIME2(3)), 120) AS g, CONVERT(VARCHAR(40), CAST('2024-03-10 10:10:10.1234567' AS DATETIME2(3)), 108) AS h;

--# power sqrt square exp log
SELECT POWER(2, 10) AS a, POWER(2.0, 3) AS b, POWER(CAST(2 AS float), 0.5) AS c, SQRT(16) AS d, SQUARE(3) AS e, EXP(0) AS f, LOG(1) AS g,
       LOG10(1000) AS h, LOG(8, 2) AS i, POWER(10, 2) AS j, PI() AS k;

--# sqrt of negative
SELECT SQRT(-1) AS a;

--# power overflow
SELECT POWER(10, 10) AS a;

--# trigonometry
SELECT SIN(0) AS a, COS(0) AS b, TAN(0) AS c, ATAN(1) AS d, ATN2(1, 1) AS e, DEGREES(PI()) AS f, RADIANS(180e0) AS g, ASIN(1) AS h, ACOS(1) AS i;

--# greatest least
SELECT GREATEST(1, 5, 3) AS a, LEAST(1, 5, 3) AS b, GREATEST(1, NULL, 3) AS c, GREATEST('a', 'c', 'b') AS d, LEAST(1.5, 2) AS e;

--# isnumeric
SELECT ISNUMERIC('123') AS a, ISNUMERIC('12.5') AS b, ISNUMERIC('abc') AS c, ISNUMERIC('') AS d, ISNUMERIC(NULL) AS e, ISNUMERIC('1e5') AS f, ISNUMERIC(5) AS g;

--# datepart family
SELECT YEAR('2024-03-15') AS y, MONTH('2024-03-15') AS m, DAY('2024-03-15') AS d,
       DATEPART(quarter, '2024-08-15') AS q, DATEPART(dayofyear, '2024-03-01') AS doy, DATEPART(weekday, '2024-03-15') AS dw,
       DATEPART(week, '2024-03-15') AS wk, DATEPART(hour, '2024-03-15 13:45:30.123') AS hh, DATEPART(minute, '2024-03-15 13:45:30') AS mi,
       DATEPART(second, '2024-03-15 13:45:30') AS ss, DATEPART(millisecond, '2024-03-15 13:45:30.123') AS ms,
       DATEPART(iso_week, '2024-12-30') AS isowk, DATEPART(yy, '2024-03-15') AS yy, DATEPART(dd, '2024-03-15') AS dd;

--# datename
SELECT DATENAME(month, '2024-03-15') AS a, DATENAME(weekday, '2024-03-15') AS b, DATENAME(year, '2024-03-15') AS c, DATENAME(day, '2024-03-05') AS d;

--# dateadd
SELECT DATEADD(day, 1, CAST('2024-02-28' AS date)) AS a, DATEADD(month, 1, CAST('2024-01-31' AS date)) AS b,
       DATEADD(year, 1, CAST('2024-02-29' AS date)) AS c, DATEADD(hour, 25, CAST('2024-01-01' AS datetime)) AS d,
       DATEADD(minute, -90, CAST('2024-01-01 01:00' AS datetime)) AS e, DATEADD(week, 2, CAST('2024-01-01' AS date)) AS f,
       DATEADD(quarter, 1, CAST('2024-01-15' AS date)) AS g, DATEADD(day, 1, '2024-01-01') AS h,
       DATEADD(millisecond, 500, CAST('2024-01-01' AS datetime2)) AS i, DATEADD(second, 1, CAST('2024-12-31 23:59:59' AS datetime)) AS j;

--# datediff
SELECT DATEDIFF(day, '2024-01-01', '2024-03-01') AS a, DATEDIFF(month, '2024-01-31', '2024-02-01') AS b, DATEDIFF(year, '2023-12-31', '2024-01-01') AS c,
       DATEDIFF(hour, '2024-01-01 23:59', '2024-01-02 00:01') AS d, DATEDIFF(week, '2024-03-09', '2024-03-10') AS e,
       DATEDIFF(minute, '2024-01-01 10:00:59', '2024-01-01 10:01:00') AS f, DATEDIFF(day, '2024-03-01', '2024-01-01') AS g,
       DATEDIFF(quarter, '2024-03-31', '2024-04-01') AS h, DATEDIFF(second, '2024-01-01', '2024-01-02') AS i,
       DATEDIFF_BIG(millisecond, '2000-01-01', '2024-01-01') AS j;

--# eomonth and fromparts
SELECT EOMONTH('2024-02-10') AS a, EOMONTH('2023-02-10') AS b, EOMONTH('2024-01-31', 1) AS c, DATEFROMPARTS(2024, 3, 15) AS d,
       DATETIMEFROMPARTS(2024, 3, 15, 13, 45, 30, 500) AS e, DATETIME2FROMPARTS(2024, 3, 15, 13, 45, 30, 5, 1) AS f;

--# datefromparts invalid
SELECT DATEFROMPARTS(2024, 2, 30) AS a;

--# isdate
SELECT ISDATE('2024-03-15') AS a, ISDATE('2024-13-45') AS b, ISDATE('abc') AS c, ISDATE(NULL) AS d, ISDATE('20240315') AS e;

--# datetrunc
SELECT DATETRUNC(month, CAST('2024-03-15 13:45:30' AS datetime2)) AS a, DATETRUNC(year, CAST('2024-03-15' AS date)) AS b,
       DATETRUNC(hour, CAST('2024-03-15 13:45:30' AS datetime2)) AS c, DATETRUNC(quarter, CAST('2024-08-15' AS date)) AS d;

--# date casts and comparisons
SELECT CAST('2024-03-15' AS date) AS a, CAST('2024-03-15 13:45:30.127' AS datetime) AS b, CAST('2024-03-15 13:45:30.1234567' AS datetime2) AS c,
       CAST('2024-03-15 13:45:31' AS smalldatetime) AS d, CAST('13:45:30.5' AS time) AS e, CAST('20240315' AS datetime) AS f,
       CAST(CAST('2024-03-15 13:45:30' AS datetime) AS date) AS g, CAST('2024-03-15T13:45:30' AS datetime) AS h,
       CASE WHEN CAST('2024-03-15' AS date) < '2024-03-16' THEN 1 ELSE 0 END AS i, CAST('2024-03-15 13:45:30.1234567' AS datetime2(3)) AS j;

--# datetime rounding to 1/300 second
SELECT CAST('2024-01-01 00:00:00.001' AS datetime) AS a, CAST('2024-01-01 00:00:00.002' AS datetime) AS b, CAST('2024-01-01 00:00:00.005' AS datetime) AS c,
       CAST('2024-01-01 00:00:00.009' AS datetime) AS d, CAST('2024-01-01 23:59:59.999' AS datetime) AS e;

--# date to string styles
SELECT CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 101) AS s101, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 103) AS s103,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 112) AS s112, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 120) AS s120,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 121) AS s121, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 126) AS s126,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 108) AS s108, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 0) AS s0,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 109) AS s109, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 106) AS s106,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 107) AS s107, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 1) AS s1,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 23) AS s23, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 114) AS s114,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 105) AS s105, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 110) AS s110,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 111) AS s111, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 113) AS s113,
       CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 104) AS s104, CONVERT(varchar(30), CAST('2024-03-05 14:07:09.123' AS datetime), 102) AS s102;

--# default date to string
SELECT CAST(CAST('2024-03-05 14:07:09' AS datetime) AS varchar(30)) AS a, CAST(CAST('2024-03-05' AS date) AS varchar(30)) AS b,
       CAST(CAST('2024-03-05 14:07:09.5' AS datetime2) AS varchar(30)) AS c, CAST(CAST('2024-12-25 00:00:00' AS datetime) AS varchar(30)) AS d,
       CAST(CAST('14:07:09' AS time) AS varchar(30)) AS e, CAST(CAST('2024-03-05 14:07:09' AS smalldatetime) AS varchar(30)) AS f,
       CAST(CAST('2024-03-05 14:07:09.5' AS datetime2(2)) AS varchar(30)) AS g;

--# string to date with style
SELECT CONVERT(date, '15/03/2024', 103) AS a, CONVERT(date, '03/15/2024', 101) AS b, CONVERT(datetime, '2024-03-15 10:00:00', 120) AS c, CONVERT(date, '20240315', 112) AS d;

--# invalid date string
SELECT CAST('not a date' AS datetime) AS a;

--# datetime arithmetic
SELECT CAST('2024-01-01' AS datetime) + 1 AS a, CAST('2024-01-01' AS datetime) - 0.5 AS b, CAST('2024-01-10' AS datetime) - CAST('2024-01-01' AS datetime) AS c;

--# datetimeoffset
SELECT CAST('2024-03-15 13:45:30 +02:00' AS datetimeoffset) AS a, SWITCHOFFSET(CAST('2024-03-15 13:45:30 +02:00' AS datetimeoffset), '-05:00') AS b,
       TODATETIMEOFFSET(CAST('2024-03-15 13:45:30' AS datetime2), '+01:00') AS c;

--# date function result types
SELECT DATEADD(day, 1, CAST('2024-01-01' AS date)) AS d1, DATEADD(day, 1, CAST('2024-01-01' AS datetime)) AS d2, DATEADD(day, 1, CAST('2024-01-01' AS datetime2)) AS d3,
       DATEADD(day, 1, CAST('2024-01-01' AS smalldatetime)) AS d4, EOMONTH(CAST('2024-01-01' AS datetime)) AS d5, DATEADD(day, 1, '2024-01-01') AS d6;
