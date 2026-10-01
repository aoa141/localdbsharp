# LocalDbSharp

A pure C#, in-memory database engine that speaks T-SQL. It is meant to stand in for SQL Server LocalDB wherever
a real server is inconvenient: unit and integration tests, demos, tools, and embedded scenarios that need a
relational engine but no persistence.

- No native dependencies, no installation, no files on disk: a database is an object in your process.
- An ADO.NET provider (`DbConnection` / `DbCommand` / `DbDataReader`), so it works with Dapper and anything
  else written against `System.Data.Common`.
- Behaviour is checked against a real SQL Server: 530 recorded comparison cases and two differential fuzzers
  (see [Testing](#testing)).

## How it was built (and how it was not)

LocalDbSharp is an independent implementation written from the public T-SQL documentation. It contains no
code from SQL Server and was not produced by decompiling or disassembling SQL Server binaries. The installed
SQL Server is used only as a black box: the same SQL is run on both engines and the visible results are compared.

## Quick start

```csharp
using LocalDbSharp.Data;

using var conn = new LocalDbSharpConnection("Data Source=demo");   // connections with the same Data Source share data
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = """
    CREATE TABLE dbo.Customer (Id INT IDENTITY PRIMARY KEY, Name NVARCHAR(50) NOT NULL, Balance DECIMAL(10,2) NOT NULL DEFAULT 0);
    INSERT INTO dbo.Customer (Name, Balance) VALUES (N'Ann', 10.50), (N'Bob', 20);
    """;
cmd.ExecuteNonQuery();

cmd.CommandText = "SELECT Id, Name FROM dbo.Customer WHERE Balance > @min ORDER BY Name";
cmd.Parameters.AddWithValue("@min", 15m);
using var reader = cmd.ExecuteReader();
while (reader.Read()) Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
```

Other entry points:

| API | Use |
| --- | --- |
| `new LocalDbSharpConnection(engine)` | Bind connections to a `LocalDbEngine` instance you own (ideal for isolated tests). |
| `LocalDbEngine.GetNamed(name)` / `DropNamed(name)` | The process-wide engines behind `Data Source=name`. |
| `engine.CreateSession()` + `session.Execute(sql, ("@p", value))` | Lightweight API without ADO.NET; returns materialised `ResultSet`s. |
| `LocalDbSharpFactory.Instance` | `DbProviderFactory` for code that takes a factory. |

There is also a small `sqlcmd`-like shell:

```
dotnet run --project src/LocalDbSharp.Cli                     # interactive, end batches with GO
dotnet run --project src/LocalDbSharp.Cli -- -Q "SELECT 1"    # one query
dotnet run --project src/LocalDbSharp.Cli -- -i script.sql    # a script
```

## What is supported

**Queries** — `SELECT` with `WHERE`, `GROUP BY` (including `ROLLUP`, `CUBE`, `GROUPING SETS`, `GROUPING()`),
`HAVING`, `ORDER BY`, `DISTINCT`, `TOP` (`PERCENT`, `WITH TIES`), `OFFSET … FETCH`; inner / left / right / full /
cross joins, `CROSS APPLY` / `OUTER APPLY`; derived tables, `VALUES` constructors, `PIVOT` / `UNPIVOT`; scalar,
`IN`, `EXISTS`, `ANY` / `ALL` and correlated subqueries; `UNION [ALL]`, `INTERSECT`, `EXCEPT`; common table
expressions including recursive ones (`MAXRECURSION`); window functions (`ROW_NUMBER`, `RANK`, `DENSE_RANK`,
`NTILE`, `LAG`, `LEAD`, `FIRST_VALUE`, `LAST_VALUE`, `PERCENT_RANK`, `CUME_DIST`, and aggregates `OVER` with
`ROWS` / `RANGE` frames); `SELECT … INTO`; `FOR JSON PATH | AUTO`.

**Data modification** — `INSERT` (`VALUES`, `SELECT`, `EXEC`, `DEFAULT VALUES`), `UPDATE` and `DELETE` (with
`FROM` / joins, `TOP`), `MERGE`, `TRUNCATE`, the `OUTPUT` clause (also `OUTPUT … INTO`), modification through
simple CTEs and views.

**Schema** — tables with `IDENTITY`, defaults, computed columns, `PRIMARY KEY`, `UNIQUE`, `CHECK` and
`FOREIGN KEY` constraints (`ON DELETE CASCADE | SET NULL`, `ON UPDATE CASCADE`); `ALTER TABLE` (add / drop / alter column, add / drop
constraint); unique and clustered indexes (a scan returns rows in clustered-key order); views; schemas; multiple
databases with `USE` and three-part names; temp tables (`#t`) and table variables; sequences; `rowversion`
columns; alias types and table types (`CREATE TYPE`), including table-valued parameters (pass a `DataTable`);
`INFORMATION_SCHEMA` and a subset of the `sys` catalog views; `sp_rename`.

**Procedural T-SQL** — variables, `IF` / `ELSE`, `WHILE`, `BREAK`, `CONTINUE`, `RETURN`, `BEGIN … END`,
`TRY … CATCH`, `THROW`, `RAISERROR`, `PRINT`, stored procedures (defaults, `OUTPUT` parameters, return values),
scalar / inline / multi-statement functions, `AFTER` and `INSTEAD OF` triggers (`inserted`, `deleted`,
`UPDATE()`), dynamic SQL (`EXEC(...)`, `sp_executesql`), forward-only cursors, `GO` batches.

**Transactions** — `BEGIN` / `COMMIT` / `ROLLBACK`, nesting via `@@TRANCOUNT`, savepoints, `XACT_ABORT`,
statement-level atomicity (a failed statement leaves no partial changes).

**Types** — `bit`, `tinyint`, `smallint`, `int`, `bigint`, `decimal` / `numeric`, `money`, `smallmoney`, `real`,
`float`, `char`, `varchar`, `nchar`, `nvarchar` (including `max`), `binary`, `varbinary`, `date`, `time`,
`datetime`, `smalldatetime`, `datetime2`, `datetimeoffset`, `uniqueidentifier`, with SQL Server's precedence,
implicit conversion, decimal precision / scale and string truncation rules.

**Functions** — about 150 built-ins: string, math, date / time, conversion (`CAST`, `CONVERT` with styles,
`TRY_CAST`, `TRY_CONVERT`), aggregate (`STRING_AGG`, `STDEV`, …), JSON (`JSON_VALUE`, `JSON_QUERY`, `ISJSON`,
`OPENJSON`), metadata (`OBJECT_ID`, `SCOPE_IDENTITY`, …) and error functions.

**Fidelity details that are emulated** — three-valued logic; the default collation
`SQL_Latin1_General_CP1_CI_AS` (case-insensitive, accent-sensitive, trailing blanks ignored, separate sort
rules for `varchar` and `nvarchar`); `datetime` rounding to 1/300 s; SQL Server error numbers and messages
for common errors; statement-terminating versus batch-aborting errors; `@@ROWCOUNT`, `@@IDENTITY`, `@@ERROR`.

## What is not supported

- Persistence, backup / restore, files and filegroups. Everything lives in memory and is gone with the engine.
- Isolation between concurrent transactions. Each batch runs under one engine-wide lock, so batches are atomic
  with respect to each other, but there are no row locks, no blocking and no snapshot isolation.
- `FOR XML`, the `xml` type and XQuery, full-text search, spatial types, `hierarchyid`, `sql_variant` semantics.
- CLR integration and CLR types, linked servers, `OPENROWSET`.
- `GOTO`, scrollable cursors, `AT TIME ZONE`, DDL triggers, temporal tables.
- Security: logins, users, permissions and `EXECUTE AS` are accepted where harmless but not enforced.
- Non-default collations: `COLLATE` clauses are parsed and ignored.
- The TDS wire protocol: existing `SqlConnection`-based code (and therefore EF Core's SQL Server provider)
  cannot connect; use `LocalDbSharpConnection`.

Known behavioural differences:

- Name and type errors that SQL Server reports when it compiles a batch (an invalid column, for example) are
  reported when the offending statement runs, so earlier statements of the batch have already executed.
- SQL Server rejects some constructs up front (a non-grouped column in the select list, a constant in
  `GROUP BY`, …); LocalDbSharp is more lenient and evaluates them.
- Values are bounded by the CLR: `decimal` holds 28–29 significant digits rather than 38.
- Result sets are materialised; a reader does not stream rows, and when a batch fails the result sets
  produced before the error are not delivered.
- Session options: `NOCOUNT`, `XACT_ABORT`, `IDENTITY_INSERT`, `DATEFIRST`, `DATEFORMAT`, `LANGUAGE` (date order
  only) and `ROWCOUNT` (for `SELECT`) are honoured. Turning off `ANSI_NULLS`, `ANSI_WARNINGS`, `ARITHABORT`,
  `ANSI_PADDING`, `QUOTED_IDENTIFIER` or `CONCAT_NULL_YIELDS_NULL` is rejected; other options are ignored.
- `MERGE` does not fire `INSTEAD OF` triggers, and `READONLY` is not enforced on table-valued parameters.
- nvarchar equality follows the varchar rules of the collation (`N'ß' = N'ss'` is false here, true on SQL Server).
- Query hints, index hints and `WITH (NOLOCK)` are accepted and ignored. Non-unique indexes are accepted but
  have no effect; lookups by equality use hash structures built on demand.

## Repository layout

```
src/LocalDbSharp/            the engine
    Parsing/                 lexer, AST, recursive-descent parser
    Storage/                 catalog: tables, constraints, unique indexes, views, routines, sequences, triggers
    Execution/               sessions, statement execution, query processing, expression evaluation, functions
    Data/                    the ADO.NET provider
    TypeSystem.cs            conversions, comparison, arithmetic        SqlCollation.cs   string ordering
src/LocalDbSharp.Cli/        command line shell
tests/LocalDbSharp.Tests/    xUnit tests: provider, engine API and the SQL Server conformance cases
tests/LocalDbSharp.Conformance/   console runner for the conformance cases and the differential fuzzer
tools/conf.sh                shortcut for the console runner
```

## Testing

```
dotnet test                                   # everything, against the recorded SQL Server output
tools/conf.sh [filter]                        # conformance cases only, compact failure output
tools/conf.sh fuzz 5000 42                    # 5000 random queries (seed 42) compared live with SQL Server
tools/conf.sh fuzzdml 1500 42                 # 1500 random data modifications compared live with SQL Server
```

**Conformance cases.** Each file in `tests/LocalDbSharp.Tests/Conformance/Cases` is a list of named cases
(`--# name`) that run in order against one fresh database. Their expected output — column names and types,
every value, or the error number — was recorded from a real SQL Server into `Conformance/Expected/*.json`,
so the suite runs anywhere without a server. When a `.sql` file changes, its recording is refreshed
automatically from the server named by `LOCALDBSHARP_ORACLE` (default: the local default instance with Windows
authentication); set `LOCALDBSHARP_RECORD=1` to refresh everything.

**Fuzzers.** `fuzz` generates type-correct random queries over a small schema (projections, joins,
aggregation, window functions, set operations, subqueries) and runs each on both engines. `fuzzdml` applies a
random stream of `INSERT` / `UPDATE` / `DELETE` / `MERGE` statements and transactions to constrained tables and
compares every outcome and the table contents after each statement. Both need a reachable SQL Server and are
the quickest way to find semantic differences; turn anything they find into a conformance case.

Some differences are deliberately not chased, because on SQL Server they depend on the query plan rather than
on the query: which error is reported when a statement breaks several constraints at once, whether an
expression that fails is evaluated for rows that are filtered out anyway, and the intermediate decimal scale
of literals in statements that SQL Server auto-parameterizes.

On machines with Windows Smart App Control enabled, freshly built test assemblies are sometimes blocked from
loading (`dotnet test` then reports that no test is available). The console runner behind `tools/conf.sh`
exercises the same conformance cases.
