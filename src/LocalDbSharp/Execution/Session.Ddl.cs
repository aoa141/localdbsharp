using LocalDbSharp.Execution;
using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

public sealed partial class Session
{
    // ------------------------------------------------------------ name resolution

    Database ResolveDatabase(string? name)
    {
        if (name == null) return Db;
        return Engine.Databases.GetValueOrDefault(name)
               ?? throw Err.New(911, $"Database '{name}' does not exist. Make sure that the name is entered correctly.");
    }

    internal DbObject? FindObject(TableName name)
    {
        if (name.IsTemp) return TempTables.GetValueOrDefault(name.Name);
        if (name.IsVariable) return _frames.Count > 0 ? Frame.Vars.GetValueOrDefault(name.Name)?.Table : null;
        if (name.Database != null && !Engine.Databases.ContainsKey(name.Database)) return null;
        return ResolveDatabase(name.Database).Find(name.Schema, name.Name);
    }

    Table ResolveTable(TableName name)
    {
        if (name.IsVariable)
            return (_frames.Count > 0 ? Frame.Vars.GetValueOrDefault(name.Name)?.Table : null)
                   ?? throw Err.New(1087, $"Must declare the table variable \"{name.Name}\".", 15);
        var obj = FindObject(name);
        if (obj is Table t) return t;
        if (obj is View) throw Err.NotSupported($"Modifying data through the view '{name}'");
        throw Err.InvalidObject(name.ToString());
    }

    // ------------------------------------------------------------ catalog functions

    internal int? ObjectId(string text, string? type)
    {
        var parts = SplitName(text);
        if (parts.Count == 0) return null;
        var name = new TableName
        {
            Name = parts[^1],
            Schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null,
            Database = parts.Count >= 3 && parts[^3].Length > 0 ? parts[^3] : null,
        };
        if (name.IsTemp) name = new TableName { Name = name.Name };
        var obj = FindObject(name);
        if (obj == null) return null;
        if (type != null && !obj.TypeCode.Equals(type.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return obj.Id;
    }

    static List<string> SplitName(string text)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        char closer = '\0';
        foreach (char c in text.Trim())
        {
            if (closer != '\0')
            {
                if (c == closer) closer = '\0';
                else sb.Append(c);
            }
            else if (c == '[') closer = ']';
            else if (c == '"') closer = '"';
            else if (c == '.')
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    internal DbObject? FindObjectById(int id)
    {
        foreach (var o in Db.Objects.Values) if (o.Id == id) return o;
        foreach (var t in TempTables.Values) if (t.Id == id) return t;
        return null;
    }

    internal decimal? IdentCurrent(string tableName)
    {
        var parts = SplitName(tableName);
        var obj = FindObject(new TableName { Name = parts[^1], Schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null }) as Table;
        if (obj == null || obj.IdentityColumn < 0) return null;
        return obj.LastIdentity ?? obj.IdentitySeed;
    }

    internal string? DatabaseName(int id) => Engine.Databases.Values.FirstOrDefault(d => d.Id == id)?.Name;
    internal int? DatabaseId(string name) => Engine.Databases.GetValueOrDefault(name)?.Id;
    internal string? SchemaName(int id) => id >= 1 && id <= Db.Schemas.Count ? Db.Schemas[id - 1] : null;
    internal int? SchemaId(string name) => Db.SchemaId(name) is > 0 and var id ? id : null;

    // ------------------------------------------------------------ CREATE TABLE

    int[] ColumnIndexes(Table table, List<string> names)
    {
        var cols = new int[names.Count];
        for (int i = 0; i < cols.Length; i++)
        {
            cols[i] = table.ColumnIndex(names[i]);
            if (cols[i] < 0)
                throw Err.New(1911, $"Column name '{names[i]}' does not exist in the target table or view.");
        }
        return cols;
    }

    string AutoName(string prefix, Table table) => $"{prefix}__{(table.Name.Length > 9 ? table.Name[..9] : table.Name)}__{Engine.NewObjectId():X8}";

    static bool ConstraintNameInUse(Table table, string name)
        => table.Unique.Exists(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           || table.ForeignKeys.Exists(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           || table.Checks.Exists(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           || table.Columns.Exists(c => name.Equals(c.DefaultName, StringComparison.OrdinalIgnoreCase));

    void AddConstraint(Table table, ConstraintDef d, bool creating = false)
    {
        if (d.Name != null && ConstraintNameInUse(table, d.Name))
            throw Err.New(2714, $"There is already an object named '{d.Name}' in the database.");
        switch (d.Kind)
        {
            case ConstraintKind.PrimaryKey:
            case ConstraintKind.Unique:
            {
                bool pk = d.Kind == ConstraintKind.PrimaryKey;
                if (pk && table.PrimaryKey != null)
                    throw Err.New(1779, $"Table '{table.Name}' already has a primary key defined on it.");
                var cols = ColumnIndexes(table, d.Columns);
                if (pk)
                {
                    // CREATE TABLE makes key columns NOT NULL implicitly; ALTER TABLE requires them to be so already
                    foreach (int c in cols)
                    {
                        if (!creating && table.Columns[c].Nullable)
                            throw Err.New(8111, $"Cannot define PRIMARY KEY constraint on nullable column in table '{table.Name}'.");
                        table.Columns[c].Nullable = false;
                    }
                }
                var index = new UniqueIndex { Name = d.Name ?? AutoName(pk ? "PK" : "UQ", table), Cols = cols, IsPrimary = pk, IsConstraint = true };
                table.AddUniqueIndex(index);
                // a primary key is the clustered index unless told otherwise or the table already has one
                if (table.ClusterCols == null && (d.Clustered ?? pk)) table.SetCluster(cols, index.Name, d.Descending);
                break;
            }
            case ConstraintKind.ForeignKey:
            {
                if (table.IsTemp || table.Name.StartsWith('#')) break; // SQL Server does not enforce foreign keys on temporary tables
                var cols = ColumnIndexes(table, d.Columns);
                Table parent;
                if (d.RefTable!.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase)
                    && (d.RefTable.Schema ?? "dbo").Equals(table.Schema, StringComparison.OrdinalIgnoreCase)) parent = table;
                else
                    parent = FindObject(d.RefTable) as Table
                             ?? throw Err.New(1767, $"Foreign key '{d.Name ?? "FK"}' references invalid table '{d.RefTable}'.");
                int[] refCols;
                if (d.RefColumns != null) refCols = ColumnIndexes(parent, d.RefColumns);
                else
                    refCols = (int[]?)parent.PrimaryKey?.Cols.Clone()
                              ?? throw Err.New(1773, $"Foreign key '{d.Name ?? "FK"}' has implicit reference to object '{parent.Name}' which does not have a primary key defined on it.");
                if (refCols.Length != cols.Length)
                    throw Err.New(8139, "Number of referencing columns in foreign key differs from number of referenced columns, table '" + table.Name + "'.");
                if (parent.FindUniqueOn(refCols) == null && !parent.Unique.Exists(u => u.Cols.Length == refCols.Length && u.Cols.All(refCols.Contains)))
                    throw Err.New(1776, $"There are no primary or candidate keys in the referenced table '{parent.FullName}' that match the referencing column list in the foreign key '{d.Name ?? "FK"}'.");
                var fk = new ForeignKey
                {
                    Name = d.Name ?? AutoName("FK", table), Cols = cols, RefTable = parent, RefCols = refCols,
                    Cascade = d.CascadeDelete, SetNull = d.SetNullOnDelete, UpdateCascade = d.CascadeUpdate,
                };
                table.ForeignKeys.Add(fk);
                try
                {
                    foreach (var row in table.Rows) CheckConstraints(table, row, "ALTER TABLE");
                }
                catch
                {
                    table.ForeignKeys.Remove(fk);
                    throw;
                }
                break;
            }
            case ConstraintKind.Check:
            {
                var check = new CheckConstraint { Name = d.Name ?? AutoName("CK", table), E = d.Expr! };
                table.Checks.Add(check);
                try
                {
                    foreach (var row in table.Rows) CheckConstraints(table, row, "ALTER TABLE");
                }
                catch
                {
                    table.Checks.Remove(check);
                    throw;
                }
                break;
            }
            case ConstraintKind.Default:
            {
                int ci = table.ColumnIndex(d.Columns[0]);
                if (ci < 0) throw Err.InvalidColumn(d.Columns[0]);
                if (table.Columns[ci].Default != null)
                    throw Err.New(1781, $"Column already has a DEFAULT bound to it.");
                table.Columns[ci].Default = d.Expr;
                table.Columns[ci].DefaultName = d.Name ?? AutoName("DF", table);
                break;
            }
        }
    }

    Column BuildColumn(Table table, ColumnDef c)
    {
        if (table.ColumnIndex(c.Name) >= 0)
            throw Err.New(2705, $"Column names in each table must be unique. Column name '{c.Name}' in table '{table.Name}' is specified more than once.");
        var declared = c.Type;
        bool? typeNullable = null;
        if (c.UserType != null)
        {
            var userType = ResolveUserType(c.UserType);
            if (userType.TableDef != null) throw Err.New(350, $"The column \"{c.Name}\" does not have a valid data type. A column cannot be of a user-defined table type.");
            declared = userType.Base;
            typeNullable = userType.Nullable;
        }
        var col = new Column
        {
            Name = c.Name,
            Type = declared ?? SqlType.Variant,
            RowVersion = c.RowVersion,
            Nullable = c.Nullable ?? typeNullable ?? !(c.Identity || c.RowVersion),
            Default = c.Default,
            DefaultName = c.Default != null ? c.DefaultName ?? AutoName("DF", table) : null,
            Identity = c.Identity,
            Computed = c.Computed,
        };
        if (c.Identity)
        {
            if (table.IdentityColumn >= 0)
                throw Err.New(2744, $"Multiple identity columns specified for table '{table.Name}'. Only one identity column per table is allowed.");
            if (!(col.Type.IsInteger || col.Type.Kind == SqlTypeKind.Decimal && col.Type.Scale == 0))
                throw Err.New(2749, $"Identity column '{c.Name}' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, and constrained to be nonnullable.");
            table.IdentitySeed = c.Seed;
            table.IdentityIncrement = c.Increment;
        }
        return col;
    }

    /// <summary>Builds an unregistered table object from a definition (also used for table variables).</summary>
    Table BuildTable(CreateTableStmt def, string schema, string name, Database db)
    {
        var table = new Table { Schema = schema, Name = name, Id = Engine.NewObjectId(), Database = db };
        foreach (var c in def.Columns)
        {
            table.Columns.Add(BuildColumn(table, c));
            table.SchemaChanged();
        }
        if (table.Columns.Count == 0) throw Err.New(102, "Incorrect syntax near ')'.", 15);
        foreach (var d in def.Constraints) AddConstraint(table, d, creating: true);
        // computed column types are inferred from their expressions
        var ctx = new EvalContext(table.GetSchema(name, false), null, null);
        foreach (var col in table.Columns)
        {
            if (col.Computed == null || col.Type.Kind != SqlTypeKind.Variant) continue;
            col.Type = InferType(col.Computed, ctx) ?? SqlType.Variant;
        }
        table.SchemaChanged();
        return table;
    }

    Table CreateTableObject(CreateTableStmt def)
    {
        var name = def.Name;
        if (name.IsTemp)
        {
            if (TempTables.TryGetValue(name.Name, out var outer))
            {
                bool ownScope = _frames.Count <= 1 || Frame.TempTables?.Contains(name.Name) == true;
                if (ownScope || name.Name.StartsWith("##", StringComparison.Ordinal))
                    throw Err.New(2714, $"There is already an object named '{name.Name}' in the database.");
                // a procedure may create its own table with the name of a caller's: it hides that one until it returns
                (Frame.ShadowedTempTables ??= new(StringComparer.OrdinalIgnoreCase))[name.Name] = outer;
            }
            var temp = BuildTable(def, "dbo", name.Name, Engine.Databases["tempdb"]);
            temp.IsTemp = true;
            TempTables[name.Name] = temp;
            // temp tables created inside a procedure or dynamic batch vanish when it ends
            if (_frames.Count > 1 && !name.Name.StartsWith("##", StringComparison.Ordinal)) (Frame.TempTables ??= []).Add(name.Name);
            Log(new UndoEntry { Custom = () => TempTables.Remove(name.Name) });
            return temp;
        }
        var db = ResolveDatabase(name.Database);
        string schema = name.Schema ?? "dbo";
        if (!db.HasSchema(schema))
            throw Err.New(2760, $"The specified schema name \"{schema}\" either does not exist or you do not have permission to use it.");
        if (db.Find(schema, name.Name) != null)
            throw Err.New(2714, $"There is already an object named '{name.Name}' in the database.");
        var table = BuildTable(def, schema, name.Name, db);
        db.Add(table);
        Log(new UndoEntry { Custom = () => db.Remove(table) });
        return table;
    }

    void ExecCreateTable(CreateTableStmt s) => CreateTableObject(s);

    // ------------------------------------------------------------ ALTER TABLE

    void ExecAlterTable(AlterTableStmt s)
    {
        var table = ResolveTable(s.Name);
        if (s.AddColumns.Count > 0 || s.DropColumns.Count > 0 || s.AlterColumn != null)
            _undo.RemoveAll(u => ReferenceEquals(u.Table, table)); // their row images no longer fit the table

        foreach (var c in s.AddColumns)
        {
            var col = BuildColumn(table, c);
            object? fill = null;
            if (col.Identity || col.RowVersion) { }
            else if (col.Default != null && (!col.Nullable || c.WithValues)) fill = ConvertForStorage(Eval(col.Default, null), table, col);
            else if (!col.Nullable && col.Computed == null && table.Rows.Count > 0)
                throw Err.New(4901, $"ALTER TABLE only allows columns to be added that can contain nulls, or have a DEFAULT definition specified, or the column being added is an identity or timestamp column, or alternatively if none of the previous conditions are satisfied the table must be empty to allow addition of this column. Column '{col.Name}' cannot be added to non-empty table '{table.Name}' because it does not satisfy these conditions.");
            table.Columns.Add(col);
            int n = table.Columns.Count;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var row = new object?[n];
                Array.Copy(table.Rows[i], row, n - 1);
                row[n - 1] = col.Identity ? TypeSystem.ConvertTo(NextIdentity(table), col.Type) : col.RowVersion ? NextRowVersion(table) : fill;
                table.Rows[i] = row;
            }
            table.SchemaChanged();
            if (col.Computed != null)
            {
                var ctx = new EvalContext(table.GetSchema(table.Name, false), null, null);
                if (col.Type.Kind == SqlTypeKind.Variant) col.Type = InferType(col.Computed, ctx) ?? SqlType.Variant;
                foreach (var row in table.Rows) ComputeColumns(table, row);
                table.SchemaChanged();
            }
            table.RebuildIndexes();
        }

        foreach (var d in s.AddConstraints) AddConstraint(table, d);

        foreach (string name in s.DropConstraints)
        {
            var key = table.Unique.Find(u => u.IsConstraint && u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (key != null && ReferencingKeys(table).FirstOrDefault(r => r.Fk.RefCols.SequenceEqual(key.Cols)).Fk is { } referencing)
                throw Err.New(3725, $"The constraint '{name}' is being referenced by table '{ReferencingKeys(table).First(r => ReferenceEquals(r.Fk, referencing)).Child.Name}', foreign key constraint '{referencing.Name}'.");
            if (name.Equals(table.ClusterName, StringComparison.OrdinalIgnoreCase)) table.SetCluster(null, null);
            int removed = table.Unique.RemoveAll(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                          + table.ForeignKeys.RemoveAll(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                          + table.Checks.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            foreach (var col in table.Columns)
            {
                if (!name.Equals(col.DefaultName, StringComparison.OrdinalIgnoreCase)) continue;
                col.Default = null;
                col.DefaultName = null;
                removed++;
            }
            if (removed == 0 && !s.IfExists)
                throw Err.New(3728, $"'{name}' is not a constraint.");
        }

        foreach (string name in s.DropColumns)
        {
            int ci = table.ColumnIndex(name);
            if (ci < 0)
            {
                if (s.IfExists) continue;
                throw Err.New(4924, $"ALTER TABLE DROP COLUMN failed because column '{name}' does not exist in table '{table.Name}'.");
            }
            string? dependent = table.Unique.FirstOrDefault(u => u.Cols.Contains(ci))?.Name
                                ?? table.ForeignKeys.FirstOrDefault(f => f.Cols.Contains(ci))?.Name
                                ?? ReferencingKeys(table).FirstOrDefault(r => r.Fk.RefCols.Contains(ci)).Fk?.Name
                                ?? table.Columns[ci].DefaultName
                                ?? table.Checks.FirstOrDefault(c => References(c.E, name))?.Name
                                ?? table.Columns.FirstOrDefault(c => c.Computed != null && References(c.Computed, name))?.Name;
            if (dependent != null)
                throw Err.New(5074, $"The object '{dependent}' is dependent on column '{name}'.");
            if (table.Columns.Count == 1)
                throw Err.New(4923, $"ALTER TABLE DROP COLUMN failed because '{name}' is the only data column in table '{table.Name}'. A table must have at least one data column.");
            table.Columns.RemoveAt(ci);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var old = table.Rows[i];
                var row = new object?[old.Length - 1];
                Array.Copy(old, row, ci);
                Array.Copy(old, ci + 1, row, ci, old.Length - ci - 1);
                table.Rows[i] = row;
            }
            static void Shift(int[] cols, int removed)
            {
                for (int i = 0; i < cols.Length; i++) if (cols[i] > removed) cols[i]--;
            }
            if (table.ClusterCols != null)
            {
                if (table.ClusterCols.Contains(ci)) table.SetCluster(null, null);
                else Shift(table.ClusterCols, ci);
            }
            foreach (var u in table.Unique) Shift(u.Cols, ci);
            foreach (var f in table.ForeignKeys) Shift(f.Cols, ci);
            foreach (var (_, fk) in ReferencingKeys(table).ToList()) Shift(fk.RefCols, ci);
            table.SchemaChanged();
            table.RebuildIndexes();
        }

        if (s.AlterColumn != null)
        {
            var def = s.AlterColumn;
            int ci = table.ColumnIndex(def.Name);
            if (ci < 0) throw Err.InvalidColumn(def.Name);
            var col = table.Columns[ci];
            string? usedBy = table.Unique.FirstOrDefault(u => u.Cols.Contains(ci))?.Name
                             ?? table.ForeignKeys.FirstOrDefault(f => f.Cols.Contains(ci))?.Name
                             ?? ReferencingKeys(table).FirstOrDefault(r => r.Fk.RefCols.Contains(ci)).Fk?.Name
                             ?? table.Checks.FirstOrDefault(c => References(c.E, col.Name))?.Name;
            if (usedBy != null)
                throw Err.New(5074, $"The object '{usedBy}' is dependent on column '{col.Name}'.");
            var target = new Column { Name = col.Name, Type = def.Type! };
            var converted = new object?[table.Rows.Count];
            for (int i = 0; i < converted.Length; i++)
            {
                converted[i] = ConvertForStorage(table.Rows[i][ci], table, target);
                if (converted[i] == null && def.Nullable == false)
                    throw Err.New(515, $"Cannot insert the value NULL into column '{col.Name}', table '{QualifiedName(table)}'; column does not allow nulls. UPDATE fails.");
            }
            for (int i = 0; i < converted.Length; i++) table.Rows[i][ci] = converted[i];
            col.Type = def.Type!;
            col.Nullable = def.Nullable ?? true; // without NULL / NOT NULL the column becomes nullable
            table.SchemaChanged();
            table.RebuildIndexes();
            table.Resort();
        }
    }

    /// <summary>True if the expression mentions a column of the given name.</summary>
    static bool References(Parsing.Expr e, string column)
    {
        bool found = false;
        Walk(e, x =>
        {
            if (x is Parsing.ColumnRef c && c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) found = true;
            return !found;
        });
        return found;
    }

    // ------------------------------------------------------------ DROP / CREATE INDEX

    void ExecDrop(DropStmt s)
    {
        foreach (var name in s.Names)
        {
            switch (s.Kind)
            {
                case ObjectKind.Table:
                {
                    if (name.IsTemp)
                    {
                        if (TempTables.TryGetValue(name.Name, out var temp))
                        {
                            TempTables.Remove(name.Name);
                            Log(new UndoEntry { Custom = () => TempTables[name.Name] = temp });
                        }
                        else if (!s.IfExists)
                            throw Err.New(3701, $"Cannot drop the table '{name.Name}', because it does not exist or you do not have permission.", 11);
                        break;
                    }
                    if (FindObject(name) is not Table table)
                    {
                        if (s.IfExists) break;
                        throw Err.New(3701, $"Cannot drop the table '{name}', because it does not exist or you do not have permission.", 11);
                    }
                    var blocking = ReferencingKeys(table).FirstOrDefault(r => !ReferenceEquals(r.Child, table));
                    if (blocking.Fk != null)
                        throw Err.New(3726, $"Could not drop object '{name}' because it is referenced by a FOREIGN KEY constraint.");
                    var db = table.Database!;
                    db.Remove(table);
                    foreach (var trg in table.Triggers) db.Remove(trg);
                    Log(new UndoEntry
                    {
                        Custom = () =>
                        {
                            db.Add(table);
                            foreach (var trg in table.Triggers) db.Add(trg);
                        },
                    });
                    break;
                }
                case ObjectKind.Type:
                    DropUserType(name, s.IfExists);
                    break;
                case ObjectKind.Trigger:
                case ObjectKind.Sequence:
                {
                    var obj = FindObject(name);
                    if (s.Kind == ObjectKind.Trigger ? obj is not Trigger : obj is not Sequence)
                    {
                        if (s.IfExists) break;
                        string kind = s.Kind == ObjectKind.Trigger ? "trigger" : "sequence object";
                        throw Err.New(3701, $"Cannot drop the {kind} '{name}', because it does not exist or you do not have permission.", 11);
                    }
                    obj!.Database!.Remove(obj);
                    if (obj is Trigger trigger) trigger.Table.Triggers.Remove(trigger);
                    break;
                }
                case ObjectKind.View:
                case ObjectKind.Procedure:
                case ObjectKind.Function:
                {
                    var obj = FindObject(name);
                    bool ok = s.Kind switch { ObjectKind.View => obj is View, ObjectKind.Procedure => obj is Procedure, _ => obj is Function };
                    if (!ok)
                    {
                        if (s.IfExists) break;
                        string kind = s.Kind switch { ObjectKind.View => "view", ObjectKind.Procedure => "procedure", _ => "function" };
                        throw Err.New(3701, $"Cannot drop the {kind} '{name}', because it does not exist or you do not have permission.", 11);
                    }
                    obj!.Database!.Remove(obj);
                    break;
                }
                case ObjectKind.Index:
                {
                    Table? table = s.OnTable != null ? ResolveTable(s.OnTable) : null;
                    bool removed = false;
                    foreach (var t in table != null ? [table] : Db.Tables.ToList())
                    {
                        if (name.Name.Equals(t.ClusterName, StringComparison.OrdinalIgnoreCase) && !t.Unique.Exists(u => u.IsConstraint && u.Name.Equals(t.ClusterName, StringComparison.OrdinalIgnoreCase)))
                            t.SetCluster(null, null);
                        if (t.Unique.RemoveAll(u => !u.IsConstraint && u.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase)) > 0) removed = true;
                        if (t.IndexNames.Remove(name.Name)) removed = true;
                    }
                    if (!removed && !s.IfExists)
                        throw Err.New(3701, $"Cannot drop the index '{name.Name}', because it does not exist or you do not have permission.", 11);
                    break;
                }
                case ObjectKind.Schema:
                {
                    int ix = Db.Schemas.FindIndex(x => x.Equals(name.Name, StringComparison.OrdinalIgnoreCase));
                    if (ix < 0)
                    {
                        if (s.IfExists) break;
                        throw Err.New(3701, $"Cannot drop the schema '{name.Name}', because it does not exist or you do not have permission.", 11);
                    }
                    if (Db.Objects.Values.Any(o => o.Schema.Equals(name.Name, StringComparison.OrdinalIgnoreCase)))
                        throw Err.New(3729, $"Cannot drop schema '{name.Name}' because it is being referenced by an object.");
                    Db.Schemas.RemoveAt(ix);
                    break;
                }
                case ObjectKind.Database:
                {
                    if (!Engine.Databases.TryGetValue(name.Name, out var db))
                    {
                        if (s.IfExists) break;
                        throw Err.New(3701, $"Cannot drop the database '{name.Name}', because it does not exist or you do not have permission.", 11);
                    }
                    if (ReferenceEquals(db, Db)) throw Err.New(3702, $"Cannot drop database \"{name.Name}\" because it is currently in use.");
                    if (name.Name.Equals("master", StringComparison.OrdinalIgnoreCase) || name.Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
                        throw Err.New(3708, $"Cannot drop the database '{name.Name}' because it is a system database.");
                    Engine.Databases.Remove(name.Name);
                    break;
                }
                default: throw Err.NotSupported("DROP " + s.Kind);
            }
        }
    }

    void ExecCreateIndex(CreateIndexStmt s)
    {
        var table = ResolveTable(s.Table);
        if (table.IndexNames.Contains(s.Name) || table.Unique.Exists(u => u.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
            throw Err.New(1913, $"The operation failed because an index or statistics with name '{s.Name}' already exists on table '{table.FullName}'.");
        var cols = ColumnIndexes(table, s.Columns);
        if (s.Clustered && table.ClusterCols != null)
            throw Err.New(1902, $"Cannot create more than one clustered index on table '{table.FullName}'. Drop the existing clustered index '{table.ClusterName}' before creating another.");
        if (s.Unique) table.AddUniqueIndex(new UniqueIndex { Name = s.Name, Cols = cols });
        else table.IndexNames.Add(s.Name);
        if (s.Clustered) table.SetCluster(cols, s.Name, s.Descending);
    }

    // ------------------------------------------------------------ views, procedures, functions

    void RegisterModule(TableName name, bool orAlter, Func<DbObject, bool> sameKind, Func<DbObject> create)
    {
        if (name.IsTemp) throw Err.NotSupported("Temporary procedures and functions");
        var db = ResolveDatabase(name.Database);
        string schema = name.Schema ?? "dbo";
        if (!db.HasSchema(schema))
            throw Err.New(2760, $"The specified schema name \"{schema}\" either does not exist or you do not have permission to use it.");
        var existing = db.Find(schema, name.Name);
        int? id = null;
        if (existing != null)
        {
            if (!orAlter || !sameKind(existing))
                throw Err.New(2714, $"There is already an object named '{name.Name}' in the database.");
            id = existing.Id;
        }
        var obj = create();
        obj.Schema = schema;
        obj.Name = name.Name;
        obj.Id = id ?? Engine.NewObjectId();
        db.Add(obj);
    }

    void ExecCreateView(CreateViewStmt s)
    {
        // validate eagerly so that errors surface at CREATE time, as in SQL Server
        var probe = new View { Query = s.Query, Columns = s.Columns, Database = ResolveDatabase(s.Name.Database) };
        var rs = ExecView(probe, null);
        if (s.Columns != null && s.Columns.Count != rs.Schema.Count)
            throw Err.New(8158, $"'{s.Name.Name}' has {(s.Columns.Count > rs.Schema.Count ? "more" : "fewer")} columns than were specified in the column list.");
        RegisterModule(s.Name, s.OrAlter, o => o is View, () => new View { Query = s.Query, Columns = s.Columns });
    }

    void ExecCreateProc(CreateProcStmt s)
        => RegisterModule(s.Name, s.OrAlter, o => o is Procedure, () => new Procedure { Def = s });

    void ExecCreateFunction(CreateFunctionStmt s)
        => RegisterModule(s.Name, s.OrAlter, o => o is Function, () => new Function { Def = s });

    // ------------------------------------------------------------ sequences

    static (long Min, long Max) IntegerRange(SqlType t) => t.Kind switch
    {
        SqlTypeKind.TinyInt => (0, 255),
        SqlTypeKind.SmallInt => (short.MinValue, short.MaxValue),
        SqlTypeKind.Int => (int.MinValue, int.MaxValue),
        _ => (long.MinValue, long.MaxValue),
    };

    void ExecCreateSequence(CreateSequenceStmt s)
    {
        var type = s.Type ?? SqlType.BigInt;
        if (!(type.IsInteger || (type.Kind == SqlTypeKind.Decimal && type.Scale == 0)))
            throw Err.New(11702, $"The sequence object '{s.Name.Name}' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, or any user-defined data type that is based on one of the above integer data types.");
        var (lo, hi) = IntegerRange(type);
        long increment = s.Increment ?? 1;
        if (increment == 0) throw Err.New(11700, "The increment for sequence object '" + s.Name.Name + "' cannot be zero.");
        long min = s.Min ?? lo, max = s.Max ?? hi;
        long start = s.Start ?? (increment > 0 ? min : max);
        if (start < min || start > max)
            throw Err.New(11703, $"The start value for sequence object '{s.Name.Name}' must be between the minimum and maximum value of the sequence object.");
        RegisterModule(s.Name, false, _ => false,
            () => new Sequence { Type = type, Start = start, Increment = increment, Min = min, Max = max, Cycle = s.Cycle });
    }

    Sequence FindSequence(TableName name)
        => FindObject(name) as Sequence ?? throw Err.InvalidObject(name.ToString());

    void ExecAlterSequence(AlterSequenceStmt s)
    {
        var seq = FindSequence(s.Name);
        if (s.Increment != null) seq.Increment = s.Increment.Value;
        if (s.Restart)
        {
            if (s.RestartWith != null) seq.Start = s.RestartWith.Value;
            seq.Current = null;
        }
    }

    object? NextSequenceValue(TableName name)
    {
        var seq = FindSequence(name);
        long next;
        if (seq.Current == null) next = seq.Start;
        else
        {
            bool exhausted = seq.Increment > 0 ? seq.Current.Value > seq.Max - seq.Increment : seq.Current.Value < seq.Min - seq.Increment;
            if (exhausted)
            {
                if (!seq.Cycle)
                    throw Err.New(11728, $"The sequence object '{seq.Name}' has reached its minimum or maximum value. Restart the sequence object to allow new values to be generated.");
                next = seq.Increment > 0 ? seq.Min : seq.Max;
            }
            else next = seq.Current.Value + seq.Increment;
        }
        seq.Current = next;
        return TypeSystem.ConvertTo(next, seq.Type);
    }

    // ------------------------------------------------------------ triggers

    void ExecCreateTrigger(CreateTriggerStmt s)
    {
        if (FindObject(s.Table) is not Table table || table.IsVariable)
            throw Err.InvalidObject(s.Table.ToString());
        if (table.IsTemp) throw Err.NotSupported("Triggers on temporary tables");
        var name = new TableName { Name = s.Name.Name, Schema = s.Name.Schema ?? table.Schema, Database = s.Name.Database };
        Trigger? created = null;
        var old = FindObject(name) as Trigger;
        RegisterModule(name, s.OrAlter, o => o is Trigger, () => created = new Trigger { Def = s, Table = table });
        if (old != null) old.Table.Triggers.Remove(old);
        table.Triggers.Add(created!);
    }

    void ExecEnableTrigger(EnableTriggerStmt s)
    {
        var table = ResolveTable(s.Table);
        bool any = false;
        foreach (var t in table.Triggers)
        {
            if (s.Name != null && !t.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) continue;
            t.Enabled = s.Enable;
            any = true;
        }
        if (!any && s.Name != null)
            throw Err.New(4902, $"Cannot find the object \"{s.Name}\" because it does not exist or you do not have permissions.");
    }

    static Table PseudoTable(Table source, string name, List<object?[]>? rows)
    {
        var t = new Table { Name = name, Schema = source.Schema, Database = source.Database, IsVariable = true };
        t.Columns.AddRange(source.Columns);
        if (rows != null)
            foreach (var row in rows) t.Rows.Add((object?[])row.Clone());
        return t;
    }

    /// <summary>Runs the AFTER triggers of a table for one statement. 'kind' is 'I', 'U' or 'D'.</summary>
    void FireTriggers(Table table, char kind, List<object?[]>? inserted, List<object?[]>? deleted, HashSet<int>? updatedColumns, bool insteadOf = false)
    {
        if (table.Triggers.Count == 0) return;
        foreach (var trigger in table.Triggers.ToList())
        {
            var def = trigger.Def;
            if (!trigger.Enabled || def.InsteadOf != insteadOf) continue;
            if (!(kind == 'I' ? def.OnInsert : kind == 'U' ? def.OnUpdate : def.OnDelete)) continue;
            if (_activeTriggers.Contains(trigger)) continue; // no direct recursion
            RunTrigger(trigger, table, inserted, deleted, updatedColumns);
        }
    }

    Trigger? InsteadOfTrigger(Table table, char kind)
    {
        if (_activeTriggers.Exists(a => a.Def.InsteadOf && ReferenceEquals(a.Table, table))) return null;
        foreach (var t in table.Triggers)
        {
            if (!t.Enabled || !t.Def.InsteadOf) continue;
            if (kind == 'I' ? t.Def.OnInsert : kind == 'U' ? t.Def.OnUpdate : t.Def.OnDelete) return t;
        }
        return null;
    }

    void RunTrigger(Trigger trigger, Table table, List<object?[]>? inserted, List<object?[]>? deleted, HashSet<int>? updatedColumns)
    {
        var frame = new Frame
        {
            Procedure = trigger.FullName,
            TriggerTable = table,
            Inserted = PseudoTable(table, "inserted", inserted),
            Deleted = PseudoTable(table, "deleted", deleted),
            UpdatedColumns = updatedColumns,
        };
        // a trigger always runs inside a transaction: the statement's own if there is no explicit one
        bool implicitTran = _tranCount == 0;
        if (implicitTran) _tranCount = 1;
        int rowCount = inserted?.Count ?? deleted?.Count ?? 0;
        _rowCount = rowCount;
        var savedDb = Db;
        var savedReturn = _returnValue;
        if (trigger.Database != null) Db = trigger.Database;
        _activeTriggers.Add(trigger);
        _triggerDepth++; // XACT_ABORT is implicitly ON inside a trigger (see ExecStatement)
        try
        {
            RunInFrame(frame, () => ExecBlock(trigger.Def.Body));
        }
        catch (TSqlException ex)
        {
            ex.Procedure ??= trigger.FullName;
            if (implicitTran) _tranCount = 0;
            throw;
        }
        finally
        {
            _triggerDepth--;
            _activeTriggers.Remove(trigger);
            Db = savedDb;
            _returnValue = savedReturn;
        }
        if (_tranCount == 0)
            throw new TSqlException(3609, "The transaction ended in the trigger. The batch has been aborted.") { ForceBatchAbort = true };
        if (implicitTran) _tranCount = 0;
        _rowCount = rowCount;
    }

    // ------------------------------------------------------------ DBCC

    void ExecDbcc(DbccStmt s)
    {
        if (s.Command != "CHECKIDENT") return; // other DBCC commands have nothing to do in memory
        if (s.Args.Count == 0) throw Err.New(102, "Incorrect syntax near ')'.", 15);
        var parts = SplitName(TypeSystem.ToStr(Eval(s.Args[0], null) ?? ""));
        var table = ResolveTable(new TableName { Name = parts[^1], Schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null });
        if (table.IdentityColumn < 0) throw Err.New(7997, $"'{table.Name}' does not contain an identity column.");
        if (s.Args.Count < 3) return;
        long seed = TypeSystem.ToLong(Eval(s.Args[2], null) ?? 0);
        // on a table that never held rows the next value is the seed itself, otherwise seed + increment
        if (table.LastIdentity == null) table.IdentitySeed = seed;
        else table.LastIdentity = seed;
    }

    // ------------------------------------------------------------ system catalog views

    static RowSet MakeSet(string[] names, SqlType[] types, List<object?[]> rows)
    {
        var cols = new List<ColInfo>(names.Length);
        for (int i = 0; i < names.Length; i++) cols.Add(new ColInfo(null, names[i], types[i]));
        return new RowSet(new RowSchema(cols), rows);
    }

    RowSet? SystemView(TableName name)
    {
        string schema = (name.Schema ?? "").ToUpperInvariant();
        if (schema != "SYS" && schema != "INFORMATION_SCHEMA") return null;
        var db = ResolveDatabase(name.Database);
        var sysname = SqlType.NVarChar(128);
        var rows = new List<object?[]>();
        var objects = db.Objects.Values.OrderBy(o => o.Id).ToList();
        switch (schema + "." + name.Name.ToUpperInvariant())
        {
            case "INFORMATION_SCHEMA.TABLES":
                foreach (var o in objects)
                    if (o is Table or View) rows.Add([db.Name, o.Schema, o.Name, o is Table ? "BASE TABLE" : "VIEW"]);
                return MakeSet(["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE"], [sysname, sysname, sysname, SqlType.VarChar(10)], rows);
            case "INFORMATION_SCHEMA.COLUMNS":
                foreach (var t in objects.OfType<Table>())
                {
                    for (int i = 0; i < t.Columns.Count; i++)
                    {
                        var c = t.Columns[i];
                        rows.Add([
                            db.Name, t.Schema, t.Name, c.Name, i + 1, c.Nullable ? "YES" : "NO", c.Type.Name,
                            c.Type.IsString || c.Type.IsBinary ? c.Type.Length : null,
                            c.Type.IsNumeric && c.Type.Kind != SqlTypeKind.Bit ? (byte?)Math.Max(c.Type.Precision, c.Type.Kind == SqlTypeKind.Float ? 53 : c.Type.Kind == SqlTypeKind.Real ? 24 : 0) : null,
                            c.Type.IsInteger || c.Type.IsDecimalLike ? c.Type.Scale : null,
                        ]);
                    }
                }
                return MakeSet(
                    ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "IS_NULLABLE", "DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE"],
                    [sysname, sysname, sysname, sysname, SqlType.Int, SqlType.VarChar(3), sysname, SqlType.Int, SqlType.TinyInt, SqlType.Int], rows);
            case "INFORMATION_SCHEMA.ROUTINES":
                foreach (var o in objects)
                    if (o is Procedure or Function) rows.Add([db.Name, o.Schema, o.Name, o is Procedure ? "PROCEDURE" : "FUNCTION"]);
                return MakeSet(["ROUTINE_CATALOG", "ROUTINE_SCHEMA", "ROUTINE_NAME", "ROUTINE_TYPE"], [sysname, sysname, sysname, SqlType.NVarChar(20)], rows);
            case "INFORMATION_SCHEMA.SCHEMATA":
                foreach (string s in db.Schemas) rows.Add([db.Name, s]);
                return MakeSet(["CATALOG_NAME", "SCHEMA_NAME"], [sysname, sysname], rows);
            case "SYS.OBJECTS":
            case "SYS.TABLES":
            case "SYS.VIEWS":
            case "SYS.PROCEDURES":
            case "SYS.ALL_OBJECTS":
            {
                string which = name.Name.ToUpperInvariant();
                foreach (var o in objects)
                {
                    bool include = which switch { "TABLES" => o is Table, "VIEWS" => o is View, "PROCEDURES" => o is Procedure, _ => true };
                    if (include) rows.Add([o.Name, o.Id, db.SchemaId(o.Schema), o.TypeCode.PadRight(2), o.TypeDesc, o.Created, o.Created]);
                }
                return MakeSet(["name", "object_id", "schema_id", "type", "type_desc", "create_date", "modify_date"],
                    [sysname, SqlType.Int, SqlType.Int, new SqlType(SqlTypeKind.Char, 2), SqlType.NVarChar(60), SqlType.DateTime, SqlType.DateTime], rows);
            }
            case "SYS.SCHEMAS":
                for (int i = 0; i < db.Schemas.Count; i++) rows.Add([db.Schemas[i], i + 1]);
                return MakeSet(["name", "schema_id"], [sysname, SqlType.Int], rows);
            case "SYS.DATABASES":
                foreach (var d in Engine.Databases.Values.OrderBy(d => d.Id)) rows.Add([d.Name, d.Id]);
                return MakeSet(["name", "database_id"], [sysname, SqlType.Int], rows);
            case "SYS.COLUMNS":
                foreach (var t in objects.OfType<Table>())
                {
                    for (int i = 0; i < t.Columns.Count; i++)
                    {
                        var c = t.Columns[i];
                        short maxLength = (short)(c.Type.IsString || c.Type.IsBinary
                            ? c.Type.Length == SqlType.Max ? -1 : c.Type.Length * (c.Type.IsUnicode ? 2 : 1)
                            : Functions.DataLength(DefaultOf(c.Type), c.Type));
                        rows.Add([t.Id, c.Name, i + 1, c.Type.Name, maxLength, (byte)c.Type.Precision, (byte)c.Type.Scale, c.Nullable, c.Identity, c.Computed != null]);
                    }
                }
                return MakeSet(["object_id", "name", "column_id", "type_name", "max_length", "precision", "scale", "is_nullable", "is_identity", "is_computed"],
                    [SqlType.Int, sysname, SqlType.Int, sysname, SqlType.SmallInt, SqlType.TinyInt, SqlType.TinyInt, SqlType.Bit, SqlType.Bit, SqlType.Bit], rows);
            case "SYS.INDEXES":
                foreach (var t in objects.OfType<Table>())
                {
                    int n = 0;
                    foreach (var u in t.Unique) rows.Add([t.Id, u.Name, ++n, true, u.IsPrimary, u.IsConstraint && !u.IsPrimary]);
                    foreach (string ix in t.IndexNames) rows.Add([t.Id, ix, ++n, false, false, false]);
                }
                return MakeSet(["object_id", "name", "index_id", "is_unique", "is_primary_key", "is_unique_constraint"],
                    [SqlType.Int, sysname, SqlType.Int, SqlType.Bit, SqlType.Bit, SqlType.Bit], rows);
            case "SYS.FOREIGN_KEYS":
                foreach (var t in objects.OfType<Table>())
                    foreach (var fk in t.ForeignKeys) rows.Add([fk.Name, t.Id, fk.RefTable.Id]);
                return MakeSet(["name", "parent_object_id", "referenced_object_id"], [sysname, SqlType.Int, SqlType.Int], rows);
            default:
                return MoreSystemViews(schema + "." + name.Name.ToUpperInvariant(), db, objects);
        }
    }

    static object DefaultOf(SqlType t) => t.Kind switch
    {
        SqlTypeKind.Bit => false,
        SqlTypeKind.TinyInt => (byte)0,
        SqlTypeKind.SmallInt => (short)0,
        SqlTypeKind.Int => 0,
        SqlTypeKind.BigInt => 0L,
        SqlTypeKind.Real => 0f,
        SqlTypeKind.Float => 0d,
        SqlTypeKind.Decimal or SqlTypeKind.Money or SqlTypeKind.SmallMoney => 0m,
        SqlTypeKind.UniqueIdentifier => Guid.Empty,
        SqlTypeKind.Time => TimeSpan.Zero,
        SqlTypeKind.DateTimeOffset => DateTimeOffset.MinValue,
        _ => DateTime.MinValue,
    };
}
