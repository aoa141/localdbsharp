using System.Data;
using LocalDbSharp.Execution;
using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

// User-defined types, table-valued parameters, rowversion columns and a few system procedures.
public sealed partial class Session
{
    readonly Dictionary<string, object?> _sessionContext = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------ CREATE TYPE

    UserType? FindUserType(TableName name)
    {
        if (name.Database != null && !Engine.Databases.ContainsKey(name.Database)) return null;
        return ResolveDatabase(name.Database).Types.GetValueOrDefault((name.Schema ?? "dbo") + "." + name.Name);
    }

    UserType ResolveUserType(TableName name)
        => FindUserType(name) ?? throw Err.New(2715, $"Column, parameter, or variable #1: Cannot find data type {name.Name}.");

    void ExecCreateType(CreateTypeStmt s)
    {
        var db = ResolveDatabase(s.Name.Database);
        string schema = s.Name.Schema ?? "dbo";
        if (!db.HasSchema(schema))
            throw Err.New(2760, $"The specified schema name \"{schema}\" either does not exist or you do not have permission to use it.");
        string key = schema + "." + s.Name.Name;
        if (db.Types.ContainsKey(key) || SqlType.FromName(s.Name.Name) != null)
            throw Err.New(219, $"The type '{s.Name.Name}' already exists, or you do not have permission to create it.");
        if (s.TableDef != null) BuildTable(s.TableDef, schema, s.Name.Name, db); // validates the definition
        db.Types[key] = new UserType
        {
            Schema = schema, Name = s.Name.Name, Id = Engine.NewObjectId(), Database = db,
            Base = s.BaseType, Nullable = s.Nullable, TableDef = s.TableDef,
        };
    }

    void DropUserType(TableName name, bool ifExists)
    {
        var type = FindUserType(name);
        if (type == null)
        {
            if (ifExists) return;
            throw Err.New(218, $"Could not find the type '{name.Name}'. Either it does not exist or you do not have the necessary permission.");
        }
        type.Database!.Types.Remove(type.Schema + "." + type.Name);
    }

    Table NewTableOfType(UserType type, string name)
    {
        var table = BuildTable(type.TableDef!, "dbo", name, type.Database ?? Db);
        table.IsVariable = true;
        return table;
    }

    // ------------------------------------------------------------ parameter binding

    /// <summary>Evaluates a routine argument; a table variable is passed as the table itself.</summary>
    object? EvalArgument(Expr e, EvalContext? ctx)
    {
        if (e is VarRef v && !v.Name.StartsWith("@@", StringComparison.Ordinal) && _frames.Count > 0
            && Frame.Vars.TryGetValue(v.Name, out var variable) && variable.Table != null)
            return variable.Table;
        return Eval(e, ctx);
    }

    /// <summary>Stores an argument value in the frame of a procedure or function, converting it to the parameter type.</summary>
    void BindParameter(Frame frame, ParamDef p, object? value)
    {
        if (p.UserType != null)
        {
            var type = ResolveUserType(p.UserType);
            if (type.TableDef != null)
            {
                // table-valued parameter: read-only, so the caller's table is shared rather than copied
                frame.Vars[p.Name] = value switch
                {
                    Table t => new Variable { Table = t },
                    null or DefaultMarker => new Variable { Table = NewTableOfType(type, p.Name) },
                    _ => throw Err.New(206, $"Operand type clash: {TypeSystem.TypeOf(value).Name} is incompatible with {type.Name}"),
                };
                return;
            }
            if (value is Table) throw Err.New(206, $"Operand type clash: table type is incompatible with {type.Name}");
            frame.Vars[p.Name] = new Variable { Type = type.Base, Value = TypeSystem.ConvertTo(value, type.Base!) };
            return;
        }
        if (value is Table) throw Err.New(206, $"Operand type clash: table type is incompatible with {p.Type.Name}");
        frame.Vars[p.Name] = new Variable { Type = p.Type, Value = TypeSystem.ConvertTo(value, p.Type) };
    }

    bool IsTableParameter(ParamDef p) => p.UserType != null && FindUserType(p.UserType)?.TableDef != null;

    static SqlType ClrColumnType(DataColumn column, DataTable data)
    {
        var t = Nullable.GetUnderlyingType(column.DataType) ?? column.DataType;
        if (t == typeof(int)) return SqlType.Int;
        if (t == typeof(long)) return SqlType.BigInt;
        if (t == typeof(short)) return SqlType.SmallInt;
        if (t == typeof(byte)) return SqlType.TinyInt;
        if (t == typeof(bool)) return SqlType.Bit;
        if (t == typeof(double)) return SqlType.Float;
        if (t == typeof(float)) return SqlType.Real;
        if (t == typeof(DateTime)) return SqlType.DateTime2;
        if (t == typeof(DateTimeOffset)) return SqlType.DateTimeOffset;
        if (t == typeof(TimeSpan)) return SqlType.Time;
        if (t == typeof(Guid)) return SqlType.UniqueIdentifier;
        if (t == typeof(byte[])) return SqlType.VarBinaryMax;
        if (t == typeof(decimal))
        {
            int scale = 0;
            foreach (DataRow row in data.Rows)
                if (row[column] is decimal d) scale = Math.Max(scale, d.Scale);
            return SqlType.Decimal(38, scale);
        }
        return SqlType.NVarCharMax;
    }

    /// <summary>Turns a DataTable passed as a parameter value into a table variable (columns are matched by position).</summary>
    Table TableFromDataTable(DataTable data, string? typeName, string name)
    {
        Table table;
        if (!string.IsNullOrEmpty(typeName))
        {
            var parts = SplitName(typeName);
            var type = ResolveUserType(new TableName { Name = parts[^1], Schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null });
            if (type.TableDef == null) throw Err.New(206, $"Operand type clash: {type.Name} is not a table type");
            table = NewTableOfType(type, name);
        }
        else
        {
            var def = new CreateTableStmt { Name = new TableName { Name = name } };
            foreach (DataColumn c in data.Columns) def.Columns.Add(new ColumnDef { Name = c.ColumnName, Type = ClrColumnType(c, data), Nullable = true });
            table = BuildTable(def, "dbo", name, Db);
            table.IsVariable = true;
        }
        var map = InsertMap(table, null);
        if (map.Length != data.Columns.Count)
            throw Err.New(213, "Column name or number of supplied values does not match table definition.");
        var rows = new List<object?[]>(data.Rows.Count);
        foreach (DataRow row in data.Rows)
        {
            var values = new object?[map.Length];
            for (int i = 0; i < values.Length; i++) values[i] = FromClr(row[i]);
            rows.Add(values);
        }
        InsertRows(table, map, rows);
        return table;
    }

    // ------------------------------------------------------------ rowversion

    static byte[] RowVersionBytes(long value)
    {
        var bytes = new byte[8];
        for (int i = 0; i < 8; i++) bytes[i] = (byte)(value >> (56 - 8 * i));
        return bytes;
    }

    byte[] NextRowVersion(Table table)
    {
        var db = table.Database ?? Db;
        return RowVersionBytes(++db.RowVersion);
    }

    // ------------------------------------------------------------ system procedures

    static readonly HashSet<string> IgnoredProcedures = new(StringComparer.OrdinalIgnoreCase)
    {
        // maintenance and documentation procedures that have nothing to do in an in-memory engine
        "sp_addextendedproperty", "sp_updateextendedproperty", "sp_dropextendedproperty", "sp_recompile", "sp_refreshview",
        "sp_refreshsqlmodule", "sp_updatestats", "sp_configure", "sp_dboption", "sp_tableoption", "sp_indexoption",
        "sp_settriggerorder", "sp_bindefault", "sp_addrolemember", "sp_droprolemember", "sp_changedbowner",
    };

    /// <summary>Named or positional argument of a system procedure call.</summary>
    object? SystemArg(ExecStmt s, int position, string name)
    {
        foreach (var a in s.Args)
            if (a.Name != null && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return EvalExecArg(a);
        return position < s.Args.Count && s.Args[position].Name == null ? EvalExecArg(s.Args[position]) : null;
    }

    /// <summary>Handles a call to a built-in system procedure. Returns false if the name is not one.</summary>
    bool TryExecSystemProcedure(ExecStmt s)
    {
        string name = s.Proc!.Name;
        if (IgnoredProcedures.Contains(name)) return true;
        switch (name.ToLowerInvariant())
        {
            case "sp_executesql":
                ExecSpExecuteSql(s);
                return true;
            case "sp_rename":
                ExecSpRename(TypeSystem.ToStr(SystemArg(s, 0, "@objname") ?? ""), TypeSystem.ToStr(SystemArg(s, 1, "@newname") ?? ""),
                    SystemArg(s, 2, "@objtype") is { } t ? TypeSystem.ToStr(t) : null);
                return true;
            case "sp_set_session_context":
            {
                string key = TypeSystem.ToStr(SystemArg(s, 0, "@key") ?? throw Err.New(15600, "An invalid parameter or option was specified for procedure 'sp_set_session_context'."));
                _sessionContext[key] = SystemArg(s, 1, "@value");
                return true;
            }
            default:
                return false;
        }
    }

    internal object? SessionContext(string key) => _sessionContext.GetValueOrDefault(key);

    void ExecSpRename(string objName, string newName, string? objType)
    {
        var parts = SplitName(objName);
        var notFound = Err.New(15248, "Either the parameter @objname is ambiguous or the claimed @objtype (" + (objType ?? "OBJECT") + ") is wrong.", 11);
        if (newName.Length == 0) throw Err.New(15253, "Syntax error parsing SQL identifier ''.", 11);
        switch ((objType ?? "OBJECT").ToUpperInvariant())
        {
            case "COLUMN":
            {
                if (parts.Count < 2) throw notFound;
                var table = FindObject(new TableName { Name = parts[^2], Schema = parts.Count >= 3 && parts[^3].Length > 0 ? parts[^3] : null }) as Table ?? throw notFound;
                int ci = table.ColumnIndex(parts[^1]);
                if (ci < 0) throw notFound;
                if (table.ColumnIndex(newName) >= 0)
                    throw Err.New(15335, $"Error: The new name '{newName}' is already in use as a COLUMN name and would cause a duplicate that is not permitted.", 11);
                var old = table.Columns[ci];
                table.Columns[ci] = new Column
                {
                    Name = newName, Type = old.Type, Nullable = old.Nullable, Default = old.Default, DefaultName = old.DefaultName,
                    Identity = old.Identity, RowVersion = old.RowVersion, Computed = old.Computed,
                };
                table.SchemaChanged();
                break;
            }
            case "INDEX":
            {
                if (parts.Count < 2) throw notFound;
                var table = FindObject(new TableName { Name = parts[^2], Schema = parts.Count >= 3 && parts[^3].Length > 0 ? parts[^3] : null }) as Table ?? throw notFound;
                var index = table.Unique.Find(u => u.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
                if (index != null)
                {
                    int at = table.Unique.IndexOf(index);
                    var renamed = new UniqueIndex { Name = newName, Cols = index.Cols, IsPrimary = index.IsPrimary, IsConstraint = index.IsConstraint };
                    foreach (var kv in index.Map) renamed.Map[kv.Key] = kv.Value;
                    table.Unique[at] = renamed;
                }
                else if (table.IndexNames.Remove(parts[^1])) table.IndexNames.Add(newName);
                else throw notFound;
                if (parts[^1].Equals(table.ClusterName, StringComparison.OrdinalIgnoreCase)) table.ClusterName = newName;
                break;
            }
            default:
            {
                var obj = FindObject(new TableName { Name = parts[^1], Schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null })
                          ?? throw Err.New(15225, $"No item by the name of '{objName}' could be found in the current database '{Db.Name}', given that @itemtype was input as '{objType ?? "(null)"}'.", 11);
                if (obj is Table { IsTemp: true }) throw Err.NotSupported("Renaming a temporary table");
                var db = obj.Database!;
                if (db.Find(obj.Schema, newName) != null)
                    throw Err.New(15335, $"Error: The new name '{newName}' is already in use as a object name and would cause a duplicate that is not permitted.", 11);
                db.Remove(obj);
                obj.Name = newName;
                db.Add(obj);
                break;
            }
        }
        Message("Caution: Changing any part of an object name could break scripts and stored procedures.");
    }
}
