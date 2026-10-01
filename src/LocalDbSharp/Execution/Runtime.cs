using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp.Execution;

internal sealed class ColInfo(string? table, string name, SqlType? type)
{
    public readonly string? Table = table;
    public readonly string Name = name;
    public SqlType? Type = type;
    public bool Hidden;
}

/// <summary>Column layout of a row stream, with name resolution.</summary>
internal sealed class RowSchema
{
    public const string RowRefName = "$row";
    public static readonly RowSchema Empty = new([]);

    public readonly List<ColInfo> Cols;
    Dictionary<string, int[]>? _byName;

    public RowSchema(List<ColInfo> cols) => Cols = cols;
    public int Count => Cols.Count;

    public static RowSchema Concat(RowSchema a, RowSchema b)
    {
        var cols = new List<ColInfo>(a.Count + b.Count);
        cols.AddRange(a.Cols);
        cols.AddRange(b.Cols);
        return new RowSchema(cols);
    }

    /// <summary>Returns the column index, -1 if not found, or -2 if ambiguous.</summary>
    public int TryResolve(string? table, string name)
    {
        if (_byName == null)
        {
            var tmp = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Cols.Count; i++)
            {
                if (Cols[i].Hidden) continue;
                if (!tmp.TryGetValue(Cols[i].Name, out var l)) tmp[Cols[i].Name] = l = [];
                l.Add(i);
            }
            _byName = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tmp) _byName[kv.Key] = kv.Value.ToArray();
        }
        if (!_byName.TryGetValue(name, out var idx)) return -1;
        if (table == null) return idx.Length == 1 ? idx[0] : -2;
        int found = -1;
        foreach (int i in idx)
        {
            if (!string.Equals(Cols[i].Table, table, StringComparison.OrdinalIgnoreCase)) continue;
            if (found >= 0) return -2;
            found = i;
        }
        return found;
    }

    public int HiddenIndex()
    {
        for (int i = 0; i < Cols.Count; i++) if (Cols[i].Hidden) return i;
        return -1;
    }

    public bool HasTable(string table)
    {
        foreach (var c in Cols)
            if (string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

internal sealed class RowSet(RowSchema schema, List<object?[]> rows)
{
    public readonly RowSchema Schema = schema;
    public readonly List<object?[]> Rows = rows;
    /// <summary>Lazily built membership set for IN (subquery).</summary>
    public InSet? InSet;
}

internal sealed class InSet
{
    public readonly HashSet<object> Keys = [];
    public int Mask;
    public bool HasNull;
}

/// <summary>Tracks whether a subquery referenced columns of an enclosing query.</summary>
internal sealed class Marker(Marker? parent)
{
    public readonly Marker? Parent = parent;
    public bool Correlated;
}

internal sealed class CteState
{
    public required Cte Def;
    public RowSet? Result;
    public RowSet? Working;
    public bool Evaluating, Referenced;
}

/// <summary>Lexical environment of a query: visible CTEs and the correlation marker.</summary>
internal sealed class QueryEnv
{
    public readonly QueryEnv? Parent;
    public Marker? Marker;
    public Dictionary<string, CteState>? Ctes;

    public QueryEnv(QueryEnv? parent, List<Cte>? ctes = null)
    {
        Parent = parent;
        Marker = parent?.Marker;
        if (ctes != null)
        {
            Ctes = new Dictionary<string, CteState>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in ctes) Ctes[c.Name] = new CteState { Def = c };
        }
    }

    public CteState? FindCte(string name, out QueryEnv? owner)
    {
        for (var e = this; e != null; e = e.Parent)
        {
            if (e.Ctes != null && e.Ctes.TryGetValue(name, out var s))
            {
                owner = e;
                return s;
            }
        }
        owner = null;
        return null;
    }
}

/// <summary>Row-level evaluation context. One instance is reused while scanning a row stream.</summary>
internal sealed class EvalContext(RowSchema? schema, EvalContext? outer, QueryEnv? env)
{
    public readonly RowSchema? Schema = schema;
    public readonly EvalContext? Outer = outer;
    public readonly QueryEnv? Env = env;
    public object?[]? Row;
    public List<object?[]>? Group;
    public Dictionary<Expr, object?[]>? WindowValues;
    public int ItemIndex;
    public EvalContext? AggScratch;

    // GROUPING SETS support, indexed by ItemIndex: expressions that are aggregated away in the item's
    // grouping set (they evaluate to NULL), and the canonical keys of those expressions for GROUPING().
    public List<HashSet<Expr>?>? ItemNulled;
    public List<HashSet<string>?>? ItemGroupedOut;
}

internal sealed class Variable
{
    public SqlType? Type;
    public object? Value;
    public Table? Table;
}

internal sealed class Frame
{
    public readonly Dictionary<string, Variable> Vars = new(StringComparer.OrdinalIgnoreCase);
    public int Version;
    public string? Procedure;
    public List<string>? TempTables;
    /// <summary>Temp tables of outer scopes hidden by a table of the same name created in this scope.</summary>
    public Dictionary<string, Table>? ShadowedTempTables;
    /// <summary>SCOPE_IDENTITY(): the last identity value generated by a statement of this scope.</summary>
    public decimal? ScopeIdentity;

    // set while a trigger body runs
    public Table? Inserted, Deleted, TriggerTable;
    public HashSet<int>? UpdatedColumns;
}

internal enum Flow { Next, Break, Continue, Return }

internal sealed class CursorState
{
    public required Query Query;
    public List<object?[]>? Rows;
    public int Position = -1;
}

internal sealed class UndoEntry
{
    public Table? Table;
    public object?[]? InsertedRow;
    public object?[]? UpdatedRow, OldValues;
    public List<object?[]>? OldRows;
    public Action? Custom;
}
