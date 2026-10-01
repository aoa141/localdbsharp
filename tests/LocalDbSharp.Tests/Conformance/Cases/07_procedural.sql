CREATE TABLE Acct (Id INT PRIMARY KEY, Owner VARCHAR(20) NOT NULL, Balance DECIMAL(10,2) NOT NULL CHECK (Balance >= 0));
INSERT INTO Acct VALUES (1, 'ann', 100), (2, 'bob', 50), (3, 'cid', 0);
CREATE TABLE Log (Id INT IDENTITY PRIMARY KEY, Msg VARCHAR(100));

--# variables and set
DECLARE @a INT = 5, @b INT, @s VARCHAR(10) = 'x', @d DECIMAL(5,2) = 1.239, @dt DATE = '2024-05-06';
SET @b = @a * 2;
SET @s = @s + 'yz';
SET @a += 1;
SELECT @a AS a, @b AS b, @s AS s, @d AS d, @dt AS dt, @b / @a AS q;

--# variable truncation and conversion
DECLARE @s VARCHAR(3) = 'abcdef', @i INT = '42', @c CHAR(4) = 'ab', @t TINYINT = 255;
SELECT @s AS s, @i AS i, @c + '|' AS c, @t AS t;

--# variable overflow
DECLARE @t TINYINT = 256;

--# undeclared variable
SELECT @nope AS x;

--# uninitialised variable is null
DECLARE @x INT;
SELECT @x AS x, ISNULL(@x, -1) AS y, CASE WHEN @x IS NULL THEN 1 ELSE 0 END AS z;

--# if else
DECLARE @n INT = 3, @r VARCHAR(10);
IF @n > 5 SET @r = 'big'
ELSE IF @n > 2 SET @r = 'mid'
ELSE SET @r = 'small';
IF EXISTS (SELECT 1 FROM Acct WHERE Balance = 0) AND @n = 3
BEGIN
    SET @r = @r + '+zero';
END
SELECT @r AS r;

--# while break continue
DECLARE @i INT = 0, @sum INT = 0;
WHILE 1 = 1
BEGIN
    SET @i = @i + 1;
    IF @i > 10 BREAK;
    IF @i % 2 = 0 CONTINUE;
    SET @sum = @sum + @i;
END
SELECT @i AS i, @sum AS s;

--# nested loops
DECLARE @i INT = 1, @j INT, @out VARCHAR(100) = '';
WHILE @i <= 3
BEGIN
    SET @j = 1;
    WHILE @j <= @i
    BEGIN
        SET @out = @out + CAST(@i * @j AS VARCHAR(5)) + ' ';
        SET @j += 1;
    END
    SET @i += 1;
END
SELECT @out AS out;

--# try catch basics
BEGIN TRY
    SELECT 1 / 0 AS x;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS num, ERROR_SEVERITY() AS sev, ERROR_STATE() AS st, ERROR_MESSAGE() AS msg, ERROR_LINE() AS line;
END CATCH

--# try catch constraint error and continue
BEGIN TRY
    INSERT INTO Acct VALUES (1, 'dup', 1);
    INSERT INTO Log (Msg) VALUES ('not reached');
END TRY
BEGIN CATCH
    INSERT INTO Log (Msg) VALUES ('caught ' + CAST(ERROR_NUMBER() AS VARCHAR(10)));
END CATCH
SELECT Msg FROM Log ORDER BY Id;
SELECT ERROR_NUMBER() AS outside;

--# nested try catch and rethrow
BEGIN TRY
    BEGIN TRY
        SELECT CAST('abc' AS INT) AS x;
    END TRY
    BEGIN CATCH
        INSERT INTO Log (Msg) VALUES ('inner ' + CAST(ERROR_NUMBER() AS VARCHAR(10)));
        THROW;
    END CATCH
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS num, ERROR_MESSAGE() AS msg;
END CATCH

--# throw custom error
THROW 51000, 'Custom failure', 1;

--# throw caught
BEGIN TRY
    THROW 50123, 'Something bad', 7;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS num, ERROR_MESSAGE() AS msg, ERROR_STATE() AS st, ERROR_SEVERITY() AS sev;
END CATCH

--# raiserror
RAISERROR('Problem %d with %s', 16, 1, 42, 'thing');

--# raiserror caught with formatting
BEGIN TRY
    RAISERROR('Value %s is %d%% done', 15, 3, 'abc', 80);
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS num, ERROR_MESSAGE() AS msg, ERROR_SEVERITY() AS sev, ERROR_STATE() AS st;
END CATCH

--# raiserror low severity does not throw
RAISERROR('just info', 10, 1);
SELECT 1 AS after;

--# error in batch continues after statement level error
DECLARE @x INT = 0;
SELECT 1 / @x AS boom;
INSERT INTO Log (Msg) VALUES ('after divide');
GO
SELECT COUNT(*) AS n FROM Log WHERE Msg = 'after divide';

--# @@error and @@rowcount
UPDATE Acct SET Balance = Balance WHERE Id > 1;
SELECT @@ROWCOUNT AS rc, @@ERROR AS err;
DECLARE @e INT;
BEGIN TRY
    INSERT INTO Acct VALUES (1, 'dup', 1);
END TRY
BEGIN CATCH
    SET @e = ERROR_NUMBER();
END CATCH
SELECT @e AS e;

--# stored procedure with parameters
GO
CREATE PROCEDURE dbo.Deposit @Id INT, @Amount DECIMAL(10,2), @NewBalance DECIMAL(10,2) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    IF @Amount <= 0
    BEGIN
        RAISERROR('Amount must be positive', 16, 1);
        RETURN -1;
    END
    UPDATE Acct SET Balance = Balance + @Amount WHERE Id = @Id;
    IF @@ROWCOUNT = 0 RETURN 1;
    SELECT @NewBalance = Balance FROM Acct WHERE Id = @Id;
    RETURN 0;
END
GO
DECLARE @bal DECIMAL(10,2), @rc INT;
EXEC @rc = dbo.Deposit @Id = 1, @Amount = 25.5, @NewBalance = @bal OUTPUT;
SELECT @rc AS rc, @bal AS bal;
EXEC @rc = Deposit 99, 1, @bal OUTPUT;
SELECT @rc AS rc;

--# procedure raising error returns status
DECLARE @bal DECIMAL(10,2), @rc INT = 5;
BEGIN TRY
    EXEC @rc = dbo.Deposit 1, -5, @bal OUTPUT;
END TRY
BEGIN CATCH
    SELECT ERROR_MESSAGE() AS msg, ERROR_PROCEDURE() AS prc, @rc AS rc;
END CATCH

--# procedure defaults and result sets
GO
CREATE PROC ListAccts @MinBalance DECIMAL(10,2) = 0, @Prefix VARCHAR(10) = NULL
AS
    SELECT Id, Owner FROM Acct WHERE Balance >= @MinBalance AND (@Prefix IS NULL OR Owner LIKE @Prefix + '%') ORDER BY Id;
    SELECT COUNT(*) AS total FROM Acct;
GO
EXEC ListAccts;
EXEC ListAccts @Prefix = 'b';
EXEC ListAccts 60, DEFAULT;

--# missing procedure parameter
GO
CREATE PROC NeedsParam @p INT AS SELECT @p AS p;
GO
EXEC NeedsParam;

--# unknown procedure
EXEC dbo.NoSuchProc 1;

--# alter and drop procedure
GO
ALTER PROC NeedsParam @p INT = 7 AS SELECT @p * 2 AS doubled;
GO
EXEC NeedsParam;
DROP PROCEDURE NeedsParam;
SELECT OBJECT_ID('NeedsParam') AS id;

--# create or alter
GO
CREATE OR ALTER PROC Twice AS SELECT 1 AS v;
GO
CREATE OR ALTER PROC Twice AS SELECT 2 AS v;
GO
EXEC Twice;

--# module options are accepted
GO
CREATE PROC WithOptions @x INT WITH RECOMPILE, EXECUTE AS OWNER AS SELECT @x + 1 AS y;
GO
CREATE FUNCTION dbo.WithOptionsFn (@x INT) RETURNS INT WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT AS BEGIN RETURN @x * 2 END
GO
EXEC WithOptions 1;
SELECT dbo.WithOptionsFn(4) AS z;

--# insert exec
CREATE TABLE #r (Id INT, Owner VARCHAR(20));
INSERT INTO #r EXEC('SELECT Id, Owner FROM Acct WHERE Id <= 2');
SELECT * FROM #r ORDER BY Id;
DROP TABLE #r;

--# nested procedures and recursion
GO
CREATE PROC Fact @n INT, @result BIGINT OUTPUT AS
BEGIN
    IF @n <= 1 SET @result = 1;
    ELSE
    BEGIN
        DECLARE @sub BIGINT, @m INT = @n - 1;
        EXEC Fact @m, @sub OUTPUT;
        SET @result = @n * @sub;
    END
END
GO
DECLARE @f BIGINT;
EXEC Fact 10, @f OUTPUT;
SELECT @f AS f, @@NESTLEVEL AS lvl;

--# scalar functions
GO
CREATE FUNCTION dbo.Tax (@amount DECIMAL(10,2), @rate DECIMAL(4,2) = 0.20) RETURNS DECIMAL(10,2) AS
BEGIN
    DECLARE @t DECIMAL(10,2);
    SET @t = @amount * @rate;
    IF @t < 1 RETURN 0;
    RETURN @t;
END
GO
SELECT dbo.Tax(100, 0.25) AS a, dbo.Tax(100, DEFAULT) AS b, dbo.Tax(2, 0.1) AS c, dbo.Tax(NULL, 0.1) AS d;
SELECT Id, dbo.Tax(Balance, 0.1) AS tax FROM Acct WHERE dbo.Tax(Balance, 0.1) > 0 ORDER BY Id;

--# function with query and recursion
GO
CREATE FUNCTION dbo.OwnerOf (@id INT) RETURNS VARCHAR(20) AS BEGIN RETURN (SELECT Owner FROM Acct WHERE Id = @id) END
GO
CREATE FUNCTION dbo.Fib (@n INT) RETURNS INT AS BEGIN IF @n < 2 RETURN @n; RETURN dbo.Fib(@n - 1) + dbo.Fib(@n - 2) END
GO
SELECT dbo.OwnerOf(2) AS o, dbo.OwnerOf(99) AS missing, dbo.Fib(10) AS fib;

--# inline table valued function
GO
CREATE FUNCTION dbo.Rich (@min DECIMAL(10,2)) RETURNS TABLE AS RETURN (SELECT Id, Owner, Balance FROM Acct WHERE Balance >= @min);
GO
SELECT Id, Owner FROM dbo.Rich(50) ORDER BY Id;
SELECT a.Id, r.Owner FROM Acct a CROSS APPLY dbo.Rich(a.Balance) r WHERE a.Id = 2 ORDER BY r.Id;
SELECT COUNT(*) AS n FROM dbo.Rich(1000);

--# multi statement table valued function
GO
CREATE FUNCTION dbo.Numbers (@from INT, @to INT) RETURNS @t TABLE (n INT PRIMARY KEY, sq INT) AS
BEGIN
    DECLARE @i INT = @from;
    WHILE @i <= @to BEGIN INSERT INTO @t VALUES (@i, @i * @i); SET @i += 1; END
    RETURN;
END
GO
SELECT n, sq FROM dbo.Numbers(3, 6) ORDER BY n;
SELECT SUM(sq) AS total FROM dbo.Numbers(1, 10) WHERE n % 2 = 1;

--# error inside function aborts the statement
GO
CREATE FUNCTION dbo.Boom (@x INT) RETURNS INT AS BEGIN RETURN 10 / @x END
GO
SELECT dbo.Boom(0) AS x;

--# dynamic sql
DECLARE @sql NVARCHAR(200) = N'SELECT COUNT(*) AS n FROM Acct WHERE Balance > 10';
EXEC (@sql);
EXEC ('SELECT ' + '1 + 1' + ' AS two');
EXECUTE sp_executesql N'SELECT Owner FROM Acct WHERE Id = @id', N'@id INT', @id = 2;

--# sp_executesql with output
DECLARE @cnt INT, @total DECIMAL(10,2);
EXEC sp_executesql N'SELECT @c = COUNT(*), @t = SUM(Balance) FROM Acct WHERE Balance >= @min', N'@min INT, @c INT OUTPUT, @t DECIMAL(10,2) OUTPUT', @min = 1, @c = @cnt OUTPUT, @t = @total OUTPUT;
SELECT @cnt AS cnt, @total AS total;

--# dynamic sql cannot see outer variables
DECLARE @v INT = 1;
EXEC ('SELECT @v AS v');

--# cursor loop
DECLARE @id INT, @owner VARCHAR(20), @acc VARCHAR(200) = '';
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Id, Owner FROM Acct ORDER BY Id DESC;
OPEN cur;
FETCH NEXT FROM cur INTO @id, @owner;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @acc = @acc + CAST(@id AS VARCHAR(5)) + ':' + @owner + ';';
    FETCH NEXT FROM cur INTO @id, @owner;
END
CLOSE cur;
DEALLOCATE cur;
SELECT @acc AS acc, @@FETCH_STATUS AS fs;

--# print and nocount do not produce result sets
SET NOCOUNT ON;
PRINT 'hello';
PRINT 1 + 2;
SELECT 1 AS only;
SET NOCOUNT OFF;

--# return ends batch
SELECT 1 AS a;
RETURN;
SELECT 2 AS b;

--# begin end blocks and nested if
DECLARE @x INT = 2;
IF @x = 1
BEGIN
    SELECT 'one' AS r;
END
ELSE
BEGIN
    IF @x = 2 BEGIN SELECT 'two' AS r; END
    ELSE SELECT 'other' AS r;
END

--# case insensitive variable and keyword names
declare @MyVar int = 1;
select @myvar + 1 as Result where @MYVAR = 1;

--# goto style labels are not needed for while with complex conditions
DECLARE @n INT = 100, @steps INT = 0;
WHILE @n <> 1 AND @steps < 1000
BEGIN
    SET @n = CASE WHEN @n % 2 = 0 THEN @n / 2 ELSE 3 * @n + 1 END;
    SET @steps += 1;
END
SELECT @steps AS steps;
