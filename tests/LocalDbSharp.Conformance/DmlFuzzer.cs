using System.Data.Common;
using LocalDbSharp;
using LocalDbSharp.Data;
using LocalDbSharp.Tests.Conformance;
using Microsoft.Data.SqlClient;

/// <summary>
/// Differential fuzzer for data modification: applies the same random stream of INSERT / UPDATE / DELETE /
/// MERGE statements and transactions to both engines. After every statement the outcome (rows affected or
/// error number) must agree, and the full table contents are compared at regular intervals.
/// </summary>
internal sealed class DmlFuzzer(int seed)
{
    readonly Random _rnd = new(seed);

    const string Setup = """
        CREATE TABLE P (id INT PRIMARY KEY, name VARCHAR(10) NULL, qty INT NOT NULL DEFAULT 1 CHECK (qty >= 0), price DECIMAL(8,2) NULL, CONSTRAINT UQ_P_name UNIQUE (name));
        CREATE TABLE C (id INT IDENTITY PRIMARY KEY, pid INT NULL REFERENCES P(id), note VARCHAR(8) NOT NULL DEFAULT 'n', amount DECIMAL(6,2) NULL CHECK (amount < 500));
        CREATE TABLE K (id INT IDENTITY(10, 5) PRIMARY KEY, pid INT NOT NULL REFERENCES P(id) ON DELETE CASCADE, tag CHAR(3) NULL);
        """;

    static readonly string[] Dumps =
    [
        "SELECT id, name, qty, price FROM P ORDER BY id",
        "SELECT id, pid, note, amount FROM C ORDER BY id",
        "SELECT id, pid, tag FROM K ORDER BY id",
        "SELECT @@TRANCOUNT AS tc, IDENT_CURRENT('C') AS identC, IDENT_CURRENT('K') AS identK",
    ];

    int Next(int n) => _rnd.Next(n);
    T Pick<T>(params T[] items) => items[_rnd.Next(items.Length)];
    string Id() => Next(12).ToString();
    string Name() => Pick("'a'", "'b'", "'c'", "'d'", "'e'", "NULL", "'A'", "'toolongname1'", "'b '");
    string Qty() => Pick("0", "1", "5", "20", "-1", "NULL", "DEFAULT");
    string Price() => Pick("1.5", "99.999", "NULL", "0", "123456.78", "1234567.8", "'7.25'");
    // (an out-of-range *integer* constant is avoided: whether SQL Server rejects it when no row qualifies
    // depends on constant folding in the particular plan)
    string Amount() => Pick("10", "499.99", "500", "NULL", "-3.333", "9999.99", "12345.5");
    string Note() => Pick("'x'", "'note'", "DEFAULT", "NULL", "'123456789'", "''");
    string Cmp() => Pick("=", "<", ">", "<>", "<=");

    string Statement()
    {
        switch (Next(24))
        {
            case 0: case 1: case 2: return $"INSERT INTO P (id, name, qty, price) VALUES ({Id()}, {Name()}, {Qty()}, {Price()})";
            case 3: return $"INSERT INTO P (id, name) VALUES ({Id()}, {Name()})";
            case 4: case 5: return $"INSERT INTO C (pid, note, amount) VALUES ({Pick(Id(), "NULL")}, {Note()}, {Amount()})";
            case 6: return $"INSERT INTO K (pid, tag) VALUES ({Id()}, {Pick("'ab'", "'xyz'", "NULL", "'toolong'")})";
            case 7: return $"INSERT INTO C (pid, amount) SELECT id, price FROM P WHERE qty {Cmp()} {Next(6)}";
            case 8: return $"UPDATE P SET qty = qty {Pick("+", "-")} {Next(4)} WHERE id {Cmp()} {Id()}";
            case 9: return $"UPDATE P SET name = {Name()} WHERE id = {Id()}";
            case 10: return $"UPDATE P SET id = id + {Pick("1", "20", "-1")} WHERE id {Cmp()} {Id()}";
            // only in-range constants here: whether an out-of-range constant fails an UPDATE that matches no
            // rows depends on constant folding in SQL Server's plan
            case 11: return $"UPDATE C SET pid = {Pick(Id(), "NULL")}, amount = {Pick("10", "499.99", "500", "NULL", "-3.333")} WHERE id {Cmp()} {Next(15)}";
            case 12: return $"UPDATE c SET c.note = LEFT(p.name + 'zz', 8) FROM C c JOIN P p ON p.id = c.pid WHERE p.qty {Cmp()} {Next(6)}";
            case 13: return $"DELETE FROM P WHERE id {Cmp()} {Id()}";
            case 14: return $"DELETE FROM C WHERE {Pick($"id {Cmp()} {Next(15)}", "pid IS NULL", $"amount {Cmp()} 10")}";
            case 15: return $"DELETE FROM K WHERE id {Cmp()} {10 + 5 * Next(8)}";
            case 16: return $"DELETE p FROM P p WHERE NOT EXISTS (SELECT 1 FROM C c WHERE c.pid = p.id) AND p.qty {Cmp()} {Next(6)}";
            case 17:
                return $"MERGE P AS t USING (VALUES ({Id()}, {Name()}, {Next(9)})) AS s(id, name, qty) ON t.id = s.id "
                       + "WHEN MATCHED THEN UPDATE SET qty = s.qty, name = s.name WHEN NOT MATCHED THEN INSERT (id, name, qty) VALUES (s.id, s.name, s.qty);";
            case 18: return "BEGIN TRANSACTION";
            case 19: return Pick("COMMIT", "ROLLBACK");
            case 20: return $"UPDATE TOP (1) P SET price = price * 1.1 WHERE price IS NOT NULL AND id {Cmp()} {Id()}";
            case 21: return $"UPDATE P SET price = ROUND(price / 3, 2), qty = qty * 2 WHERE id IN (SELECT pid FROM C WHERE amount {Cmp()} 10)";
            case 22: return $"INSERT INTO P (id, name, qty) VALUES ({Id()}, {Name()}, 1), ({Id()}, {Name()}, 2)";
            default: return Pick("TRUNCATE TABLE C", "DELETE FROM K", "SAVE TRANSACTION s1", "ROLLBACK TRANSACTION s1", "TRUNCATE TABLE K");
        }
    }

    static (int? Error, int Rows) Exec(DbConnection conn, string sql, Func<Exception, int?> errorOf)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return (null, cmd.ExecuteNonQuery());
        }
        catch (Exception ex) when (errorOf(ex) is int number)
        {
            return (number, 0);
        }
    }

    public static int Run(int count, int seed, bool verbose)
    {
        var fuzzer = new DmlFuzzer(seed);
        int problems = 0;
        ConformanceHarness.WithSqlServerDatabase(sqlServer =>
        {
            using var local = new LocalDbSharpConnection(new LocalDbEngine());
            local.Open();
            static int? SqlError(Exception ex) => ex is SqlException se ? se.Number : null;
            static int? LocalError(Exception ex) => ex is TSqlException te ? te.Number : null;
            Exec(sqlServer, Setup, SqlError);
            Exec(local, Setup, LocalError);
            var history = new List<string>();
            for (int i = 0; i < count && problems < 5; i++)
            {
                string sql = fuzzer.Statement();
                history.Add(sql);
                var expected = Exec(sqlServer, sql, SqlError);
                var actual = Exec(local, sql, LocalError);
                // When one statement breaks several constraints at once, which violation SQL Server reports
                // depends on the shape of its update plan; any constraint error counts as the same outcome.
                static bool IsConstraintError(int? n) => n is 547 or 2627 or 2601;
                bool failed = expected != actual && !(IsConstraintError(expected.Error) && IsConstraintError(actual.Error));
                string? diff = null;
                if (!failed)
                {
                    foreach (string dump in Dumps)
                    {
                        var e = ConformanceHarness.RunOne(sqlServer, dump, ex => ex is SqlException se ? (se.Number, se.Message) : null);
                        var a = ConformanceHarness.RunOne(local, dump, ex => ex is TSqlException te ? (te.Number, te.Message) : null);
                        diff = ConformanceHarness.Diff(e, a);
                        if (diff != null) { diff = dump + "\n" + diff; break; }
                    }
                    failed = diff != null;
                }
                if (!failed) continue;
                problems++;
                Console.WriteLine($"--- DML mismatch (seed {seed}, statement {i}): {sql}");
                Console.WriteLine(diff ?? $"expected {Describe(expected)}, actual {Describe(actual)}");
                Console.WriteLine("recent statements:");
                foreach (string h in history.Skip(Math.Max(0, history.Count - (verbose ? 40 : 12)))) Console.WriteLine("    " + h + ";");
                Console.WriteLine();
                break; // the engines have diverged; later differences would only be consequences
            }
            return 0;
        });
        Console.WriteLine($"dml fuzz seed {seed}: {(problems == 0 ? $"{count} statements, identical outcomes and table contents" : "DIFFERENT")}");
        return problems == 0 ? 0 : 1;

        static string Describe((int? Error, int Rows) r) => r.Error != null ? $"error {r.Error}" : $"{r.Rows} rows affected";
    }
}
