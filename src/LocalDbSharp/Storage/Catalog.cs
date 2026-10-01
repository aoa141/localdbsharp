using LocalDbSharp.Execution;
using LocalDbSharp.Parsing;

namespace LocalDbSharp.Storage;

internal abstract class DbObject
{
    public int Id;
    public string Schema = "dbo";
    public string Name = "";
    public DateTime Created = DateTime.Now;
    public Database? Database;
    public abstract string TypeCode { get; }
    public abstract string TypeDesc { get; }
    public string FullName => Schema + "." + Name;
}

internal sealed class View : DbObject
{
    public required Query Query;
    public List<string>? Columns;
    public override string TypeCode => "V";
    public override string TypeDesc => "VIEW";
}

internal sealed class Procedure : DbObject
{
    public required CreateProcStmt Def;
    public override string TypeCode => "P";
    public override string TypeDesc => "SQL_STORED_PROCEDURE";
}

internal sealed class Function : DbObject
{
    public required CreateFunctionStmt Def;
    public override string TypeCode => Def.ReturnType != null ? "FN" : Def.InlineQuery != null ? "IF" : "TF";
    public override string TypeDesc => Def.ReturnType != null ? "SQL_SCALAR_FUNCTION"
        : Def.InlineQuery != null ? "SQL_INLINE_TABLE_VALUED_FUNCTION" : "SQL_TABLE_VALUED_FUNCTION";
}

/// <summary>CREATE TYPE: an alias for a base type, or a table type (for table variables and table-valued parameters).</summary>
internal sealed class UserType : DbObject
{
    public SqlType? Base;
    public bool? Nullable;
    public CreateTableStmt? TableDef;
    public override string TypeCode => "TT";
    public override string TypeDesc => "TYPE_TABLE";
}

internal sealed class Sequence : DbObject
{
    public SqlType Type = SqlType.BigInt;
    public long Start, Increment = 1, Min, Max;
    public bool Cycle;
    public long? Current;
    public override string TypeCode => "SO";
    public override string TypeDesc => "SEQUENCE_OBJECT";
}

internal sealed class Trigger : DbObject
{
    public required CreateTriggerStmt Def;
    public required Table Table;
    public bool Enabled = true;
    public override string TypeCode => "TR";
    public override string TypeDesc => "SQL_TRIGGER";
}

internal sealed class Column
{
    public required string Name;
    public required SqlType Type;
    public bool Nullable = true;
    public Expr? Default;
    public string? DefaultName;
    public bool Identity;
    public bool RowVersion;
    public Expr? Computed;
}

internal sealed class UniqueIndex
{
    public required string Name;
    public required int[] Cols;
    public bool IsPrimary, IsConstraint;
    public readonly Dictionary<RowKey, object?[]> Map = [];

    public RowKey KeyOf(object?[] row)
    {
        var k = new object[Cols.Length];
        for (int i = 0; i < Cols.Length; i++) k[i] = TypeSystem.NormalizeKey(row[Cols[i]]);
        return new RowKey(k);
    }

    public TSqlException Violation(Table t, object?[] row)
    {
        string key = string.Join(", ", Cols.Select(c => row[c] == null ? "<NULL>" : TypeSystem.ToStr(row[c]!)));
        string obj = t.FullName;
        if (!IsConstraint)
            return Err.New(2601, $"Cannot insert duplicate key row in object '{obj}' with unique index '{Name}'. The duplicate key value is ({key}).", 14);
        string kind = IsPrimary ? "PRIMARY KEY" : "UNIQUE KEY";
        return Err.New(2627, $"Violation of {kind} constraint '{Name}'. Cannot insert duplicate key in object '{obj}'. The duplicate key value is ({key}).", 14);
    }
}

internal sealed class ForeignKey
{
    public required string Name;
    public required int[] Cols;
    public required Table RefTable;
    public required int[] RefCols;
    public bool Cascade, SetNull, UpdateCascade;
}

internal sealed class CheckConstraint
{
    public required string Name;
    public required Expr E;
}

internal sealed class Table : DbObject
{
    public readonly List<Column> Columns = [];
    public List<object?[]> Rows = [];
    public readonly List<UniqueIndex> Unique = [];
    public readonly List<ForeignKey> ForeignKeys = [];
    public readonly List<CheckConstraint> Checks = [];
    public readonly HashSet<string> IndexNames = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<Trigger> Triggers = [];

    /// <summary>
    /// Columns of the clustered index (by default the primary key). Rows are kept in this order, which is
    /// the order a plain scan returns them in - as on SQL Server, where the clustered index is the table.
    /// </summary>
    public int[]? ClusterCols;
    public bool[]? ClusterDesc;
    public string? ClusterName;
    public long IdentitySeed = 1, IdentityIncrement = 1;
    public long? LastIdentity;
    public bool IdentityInsert;
    public bool IsTemp, IsVariable;

    /// <summary>Incremented on every data modification; used to invalidate lookup hashes.</summary>
    public int Version;

    public override string TypeCode => "U";
    public override string TypeDesc => "USER_TABLE";

    Dictionary<string, int>? _colIndex;
    readonly Dictionary<string, RowSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, (int Version, Dictionary<object, List<object?[]>> Map)> _lookups = [];

    public int ColumnIndex(string name)
    {
        if (_colIndex == null)
        {
            _colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Columns.Count; i++) _colIndex[Columns[i].Name] = i;
        }
        return _colIndex.TryGetValue(name, out int ix) ? ix : -1;
    }

    public int IdentityColumn
    {
        get
        {
            for (int i = 0; i < Columns.Count; i++) if (Columns[i].Identity) return i;
            return -1;
        }
    }

    /// <summary>Must be called after any change to the column list.</summary>
    public void SchemaChanged()
    {
        _colIndex = null;
        _schemas.Clear();
        _lookups.Clear();
        Version++;
    }

    public RowSchema GetSchema(string alias, bool withRowRef)
    {
        string key = withRowRef ? "\0" + alias : alias;
        if (_schemas.TryGetValue(key, out var s)) return s;
        var cols = new List<ColInfo>(Columns.Count + 1);
        foreach (var c in Columns) cols.Add(new ColInfo(alias, c.Name, c.Type));
        if (withRowRef) cols.Add(new ColInfo(alias, RowSchema.RowRefName, null) { Hidden = true });
        if (_schemas.Count > 64) _schemas.Clear();
        return _schemas[key] = new RowSchema(cols);
    }

    public UniqueIndex? FindUniqueOn(int[] cols)
    {
        foreach (var ix in Unique)
            if (ix.Cols.AsSpan().SequenceEqual(cols)) return ix;
        return null;
    }

    public UniqueIndex? PrimaryKey => Unique.FirstOrDefault(u => u.IsPrimary);

    /// <summary>Equality lookup on a single column through a lazily built hash (rebuilt after modifications).</summary>
    public List<object?[]>? Lookup(int col, object normalizedKey)
    {
        if (!_lookups.TryGetValue(col, out var e) || e.Version != Version)
        {
            var map = new Dictionary<object, List<object?[]>>();
            foreach (var row in Rows)
            {
                if (row[col] == null) continue;
                object k = TypeSystem.NormalizeKey(row[col]);
                if (!map.TryGetValue(k, out var list)) map[k] = list = [];
                list.Add(row);
            }
            _lookups[col] = e = (Version, map);
        }
        return e.Map.GetValueOrDefault(normalizedKey);
    }

    // ------------------------------------------------------------ mutation primitives

    /// <summary>
    /// Adds a row. With 'deferOrder' the row is appended without regard to the clustered order; the caller
    /// must then call <see cref="Resort"/> once the whole batch is in (much cheaper for large inserts).
    /// </summary>
    public void Insert(object?[] row, bool deferOrder = false)
    {
        for (int i = 0; i < Unique.Count; i++)
        {
            var ix = Unique[i];
            if (!ix.Map.TryAdd(ix.KeyOf(row), row))
            {
                for (int j = 0; j < i; j++) Unique[j].Map.Remove(Unique[j].KeyOf(row));
                throw ix.Violation(this, row);
            }
        }
        if (deferOrder || ClusterCols == null || Rows.Count == 0 || CompareCluster(Rows[^1], row) <= 0) Rows.Add(row);
        else
        {
            // first position whose key is greater than the new row's key
            int lo = 0, hi = Rows.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (CompareCluster(Rows[mid], row) <= 0) lo = mid + 1;
                else hi = mid;
            }
            Rows.Insert(lo, row);
        }
        Version++;
    }

    int CompareCluster(object?[] a, object?[] b)
    {
        for (int k = 0; k < ClusterCols!.Length; k++)
        {
            int c = ClusterCols[k];
            object? x = a[c], y = b[c];
            int r = x == null ? (y == null ? 0 : -1) : y == null ? 1 : TypeSystem.CompareForSort(x, y, Columns[c].Type.IsUnicode);
            if (r != 0) return ClusterDesc != null && ClusterDesc[k] ? -r : r;
        }
        return 0;
    }

    /// <summary>Re-establishes clustered order after key values changed (stable for equal keys).</summary>
    public void Resort()
    {
        if (ClusterCols == null || Rows.Count < 2) return;
        bool sorted = true;
        for (int i = 1; i < Rows.Count && sorted; i++) sorted = CompareCluster(Rows[i - 1], Rows[i]) <= 0;
        if (sorted) return;
        var rows = Rows;
        var idx = new int[rows.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (i, j) =>
        {
            int c = CompareCluster(rows[i], rows[j]);
            return c != 0 ? c : i.CompareTo(j);
        });
        var result = new List<object?[]>(rows.Count);
        foreach (int i in idx) result.Add(rows[i]);
        Rows = result;
        Version++;
    }

    public void SetCluster(int[]? cols, string? name, bool[]? descending = null)
    {
        ClusterCols = (int[]?)cols?.Clone(); // never share the array with an index: ordinals are shifted in place
        ClusterDesc = descending;
        ClusterName = name;
        Resort();
    }

    int IndexOfRow(object?[] row)
    {
        if (Rows.Count > 0 && ReferenceEquals(Rows[^1], row)) return Rows.Count - 1;
        if (ClusterCols != null && Rows.Count > 64)
        {
            // rows are ordered by key: jump to the first row with this key, then look for the exact object
            int lo = 0, hi = Rows.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (CompareCluster(Rows[mid], row) < 0) lo = mid + 1;
                else hi = mid;
            }
            for (int i = lo; i < Rows.Count && CompareCluster(Rows[i], row) == 0; i++)
                if (ReferenceEquals(Rows[i], row)) return i;
        }
        return Rows.LastIndexOf(row);
    }

    public void UndoInsert(object?[] row, bool maintainIndexes)
    {
        int idx = IndexOfRow(row);
        if (idx < 0) return;
        Rows.RemoveAt(idx);
        if (maintainIndexes)
            foreach (var ix in Unique) ix.Map.Remove(ix.KeyOf(row));
        Version++;
    }

    /// <summary>Removes the given rows (by reference). Returns the previous row list for undo.</summary>
    public List<object?[]> DeleteRows(HashSet<object?[]> rows)
    {
        var old = Rows;
        foreach (var ix in Unique)
            foreach (var row in rows) ix.Map.Remove(ix.KeyOf(row));
        var kept = new List<object?[]>(Math.Max(old.Count - rows.Count, 0));
        foreach (var row in old)
            if (!rows.Contains(row)) kept.Add(row);
        Rows = kept;
        Version++;
        return old;
    }

    /// <summary>Applies new values to rows in place, maintaining unique indexes. Returns copies of the old values.</summary>
    public List<object?[]> UpdateRows(List<(object?[] Row, object?[] NewValues)> changes)
    {
        foreach (var ix in Unique)
        {
            var moved = new List<(object?[] Row, RowKey NewKey)>();
            foreach (var (row, nv) in changes)
            {
                var oldKey = ix.KeyOf(row);
                var newKey = ix.KeyOf(nv);
                if (oldKey.Equals(newKey)) continue;
                ix.Map.Remove(oldKey);
                moved.Add((row, newKey));
            }
            for (int i = 0; i < moved.Count; i++)
            {
                if (ix.Map.TryAdd(moved[i].NewKey, moved[i].Row)) continue;
                var bad = changes.First(c => ReferenceEquals(c.Row, moved[i].Row)).NewValues;
                RebuildIndexes(); // rows are still unmodified, so this restores a consistent state
                throw ix.Violation(this, bad);
            }
        }
        var olds = new List<object?[]>(changes.Count);
        bool keyChanged = false;
        foreach (var (row, nv) in changes)
        {
            if (ClusterCols != null && !keyChanged)
                foreach (int c in ClusterCols)
                    if (!Equals(row[c], nv[c])) { keyChanged = true; break; }
            olds.Add((object?[])row.Clone());
            Array.Copy(nv, row, row.Length);
        }
        Version++;
        if (keyChanged) Resort();
        return olds;
    }

    public void RebuildIndexes()
    {
        foreach (var ix in Unique)
        {
            ix.Map.Clear();
            foreach (var row in Rows) ix.Map[ix.KeyOf(row)] = row;
        }
        Version++;
    }

    /// <summary>Builds a unique index over existing rows, failing on duplicates.</summary>
    public void AddUniqueIndex(UniqueIndex ix)
    {
        foreach (var row in Rows)
        {
            if (!ix.Map.TryAdd(ix.KeyOf(row), row))
            {
                string key = string.Join(", ", ix.Cols.Select(c => row[c] == null ? "<NULL>" : TypeSystem.ToStr(row[c]!)));
                throw Err.New(1505, $"The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name '{FullName}' and the index name '{ix.Name}'. The duplicate key value is ({key}).");
            }
        }
        Unique.Add(ix);
    }
}

internal sealed class Database
{
    public readonly string Name;
    public readonly int Id;
    public readonly List<string> Schemas = ["dbo", "guest", "INFORMATION_SCHEMA", "sys"];
    public readonly Dictionary<string, DbObject> Objects = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>User-defined types live in their own namespace ("schema.name").</summary>
    public readonly Dictionary<string, UserType> Types = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Database-wide counter behind rowversion columns and @@DBTS.</summary>
    public long RowVersion = 2000;

    public Database(string name, int id)
    {
        Name = name;
        Id = id;
    }

    public bool HasSchema(string name) => Schemas.Contains(name, StringComparer.OrdinalIgnoreCase);
    public int SchemaId(string name) => Schemas.FindIndex(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)) + 1;

    public DbObject? Find(string? schema, string name) => Objects.GetValueOrDefault((schema ?? "dbo") + "." + name);

    public void Add(DbObject o)
    {
        o.Database = this;
        Objects[o.Schema + "." + o.Name] = o;
    }

    public void Remove(DbObject o) => Objects.Remove(o.Schema + "." + o.Name);

    public IEnumerable<Table> Tables => Objects.Values.OfType<Table>();
}
