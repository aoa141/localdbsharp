using System.Data.Common;
using System.Globalization;
using System.Text;
using LocalDbSharp;
using LocalDbSharp.Data;
using LocalDbSharp.Tests.Conformance;
using Microsoft.Data.SqlClient;

/// <summary>
/// Differential fuzzer: generates random (but type-correct) queries over a small fixed schema and runs each one
/// on both LocalDbSharp and a real SQL Server, reporting any query whose outcome differs.
///
/// The tables are temporary tables on purpose: for simple queries on permanent tables SQL Server replaces
/// literals with typed parameters (simple parameterization), which changes intermediate decimal scales in
/// ways that depend on plan-cache heuristics rather than on the query.
///
/// Every query orders by all of its output columns, so results are deterministic. Queries that fail on both
/// engines count as agreement even if the error numbers differ, because SQL Server is free to evaluate
/// expressions in a different order.
/// </summary>
internal sealed class Fuzzer(int seed)
{
    readonly Random _rnd = new(seed);

    public const string Setup = """
        CREATE TABLE #T1 (id INT PRIMARY KEY, a INT NULL, b INT NULL, d DECIMAL(9,2) NULL, s VARCHAR(20) NULL, n NVARCHAR(20) NULL, dt DATE NULL, f FLOAT NULL, bt BIT NULL);
        CREATE TABLE #T2 (id INT PRIMARY KEY, t1id INT NULL, x INT NULL, m MONEY NULL, c CHAR(5) NULL, big BIGINT NULL, ts DATETIME NULL);
        INSERT INTO #T1 VALUES
            (1, 10, 3, 12.50, 'apple', N'Zoë', '2024-01-15', 1.5, 1),
            (2, -4, 3, 0.00, 'Banana', N'o''neil', '2024-02-29', -2.25, 0),
            (3, NULL, 7, 99.99, NULL, N'smith-jones', '2023-12-31', NULL, NULL),
            (4, 10, NULL, -7.10, 'cherry pie', N'Smith', NULL, 0, 1),
            (5, 0, 0, NULL, '', N'', '2024-03-01', 1e3, 0),
            (6, 25, 12, 1000.00, 'apple ', N'ÅNGSTRÖM', '2020-06-30', 0.1, 1),
            (7, 7, 7, 7.77, 'x_y%z', NULL, '2024-01-15', 3.14159, NULL),
            (8, 21474, -9, 0.01, 'APPLE', N'co-op', '1999-12-31', -0.5, 0),
            (9, 3, 100, 5.55, '10', N'coop', '2024-12-25', 2, 1),
            (10, NULL, NULL, NULL, 'zebra', N'de la Cruz', NULL, NULL, 0);
        INSERT INTO #T2 VALUES
            (1, 1, 5, 10.5000, 'ab', 100, '2024-01-15 08:30:00'),
            (2, 1, -5, 0.3333, 'AB   ', 3000000000, '2024-01-15 23:59:59'),
            (3, 2, 0, NULL, NULL, -1, NULL),
            (4, 4, 12, 99.9900, 'x', NULL, '2023-06-01 12:00:00'),
            (5, 4, 12, 1.0000, 'xy', 7, '2024-02-29 00:00:00'),
            (6, 6, NULL, 250.0000, 'zz', 0, '2020-01-01 00:00:01'),
            (7, 9, 3, 0.0100, '', 42, '2024-03-10 10:10:10'),
            (8, NULL, 8, 8.8800, 'orph', 8, '2024-03-10 10:10:10'),
            (9, 99, 1, 5.0000, 'q', 123456789012, '2025-01-01 00:00:00'),
            (10, 7, 2, -3.5000, 'ab', 100, NULL);
        """;

    bool _noSubqueries; // SQL Server does not allow subqueries inside aggregate arguments

    int Next(int n) => _rnd.Next(n);
    bool Chance(int percent) => _rnd.Next(100) < percent;
    T Pick<T>(params T[] items) => items[_rnd.Next(items.Length)];

    // ------------------------------------------------------------ expressions

    /// <summary>Which table aliases are in scope: "t" (#T1) and optionally "u" (#T2).</summary>
    sealed record Scope(bool HasT1, bool HasT2);

    string IntCol(Scope sc)
    {
        var cols = new List<string>();
        if (sc.HasT1) cols.AddRange(["t.id", "t.a", "t.b"]);
        if (sc.HasT2) cols.AddRange(["u.id", "u.x", "u.t1id"]);
        return cols[Next(cols.Count)];
    }

    string IntLit() => Pick("0", "1", "2", "3", "5", "7", "10", "-1", "-3", "100", "CAST(NULL AS INT)");
    string NonZeroInt() => Pick("1", "2", "3", "7", "-2", "10");

    string Int(Scope sc, int depth)
    {
        if (depth <= 0) return Chance(70) ? IntCol(sc) : IntLit();
        switch (Next(22))
        {
            case 0: case 1: case 2: return IntCol(sc);
            case 3: return IntLit();
            case 4: return $"({Int(sc, depth - 1)} + {Int(sc, depth - 1)})";
            case 5: return $"({Int(sc, depth - 1)} - {Int(sc, depth - 1)})";
            case 6: return $"({Int(sc, depth - 1)} * {Pick("2", "3", "-1", "10")})";
            case 7: return $"({Int(sc, depth - 1)} / {NonZeroInt()})";
            case 8: return $"({Int(sc, depth - 1)} % {NonZeroInt()})";
            case 9: return $"ABS({Int(sc, depth - 1)})";
            case 10: return $"LEN({Str(sc, depth - 1)})";
            case 11: return $"CHARINDEX({Pick("'a'", "'p'", "'e'", "''", "' '")}, {Str(sc, depth - 1)})";
            case 12: return $"DATEDIFF({Pick("day", "month", "year", "week", "quarter")}, {Date(sc, depth - 1)}, {Date(sc, depth - 1)})";
            case 13: return $"CASE WHEN {Pred(sc, depth - 1)} THEN {Int(sc, depth - 1)} ELSE {Int(sc, depth - 1)} END";
            case 14: return $"ISNULL({Int(sc, depth - 1)}, {Int(sc, depth - 1)})";
            case 15: return $"COALESCE({Int(sc, depth - 1)}, {Int(sc, depth - 1)}, {IntLit()})";
            case 16: return $"NULLIF({IntCol(sc)}, {Int(sc, depth - 1)})";
            case 17: return $"CAST({Dec(sc, depth - 1)} AS INT)";
            case 18: return $"{Pick("YEAR", "MONTH", "DAY")}({Date(sc, depth - 1)})";
            case 19: return $"SIGN({Int(sc, depth - 1)})";
            case 20: return $"DATEPART({Pick("weekday", "dayofyear", "quarter", "week")}, {Date(sc, depth - 1)})";
            default:
                return Next(9) switch
                {
                    0 => $"IIF({Pred(sc, depth - 1)}, {Int(sc, depth - 1)}, {Int(sc, depth - 1)})",
                    1 => $"PATINDEX({Pick("'%[0-9]%'", "'%p%'", "'a%'", "'%[^a-z]%'")}, {Str(sc, depth - 1)})",
                    2 => $"CHARINDEX({Pick("'p'", "'e'", "'-'")}, {Str(sc, depth - 1)}, {Pick("1", "2", "4")})",
                    3 => $"TRY_CAST({Str(sc, depth - 1)} AS INT)",
                    4 => $"{Pick("ASCII", "UNICODE")}({Str(sc, depth - 1)})",
                    5 => $"DATALENGTH({Pick(Str(sc, depth - 1), Int(sc, 0), Dec(sc, 0))})",
                    6 => sc.HasT2 ? $"DATEPART({Pick("hour", "minute", "second", "month")}, u.ts)" : "DATEPART(month, t.dt)",
                    7 => $"CAST({Bit(sc)} AS INT)",
                    _ => $"ISNUMERIC({Str(sc, depth - 1)})",
                };
        }
    }

    string Bit(Scope sc) => Next(4) switch
    {
        0 => sc.HasT1 ? "t.bt" : "CAST(u.x AS BIT)",
        1 => $"CAST({IntCol(sc)} AS BIT)",
        2 => sc.HasT1 ? "ISNULL(t.bt, 0)" : "CAST(1 AS BIT)",
        _ => sc.HasT1 ? "t.bt" : "CAST(0 AS BIT)",
    };

    string Float(Scope sc, int depth)
    {
        string col = sc.HasT1 ? "t.f" : "CAST(u.x AS FLOAT)";
        if (depth <= 0) return col;
        return Next(10) switch
        {
            0 or 1 => col,
            2 => $"({Float(sc, depth - 1)} * {Pick("2", "0.5", "1e2")})",
            3 => $"({Float(sc, depth - 1)} / {Pick("3", "7", "0.25")})",
            4 => $"({Float(sc, depth - 1)} + {Pick(Int(sc, depth - 1), Dec(sc, depth - 1))})",
            5 => $"ROUND({Float(sc, depth - 1)}, {Pick("0", "1", "2")})",
            6 => $"{Pick("FLOOR", "CEILING", "ABS", "SQUARE")}({Float(sc, depth - 1)})",
            7 => $"SQRT(ABS({Float(sc, depth - 1)}))",
            8 => $"CAST({Pick(Int(sc, depth - 1), Dec(sc, depth - 1))} AS FLOAT)",
            _ => $"POWER({Float(sc, depth - 1)}, 2)",
        };
    }

    string Money(Scope sc, int depth)
    {
        string col = sc.HasT2 ? "u.m" : "CAST(t.d AS MONEY)";
        if (depth <= 0) return col;
        return Next(9) switch
        {
            0 or 1 => col,
            2 => $"({Money(sc, depth - 1)} * {Pick("2", "3", "10")})",
            3 => $"({Money(sc, depth - 1)} / {Pick("3", "7", "2")})",
            4 => $"({Money(sc, depth - 1)} + {Pick("1", "$0.50", Int(sc, 0))})",
            5 => $"ABS({Money(sc, depth - 1)})",
            6 => $"ROUND({Money(sc, depth - 1)}, {Pick("0", "1", "2")})",
            7 => $"ISNULL({Money(sc, depth - 1)}, 0)",
            _ => $"({Money(sc, depth - 1)} - {Money(sc, depth - 1)})",
        };
    }

    string Big(Scope sc, int depth)
    {
        string col = sc.HasT2 ? "u.big" : "CAST(t.a AS BIGINT)";
        if (depth <= 0) return col;
        return Next(7) switch
        {
            0 or 1 => col,
            2 => $"({Big(sc, depth - 1)} + {Int(sc, depth - 1)})",
            3 => $"({Big(sc, depth - 1)} * {Pick("2", "1000", "-1")})",
            4 => $"({Big(sc, depth - 1)} / {Pick("7", "1000")})",
            5 => $"ABS({Big(sc, depth - 1)})",
            _ => $"ISNULL({Big(sc, depth - 1)}, {Int(sc, 0)})",
        };
    }

    string DateTimeExpr(Scope sc, int depth)
    {
        string col = sc.HasT2 ? "u.ts" : "CAST(t.dt AS DATETIME)";
        if (depth <= 0) return col;
        return Next(6) switch
        {
            0 or 1 => col,
            2 => $"DATEADD({Pick("hour", "minute", "day", "month", "second")}, {Pick("1", "-90", "36", Int(sc, 0))}, {DateTimeExpr(sc, depth - 1)})",
            3 => $"CAST({Date(sc, depth - 1)} AS DATETIME)",
            4 => $"ISNULL({DateTimeExpr(sc, depth - 1)}, '2024-01-01 12:00:00')",
            _ => $"CAST({DateTimeExpr(sc, depth - 1)} AS DATETIME2(3))",
        };
    }

    string DecCol(Scope sc) => sc.HasT2 && (!sc.HasT1 || Chance(30)) ? "CAST(u.m AS DECIMAL(12,4))" : sc.HasT1 ? "t.d" : "CAST(u.m AS DECIMAL(12,4))";
    string DecLit() => Pick("1.5", "0.25", "10.00", "-2.5", "3.333", "100.0", "0.1");

    string Dec(Scope sc, int depth)
    {
        if (depth <= 0) return Chance(70) ? DecCol(sc) : DecLit();
        switch (Next(16))
        {
            case 0: case 1: case 2: return DecCol(sc);
            case 3: return DecLit();
            case 4: return $"({Dec(sc, depth - 1)} + {Dec(sc, depth - 1)})";
            case 5: return $"({Dec(sc, depth - 1)} - {Int(sc, depth - 1)})";
            case 6: return $"({Dec(sc, depth - 1)} * {Pick("2", "1.5", "0.1", "-1")})";
            case 7: return $"({Dec(sc, depth - 1)} / {Pick("2", "3", "1.5", "0.3", "7")})";
            case 8: return $"({Int(sc, depth - 1)} * {DecLit()})";
            case 9: return $"ROUND({Dec(sc, depth - 1)}, {Pick("0", "1", "2", "-1")})";
            case 10: return $"CAST({Int(sc, depth - 1)} AS DECIMAL({Pick("10,2", "12,0", "18,4", "9,3")}))";
            case 11: return $"ABS({Dec(sc, depth - 1)})";
            case 12: return $"CASE WHEN {Pred(sc, depth - 1)} THEN {Dec(sc, depth - 1)} ELSE {Pick(Int(sc, depth - 1), Dec(sc, depth - 1))} END";
            case 13: return $"ISNULL({Dec(sc, depth - 1)}, {Pick("0", "1.5", Dec(sc, depth - 1))})";
            case 14: return $"{Pick("CEILING", "FLOOR")}({Dec(sc, depth - 1)})";
            default: return $"COALESCE({Dec(sc, depth - 1)}, {Int(sc, depth - 1)})";
        }
    }

    string StrCol(Scope sc)
    {
        var cols = new List<string>();
        if (sc.HasT1) cols.AddRange(["t.s", "t.s", "t.n"]);
        if (sc.HasT2) cols.Add("u.c");
        return cols[Next(cols.Count)];
    }

    string StrLit() => Pick("'apple'", "'a'", "''", "'Z'", "' x '", "'10'", "N'é'", "'co-op'", "CAST(NULL AS VARCHAR(5))", "'_'");

    string Str(Scope sc, int depth)
    {
        if (depth <= 0) return Chance(70) ? StrCol(sc) : StrLit();
        switch (Next(18))
        {
            case 0: case 1: case 2: return StrCol(sc);
            case 3: return StrLit();
            case 4: return $"({Str(sc, depth - 1)} + {Str(sc, depth - 1)})";
            case 5: return $"{Pick("UPPER", "LOWER", "LTRIM", "RTRIM", "REVERSE", "TRIM")}({Str(sc, depth - 1)})";
            case 6: return $"{Pick("LEFT", "RIGHT")}({Str(sc, depth - 1)}, {Pick("0", "1", "2", "5")})";
            case 7: return $"SUBSTRING({Str(sc, depth - 1)}, {Pick("0", "1", "2", "4")}, {Pick("0", "1", "3", "10")})";
            case 8: return $"REPLACE({Str(sc, depth - 1)}, {Pick("'a'", "'p'", "' '", "'e'")}, {Pick("''", "'X'", "'--'")})";
            case 9: return $"CAST({Int(sc, depth - 1)} AS VARCHAR(12))";
            case 10: return $"CONCAT({Str(sc, depth - 1)}, {Pick(Int(sc, depth - 1), Str(sc, depth - 1), Dec(sc, depth - 1))})";
            case 11: return $"ISNULL({Str(sc, depth - 1)}, {Pick("'?'", "''", "'none'")})";
            case 12: return $"REPLICATE({Str(sc, depth - 1)}, {Pick("0", "1", "2")})";
            case 13: return $"CASE WHEN {Pred(sc, depth - 1)} THEN {Str(sc, depth - 1)} ELSE {Str(sc, depth - 1)} END";
            case 14: return $"CONVERT(VARCHAR(10), {Date(sc, depth - 1)}, {Pick("23", "112", "101", "103", "120")})";
            case 15: return $"CAST({Dec(sc, depth - 1)} AS VARCHAR(30))";
            case 16: return $"STUFF({Str(sc, depth - 1)}, {Pick("1", "2", "3")}, {Pick("0", "1", "2")}, {Pick("'*'", "''")})";
            default:
                return Next(12) switch
                {
                    0 => $"COALESCE({Str(sc, depth - 1)}, {Str(sc, depth - 1)}, '')",
                    1 => $"CAST({Float(sc, depth - 1)} AS VARCHAR(30))",
                    2 => $"CONVERT(VARCHAR(30), {DateTimeExpr(sc, depth - 1)}, {Pick("0", "100", "108", "120", "121", "126", "113", "109")})",
                    3 => $"CAST({Money(sc, depth - 1)} AS VARCHAR(30))",
                    4 => $"RIGHT('000' + CAST({Int(sc, depth - 1)} AS VARCHAR(5)), 3)",
                    5 => $"CONCAT_WS({Pick("'-'", "', '", "''")}, {Str(sc, depth - 1)}, {Pick(Int(sc, depth - 1), Str(sc, depth - 1))}, {StrCol(sc)})",
                    6 => $"QUOTENAME({Str(sc, depth - 1)})",
                    7 => $"STR({Pick(Dec(sc, depth - 1), Int(sc, depth - 1))}, {Pick("8", "10", "4")}, {Pick("0", "2")})",
                    8 => $"CHAR(65 + ABS({IntCol(sc)}) % 26)",
                    9 => $"DATENAME({Pick("month", "weekday", "year", "day")}, {Date(sc, depth - 1)})",
                    10 => $"CAST({Bit(sc)} AS VARCHAR(5))",
                    _ => $"CAST({Big(sc, depth - 1)} AS VARCHAR(25))",
                };
        }
    }

    string DateLit() => Pick("CAST('2024-01-15' AS DATE)", "CAST('2023-12-31' AS DATE)", "CAST('2024-02-29' AS DATE)", "CAST('2000-01-01' AS DATE)");

    string Date(Scope sc, int depth)
    {
        string col = sc.HasT1 ? "t.dt" : "CAST(u.ts AS DATE)";
        if (depth <= 0) return Chance(70) ? col : DateLit();
        switch (Next(8))
        {
            case 0: case 1: case 2: return col;
            case 3: return DateLit();
            case 4: return $"DATEADD({Pick("day", "month", "year", "week")}, {Pick("1", "-1", "30", "12", Int(sc, 0))}, {Date(sc, depth - 1)})";
            case 5: return $"EOMONTH({Date(sc, depth - 1)})";
            case 6: return $"ISNULL({Date(sc, depth - 1)}, {DateLit()})";
            default: return $"CASE WHEN {Pred(sc, depth - 1)} THEN {Date(sc, depth - 1)} ELSE {Date(sc, depth - 1)} END";
        }
    }

    string Pred(Scope sc, int depth)
    {
        string cmp = Pick("=", "<>", "<", "<=", ">", ">=");
        switch (Next(depth <= 0 ? 8 : _noSubqueries ? 14 : 16))
        {
            case 0: case 1: return $"{Int(sc, depth - 1)} {cmp} {Int(sc, depth - 1)}";
            case 2: return $"{Dec(sc, depth - 1)} {cmp} {Pick(Dec(sc, depth - 1), Int(sc, depth - 1))}";
            case 3: return $"{Str(sc, depth - 1)} {cmp} {Str(sc, depth - 1)}";
            case 4: return $"{Pick(Int(sc, depth - 1), Str(sc, depth - 1), Date(sc, depth - 1))} IS {Pick("", "NOT ")}NULL";
            case 5: return $"{Int(sc, depth - 1)} {Pick("", "NOT ")}IN ({IntLit()}, {IntLit()}, {IntLit()})";
            case 6: return $"{Str(sc, depth - 1)} {Pick("", "NOT ")}LIKE {Pick("'a%'", "'%e'", "'%pp%'", "'_'", "'[a-c]%'", "'%[^a-y]'", "''", "'%'", "'x!_y%' ESCAPE '!'")}";
            case 7:
                return Next(6) switch
                {
                    0 or 1 => $"{Date(sc, depth - 1)} {cmp} {Date(sc, depth - 1)}",
                    2 => $"{Bit(sc)} = {Pick("1", "0")}",
                    3 => $"{Float(sc, depth - 1)} {cmp} {Pick(Dec(sc, depth - 1), Int(sc, depth - 1), Float(sc, depth - 1))}",
                    4 => $"{Money(sc, depth - 1)} {cmp} {Pick(Int(sc, depth - 1), Dec(sc, depth - 1))}",
                    _ => $"{DateTimeExpr(sc, depth - 1)} {cmp} {Pick(Date(sc, depth - 1), DateTimeExpr(sc, depth - 1))}",
                };
            case 8: case 9: return $"({Pred(sc, depth - 1)} AND {Pred(sc, depth - 1)})";
            case 10: case 11: return $"({Pred(sc, depth - 1)} OR {Pred(sc, depth - 1)})";
            case 12: return $"NOT ({Pred(sc, depth - 1)})";
            case 13: return $"{Int(sc, depth - 1)} BETWEEN {Int(sc, depth - 1)} AND {Int(sc, depth - 1)}";
            case 14:
                return sc.HasT1
                    ? $"{Pick("", "NOT ")}EXISTS (SELECT 1 FROM #T2 u2 WHERE u2.t1id = t.id AND u2.x {cmp} {IntLit()})"
                    : $"{Int(sc, depth - 1)} {cmp} {IntLit()}";
            default:
                return $"{Int(sc, depth - 1)} {Pick("", "NOT ")}IN (SELECT {Pick("x", "t1id", "id")} FROM #T2 WHERE {Pick("x > 0", "c IS NOT NULL", "1 = 1", "x IS NOT NULL")})";
        }
    }

    /// <summary>An expression that is guaranteed to reference a column (SQL Server rejects constants in GROUP BY / window ORDER BY).</summary>
    string Keyed(Scope sc) => Next(8) switch
    {
        0 or 1 => IntCol(sc),
        2 => StrCol(sc),
        3 => $"({IntCol(sc)} % {Pick("2", "3")})",
        4 => $"UPPER({StrCol(sc)})",
        5 => $"LEN({StrCol(sc)})",
        6 => $"({IntCol(sc)} + {Pick("1", "10")})",
        _ => sc.HasT1 ? "YEAR(t.dt)" : "u.c",
    };

    string Any(Scope sc, int depth) => Next(12) switch
    {
        0 or 1 or 2 => Int(sc, depth),
        3 or 4 => Dec(sc, depth),
        5 or 6 => Str(sc, depth),
        7 => Date(sc, depth),
        8 => Float(sc, depth),
        9 => Money(sc, depth),
        10 => Pick(Big(sc, depth), Bit(sc)),
        _ => DateTimeExpr(sc, depth),
    };

    // ------------------------------------------------------------ queries

    static string OrderAll(int n) => " ORDER BY " + string.Join(", ", Enumerable.Range(1, n));

    string From(out Scope sc)
    {
        switch (Next(6))
        {
            case 0: case 1: case 2:
                sc = new Scope(true, false);
                return "#T1 t";
            case 3:
                sc = new Scope(true, true);
                return $"#T1 t {Pick("JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN")} #T2 u ON u.t1id = t.id" + (Chance(30) ? $" AND {Pred(new Scope(true, true), 1)}" : "");
            case 4:
                sc = new Scope(true, true);
                return $"#T1 t {Pick("JOIN", "LEFT JOIN")} #T2 u ON u.x = t.b";
            default:
                sc = new Scope(false, true);
                return "#T2 u";
        }
    }

    string Agg(Scope sc)
    {
        _noSubqueries = true;
        try { return AggCore(sc); }
        finally { _noSubqueries = false; }
    }

    string AggCore(Scope sc)
    {
        switch (Next(10))
        {
            case 0: return "COUNT(*)";
            case 1: return $"COUNT({Any(sc, 1)})";
            case 2: return $"SUM({Pick(Int(sc, 1), Dec(sc, 1))})";
            case 3: return $"AVG({Pick(Int(sc, 1), Dec(sc, 1))})";
            case 4: return $"MIN({Pick(Int(sc, 1), Dec(sc, 1), Date(sc, 1))})";
            case 5: return $"MAX({Pick(Int(sc, 1), Dec(sc, 1), Date(sc, 1))})";
            case 6: return $"COUNT(DISTINCT {Pick(Int(sc, 1), Str(sc, 0))})";
            case 7: return $"SUM(CASE WHEN {Pred(sc, 1)} THEN 1 ELSE 0 END)";
            case 8: return $"MAX(LEN({Str(sc, 1)}))";
            default:
                return Next(8) switch
                {
                    0 => $"SUM(DISTINCT {Int(sc, 1)})",
                    1 => $"{Pick("SUM", "AVG", "MIN", "MAX")}({Money(sc, 1)})",
                    2 => $"{Pick("SUM", "AVG", "MIN", "MAX")}({Float(sc, 1)})",
                    3 => $"{Pick("SUM", "AVG", "MAX")}({Big(sc, 1)})",
                    4 => $"{Pick("MIN", "MAX")}({DateTimeExpr(sc, 1)})",
                    5 => $"{Pick("MIN", "MAX")}({Str(sc, 1)})",
                    6 => $"STRING_AGG(CAST({(sc.HasT1 ? "t.id" : "u.id")} AS VARCHAR(10)), ',') WITHIN GROUP (ORDER BY {(sc.HasT1 ? "t.id" : "u.id")}{(sc.HasT1 && sc.HasT2 ? ", u.id" : "")})",
                    _ => $"{Pick("STDEV", "VAR", "STDEVP")}({Int(sc, 1)})",
                };
        }
    }

    public string Query()
    {
        string from = From(out var sc);
        string where = Chance(55) ? " WHERE " + Pred(sc, 2) : "";
        int shape = Next(12);
        switch (shape)
        {
            case 0: case 1: case 2: case 3:
            {
                // plain projection
                int n = 1 + Next(4);
                var cols = Enumerable.Range(0, n).Select(i => $"{Any(sc, 2)} AS c{i}").ToList();
                string distinct = Chance(15) ? "DISTINCT " : "";
                return $"SELECT {distinct}{string.Join(", ", cols)} FROM {from}{where}{OrderAll(n)}";
            }
            case 4: case 5: case 6:
            {
                // grouped aggregation
                int keys = Next(3);
                var groupExprs = Enumerable.Range(0, keys).Select(_ => Keyed(sc)).Distinct().ToList();
                var aggs = Enumerable.Range(0, 1 + Next(3)).Select(i => $"{Agg(sc)} AS a{i}").ToList();
                var select = groupExprs.Select((g, i) => $"{g} AS g{i}").Concat(aggs).ToList();
                string group = groupExprs.Count > 0 ? " GROUP BY " + string.Join(", ", groupExprs) : "";
                string having = groupExprs.Count > 0 && Chance(30) ? $" HAVING {Pick("COUNT(*)", $"SUM({IntCol(sc)})", $"MAX({IntCol(sc)})", $"COUNT({StrCol(sc)})")} {Pick(">", "<", ">=", "<>")} {Pick("0", "1", "2", "10")}" : "";
                return $"SELECT {string.Join(", ", select)} FROM {from}{where}{group}{having}{OrderAll(select.Count)}";
            }
            case 7: case 8:
            {
                // window functions; the row id makes every window ordering deterministic
                string id = sc.HasT1 ? "t.id" : "u.id";
                string id2 = sc.HasT1 && sc.HasT2 ? ", u.id" : "";
                string part = Chance(60) ? $"PARTITION BY {Pick(IntCol(sc), Str(sc, 0), Date(sc, 0))} " : "";
                string ord = $"ORDER BY {Keyed(sc)} {Pick("", "DESC")}, {id}{id2}";
                string fn = Next(9) switch
                {
                    0 => $"ROW_NUMBER() OVER ({part}{ord})",
                    1 => $"RANK() OVER ({part}ORDER BY {Keyed(sc)})",
                    2 => $"DENSE_RANK() OVER ({part}ORDER BY {Keyed(sc)} DESC)",
                    3 => $"SUM({Pick(Int(sc, 1), Dec(sc, 0))}) OVER ({part}{ord})",
                    4 => $"COUNT(*) OVER ({part.TrimEnd()})",
                    5 => $"{Pick("LAG", "LEAD")}({Any(sc, 1)}{Pick("", ", 2")}) OVER ({part}{ord})",
                    6 => $"{Pick("MIN", "MAX", "AVG")}({Pick(Int(sc, 1), Dec(sc, 0))}) OVER ({part}{ord} ROWS BETWEEN {Pick("1 PRECEDING", "UNBOUNDED PRECEDING", "CURRENT ROW")} AND {Pick("CURRENT ROW", "1 FOLLOWING", "UNBOUNDED FOLLOWING")})",
                    7 => $"NTILE({Pick("2", "3", "4")}) OVER ({part}{ord})",
                    _ => $"{Pick("FIRST_VALUE", "LAST_VALUE")}({Any(sc, 1)}) OVER ({part}{ord})",
                };
                return $"SELECT {id} AS k{(id2 != "" ? ", u.id AS k2" : "")}, {fn} AS w, {Any(sc, 1)} AS e FROM {from}{where}{OrderAll(id2 != "" ? 4 : 3)}";
            }
            case 9:
            {
                // set operations
                var a = new Scope(true, false);
                var b = new Scope(false, true);
                string op = Pick("UNION", "UNION ALL", "INTERSECT", "EXCEPT");
                bool strings = Chance(40);
                string left = strings ? $"{Str(a, 1)}" : $"{Int(a, 1)}";
                string right = strings ? $"{Str(b, 1)}" : $"{Pick(Int(b, 1), Dec(b, 1))}";
                return $"SELECT {left} AS v FROM #T1 t {op} SELECT {right} FROM #T2 u ORDER BY 1";
            }
            case 10:
            {
                // correlated scalar subqueries
                var t = new Scope(true, false);
                string sub = $"(SELECT {Pick("COUNT(*)", "MAX(u.x)", "SUM(u.m)", "MIN(u.c)", "AVG(u.x)", "MAX(u.big)")} FROM #T2 u WHERE u.t1id = t.id{(Chance(40) ? " AND " + Pred(new Scope(true, true), 1) : "")})";
                return $"SELECT t.id AS k, {sub} AS sq, {Any(t, 1)} AS e FROM #T1 t{(Chance(50) ? " WHERE " + Pred(t, 2) : "")} ORDER BY 1, 2, 3";
            }
            default:
            {
                // derived table with outer aggregation
                var t = new Scope(true, false);
                string inner = $"SELECT {Int(t, 1)} AS p, {Pick(Dec(t, 1), Int(t, 1))} AS q, {Str(t, 1)} AS r FROM #T1 t{(Chance(50) ? " WHERE " + Pred(t, 1) : "")}";
                return Chance(50)
                    ? $"SELECT x.p, COUNT(*) AS n, SUM(x.q) AS sq, MAX(x.r) AS mr FROM ({inner}) AS x GROUP BY x.p ORDER BY 1, 2, 3, 4"
                    : $"SELECT x.p, x.q, x.r FROM ({inner}) AS x WHERE x.p {Pick(">", "<", "<>")} {IntLit()} ORDER BY 1, 2, 3";
            }
        }
    }

    // ------------------------------------------------------------ comparison

    static string RowKey(List<string?> row, bool normalize, List<ColumnShape> columns)
        => string.Join("\u0001", row.Select((v, i) =>
        {
            if (v == null) return "\u0000";
            // floating point sums may differ in the last bits depending on evaluation order
            if (columns[i].ClrType is "Double" or "Single" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d.ToString("G9", CultureInfo.InvariantCulture);
            return normalize ? v.TrimEnd(' ').ToUpperInvariant() : v;
        }));

    static bool SameMultiset(ResultShape e, ResultShape a, bool normalize)
    {
        var counts = new Dictionary<string, int>();
        foreach (var row in e.Rows) counts[RowKey(row, normalize, e.Columns)] = counts.GetValueOrDefault(RowKey(row, normalize, e.Columns)) + 1;
        foreach (var row in a.Rows)
        {
            string k = RowKey(row, normalize, e.Columns);
            if (!counts.TryGetValue(k, out int n) || n == 0) return false;
            counts[k] = n - 1;
        }
        return true;
    }

    /// <summary>
    /// Differences that are not bugs: rows that tie under the collation ('apple' vs 'APPLE ') may come back in
    /// either order, and DISTINCT / GROUP BY / MIN / MAX may return either spelling of such a tie.
    /// </summary>
    static bool Tolerable(CaseResult e, CaseResult a, string sql)
    {
        if (e.Error != null || a.Error != null || e.Results.Count != 1 || a.Results.Count != 1) return false;
        var er = e.Results[0];
        var ar = a.Results[0];
        if (er.Rows.Count != ar.Rows.Count) return false;
        var shapeOnlyE = new CaseResult { Results = [new ResultShape { Columns = er.Columns }] };
        var shapeOnlyA = new CaseResult { Results = [new ResultShape { Columns = ar.Columns }] };
        if (ConformanceHarness.Diff(shapeOnlyE, shapeOnlyA) != null) return false;
        if (SameMultiset(er, ar, false)) return true;
        bool picksRepresentative = new[] { "DISTINCT", "GROUP BY", "MIN(", "MAX(", "UNION", "INTERSECT", "EXCEPT", "FIRST_VALUE", "LAST_VALUE", "LAG(", "LEAD(", "RANK(", "ROW_NUMBER(", "NTILE(", " OVER (" }
            .Any(k => sql.Contains(k, StringComparison.Ordinal));
        return picksRepresentative && SameMultiset(er, ar, true);
    }

    // ------------------------------------------------------------ driver

    public static int Run(int count, int seed, bool verbose)
    {
        var fuzzer = new Fuzzer(seed);
        int same = 0, bothFailed = 0, different = 0;
        ConformanceHarness.WithSqlServerDatabase(sqlServer =>
        {
            using var local = new LocalDbSharpConnection(new LocalDbEngine());
            local.Open();
            foreach (DbConnection conn in new DbConnection[] { sqlServer, local })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = Setup;
                cmd.ExecuteNonQuery();
            }
            for (int i = 0; i < count; i++)
            {
                string sql = fuzzer.Query();
                var expected = ConformanceHarness.RunOne(sqlServer, sql, ex => ex is SqlException se ? (se.Number, se.Message) : null);
                CaseResult actual;
                try
                {
                    actual = ConformanceHarness.RunOne(local, sql, ex => ex is TSqlException te ? (te.Number, te.Message) : null);
                }
                catch (Exception ex)
                {
                    actual = new CaseResult { Error = -1, ErrorMessage = "CRASH " + ex.GetType().Name + ": " + ex.Message };
                }
                if (expected.Error != null && actual.Error is not (null or -1))
                {
                    bothFailed++;
                    continue;
                }
                string? diff = ConformanceHarness.Diff(expected, actual);
                if (diff == null || Tolerable(expected, actual, sql))
                {
                    same++;
                    continue;
                }
                different++;
                if (different <= 15 || verbose)
                {
                    Console.WriteLine($"--- mismatch #{different} (seed {seed}, query {i})");
                    Console.WriteLine(sql);
                    Console.WriteLine(diff);
                    if (actual.Error != null) Console.WriteLine("LocalDbSharp error: " + actual.ErrorMessage);
                    Console.WriteLine();
                }
            }
            return 0;
        });
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"fuzz seed {seed}: {same} identical, {bothFailed} failed on both, {different} DIFFERENT"));
        return different == 0 ? 0 : 1;
    }
}
