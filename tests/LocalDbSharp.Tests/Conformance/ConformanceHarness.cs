using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalDbSharp.Data;
using Microsoft.Data.SqlClient;

namespace LocalDbSharp.Tests.Conformance;

public sealed class ColumnShape
{
    public string Name { get; set; } = "";
    public string ClrType { get; set; } = "";
    public string SqlType { get; set; } = "";
}

public sealed class ResultShape
{
    public List<ColumnShape> Columns { get; set; } = [];
    public List<List<string?>> Rows { get; set; } = [];
}

public sealed class CaseResult
{
    public string Name { get; set; } = "";
    public int? Error { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ResultShape> Results { get; set; } = [];
}

/// <summary>
/// Differential test support. Each file under Cases/ is a sequence of named cases ("--# name") that run in
/// order against one fresh database. The expected output of every case is recorded from a real SQL Server
/// into Expected/*.json; the tests then replay the same file against LocalDbSharp and compare.
///
/// Recording happens when a golden file is missing, or for all files when LOCALDBSHARP_RECORD=1.
/// The SQL Server to record from is given by LOCALDBSHARP_ORACLE (default: local default instance, Windows auth).
/// </summary>
public static partial class ConformanceHarness
{
    static readonly string Root = Path.GetDirectoryName(ThisFile())!;
    public static readonly string CasesDir = Path.Combine(Root, "Cases");
    public static readonly string ExpectedDir = Path.Combine(Root, "Expected");

    static string ThisFile([CallerFilePath] string path = "") => path;

    static readonly string OracleConnectionString =
        Environment.GetEnvironmentVariable("LOCALDBSHARP_ORACLE")
        ?? "Server=localhost;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5;Pooling=false";

    static readonly bool ForceRecord = Environment.GetEnvironmentVariable("LOCALDBSHARP_RECORD") == "1";

    static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, CaseResult>>> Expected = new();
    static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, CaseResult>>> Actual = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoLine();

    public static IEnumerable<string> Files()
        => Directory.Exists(CasesDir) ? Directory.GetFiles(CasesDir, "*.sql").Select(f => Path.GetFileNameWithoutExtension(f)!).Order() : [];

    public static List<(string Name, string Sql)> ReadCases(string file)
    {
        var cases = new List<(string, string)>();
        string name = "(setup)";
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.ToString().Trim().Length > 0) cases.Add((name, sb.ToString()));
            sb.Clear();
        }
        foreach (string line in File.ReadAllLines(Path.Combine(CasesDir, file + ".sql")))
        {
            if (line.StartsWith("--#", StringComparison.Ordinal))
            {
                Flush();
                name = line[3..].Trim();
            }
            else sb.AppendLine(line);
        }
        Flush();
        var dup = cases.GroupBy(c => c.Item1).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"Duplicate case name '{dup.Key}' in {file}.sql");
        return cases;
    }

    // ------------------------------------------------------------ execution

    static string Ser(object v) => v switch
    {
        bool b => b ? "True" : "False",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        byte[] b => "0x" + Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    static Dictionary<string, CaseResult> RunAll(DbConnection conn, List<(string Name, string Sql)> cases, Func<Exception, (int, string)?> errorOf)
    {
        var results = new Dictionary<string, CaseResult>();
        foreach (var (name, sql) in cases)
        {
            var cr = new CaseResult { Name = name };
            try
            {
                foreach (string batch in GoLine().Split(sql))
                {
                    if (batch.Trim().Length == 0) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    using var reader = cmd.ExecuteReader();
                    do
                    {
                        if (reader.FieldCount == 0) continue;
                        var shape = new ResultShape();
                        for (int i = 0; i < reader.FieldCount; i++)
                            shape.Columns.Add(new ColumnShape { Name = reader.GetName(i), ClrType = reader.GetFieldType(i).Name, SqlType = reader.GetDataTypeName(i) });
                        while (reader.Read())
                        {
                            var row = new List<string?>(reader.FieldCount);
                            for (int i = 0; i < reader.FieldCount; i++) row.Add(reader.IsDBNull(i) ? null : Ser(reader.GetValue(i)));
                            shape.Rows.Add(row);
                        }
                        cr.Results.Add(shape);
                    } while (reader.NextResult());
                }
            }
            catch (Exception ex) when (errorOf(ex) is var (number, message))
            {
                cr.Error = number;
                cr.ErrorMessage = message;
                cr.Results.Clear();
            }
            results[name] = cr;
        }
        return results;
    }

    /// <summary>Runs one script on a connection and captures its result sets or its error.</summary>
    public static CaseResult RunOne(DbConnection conn, string sql, Func<Exception, (int, string)?> errorOf)
        => RunAll(conn, [("q", sql)], errorOf)["q"];

    /// <summary>Runs an action against a scratch database on the reference SQL Server, dropping it afterwards.</summary>
    public static T WithSqlServerDatabase<T>(Func<SqlConnection, T> action)
    {
        string dbName = "LocalDbSharpConf_" + Guid.NewGuid().ToString("N");
        using (var admin = new SqlConnection(OracleConnectionString))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{dbName}]";
            create.ExecuteNonQuery();
        }
        try
        {
            using var conn = new SqlConnection(new SqlConnectionStringBuilder(OracleConnectionString) { InitialCatalog = dbName }.ConnectionString);
            conn.Open();
            return action(conn);
        }
        finally
        {
            using var admin = new SqlConnection(OracleConnectionString);
            admin.Open();
            using var drop = admin.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]";
            drop.ExecuteNonQuery();
        }
    }

    static Dictionary<string, CaseResult> RunOnSqlServer(string file)
    {
        string dbName = "LocalDbSharpConf_" + Guid.NewGuid().ToString("N");
        using (var admin = new SqlConnection(OracleConnectionString))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{dbName}]";
            create.ExecuteNonQuery();
        }
        try
        {
            using var conn = new SqlConnection(new SqlConnectionStringBuilder(OracleConnectionString) { InitialCatalog = dbName }.ConnectionString);
            conn.Open();
            return RunAll(conn, ReadCases(file), ex => ex is SqlException se ? (se.Number, se.Message) : null);
        }
        finally
        {
            using var admin = new SqlConnection(OracleConnectionString);
            admin.Open();
            using var drop = admin.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]";
            drop.ExecuteNonQuery();
        }
    }

    static Dictionary<string, CaseResult> RunOnLocalDbSharp(string file)
    {
        using var conn = new LocalDbSharpConnection(new LocalDbEngine());
        conn.Open();
        return RunAll(conn, ReadCases(file), ex => ex is TSqlException te ? (te.Number, te.Message) : null);
    }

    sealed class Golden
    {
        public string SourceHash { get; set; } = "";
        public List<CaseResult> Cases { get; set; } = [];
    }

    static string HashOf(string file)
    {
        string text = File.ReadAllText(Path.Combine(CasesDir, file + ".sql")).Replace("\r\n", "\n");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public static Dictionary<string, CaseResult> GetExpected(string file) => Expected.GetOrAdd(file, f => new(() =>
    {
        string path = Path.Combine(ExpectedDir, f + ".json");
        string hash = HashOf(f);
        if (!ForceRecord && File.Exists(path))
        {
            var golden = JsonSerializer.Deserialize<Golden>(File.ReadAllText(path))!;
            if (golden.SourceHash == hash) return golden.Cases.ToDictionary(c => c.Name);
        }
        // missing or stale recording: ask the real SQL Server
        Dictionary<string, CaseResult> recorded;
        try
        {
            recorded = RunOnSqlServer(f);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Expected output for '{f}.sql' is missing or out of date and no SQL Server is reachable to record it ({ex.Message}).", ex);
        }
        Directory.CreateDirectory(ExpectedDir);
        var fresh = new Golden { SourceHash = hash, Cases = ReadCases(f).Select(c => recorded[c.Name]).ToList() };
        File.WriteAllText(path, JsonSerializer.Serialize(fresh, Json));
        return recorded;
    })).Value;

    public static Dictionary<string, CaseResult> GetActual(string file)
        => Actual.GetOrAdd(file, f => new(() => RunOnLocalDbSharp(f))).Value;

    // ------------------------------------------------------------ comparison

    static string TypeFamily(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" or "sysname" => "string",
        "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
        "varbinary" or "binary" or "image" or "timestamp" => "binary",
        var t => t,
    };

    static bool ValuesEqual(string? e, string? a, string clrType)
    {
        if (e == null || a == null) return e == a;
        if (e == a) return true;
        if (clrType is "Double" or "Single"
            && double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
        return false;
    }

    /// <summary>Returns null when the results agree, otherwise a description of the first difference.</summary>
    public static string? Diff(CaseResult expected, CaseResult actual)
    {
        if (expected.Error != actual.Error)
            return $"error: expected {Describe(expected)}, actual {Describe(actual)}";
        if (expected.Error != null) return null;
        if (expected.Results.Count != actual.Results.Count)
            return $"result set count: expected {expected.Results.Count}, actual {actual.Results.Count}";
        for (int r = 0; r < expected.Results.Count; r++)
        {
            var e = expected.Results[r];
            var a = actual.Results[r];
            if (e.Columns.Count != a.Columns.Count) return $"set {r}: column count expected {e.Columns.Count}, actual {a.Columns.Count}";
            for (int c = 0; c < e.Columns.Count; c++)
            {
                if (e.Columns[c].Name != a.Columns[c].Name) return $"set {r} col {c}: name expected '{e.Columns[c].Name}', actual '{a.Columns[c].Name}'";
                if (e.Columns[c].ClrType != a.Columns[c].ClrType || TypeFamily(e.Columns[c].SqlType) != TypeFamily(a.Columns[c].SqlType))
                    return $"set {r} col {c} ({e.Columns[c].Name}): type expected {e.Columns[c].SqlType}/{e.Columns[c].ClrType}, actual {a.Columns[c].SqlType}/{a.Columns[c].ClrType}";
            }
            if (e.Rows.Count != a.Rows.Count) return $"set {r}: row count expected {e.Rows.Count}, actual {a.Rows.Count}\n{Dump(e)}\n--- actual\n{Dump(a)}";
            for (int i = 0; i < e.Rows.Count; i++)
                for (int c = 0; c < e.Columns.Count; c++)
                    if (!ValuesEqual(e.Rows[i][c], a.Rows[i][c], e.Columns[c].ClrType))
                        return $"set {r} row {i} col {c} ({e.Columns[c].Name}): expected {Show(e.Rows[i][c])}, actual {Show(a.Rows[i][c])}\n{Dump(e)}\n--- actual\n{Dump(a)}";
        }
        return null;
    }

    static string Describe(CaseResult c) => c.Error == null ? "success" : $"error {c.Error} ({c.ErrorMessage})";
    static string Show(string? v) => v == null ? "NULL" : "'" + v + "'";

    static string Dump(ResultShape s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(" | ", s.Columns.Select(c => c.Name)));
        foreach (var row in s.Rows.Take(30)) sb.AppendLine(string.Join(" | ", row.Select(v => v ?? "NULL")));
        return sb.ToString();
    }
}
