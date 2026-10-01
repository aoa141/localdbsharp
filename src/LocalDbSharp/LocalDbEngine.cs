using System.Collections.Concurrent;
using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

public sealed class ResultColumn(string name, SqlType type)
{
    public string Name { get; } = name;
    public SqlType Type { get; } = type;
}

/// <summary>A fully materialised result set.</summary>
public sealed class ResultSet(IReadOnlyList<ResultColumn> columns, List<object?[]> rows)
{
    public IReadOnlyList<ResultColumn> Columns { get; } = columns;
    /// <summary>Rows of CLR values; SQL NULL is represented by null.</summary>
    public List<object?[]> Rows { get; } = rows;
}

public sealed class ExecutionResult
{
    public List<ResultSet> ResultSets { get; } = [];
    /// <summary>Total rows affected by INSERT / UPDATE / DELETE / MERGE statements, or -1 if there were none.</summary>
    public int RecordsAffected { get; internal set; } = -1;
    /// <summary>Output of PRINT and low-severity RAISERROR.</summary>
    public List<string> Messages { get; } = [];
}

/// <summary>A parameter passed to <see cref="Session.Execute(string, IList{SessionParameter})"/>.</summary>
public sealed class SessionParameter(string name, object? value)
{
    public string Name { get; set; } = name;
    public object? Value { get; set; } = value;
    public SqlType? Type { get; set; }
    public bool IsOutput { get; set; }
    /// <summary>For a table-valued parameter (Value is a DataTable): the name of the table type, if any.</summary>
    public string? TypeName { get; set; }
}

/// <summary>
/// An in-memory T-SQL database engine instance. Holds databases (master, tempdb and any created with
/// CREATE DATABASE); nothing is persisted. Sessions created from the same engine share data.
/// </summary>
public sealed class LocalDbEngine
{
    static readonly ConcurrentDictionary<string, LocalDbEngine> Named = new(StringComparer.OrdinalIgnoreCase);

    internal readonly Dictionary<string, Database> Databases = new(StringComparer.OrdinalIgnoreCase);
    internal readonly object Sync = new();
    readonly Dictionary<string, List<Batch>> _parseCache = [];
    int _nextObjectId = 245575913;
    int _nextDbId;
    int _nextSpid = 50;

    public LocalDbEngine()
    {
        AddDatabase("master");
        AddDatabase("tempdb");
    }

    /// <summary>Returns the process-wide engine registered under the given name, creating it on first use.</summary>
    public static LocalDbEngine GetNamed(string name) => Named.GetOrAdd(name, _ => new LocalDbEngine());

    /// <summary>Removes a named engine so that its data can be garbage collected.</summary>
    public static bool DropNamed(string name) => Named.TryRemove(name, out _);

    public Session CreateSession(string database = "master")
    {
        lock (Sync)
        {
            if (!Databases.TryGetValue(database, out var db)) db = AddDatabase(database);
            return new Session(this, db, _nextSpid++);
        }
    }

    /// <summary>Convenience: runs a script on a temporary session.</summary>
    public ExecutionResult Execute(string sql)
    {
        using var s = CreateSession();
        return s.Execute(sql);
    }

    internal Database AddDatabase(string name)
    {
        var db = new Database(name, ++_nextDbId);
        Databases[name] = db;
        return db;
    }

    internal int NewObjectId() => _nextObjectId += 36000037 % 1000 + 1;

    internal List<Batch> Parse(string sql)
    {
        if (_parseCache.TryGetValue(sql, out var cached)) return cached;
        var parsed = Parser.ParseScript(sql);
        if (_parseCache.Count >= 2000) _parseCache.Clear();
        if (sql.Length <= 100_000) _parseCache[sql] = parsed;
        return parsed;
    }
}
