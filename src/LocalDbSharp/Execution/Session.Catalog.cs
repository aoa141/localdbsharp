using LocalDbSharp.Execution;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

// Additional catalog views and metadata functions. Constraints have no object of their own in the storage
// model, so their object ids are derived from the owning table's id (object ids are spaced far enough apart).
public sealed partial class Session
{
    /// <summary>SQL Server's system_type_id / user_type_id for a data type.</summary>
    static int SystemTypeId(SqlType t) => t.Kind switch
    {
        SqlTypeKind.Bit => 104, SqlTypeKind.TinyInt => 48, SqlTypeKind.SmallInt => 52, SqlTypeKind.Int => 56, SqlTypeKind.BigInt => 127,
        SqlTypeKind.Decimal => 106, SqlTypeKind.Money => 60, SqlTypeKind.SmallMoney => 122, SqlTypeKind.Real => 59, SqlTypeKind.Float => 62,
        SqlTypeKind.Char => 175, SqlTypeKind.VarChar => 167, SqlTypeKind.NChar => 239, SqlTypeKind.NVarChar => 231,
        SqlTypeKind.Binary => 173, SqlTypeKind.VarBinary => 165, SqlTypeKind.Date => 40, SqlTypeKind.Time => 41, SqlTypeKind.DateTime => 61,
        SqlTypeKind.SmallDateTime => 58, SqlTypeKind.DateTime2 => 42, SqlTypeKind.DateTimeOffset => 43, SqlTypeKind.UniqueIdentifier => 36,
        _ => 98,
    };

    static readonly SqlType[] BuiltinTypes =
    [
        SqlType.Bit, SqlType.TinyInt, SqlType.SmallInt, SqlType.Int, SqlType.BigInt, SqlType.Decimal(38, 38), SqlType.Money, SqlType.SmallMoney,
        SqlType.Real, SqlType.Float, new(SqlTypeKind.Char, 8000), new(SqlTypeKind.VarChar, 8000), new(SqlTypeKind.NChar, 4000), new(SqlTypeKind.NVarChar, 4000),
        new(SqlTypeKind.Binary, 8000), new(SqlTypeKind.VarBinary, 8000), SqlType.Date, SqlType.Time, SqlType.DateTime, SqlType.SmallDateTime,
        SqlType.DateTime2, SqlType.DateTimeOffset, SqlType.UniqueIdentifier, SqlType.Variant,
    ];

    static int KeyId(Table t, UniqueIndex u) => t.Id + 1 + t.Unique.IndexOf(u);
    static int ForeignKeyId(Table t, ForeignKey f) => t.Id + 1 + t.Unique.Count + t.ForeignKeys.IndexOf(f);
    static int CheckId(Table t, CheckConstraint c) => t.Id + 1 + t.Unique.Count + t.ForeignKeys.Count + t.Checks.IndexOf(c);
    static int DefaultId(Table t, int column) => t.Id + 1 + t.Unique.Count + t.ForeignKeys.Count + t.Checks.Count + column;

    RowSet? MoreSystemViews(string key, Database db, List<DbObject> objects)
    {
        var sysname = SqlType.NVarChar(128);
        var rows = new List<object?[]>();
        var tables = objects.OfType<Table>().ToList();
        switch (key)
        {
            case "INFORMATION_SCHEMA.VIEWS":
                foreach (var v in objects.OfType<View>()) rows.Add([db.Name, v.Schema, v.Name]);
                return MakeSet(["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME"], [sysname, sysname, sysname], rows);

            case "INFORMATION_SCHEMA.TABLE_CONSTRAINTS":
                foreach (var t in tables)
                {
                    foreach (var u in t.Unique.Where(u => u.IsConstraint)) rows.Add([db.Name, t.Schema, u.Name, db.Name, t.Schema, t.Name, u.IsPrimary ? "PRIMARY KEY" : "UNIQUE"]);
                    foreach (var f in t.ForeignKeys) rows.Add([db.Name, t.Schema, f.Name, db.Name, t.Schema, t.Name, "FOREIGN KEY"]);
                    foreach (var c in t.Checks) rows.Add([db.Name, t.Schema, c.Name, db.Name, t.Schema, t.Name, "CHECK"]);
                }
                return MakeSet(["CONSTRAINT_CATALOG", "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "CONSTRAINT_TYPE"],
                    [sysname, sysname, sysname, sysname, sysname, sysname, SqlType.VarChar(11)], rows);

            case "INFORMATION_SCHEMA.KEY_COLUMN_USAGE":
            case "INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE":
                foreach (var t in tables)
                {
                    foreach (var u in t.Unique.Where(u => u.IsConstraint))
                        for (int k = 0; k < u.Cols.Length; k++) rows.Add([db.Name, t.Schema, u.Name, db.Name, t.Schema, t.Name, t.Columns[u.Cols[k]].Name, k + 1]);
                    foreach (var f in t.ForeignKeys)
                        for (int k = 0; k < f.Cols.Length; k++) rows.Add([db.Name, t.Schema, f.Name, db.Name, t.Schema, t.Name, t.Columns[f.Cols[k]].Name, k + 1]);
                }
                return MakeSet(["CONSTRAINT_CATALOG", "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION"],
                    [sysname, sysname, sysname, sysname, sysname, sysname, sysname, SqlType.Int], rows);

            case "INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS":
                foreach (var t in tables)
                {
                    foreach (var f in t.ForeignKeys)
                    {
                        var target = f.RefTable.FindUniqueOn(f.RefCols);
                        rows.Add([db.Name, t.Schema, f.Name, db.Name, f.RefTable.Schema, target?.Name, "SIMPLE", "NO ACTION", f.Cascade ? "CASCADE" : f.SetNull ? "SET NULL" : "NO ACTION"]);
                    }
                }
                return MakeSet(["CONSTRAINT_CATALOG", "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "UNIQUE_CONSTRAINT_CATALOG", "UNIQUE_CONSTRAINT_SCHEMA", "UNIQUE_CONSTRAINT_NAME", "MATCH_OPTION", "UPDATE_RULE", "DELETE_RULE"],
                    [sysname, sysname, sysname, sysname, sysname, sysname, SqlType.VarChar(7), SqlType.VarChar(11), SqlType.VarChar(11)], rows);

            case "SYS.KEY_CONSTRAINTS":
                foreach (var t in tables)
                    foreach (var u in t.Unique.Where(u => u.IsConstraint))
                        rows.Add([u.Name, KeyId(t, u), db.SchemaId(t.Schema), t.Id, u.IsPrimary ? "PK" : "UQ", u.IsPrimary ? "PRIMARY_KEY_CONSTRAINT" : "UNIQUE_CONSTRAINT", t.Unique.IndexOf(u) + 1]);
                return MakeSet(["name", "object_id", "schema_id", "parent_object_id", "type", "type_desc", "unique_index_id"],
                    [sysname, SqlType.Int, SqlType.Int, SqlType.Int, new SqlType(SqlTypeKind.Char, 2), SqlType.NVarChar(60), SqlType.Int], rows);

            case "SYS.INDEX_COLUMNS":
                foreach (var t in tables)
                    foreach (var u in t.Unique)
                        for (int k = 0; k < u.Cols.Length; k++) rows.Add([t.Id, t.Unique.IndexOf(u) + 1, k + 1, u.Cols[k] + 1, (byte)(k + 1), false]);
                return MakeSet(["object_id", "index_id", "index_column_id", "column_id", "key_ordinal", "is_included_column"],
                    [SqlType.Int, SqlType.Int, SqlType.Int, SqlType.Int, SqlType.TinyInt, SqlType.Bit], rows);

            case "SYS.FOREIGN_KEY_COLUMNS":
                foreach (var t in tables)
                    foreach (var f in t.ForeignKeys)
                        for (int k = 0; k < f.Cols.Length; k++) rows.Add([ForeignKeyId(t, f), k + 1, t.Id, f.Cols[k] + 1, f.RefTable.Id, f.RefCols[k] + 1]);
                return MakeSet(["constraint_object_id", "constraint_column_id", "parent_object_id", "parent_column_id", "referenced_object_id", "referenced_column_id"],
                    [SqlType.Int, SqlType.Int, SqlType.Int, SqlType.Int, SqlType.Int, SqlType.Int], rows);

            case "SYS.CHECK_CONSTRAINTS":
                foreach (var t in tables)
                    foreach (var c in t.Checks) rows.Add([c.Name, CheckId(t, c), db.SchemaId(t.Schema), t.Id, "C ", false]);
                return MakeSet(["name", "object_id", "schema_id", "parent_object_id", "type", "is_disabled"],
                    [sysname, SqlType.Int, SqlType.Int, SqlType.Int, new SqlType(SqlTypeKind.Char, 2), SqlType.Bit], rows);

            case "SYS.DEFAULT_CONSTRAINTS":
                foreach (var t in tables)
                    for (int i = 0; i < t.Columns.Count; i++)
                        if (t.Columns[i].DefaultName != null) rows.Add([t.Columns[i].DefaultName, DefaultId(t, i), db.SchemaId(t.Schema), t.Id, i + 1, "D "]);
                return MakeSet(["name", "object_id", "schema_id", "parent_object_id", "parent_column_id", "type"],
                    [sysname, SqlType.Int, SqlType.Int, SqlType.Int, SqlType.Int, new SqlType(SqlTypeKind.Char, 2)], rows);

            case "SYS.IDENTITY_COLUMNS":
                foreach (var t in tables)
                {
                    int ic = t.IdentityColumn;
                    if (ic >= 0) rows.Add([t.Id, t.Columns[ic].Name, ic + 1, t.IdentitySeed, t.IdentityIncrement, t.LastIdentity]);
                }
                return MakeSet(["object_id", "name", "column_id", "seed_value", "increment_value", "last_value"],
                    [SqlType.Int, sysname, SqlType.Int, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt], rows);

            case "SYS.TRIGGERS":
                foreach (var trg in objects.OfType<Trigger>())
                    rows.Add([trg.Name, trg.Id, trg.Table.Id, "TR", "SQL_TRIGGER", !trg.Enabled, trg.Def.InsteadOf, trg.Created]);
                return MakeSet(["name", "object_id", "parent_id", "type", "type_desc", "is_disabled", "is_instead_of_trigger", "create_date"],
                    [sysname, SqlType.Int, SqlType.Int, new SqlType(SqlTypeKind.Char, 2), SqlType.NVarChar(60), SqlType.Bit, SqlType.Bit, SqlType.DateTime], rows);

            case "SYS.SEQUENCES":
                foreach (var seq in objects.OfType<Sequence>())
                    rows.Add([seq.Name, seq.Id, db.SchemaId(seq.Schema), seq.Start, seq.Increment, seq.Min, seq.Max, seq.Cycle, seq.Current ?? seq.Start]);
                return MakeSet(["name", "object_id", "schema_id", "start_value", "increment", "minimum_value", "maximum_value", "is_cycling", "current_value"],
                    [sysname, SqlType.Int, SqlType.Int, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.Bit, SqlType.BigInt], rows);

            case "SYS.TYPES":
                foreach (var t in BuiltinTypes)
                {
                    short maxLength = (short)(t.IsString || t.IsBinary ? t.Length * (t.IsUnicode ? 2 : 1) : t.Kind == SqlTypeKind.Variant ? 8016 : Functions.DataLength(DefaultOf(t), t));
                    rows.Add([t.Name, SystemTypeId(t), SystemTypeId(t), 4, maxLength, (byte)t.Precision, (byte)t.Scale, false, false]);
                }
                rows.Add(["numeric", 108, 108, 4, (short)17, (byte)38, (byte)38, false, false]);
                rows.Add(["sysname", 231, 256, 4, (short)256, (byte)0, (byte)0, false, false]);
                rows.Add(["timestamp", 189, 189, 4, (short)8, (byte)0, (byte)0, false, false]);
                foreach (var ut in db.Types.Values.OrderBy(u => u.Id))
                    rows.Add([ut.Name, ut.Base != null ? SystemTypeId(ut.Base) : 243, ut.Id, db.SchemaId(ut.Schema), (short)(ut.Base?.Length ?? -1), (byte)(ut.Base?.Precision ?? 0), (byte)(ut.Base?.Scale ?? 0), true, ut.TableDef != null]);
                return MakeSet(["name", "system_type_id", "user_type_id", "schema_id", "max_length", "precision", "scale", "is_user_defined", "is_table_type"],
                    [sysname, SqlType.TinyInt, SqlType.Int, SqlType.Int, SqlType.SmallInt, SqlType.TinyInt, SqlType.TinyInt, SqlType.Bit, SqlType.Bit], rows);

            default:
                return null;
        }
    }

    // ------------------------------------------------------------ metadata functions

    internal object? ColumnLength(string tableName, string columnName)
    {
        var parts = SplitName(tableName);
        if (FindObject(new Parsing.TableName { Name = parts[^1], Schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null }) is not Table table) return null;
        int ci = table.ColumnIndex(columnName);
        if (ci < 0) return null;
        var t = table.Columns[ci].Type;
        if (t.IsString || t.IsBinary) return (short)(t.Length == SqlType.Max ? -1 : t.Length * (t.IsUnicode ? 2 : 1));
        if (t.Kind == SqlTypeKind.Decimal) return (short)(t.Precision <= 9 ? 5 : t.Precision <= 19 ? 9 : t.Precision <= 28 ? 13 : 17);
        return (short)Functions.DataLength(DefaultOf(t), t);
    }

    internal object? ObjectProperty(int objectId, string property)
    {
        var obj = FindObjectById(objectId);
        if (obj == null) return null;
        var table = obj as Table;
        return property.ToUpperInvariant() switch
        {
            "ISTABLE" or "ISUSERTABLE" => table != null ? 1 : 0,
            "ISVIEW" => obj is View ? 1 : 0,
            "ISPROCEDURE" => obj is Procedure ? 1 : 0,
            "ISTRIGGER" => obj is Trigger ? 1 : 0,
            "ISSCALARFUNCTION" => obj is Function { Def.ReturnType: not null } ? 1 : 0,
            "ISTABLEFUNCTION" => obj is Function { Def.ReturnType: null } ? 1 : 0,
            "ISINLINEFUNCTION" => obj is Function { Def.InlineQuery: not null } ? 1 : 0,
            "TABLEHASIDENTITY" => table == null ? null : table.IdentityColumn >= 0 ? 1 : 0,
            "TABLEHASPRIMARYKEY" => table == null ? null : table.PrimaryKey != null ? 1 : 0,
            "TABLEHASFOREIGNKEY" => table == null ? null : table.ForeignKeys.Count > 0 ? 1 : 0,
            "TABLEHASCLUSTINDEX" => table == null ? null : table.ClusterCols != null ? 1 : 0,
            "ISMSSHIPPED" => 0,
            _ => null,
        };
    }
}
