using LocalDbSharp.Execution;
using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

public sealed partial class Session
{
    internal decimal? ScopeIdentity => _frames.Count > 0 ? Frame.ScopeIdentity : _rootScopeIdentity;
    internal short XactState => (short)(_tranCount == 0 ? 0 : _doomed ? -1 : 1);

    void NoteIdentity(long value)
    {
        _identity = value;
        if (_frames.Count > 0) Frame.ScopeIdentity = value;
    }
    internal int LastRowCount => _rowCount;

    string QualifiedName(Table t) => t.IsVariable || t.IsTemp ? t.Name : $"{t.Database?.Name ?? Db.Name}.{t.Schema}.{t.Name}";

    // ------------------------------------------------------------ row construction & validation

    object? ConvertForStorage(object? value, Table table, Column col)
    {
        if (value == null) return null;
        try
        {
            return TypeSystem.ConvertTo(value, col.Type, storage: true);
        }
        catch (TSqlException ex) when (ex.Number == 2628)
        {
            string text = TypeSystem.ToStr(value);
            if (text.Length > 100) text = text[..100];
            throw Err.New(2628, $"String or binary data would be truncated in table '{QualifiedName(table)}', column '{col.Name}'. Truncated value: '{text}'.");
        }
    }

    object? DefaultValue(Table table, Column col)
        => col.Default == null ? null : ConvertForStorage(Eval(col.Default, null), table, col);

    long NextIdentity(Table table)
    {
        long next = table.LastIdentity == null ? table.IdentitySeed : table.LastIdentity.Value + table.IdentityIncrement;
        table.LastIdentity = next;
        return next;
    }

    void ComputeColumns(Table table, object?[] row)
    {
        EvalContext? ctx = null;
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (col.Computed == null) continue;
            ctx ??= new EvalContext(table.GetSchema(table.Name, false), null, null) { Row = row };
            row[i] = Eval(col.Computed, ctx);
        }
    }

    // Validation happens in SQL Server's order: NOT NULL first, then key uniqueness (when the row goes into
    // the indexes), and only then CHECK and FOREIGN KEY constraints.

    void CheckNotNull(Table table, object?[] row, string verb)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (row[i] == null && !col.Nullable)
                throw Err.New(515, $"Cannot insert the value NULL into column '{col.Name}', table '{QualifiedName(table)}'; column does not allow nulls. {verb} fails.");
        }
    }

    /// <summary>'what': 0 = CHECK and FOREIGN KEY constraints, 1 = CHECK only, 2 = FOREIGN KEY only.</summary>
    void CheckConstraints(Table table, object?[] row, string verb, object?[]? oldRow = null, int what = 0)
    {
        if (what != 2 && table.Checks.Count > 0)
        {
            var ctx = new EvalContext(table.GetSchema(table.Name, false), null, null) { Row = row };
            foreach (var check in table.Checks)
            {
                if (Eval(check.E, ctx) is false)
                    throw Err.New(547, $"The {verb} statement conflicted with the CHECK constraint \"{check.Name}\". The conflict occurred in database \"{Db.Name}\", table \"{table.FullName}\".");
            }
        }
        if (what == 1) return;
        foreach (var fk in table.ForeignKeys)
        {
            bool hasNull = false, changed = oldRow == null;
            var key = new object[fk.Cols.Length];
            for (int k = 0; k < key.Length; k++)
            {
                object? v = row[fk.Cols[k]];
                if (v == null) { hasNull = true; break; }
                key[k] = TypeSystem.NormalizeKey(v);
                if (oldRow != null && !Equals(key[k], TypeSystem.NormalizeKey(oldRow[fk.Cols[k]]))) changed = true;
            }
            if (hasNull || !changed) continue;
            var rowKey = new RowKey(key);
            var ix = fk.RefTable.FindUniqueOn(fk.RefCols);
            bool exists;
            if (ix != null) exists = ix.Map.ContainsKey(rowKey);
            else
            {
                exists = false;
                foreach (var parent in fk.RefTable.Rows)
                {
                    var pk = new object[fk.RefCols.Length];
                    for (int k = 0; k < pk.Length; k++) pk[k] = TypeSystem.NormalizeKey(parent[fk.RefCols[k]]);
                    if (new RowKey(pk).Equals(rowKey)) { exists = true; break; }
                }
            }
            // a self-referencing row may point at itself
            if (!exists && ReferenceEquals(fk.RefTable, table))
            {
                exists = true;
                for (int k = 0; k < key.Length; k++)
                    if (!Equals(key[k], TypeSystem.NormalizeKey(row[fk.RefCols[k]]))) { exists = false; break; }
            }
            if (!exists)
                throw Err.New(547, $"The {verb} statement conflicted with the FOREIGN KEY constraint \"{fk.Name}\". The conflict occurred in database \"{Db.Name}\", table \"{fk.RefTable.FullName}\", column '{fk.RefTable.Columns[fk.RefCols[0]].Name}'.");
        }
    }

    /// <summary>Inserts source rows (already evaluated) into a table. 'map' gives the target column of each source value.</summary>
    List<object?[]> InsertRows(Table table, int[] map, List<object?[]> source, bool buildOnly = false)
    {
        int n = table.Columns.Count;
        int identCol = table.IdentityColumn;
        bool identProvided = identCol >= 0 && Array.IndexOf(map, identCol) >= 0;
        if (identProvided && !table.IdentityInsert)
            throw Err.New(544, $"Cannot insert explicit value for identity column in table '{table.Name}' when IDENTITY_INSERT is set to OFF.");
        if (!identProvided && identCol >= 0 && table.IdentityInsert)
            throw Err.New(545, $"Explicit value must be specified for identity column in table '{table.Name}' either when IDENTITY_INSERT is set to ON or when a replication user is inserting into a NOT FOR REPLICATION identity column.");
        foreach (int c in map)
            if (table.Columns[c].Computed != null)
                throw Err.New(271, $"The column \"{table.Columns[c].Name}\" cannot be modified because it is either a computed column or is the result of a UNION operator.");

        var provided = new bool[n];
        var result = new List<object?[]>(source.Count);
        // large batches go in unordered and are put into clustered order once at the end
        bool deferOrder = table.ClusterCols != null && source.Count > 16;
        foreach (var src in source)
        {
            var row = new object?[n];
            Array.Clear(provided);
            // identity values are handed out first: SQL Server burns them even if the row is rejected afterwards
            if (identCol >= 0 && !identProvided && !buildOnly)
            {
                long id = NextIdentity(table);
                row[identCol] = TypeSystem.ConvertTo(id, table.Columns[identCol].Type);
                provided[identCol] = true;
                NoteIdentity(id);
            }
            for (int k = 0; k < map.Length; k++)
            {
                if (src[k] is DefaultMarker) continue;
                provided[map[k]] = true;
                row[map[k]] = src[k];
            }
            // Columns are converted and checked for NULL one at a time in table order, which decides
            // which error is reported when a row has several problems.
            for (int c = 0; c < n; c++)
            {
                var col = table.Columns[c];
                if (c == identCol && !identProvided)
                {
                    if (buildOnly) row[c] = TypeSystem.ConvertTo(0, col.Type);
                    continue;
                }
                if (col.Computed != null) continue;
                if (col.RowVersion)
                {
                    if (provided[c] && row[c] != null)
                        throw Err.New(273, "Cannot insert an explicit value into a timestamp column. Use INSERT with a column list to exclude the timestamp column, or insert a DEFAULT into the timestamp column.");
                    row[c] = buildOnly ? null : NextRowVersion(table);
                    continue;
                }
                row[c] = provided[c] ? ConvertForStorage(row[c], table, col) : DefaultValue(table, col);
                if (row[c] == null && !col.Nullable && !buildOnly)
                    throw Err.New(515, $"Cannot insert the value NULL into column '{col.Name}', table '{QualifiedName(table)}'; column does not allow nulls. INSERT fails.");
            }
            if (buildOnly)
            {
                ComputeColumns(table, row);
                result.Add(row);
                continue;
            }
            if (identProvided && row[identCol] != null)
            {
                long explicitId = TypeSystem.ToLong(row[identCol]!);
                NoteIdentity(explicitId);
                if (table.LastIdentity == null || (table.IdentityIncrement > 0 ? explicitId > table.LastIdentity : explicitId < table.LastIdentity))
                    table.LastIdentity = explicitId;
            }
            ComputeColumns(table, row);
            CheckNotNull(table, row, "INSERT");
            table.Insert(row, deferOrder);
            Log(new UndoEntry { Table = table, InsertedRow = row });
            CheckConstraints(table, row, "INSERT", null, 1);
            result.Add(row);
        }
        if (deferOrder) table.Resort();
        // foreign keys are verified once the whole statement is in, so rows may reference each other
        if (table.ForeignKeys.Count > 0)
            foreach (var row in result) CheckConstraints(table, row, "INSERT", null, 2);
        return result;
    }

    int[] InsertMap(Table table, List<string>? columns)
    {
        if (columns != null)
        {
            var map = new int[columns.Count];
            for (int i = 0; i < map.Length; i++)
            {
                map[i] = table.ColumnIndex(columns[i]);
                if (map[i] < 0) throw Err.InvalidColumn(columns[i]);
                if (Array.IndexOf(map, map[i]) < i)
                    throw Err.New(264, $"The column name '{columns[i]}' is specified more than once in the SET clause or column list of an INSERT. A column cannot be assigned more than one value in the same clause.");
            }
            return map;
        }
        var all = new List<int>();
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var c = table.Columns[i];
            if (c.Computed != null || (c.Identity && !table.IdentityInsert)) continue;
            all.Add(i);
        }
        return all.ToArray();
    }

    // ------------------------------------------------------------ INSERT

    void ExecInsert(InsertStmt s)
    {
        var env = s.Ctes != null ? new QueryEnv(null, s.Ctes) : null;
        var table = ResolveTable(UpdatableBase(s.Target, null)?.Name ?? s.Target);
        var columns = s.Columns;
        if (UpdatableColumns(s.Target, null, out var viewColumns) is { } viewMap)
            columns = (columns ?? viewColumns!).ConvertAll(c => BaseColumn(viewMap, c, s.Target));
        int[] map = s.DefaultValues ? [] : InsertMap(table, columns);
        List<object?[]> source;
        if (s.Values != null)
        {
            source = new List<object?[]>(s.Values.Count);
            foreach (var exprs in s.Values)
            {
                if (exprs.Count != map.Length)
                {
                    if (s.Columns == null) throw Err.New(213, "Column name or number of supplied values does not match table definition.");
                    throw exprs.Count < map.Length
                        ? Err.New(109, "There are more columns in the INSERT statement than values specified in the VALUES clause. The number of values in the VALUES clause must match the number of columns specified in the INSERT statement.", 15)
                        : Err.New(110, "There are fewer columns in the INSERT statement than values specified in the VALUES clause. The number of values in the VALUES clause must match the number of columns specified in the INSERT statement.", 15);
                }
                var row = new object?[exprs.Count];
                for (int i = 0; i < row.Length; i++) row[i] = EvalFor(exprs[i], null, table.Columns[map[i]].Type);
                source.Add(row);
            }
            if (s.Values.Count > 1)
            {
                // a multi-row VALUES list is a table value constructor: each column takes the highest-precedence type
                for (int j = 0; j < map.Length; j++)
                {
                    SqlType? unified = null;
                    foreach (var exprs in s.Values)
                        if (exprs[j] is not DefaultExpr) unified = Unify(unified, InferType(exprs[j], null));
                    if (unified == null || unified.IsString) continue;
                    foreach (var row in source)
                        if (row[j] != null && row[j] is not DefaultMarker && row[j]!.GetType() != unified.ClrType) row[j] = CoerceTo(row[j], unified);
                }
            }
        }
        else if (s.Select != null)
        {
            var rs = ExecQuery(s.Select, null, env);
            if (rs.Schema.Count != map.Length)
            {
                if (s.Columns == null) throw Err.New(213, "Column name or number of supplied values does not match table definition.");
                throw Err.New(121, "The select list for the INSERT statement contains " + (rs.Schema.Count < map.Length ? "fewer" : "more") + " items than the insert list. The number of SELECT values must match the number of INSERT columns.", 15);
            }
            source = rs.Rows;
            for (int j = 0; j < map.Length; j++)
            {
                var from = rs.Schema.Cols[j].Type;
                var to = table.Columns[map[j]].Type;
                bool textual = to.IsString && from is { IsDateTime: true } or { Kind: SqlTypeKind.Money or SqlTypeKind.SmallMoney };
                bool moneyToInt = to.IsInteger && from?.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney;
                if (!textual && !moneyToInt) continue;
                foreach (var row in source) row[j] = AdaptImplicit(row[j], from, to);
            }
        }
        else if (s.Exec != null)
        {
            var saved = _sink;
            var capture = new ExecutionResult();
            _sink = capture;
            try { ExecExec(s.Exec); }
            finally { _sink = saved; }
            source = [];
            foreach (var rs in capture.ResultSets)
            {
                if (rs.Columns.Count != map.Length) throw Err.New(213, "Column name or number of supplied values does not match table definition.");
                source.AddRange(rs.Rows);
            }
        }
        else source = [[]];

        if (InsteadOfTrigger(table, 'I') is { } instead)
        {
            var proposed = InsertRows(table, map, source, buildOnly: true);
            RunTrigger(instead, table, proposed, null, null);
            AddAffected(proposed.Count);
            return;
        }
        var inserted = InsertRows(table, map, source);
        if (s.Output != null) EmitOutput(s.Output, table, inserted, null, null);
        FireTriggers(table, 'I', inserted, null, null);
        AddAffected(inserted.Count);
    }

    void ExecSelectInto(Query q, SelectSpec spec)
    {
        var target = spec.Into!;
        if (target.IsVariable) throw Err.New(102, $"Incorrect syntax near '{target.Name}'.", 15);
        var rs = ExecQuery(q, null, null);
        var def = new CreateTableStmt { Name = target };
        foreach (var c in rs.Schema.Cols)
        {
            if (c.Name.Length == 0) throw Err.New(1038, "An object or column name is missing or empty. For SELECT INTO statements, verify each column has a name.", 15);
            def.Columns.Add(new ColumnDef { Name = c.Name, Type = c.Type ?? SqlType.Int, Nullable = true });
        }
        var table = CreateTableObject(def);
        var map = Enumerable.Range(0, table.Columns.Count).ToArray();
        var rows = InsertRows(table, map, rs.Rows);
        AddAffected(rows.Count);
    }

    // ------------------------------------------------------------ target row discovery (UPDATE / DELETE)

    static TableRef? FindRef(TableSource src, TableName target)
    {
        TableRef? byName = null;
        TableRef? Visit(TableSource s)
        {
            switch (s)
            {
                case TableRef tr:
                    if (tr.Alias != null)
                    {
                        if (target.Schema == null && tr.Alias.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) return tr;
                    }
                    else if (tr.Name.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
                             && (target.Schema == null || tr.Name.Schema == null || target.Schema.Equals(tr.Name.Schema, StringComparison.OrdinalIgnoreCase)))
                        return tr;
                    if (byName == null && tr.Name.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) byName = tr;
                    return null;
                case JoinSource j: return Visit(j.Left) ?? Visit(j.Right);
                default: return null;
            }
        }
        return Visit(src) ?? byName;
    }

    /// <summary>
    /// Evaluates FROM/WHERE of an UPDATE or DELETE. Each returned row carries, at index 'refIndex',
    /// a reference to the stored row of the target table. Duplicate matches of the same target row are removed.
    /// </summary>
    (Table Table, RowSet Rows, int RefIndex) FindTargetRows(TableName target, TableSource? from, Expr? where, Expr? top, QueryEnv? env)
    {
        TableRef? targetRef = from != null ? FindRef(from, target) : null;
        TableSource source;
        if (targetRef != null) source = from!;
        else
        {
            targetRef = new TableRef { Name = target };
            source = from == null ? targetRef : new JoinSource { Left = targetRef, Right = from, Kind = JoinKind.Cross };
        }
        // the target may be a CTE or a view over a single table; the stored rows are then reached through it
        var baseRef = UpdatableBase(targetRef.Name, env) ?? targetRef;
        var table = ResolveTable(baseRef.Name);
        var saved = _rowRefTarget;
        bool savedCarry = _carryRowRef;
        _rowRefTarget = baseRef;
        _carryRowRef = !ReferenceEquals(baseRef, targetRef);
        RowSet rs;
        try
        {
            rs = ExecFromWhere(source, where, null, env, int.MaxValue, out _);
        }
        finally
        {
            _rowRefTarget = saved;
            _carryRowRef = savedCarry;
        }
        int refIndex = rs.Schema.HiddenIndex();
        if (refIndex < 0)
            throw Err.New(4405, $"View or function '{target.Name}' is not updatable because the modification affects multiple base tables.");
        long limit = top == null ? long.MaxValue : TypeSystem.ToLong(Eval(top, null) ?? 0);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var rows = new List<object?[]>(rs.Rows.Count);
        foreach (var row in rs.Rows)
        {
            if (rows.Count >= limit) break;
            if (row[refIndex] == null || !seen.Add(row[refIndex]!)) continue;
            rows.Add(row);
        }
        return (table, new RowSet(rs.Schema, rows), refIndex);
    }

    /// <summary>
    /// If 'name' is a CTE or view defined as a plain SELECT over one table, returns the table reference inside
    /// that definition; data modifications through the CTE / view then apply to that table.
    /// </summary>
    TableRef? UpdatableBase(TableName name, QueryEnv? env)
    {
        Query? query = null;
        if (name.Schema == null && name.Database == null && env?.FindCte(name.Name, out _) is { } cte) query = cte.Def.Query;
        else if (!name.IsVariable && !name.IsTemp && FindObject(name) is View view) query = view.Query;
        if (query == null) return null;
        if (query.Body is not SelectSpec { From: TableRef inner, GroupingSets: null } spec || spec.Distinct || spec.GroupBy.Count > 0 || spec.HasAggregates == true)
            throw Err.New(4405, $"View or function '{name.Name}' is not updatable because the modification affects multiple base tables.");
        // a CTE or view may itself select from another one
        return UpdatableBase(inner.Name, query.Ctes != null ? new QueryEnv(env, query.Ctes) : env) ?? inner;
    }

    /// <summary>
    /// For a CTE or view whose select list renames columns: the mapping from its column names to the base
    /// table's (a null value marks a computed column). Returns null when names simply pass through.
    /// </summary>
    Dictionary<string, string?>? UpdatableColumns(TableName name, QueryEnv? env, out List<string>? ordered)
    {
        ordered = null;
        Query? query = null;
        List<string>? declared = null;
        if (name.Schema == null && name.Database == null && env?.FindCte(name.Name, out _) is { } cte) { query = cte.Def.Query; declared = cte.Def.Columns; }
        else if (!name.IsVariable && !name.IsTemp && FindObject(name) is View view) { query = view.Query; declared = view.Columns; }
        if (query?.Body is not SelectSpec spec || spec.Items.Exists(i => i.IsStar)) return null;
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        ordered = [];
        for (int i = 0; i < spec.Items.Count; i++)
        {
            var item = spec.Items[i];
            string? baseName = (item.E as ColumnRef)?.Name;
            string visible = declared != null && i < declared.Count ? declared[i] : item.Alias ?? baseName ?? "";
            map[visible] = baseName;
            ordered.Add(visible);
        }
        return map;
    }

    static string BaseColumn(Dictionary<string, string?> map, string column, TableName target)
    {
        if (!map.TryGetValue(column, out string? baseName)) throw Err.InvalidColumn(column);
        return baseName ?? throw Err.New(4406, $"Update or insert of view or function '{target.Name}' failed because it contains a derived or constant field.");
    }

    // ------------------------------------------------------------ UPDATE

    object?[] ApplySets(Table table, List<SetClause> sets, object?[] current, EvalContext ctx)
    {
        var nv = (object?[])current.Clone();
        foreach (var set in sets)
        {
            if (set.Column == null) continue;
            int ci = table.ColumnIndex(set.Column);
            if (ci < 0) throw Err.InvalidColumn(set.Column);
            var col = table.Columns[ci];
            if (col.Identity) throw Err.New(8102, $"Cannot update identity column '{col.Name}'.");
            if (col.RowVersion) throw Err.New(272, "Cannot update a timestamp column.");
            if (col.Computed != null)
                throw Err.New(271, $"The column \"{col.Name}\" cannot be modified because it is either a computed column or is the result of a UNION operator.");
            object? value = set.Value is DefaultExpr ? DefaultValue(table, col) : Eval(set.Value, ctx);
            if (set.Op != "=") value = TypeSystem.Arith(set.Op[..1], current[ci], value);
            nv[ci] = ConvertForStorage(value, table, col);
        }
        foreach (var set in sets)
        {
            if (set.Variable == null) continue;
            // "@v = column = expr" is parsed into a variable assignment that reads the new column value
            object? value = set.Value is ColumnRef { Table: "inserted" } c && table.ColumnIndex(c.Name) is >= 0 and var ix
                ? nv[ix]
                : Eval(set.Value, ctx);
            SetVar(set.Variable, set.Op, value);
        }
        // every modified row gets a fresh rowversion
        for (int c = 0; c < table.Columns.Count; c++)
            if (table.Columns[c].RowVersion) nv[c] = NextRowVersion(table);
        ComputeColumns(table, nv);
        return nv;
    }

    void ApplyUpdates(Table table, List<(object?[] Row, object?[] NewValues)> changes, out List<object?[]> oldValues)
    {
        foreach (var (_, nv) in changes) CheckNotNull(table, nv, "UPDATE");
        oldValues = table.UpdateRows(changes);
        for (int i = 0; i < changes.Count; i++)
            Log(new UndoEntry { Table = table, UpdatedRow = changes[i].Row, OldValues = oldValues[i] });
        for (int i = 0; i < changes.Count; i++) CheckConstraints(table, changes[i].Row, "UPDATE", oldValues[i]);
        CheckReferencedKeysUnchanged(table, changes, oldValues);
    }

    void ExecUpdate(UpdateStmt s)
    {
        var env = s.Ctes != null ? new QueryEnv(null, s.Ctes) : null;
        var (table, rs, refIndex) = FindTargetRows(s.Target, s.From, s.Where, s.Top, env);
        var ctx = new EvalContext(rs.Schema, null, env);
        var sets = s.Sets;
        if (UpdatableColumns(s.Target, env, out _) is { } viewMap)
            sets = sets.ConvertAll(set => set.Column == null ? set
                : new SetClause { Column = BaseColumn(viewMap, set.Column, s.Target), Variable = set.Variable, Op = set.Op, Value = set.Value });
        var changes = new List<(object?[] Row, object?[] NewValues)>(rs.Rows.Count);
        foreach (var m in rs.Rows)
        {
            ctx.Row = m;
            var target = (object?[])m[refIndex]!;
            changes.Add((target, ApplySets(table, sets, target, ctx)));
        }
        var updatedColumns = new HashSet<int>();
        foreach (var set in sets)
            if (set.Column != null) updatedColumns.Add(table.ColumnIndex(set.Column));
        if (InsteadOfTrigger(table, 'U') is { } instead)
        {
            RunTrigger(instead, table, changes.ConvertAll(c => c.NewValues), changes.ConvertAll(c => c.Row), updatedColumns);
            AddAffected(changes.Count);
            return;
        }
        ApplyUpdates(table, changes, out var olds);
        if (s.Output != null) EmitOutput(s.Output, table, changes.ConvertAll(c => c.Row), olds, null, rs.Schema, rs.Rows!);
        FireTriggers(table, 'U', changes.ConvertAll(c => c.Row), olds, updatedColumns);
        AddAffected(changes.Count);
    }

    // ------------------------------------------------------------ DELETE

    static RowKey KeyOfCols(object?[] row, int[] cols, out bool hasNull)
    {
        var k = new object[cols.Length];
        hasNull = false;
        for (int i = 0; i < cols.Length; i++)
        {
            if (row[cols[i]] == null) hasNull = true;
            k[i] = TypeSystem.NormalizeKey(row[cols[i]]);
        }
        return new RowKey(k);
    }

    IEnumerable<(Table Child, ForeignKey Fk)> ReferencingKeys(Table table)
    {
        if (table.IsVariable || table.IsTemp || table.Database == null) yield break;
        foreach (var child in table.Database.Tables)
            foreach (var fk in child.ForeignKeys)
                if (ReferenceEquals(fk.RefTable, table)) yield return (child, fk);
    }

    /// <summary>
    /// After an update of referenced key values: child rows follow (ON UPDATE CASCADE) or the statement fails
    /// if a child still references a key value that no longer exists.
    /// </summary>
    void CheckReferencedKeysUnchanged(Table table, List<(object?[] Row, object?[] NewValues)> changes, List<object?[]> oldValues)
    {
        foreach (var (child, fk) in ReferencingKeys(table).ToList())
        {
            Dictionary<RowKey, object?[]>? moved = null; // old key -> the parent row that now carries the new key
            for (int i = 0; i < changes.Count; i++)
            {
                var oldKey = KeyOfCols(oldValues[i], fk.RefCols, out bool n1);
                if (n1 || oldKey.Equals(KeyOfCols(changes[i].Row, fk.RefCols, out _))) continue;
                (moved ??= [])[oldKey] = changes[i].Row;
            }
            if (moved == null) continue;
            // a key that another updated row has taken over is still present
            if (!fk.UpdateCascade)
                foreach (var (row, _) in changes) moved.Remove(KeyOfCols(row, fk.RefCols, out _));
            if (moved.Count == 0) continue;
            var childChanges = new List<(object?[] Row, object?[] NewValues)>();
            foreach (var crow in child.Rows)
            {
                var ck = KeyOfCols(crow, fk.Cols, out bool cn);
                if (cn || !moved.TryGetValue(ck, out var parent)) continue;
                if (!fk.UpdateCascade)
                    throw Err.New(547, $"The UPDATE statement conflicted with the REFERENCE constraint \"{fk.Name}\". The conflict occurred in database \"{Db.Name}\", table \"{child.FullName}\", column '{child.Columns[fk.Cols[0]].Name}'.");
                var nv = (object?[])crow.Clone();
                for (int k = 0; k < fk.Cols.Length; k++) nv[fk.Cols[k]] = parent[fk.RefCols[k]];
                childChanges.Add((crow, nv));
            }
            if (childChanges.Count == 0) continue;
            ApplyUpdates(child, childChanges, out var childOld);
            FireTriggers(child, 'U', childChanges.ConvertAll(c => c.Row), childOld, [.. fk.Cols]);
        }
    }

    void DeleteRows(Table table, HashSet<object?[]> rows, int depth = 0)
    {
        if (rows.Count == 0) return;
        if (depth > 32) throw Err.New(217, "Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).");
        foreach (var (child, fk) in ReferencingKeys(table).ToList())
        {
            var keys = new HashSet<RowKey>();
            foreach (var row in rows)
            {
                var k = KeyOfCols(row, fk.RefCols, out bool hasNull);
                if (!hasNull) keys.Add(k);
            }
            var affected = new HashSet<object?[]>(ReferenceEqualityComparer.Instance);
            foreach (var crow in child.Rows)
            {
                if (ReferenceEquals(child, table) && rows.Contains(crow)) continue;
                var ck = KeyOfCols(crow, fk.Cols, out bool cn);
                if (!cn && keys.Contains(ck)) affected.Add(crow);
            }
            if (affected.Count == 0) continue;
            if (fk.Cascade)
            {
                DeleteRows(child, affected, depth + 1);
                FireTriggers(child, 'D', null, [.. affected], null);
            }
            else if (fk.SetNull)
            {
                var changes = new List<(object?[] Row, object?[] NewValues)>();
                foreach (var crow in affected)
                {
                    var nv = (object?[])crow.Clone();
                    foreach (int c in fk.Cols) nv[c] = null;
                    changes.Add((crow, nv));
                }
                ApplyUpdates(child, changes, out var childOld);
                FireTriggers(child, 'U', changes.ConvertAll(c => c.Row), childOld, [.. fk.Cols]);
            }
            else
                throw Err.New(547, $"The DELETE statement conflicted with the REFERENCE constraint \"{fk.Name}\". The conflict occurred in database \"{Db.Name}\", table \"{child.FullName}\", column '{child.Columns[fk.Cols[0]].Name}'.");
        }
        var old = table.DeleteRows(rows);
        Log(new UndoEntry { Table = table, OldRows = old });
    }

    void ExecDelete(DeleteStmt s)
    {
        var env = s.Ctes != null ? new QueryEnv(null, s.Ctes) : null;
        var (table, rs, refIndex) = FindTargetRows(s.Target, s.From, s.Where, s.Top, env);
        var rows = new HashSet<object?[]>(ReferenceEqualityComparer.Instance);
        var deleted = new List<object?[]>(rs.Rows.Count);
        foreach (var m in rs.Rows)
        {
            var target = (object?[])m[refIndex]!;
            rows.Add(target);
            deleted.Add(target);
        }
        if (InsteadOfTrigger(table, 'D') is { } instead)
        {
            RunTrigger(instead, table, null, deleted, null);
            AddAffected(deleted.Count);
            return;
        }
        DeleteRows(table, rows);
        if (s.Output != null) EmitOutput(s.Output, table, null, deleted, null, rs.Schema, rs.Rows!);
        FireTriggers(table, 'D', null, deleted, null);
        AddAffected(deleted.Count);
    }

    void ExecTruncate(TruncateStmt s)
    {
        var table = ResolveTable(s.Name);
        if (ReferencingKeys(table).Any(r => !ReferenceEquals(r.Child, table)))
            throw Err.New(4712, $"Cannot truncate table '{table.FullName}' because it is being referenced by a FOREIGN KEY constraint.");
        var old = table.Rows;
        long? lastIdentity = table.LastIdentity;
        table.Rows = [];
        table.RebuildIndexes();
        table.LastIdentity = null;
        Log(new UndoEntry { Table = table, OldRows = old });
        Log(new UndoEntry { Custom = () => table.LastIdentity = lastIdentity });
        _rowCount = 0;
    }

    // ------------------------------------------------------------ OUTPUT clause

    /// <summary>Evaluates an OUTPUT clause over parallel lists of inserted / deleted rows (either may be null).</summary>
    void EmitOutput(OutputClause o, Table table, List<object?[]>? inserted, List<object?[]>? deleted, List<string>? actions,
        RowSchema? extraSchema = null, List<object?[]?>? extraRows = null)
    {
        int n = table.Columns.Count;
        var cols = new List<ColInfo>(2 * n + 1);
        foreach (var c in table.Columns) cols.Add(new ColInfo("deleted", c.Name, c.Type));
        foreach (var c in table.Columns) cols.Add(new ColInfo("inserted", c.Name, c.Type));
        cols.Add(new ColInfo(null, "$action", SqlType.NVarChar(10)));
        int extraAt = cols.Count;
        if (extraSchema != null) cols.AddRange(extraSchema.Cols);
        var schema = new RowSchema(cols);
        int count = inserted?.Count ?? deleted!.Count;
        var ctx = new EvalContext(schema, null, null);

        var outCols = new List<ColInfo>();
        var proj = new List<(int Index, Expr? E)>();
        foreach (var item in o.Items)
        {
            if (item.IsStar)
            {
                if (item.StarTable == null) throw Err.New(102, "Incorrect syntax near '*'.", 15);
                bool any = false;
                for (int i = 0; i < schema.Count; i++)
                {
                    var c = schema.Cols[i];
                    if (c.Hidden || !string.Equals(c.Table, item.StarTable, StringComparison.OrdinalIgnoreCase)) continue;
                    outCols.Add(new ColInfo(null, c.Name, c.Type));
                    proj.Add((i, null));
                    any = true;
                }
                if (!any) throw Err.New(107, $"The column prefix '{item.StarTable}' does not match with a table name or alias name used in the query.", 15);
            }
            else
            {
                outCols.Add(new ColInfo(null, item.Alias ?? (item.E is ColumnRef cr ? cr.Name : ""), InferType(item.E!, ctx)));
                proj.Add((-1, item.E));
            }
        }

        var rows = new List<object?[]>(count);
        for (int r = 0; r < count; r++)
        {
            var row = new object?[cols.Count];
            if (deleted?[r] is { } d) Array.Copy(d, 0, row, 0, n);
            if (inserted?[r] is { } ins) Array.Copy(ins, 0, row, n, n);
            row[2 * n] = actions?[r] ?? (inserted == null ? "DELETE" : deleted == null ? "INSERT" : "UPDATE");
            if (extraRows?[r] is { } extra) Array.Copy(extra, 0, row, extraAt, extra.Length);
            ctx.Row = row;
            var o2 = new object?[proj.Count];
            for (int j = 0; j < o2.Length; j++) o2[j] = proj[j].Index >= 0 ? row[proj[j].Index] : Eval(proj[j].E!, ctx);
            rows.Add(o2);
        }
        for (int j = 0; j < outCols.Count; j++)
        {
            if (outCols[j].Type != null) continue;
            foreach (var row in rows)
                if (row[j] != null) { outCols[j].Type = TypeSystem.TypeOf(row[j]!); break; }
        }

        if (o.Into != null)
        {
            var target = ResolveTable(o.Into);
            var map = InsertMap(target, o.IntoColumns);
            if (map.Length != outCols.Count) throw Err.New(213, "Column name or number of supplied values does not match table definition.");
            InsertRows(target, map, rows);
        }
        else _sink.ResultSets.Add(ToResultSet(new RowSet(new RowSchema(outCols), rows)));
    }

    // ------------------------------------------------------------ MERGE

    void ExecMerge(MergeStmt s)
    {
        var env = s.Ctes != null ? new QueryEnv(null, s.Ctes) : null;
        var targetRef = new TableRef { Name = s.Target, Alias = s.TargetAlias };
        var table = ResolveTable(s.Target);
        RowSet target;
        var saved = _rowRefTarget;
        _rowRefTarget = targetRef;
        try { target = ExecTableRef(targetRef, env); }
        finally { _rowRefTarget = saved; }
        int refIndex = target.Schema.HiddenIndex();

        // the source gets a hidden marker column so that "no source row" can be told apart from NULL values
        var src = ExecSource(s.Source, null, env);
        var srcCols = new List<ColInfo>(src.Schema.Cols) { new(null, "$present", null) { Hidden = true } };
        var srcRows = new List<object?[]>(src.Rows.Count);
        foreach (var row in src.Rows)
        {
            var r = new object?[row.Length + 1];
            Array.Copy(row, r, row.Length);
            r[row.Length] = True;
            srcRows.Add(r);
        }
        var source = new RowSet(new RowSchema(srcCols), srcRows);
        int presentIndex = target.Schema.Count + src.Schema.Count;

        var joined = JoinRowSets(target, source, JoinKind.Full, s.On, null, env);
        var ctx = new EvalContext(joined.Schema, null, env);

        var updates = new List<(object?[] Row, object?[] NewValues)>();
        var updateExtra = new List<object?[]?>();
        var deletes = new List<object?[]>();
        var deleteExtra = new List<object?[]?>();
        var insertSources = new List<(MergeClause Clause, object?[] Joined)>();
        var touched = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var ops = new List<(MergeAction Action, int Index)>(); // in source order, for OUTPUT

        foreach (var row in joined.Rows)
        {
            bool hasTarget = row[refIndex] != null, hasSource = row[presentIndex] != null;
            var match = hasTarget && hasSource ? MergeMatch.Matched : hasSource ? MergeMatch.NotMatchedByTarget : MergeMatch.NotMatchedBySource;
            ctx.Row = row;
            foreach (var clause in s.Clauses)
            {
                if (clause.Match != match) continue;
                if (clause.Condition != null && !IsTrue(Eval(clause.Condition, ctx))) continue;
                switch (clause.Action)
                {
                    case MergeAction.Insert:
                        ops.Add((MergeAction.Insert, insertSources.Count));
                        insertSources.Add((clause, row));
                        break;
                    case MergeAction.Update:
                    case MergeAction.Delete:
                    {
                        var stored = (object?[])row[refIndex]!;
                        if (!touched.Add(stored))
                            throw Err.New(8672, "The MERGE statement attempted to UPDATE or DELETE the same row more than once. This happens when a target row matches more than one source row. A MERGE statement cannot UPDATE/DELETE the same row of the target table multiple times. Refine the ON clause to ensure a target row matches at most one source row, or use the GROUP BY clause to group the source rows.");
                        ops.Add((clause.Action, clause.Action == MergeAction.Update ? updates.Count : deletes.Count));
                        if (clause.Action == MergeAction.Update)
                        {
                            updates.Add((stored, ApplySets(table, clause.Sets!, stored, ctx)));
                            updateExtra.Add(row);
                        }
                        else
                        {
                            deletes.Add(stored);
                            deleteExtra.Add(row);
                        }
                        break;
                    }
                }
                break; // only the first applicable clause fires
            }
        }

        var deleteSet = new HashSet<object?[]>(ReferenceEqualityComparer.Instance);
        foreach (var d in deletes) deleteSet.Add(d);
        DeleteRows(table, deleteSet);
        ApplyUpdates(table, updates, out var olds);

        var inserted = new List<object?[]>();
        var insertExtra = new List<object?[]?>();
        foreach (var (clause, row) in insertSources)
        {
            ctx.Row = row;
            int[] map = clause.InsertDefaultValues ? [] : InsertMap(table, clause.InsertColumns);
            object?[] values = [];
            if (!clause.InsertDefaultValues)
            {
                if (clause.InsertValues!.Count != map.Length) throw Err.New(213, "Column name or number of supplied values does not match table definition.");
                values = new object?[map.Length];
                for (int i = 0; i < values.Length; i++) values[i] = Eval(clause.InsertValues[i], ctx);
            }
            inserted.AddRange(InsertRows(table, map, [values]));
            insertExtra.Add(row);
        }

        if (table.Triggers.Count > 0)
        {
            // one firing per action kind that the statement specifies, as SQL Server does
            if (s.Clauses.Exists(c => c.Action == MergeAction.Insert)) FireTriggers(table, 'I', inserted, null, null);
            if (s.Clauses.Exists(c => c.Action == MergeAction.Update))
            {
                var cols = new HashSet<int>();
                foreach (var c in s.Clauses)
                    if (c.Sets != null)
                        foreach (var set in c.Sets)
                            if (set.Column != null) cols.Add(table.ColumnIndex(set.Column));
                FireTriggers(table, 'U', updates.ConvertAll(u => u.Row), olds, cols);
            }
            if (s.Clauses.Exists(c => c.Action == MergeAction.Delete)) FireTriggers(table, 'D', null, deletes, null);
        }

        if (s.Output != null)
        {
            var ins = new List<object?[]>();
            var del = new List<object?[]>();
            var actions = new List<string>();
            var extra = new List<object?[]?>();
            foreach (var (action, i) in ops)
            {
                switch (action)
                {
                    case MergeAction.Insert: ins.Add(inserted[i]); del.Add(null!); actions.Add("INSERT"); extra.Add(insertExtra[i]); break;
                    case MergeAction.Update: ins.Add(updates[i].Row); del.Add(olds[i]); actions.Add("UPDATE"); extra.Add(updateExtra[i]); break;
                    default: ins.Add(null!); del.Add(deletes[i]); actions.Add("DELETE"); extra.Add(deleteExtra[i]); break;
                }
            }
            EmitOutput(s.Output, table, ins, del, actions, joined.Schema, extra);
        }
        AddAffected(inserted.Count + updates.Count + deletes.Count);
    }
}
