using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace LocalDbSharp.Data;

public sealed class LocalDbSharpParameter : DbParameter
{
    DbType? _dbType;

    public LocalDbSharpParameter() { }

    public LocalDbSharpParameter(string name, object? value)
    {
        ParameterName = name;
        Value = value;
    }

    public override DbType DbType
    {
        get => _dbType ?? DbType.Object;
        set => _dbType = value;
    }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    [AllowNull] public override string ParameterName { get; set; } = "";
    public override int Size { get; set; }
    public override byte Precision { get; set; }
    public override byte Scale { get; set; }
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }

    /// <summary>Name of the table type when the value is a DataTable (table-valued parameter).</summary>
    public string? TypeName { get; set; }

    public override void ResetDbType() => _dbType = null;

    internal SqlType? ExplicitType
    {
        get
        {
            if (_dbType == null) return null;
            int len = Size > 0 ? Size : SqlType.Max;
            return _dbType.Value switch
            {
                DbType.Boolean => SqlType.Bit,
                DbType.Byte => SqlType.TinyInt,
                DbType.SByte or DbType.Int16 => SqlType.SmallInt,
                DbType.UInt16 or DbType.Int32 => SqlType.Int,
                DbType.UInt32 or DbType.Int64 => SqlType.BigInt,
                DbType.UInt64 => SqlType.Decimal(20, 0),
                DbType.Decimal or DbType.VarNumeric => SqlType.Decimal(Precision > 0 ? Precision : 38, Precision > 0 || Scale > 0 ? Scale : Value is decimal d ? d.Scale : 0),
                DbType.Currency => SqlType.Money,
                DbType.Single => SqlType.Real,
                DbType.Double => SqlType.Float,
                DbType.AnsiString => new SqlType(SqlTypeKind.VarChar, len),
                DbType.AnsiStringFixedLength => new SqlType(SqlTypeKind.Char, Size > 0 ? Size : 1),
                DbType.String or DbType.Xml => new SqlType(SqlTypeKind.NVarChar, len),
                DbType.StringFixedLength => new SqlType(SqlTypeKind.NChar, Size > 0 ? Size : 1),
                DbType.Binary => new SqlType(SqlTypeKind.VarBinary, len),
                DbType.Guid => SqlType.UniqueIdentifier,
                DbType.Date => SqlType.Date,
                DbType.Time => SqlType.Time,
                DbType.DateTime => SqlType.DateTime,
                DbType.DateTime2 => SqlType.DateTime2,
                DbType.DateTimeOffset => SqlType.DateTimeOffset,
                _ => null,
            };
        }
    }
}

public sealed class LocalDbSharpParameterCollection : DbParameterCollection
{
    readonly List<LocalDbSharpParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    public LocalDbSharpParameter AddWithValue(string name, object? value)
    {
        var p = new LocalDbSharpParameter(name, value);
        _items.Add(p);
        return p;
    }

    public override int Add(object value)
    {
        _items.Add((LocalDbSharpParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values) Add(v!);
    }

    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((LocalDbSharpParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((LocalDbSharpParameter)value);

    static string Bare(string name) => name.StartsWith('@') ? name[1..] : name;

    public override int IndexOf(string parameterName)
        => _items.FindIndex(p => Bare(p.ParameterName).Equals(Bare(parameterName), StringComparison.OrdinalIgnoreCase));

    public override void Insert(int index, object value) => _items.Insert(index, (LocalDbSharpParameter)value);
    public override void Remove(object value) => _items.Remove((LocalDbSharpParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        int i = IndexOf(parameterName);
        if (i >= 0) _items.RemoveAt(i);
    }

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        int i = IndexOf(parameterName);
        return i >= 0 ? _items[i] : throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
    }

    protected override void SetParameter(int index, DbParameter value) => _items[index] = (LocalDbSharpParameter)value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        int i = IndexOf(parameterName);
        if (i >= 0) _items[i] = (LocalDbSharpParameter)value;
        else _items.Add((LocalDbSharpParameter)value);
    }

    internal IEnumerable<LocalDbSharpParameter> Items => _items;
}

public sealed class LocalDbSharpCommand : DbCommand
{
    readonly LocalDbSharpParameterCollection _parameters = new();

    public LocalDbSharpCommand() { }

    public LocalDbSharpCommand(string commandText, LocalDbSharpConnection connection)
    {
        CommandText = commandText;
        Connection = connection;
    }

    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    public new LocalDbSharpConnection? Connection { get; set; }

    protected override DbConnection? DbConnection
    {
        get => Connection;
        set => Connection = (LocalDbSharpConnection?)value;
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;
    public new LocalDbSharpParameterCollection Parameters => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new LocalDbSharpParameter();

    ExecutionResult Run()
    {
        var session = (Connection ?? throw new InvalidOperationException("The command has no connection.")).Session;
        var bound = new List<(LocalDbSharpParameter Source, SessionParameter Bound)>();
        LocalDbSharpParameter? returnParam = null;
        foreach (var p in _parameters.Items)
        {
            if (p.Direction == ParameterDirection.ReturnValue)
            {
                returnParam = p;
                continue;
            }
            var sp = new SessionParameter(p.ParameterName, p.Direction == ParameterDirection.Output ? null : p.Value)
            {
                Type = p.ExplicitType,
                IsOutput = p.Direction != ParameterDirection.Input,
                TypeName = p.TypeName,
            };
            if (sp.IsOutput && sp.Type == null && sp.Value is null or DBNull) sp.Type = SqlType.Variant;
            bound.Add((p, sp));
        }

        ExecutionResult result;
        var list = bound.ConvertAll(b => b.Bound);
        switch (CommandType)
        {
            case CommandType.StoredProcedure:
                result = session.ExecuteProcedure(CommandText, list, out object? ret);
                if (returnParam != null) returnParam.Value = ret ?? DBNull.Value;
                break;
            case CommandType.Text:
                result = session.Execute(CommandText, list);
                break;
            default:
                throw new NotSupportedException("CommandType.TableDirect is not supported.");
        }
        foreach (var (source, sp) in bound)
            if (sp.IsOutput) source.Value = sp.Value ?? DBNull.Value;
        return result;
    }

    public override int ExecuteNonQuery() => Run().RecordsAffected;

    public override object? ExecuteScalar()
    {
        var r = Run();
        if (r.ResultSets.Count == 0 || r.ResultSets[0].Rows.Count == 0 || r.ResultSets[0].Columns.Count == 0) return null;
        return r.ResultSets[0].Rows[0][0] ?? DBNull.Value;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => new LocalDbSharpDataReader(Run(), (behavior & CommandBehavior.CloseConnection) != 0 ? Connection : null);

    public new LocalDbSharpDataReader ExecuteReader() => (LocalDbSharpDataReader)base.ExecuteReader();
}
