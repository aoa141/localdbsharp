--# alias types
CREATE TYPE dbo.Email FROM VARCHAR(50) NOT NULL;
CREATE TYPE Amount FROM DECIMAL(9,2);
GO
CREATE TABLE Contact (Id INT PRIMARY KEY, Mail dbo.Email, Owed Amount);
INSERT INTO Contact VALUES (1, 'a@x', 1.239);
DECLARE @m dbo.Email = 'b@x', @a Amount = 2.555;
SELECT Mail, Owed, @m AS m, @a AS a FROM Contact;
SELECT COLUMN_NAME, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Contact' ORDER BY ORDINAL_POSITION;

--# alias type not null is enforced
INSERT INTO Contact VALUES (2, NULL, 1);

--# unknown type
DECLARE @x dbo.NoSuchType;

--# duplicate type
CREATE TYPE dbo.Email FROM INT;

--# table types and table valued parameters
CREATE TYPE dbo.IdList AS TABLE (Id INT PRIMARY KEY, Label VARCHAR(10) NULL);
GO
CREATE PROCEDURE dbo.CountIds @ids dbo.IdList READONLY, @min INT = 0 AS
    SELECT COUNT(*) AS n, MAX(Label) AS mx FROM @ids WHERE Id >= @min;
GO
CREATE FUNCTION dbo.SumIds (@ids dbo.IdList READONLY) RETURNS INT AS BEGIN RETURN (SELECT SUM(Id) FROM @ids) END
GO
DECLARE @list dbo.IdList;
INSERT INTO @list VALUES (3, 'c'), (1, 'a'), (2, NULL);
EXEC dbo.CountIds @list;
EXEC dbo.CountIds @ids = @list, @min = 2;
EXEC dbo.CountIds;
SELECT dbo.SumIds(@list) AS total;
SELECT Id FROM @list;

--# table type constraints apply
DECLARE @list dbo.IdList;
INSERT INTO @list VALUES (1, 'a'), (1, 'b');

--# drop type
DROP TYPE Amount2;

--# rowversion columns
CREATE TABLE Doc (Id INT PRIMARY KEY, Title VARCHAR(20), Ver ROWVERSION);
INSERT INTO Doc (Id, Title) VALUES (1, 'a'), (2, 'b');
DECLARE @v1 BINARY(8) = (SELECT Ver FROM Doc WHERE Id = 1), @v2 BINARY(8) = (SELECT Ver FROM Doc WHERE Id = 2);
UPDATE Doc SET Title = 'a2' WHERE Id = 1;
SELECT CASE WHEN @v2 > @v1 THEN 1 ELSE 0 END AS increasing,
       CASE WHEN (SELECT Ver FROM Doc WHERE Id = 1) > @v2 THEN 1 ELSE 0 END AS bumpedOnUpdate,
       CASE WHEN (SELECT Ver FROM Doc WHERE Id = 2) = @v2 THEN 1 ELSE 0 END AS untouched,
       DATALENGTH(@v1) AS len, CASE WHEN @@DBTS >= (SELECT MAX(Ver) FROM Doc) THEN 1 ELSE 0 END AS dbts;
UPDATE Doc SET Title = 'stale' WHERE Id = 1 AND Ver = @v1;
SELECT @@ROWCOUNT AS staleUpdateRows;

--# rowversion cannot be written
INSERT INTO Doc (Id, Title, Ver) VALUES (3, 'c', 0x01);

--# rowversion cannot be updated
UPDATE Doc SET Ver = 0x01 WHERE Id = 1;

--# rowversion with default keyword and timestamp synonym
CREATE TABLE Doc2 (Id INT, Stamp TIMESTAMP);
INSERT INTO Doc2 VALUES (1, DEFAULT);
SELECT Id, DATALENGTH(Stamp) AS len FROM Doc2;

--# sp_rename
CREATE TABLE OldName (a INT, b INT);
INSERT INTO OldName VALUES (1, 2);
EXEC sp_rename 'OldName', 'NewName';
EXEC sp_rename 'dbo.NewName.b', 'c', 'COLUMN';
GO
SELECT a, c FROM NewName;
SELECT OBJECT_ID('OldName') AS gone;

--# sp_rename missing object
EXEC sp_rename 'NoSuchThing', 'x';

--# session context
EXEC sp_set_session_context @key = N'tenant', @value = 42;
EXEC sp_set_session_context N'user', N'ann';
SELECT CAST(SESSION_CONTEXT(N'tenant') AS INT) AS tenant, CAST(SESSION_CONTEXT(N'user') AS NVARCHAR(10)) AS usr, CAST(SESSION_CONTEXT(N'nope') AS INT) AS missing;

--# extended properties are accepted
EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'docs', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Doc';
SELECT 1 AS ok;

--# constraint catalog views
CREATE TABLE Par (Id INT NOT NULL CONSTRAINT PK_Par PRIMARY KEY, Code VARCHAR(5) CONSTRAINT UQ_Par_Code UNIQUE);
CREATE TABLE Chi (Id INT IDENTITY(5, 2) CONSTRAINT PK_Chi PRIMARY KEY, ParId INT CONSTRAINT FK_Chi_Par REFERENCES Par(Id) ON DELETE CASCADE,
                  Qty INT CONSTRAINT DF_Chi_Qty DEFAULT 1 CONSTRAINT CK_Chi_Qty CHECK (Qty > 0));
SELECT CONSTRAINT_NAME, TABLE_NAME, CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME IN ('Par', 'Chi') ORDER BY CONSTRAINT_NAME;
SELECT CONSTRAINT_NAME, TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_NAME IN ('Par', 'Chi') ORDER BY CONSTRAINT_NAME;
SELECT CONSTRAINT_NAME, UNIQUE_CONSTRAINT_NAME, UPDATE_RULE, DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_NAME = 'FK_Chi_Par';
SELECT k.name, k.type, OBJECT_NAME(k.parent_object_id) AS tbl FROM sys.key_constraints k WHERE k.name IN ('PK_Par', 'UQ_Par_Code', 'PK_Chi') ORDER BY k.name;
SELECT fk.name, OBJECT_NAME(fk.parent_object_id) AS child, OBJECT_NAME(fk.referenced_object_id) AS parent FROM sys.foreign_keys fk WHERE fk.name = 'FK_Chi_Par';
SELECT OBJECT_NAME(c.parent_object_id) AS child, c.parent_column_id, OBJECT_NAME(c.referenced_object_id) AS parent, c.referenced_column_id FROM sys.foreign_key_columns c WHERE c.parent_object_id = OBJECT_ID('Chi');
SELECT name, OBJECT_NAME(parent_object_id) AS tbl FROM sys.check_constraints WHERE name = 'CK_Chi_Qty';
SELECT name, parent_column_id FROM sys.default_constraints WHERE name = 'DF_Chi_Qty';
SELECT name, column_id, CAST(seed_value AS INT) AS seed, CAST(increment_value AS INT) AS incr FROM sys.identity_columns WHERE object_id = OBJECT_ID('Chi');

--# metadata functions
SELECT COL_LENGTH('Par', 'Code') AS a, COL_LENGTH('Par', 'Id') AS b, COL_LENGTH('Par', 'Nope') AS c, COL_NAME(OBJECT_ID('Par'), 2) AS d,
       OBJECTPROPERTY(OBJECT_ID('Par'), 'IsTable') AS e, OBJECTPROPERTY(OBJECT_ID('Chi'), 'TableHasIdentity') AS f, OBJECTPROPERTY(OBJECT_ID('Par'), 'TableHasIdentity') AS g,
       OBJECTPROPERTY(OBJECT_ID('dbo.CountIds'), 'IsProcedure') AS h, DIFFERENCE('Robert', 'Rupert') AS i;
