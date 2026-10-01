using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace LocalDbSharp.Data;

/// <summary>Forward-only reader over the (already materialised) result sets of a command.</summary>
public sealed class LocalDbSharpDataReader : DbDataReader
{
    readonly ExecutionResult _result;
    readonly LocalDbSharpConnection? _closeConnection;
    int _set, _row = -1;
    bool _closed;

    internal LocalDbSharpDataReader(ExecutionResult result, LocalDbSharpConnection? closeConnection)
    {
        _result = result;
        _closeConnection = closeConnection;
    }

    ResultSet? Current => _set < _result.ResultSets.Count ? _result.ResultSets[_set] : null;

    object?[] Row
    {
        get
        {
            var rs = Current;
            if (rs == null || _row < 0 || _row >= rs.Rows.Count) throw new InvalidOperationException("Invalid attempt to read when no data is present.");
            return rs.Rows[_row];
        }
    }

    public override int Depth => 0;
    public override int FieldCount => Current?.Columns.Count ?? 0;
    public override bool HasRows => Current is { Rows.Count: > 0 };
    public override bool IsClosed => _closed;
    public override int RecordsAffected => _result.RecordsAffected;

    /// <summary>Messages produced by PRINT while the command ran.</summary>
    public IReadOnlyList<string> Messages => _result.Messages;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        var rs = Current;
        if (rs == null || _row + 1 >= rs.Rows.Count)
        {
            if (rs != null) _row = rs.Rows.Count;
            return false;
        }
        _row++;
        return true;
    }

    public override bool NextResult()
    {
        if (_set >= _result.ResultSets.Count) return false;
        _set++;
        _row = -1;
        return _set < _result.ResultSets.Count;
    }

    public override void Close()
    {
        if (_closed) return;
        _closed = true;
        _closeConnection?.Close();
    }

    ResultColumn Column(int ordinal)
    {
        var rs = Current ?? throw new InvalidOperationException("No result set is available.");
        if (ordinal < 0 || ordinal >= rs.Columns.Count) throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        return rs.Columns[ordinal];
    }

    public override string GetName(int ordinal) => Column(ordinal).Name;
    public override string GetDataTypeName(int ordinal) => Column(ordinal).Type.Name;
    public override Type GetFieldType(int ordinal) => Column(ordinal).Type.ClrType;

    public override int GetOrdinal(string name)
    {
        var rs = Current ?? throw new InvalidOperationException("No result set is available.");
        for (int i = 0; i < rs.Columns.Count; i++)
            if (rs.Columns[i].Name.Equals(name, StringComparison.Ordinal)) return i;
        for (int i = 0; i < rs.Columns.Count; i++)
            if (rs.Columns[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new IndexOutOfRangeException(name);
    }

    public override object GetValue(int ordinal)
    {
        Column(ordinal);
        return Row[ordinal] ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override bool IsDBNull(int ordinal)
    {
        Column(ordinal);
        return Row[ordinal] == null;
    }

    T Get<T>(int ordinal)
    {
        object v = GetValue(ordinal);
        if (v is T t) return t;
        if (v is DBNull) throw new InvalidCastException($"Column '{GetName(ordinal)}' is NULL.");
        return (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
    }

    public override bool GetBoolean(int ordinal) => Get<bool>(ordinal);
    public override byte GetByte(int ordinal) => Get<byte>(ordinal);
    public override char GetChar(int ordinal) => Get<string>(ordinal)[0];
    public override DateTime GetDateTime(int ordinal) => Get<DateTime>(ordinal);
    public override decimal GetDecimal(int ordinal) => Get<decimal>(ordinal);
    public override double GetDouble(int ordinal) => Get<double>(ordinal);
    public override float GetFloat(int ordinal) => Get<float>(ordinal);
    public override Guid GetGuid(int ordinal) => GetValue(ordinal) is Guid g ? g : Guid.Parse(Get<string>(ordinal));
    public override short GetInt16(int ordinal) => Get<short>(ordinal);
    public override int GetInt32(int ordinal) => Get<int>(ordinal);
    public override long GetInt64(int ordinal) => Get<long>(ordinal);
    public override string GetString(int ordinal) => Get<string>(ordinal);

    public override T GetFieldValue<T>(int ordinal)
    {
        object v = GetValue(ordinal);
        if (v is T t) return t;
        if (v is DBNull)
        {
            if (default(T) == null) return default!;
            throw new InvalidCastException($"Column '{GetName(ordinal)}' is NULL.");
        }
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(DateOnly) && v is DateTime d1) return (T)(object)DateOnly.FromDateTime(d1);
        if (target == typeof(TimeOnly) && v is TimeSpan ts) return (T)(object)TimeOnly.FromTimeSpan(ts);
        return (T)Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var data = (byte[])GetValue(ordinal);
        if (buffer == null) return data.Length;
        int n = (int)Math.Max(0, Math.Min(length, data.Length - dataOffset));
        Array.Copy(data, dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        string data = GetString(ordinal);
        if (buffer == null) return data.Length;
        int n = (int)Math.Max(0, Math.Min(length, data.Length - dataOffset));
        data.CopyTo((int)dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override DataTable? GetSchemaTable()
    {
        var rs = Current;
        if (rs == null) return null;
        var t = new DataTable("SchemaTable");
        t.Columns.Add("ColumnName", typeof(string));
        t.Columns.Add("ColumnOrdinal", typeof(int));
        t.Columns.Add("ColumnSize", typeof(int));
        t.Columns.Add("NumericPrecision", typeof(short));
        t.Columns.Add("NumericScale", typeof(short));
        t.Columns.Add("DataType", typeof(Type));
        t.Columns.Add("DataTypeName", typeof(string));
        t.Columns.Add("AllowDBNull", typeof(bool));
        t.Columns.Add("IsUnique", typeof(bool));
        t.Columns.Add("IsKey", typeof(bool));
        t.Columns.Add("IsReadOnly", typeof(bool));
        t.Columns.Add("IsLong", typeof(bool));
        for (int i = 0; i < rs.Columns.Count; i++)
        {
            var c = rs.Columns[i];
            bool isLong = (c.Type.IsString || c.Type.IsBinary) && c.Type.Length == SqlType.Max;
            t.Rows.Add(c.Name, i, isLong ? int.MaxValue : c.Type.Length, (short)c.Type.Precision, (short)c.Type.Scale,
                c.Type.ClrType, c.Type.Name, true, false, false, false, isLong);
        }
        return t;
    }
}
