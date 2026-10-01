using System.Runtime.CompilerServices;
using LocalDbSharp.Execution;
using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

public sealed partial class Session
{
    int _maxRecursion = 100;
    SelectSpec? _topSpec;
    bool _carryRowRef;

    // ------------------------------------------------------------ SELECT statement

    void ExecSelectStatement(Query q)
    {
        if (q.Body is SelectSpec { Into: not null } into)
        {
            Atomic(() => ExecSelectInto(q, into));
            return;
        }
        bool assign = q.Body is SelectSpec sp && sp.Items.Exists(i => i.Variable != null);
        RowSet rs;
        var savedTop = _topSpec;
        _topSpec = q.Body as SelectSpec;
        try
        {
            rs = ExecQuery(q, null, null);
        }
        catch (TSqlException ex) when (ex.PartialColumns != null)
        {
            if (!assign && _tryDepth > 0) _sink.ResultSets.Add(new ResultSet(ex.PartialColumns, []));
            ex.PartialColumns = null;
            throw;
        }
        finally
        {
            _topSpec = savedTop;
        }
        if (_rowCountLimit > 0 && rs.Rows.Count > _rowCountLimit) rs = new RowSet(rs.Schema, rs.Rows.GetRange(0, _rowCountLimit)); // SET ROWCOUNT
        _rowCount = rs.Rows.Count;
        if (!assign) _sink.ResultSets.Add(ToResultSet(rs));
    }

    internal static ResultSet ToResultSet(RowSet rs)
    {
        var cols = new ResultColumn[rs.Schema.Count];
        for (int j = 0; j < cols.Length; j++)
        {
            var c = rs.Schema.Cols[j];
            var type = c.Type ?? SqlType.Int;
            cols[j] = new ResultColumn(c.Name, type);
            if (type.Kind == SqlTypeKind.Variant) continue;
            // make sure every value in the column has the CLR type promised by the metadata
            var clr = type.ClrType;
            foreach (var row in rs.Rows)
            {
                object? v = row[j];
                if (v == null || v.GetType() == clr) continue;
                try { row[j] = TypeSystem.ConvertTo(v, type); }
                catch (TSqlException) { }
            }
        }
        return new ResultSet(cols, rs.Rows);
    }

    // ------------------------------------------------------------ query expressions

    internal RowSet ExecQuery(Query q, EvalContext? outer, QueryEnv? env, int limit = int.MaxValue)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        int savedRecursion = _maxRecursion;
        if (q.MaxRecursion != null) _maxRecursion = q.MaxRecursion.Value;
        try
        {
            if (q.Ctes != null) env = new QueryEnv(env, q.Ctes);
            if (q.ForJson != null) return ForJson(q, ExecQueryCore(q, outer, env, int.MaxValue));
            return ExecQueryCore(q, outer, env, limit);
        }
        finally
        {
            _maxRecursion = savedRecursion;
        }
    }

    RowSet ExecQueryCore(Query q, EvalContext? outer, QueryEnv? env, int limit)
    {
        {
            if (q.Body is SelectSpec spec) return ExecSpec(spec, outer, env, q.OrderBy, q.Offset, q.Fetch, limit);
            var rs = ExecQueryExpr(q.Body, outer, env);
            if (q.OrderBy != null)
            {
                var ctx = new EvalContext(rs.Schema, outer, env);
                var keys = new object?[rs.Rows.Count][];
                var keyTypes = new SqlType?[q.OrderBy.Count];
                for (int m = 0; m < keyTypes.Length; m++)
                    keyTypes[m] = q.OrderBy[m].E is Literal { Value: int n } && n >= 1 && n <= rs.Schema.Count ? rs.Schema.Cols[n - 1].Type : InferType(q.OrderBy[m].E, ctx);
                for (int i = 0; i < keys.Length; i++)
                {
                    ctx.Row = rs.Rows[i];
                    var k = new object?[q.OrderBy.Count];
                    for (int m = 0; m < k.Length; m++)
                    {
                        var e = q.OrderBy[m].E;
                        if (e is Literal { Value: int n })
                        {
                            if (n < 1 || n > rs.Schema.Count) throw Err.New(108, $"The ORDER BY position number {n} is out of range of the number of items in the select list.");
                            k[m] = rs.Rows[i][n - 1];
                        }
                        else k[m] = Eval(e, ctx);
                    }
                    keys[i] = k;
                }
                var idx = SortIndexes(keys, q.OrderBy, UnicodeFlags(keyTypes));
                var sorted = new List<object?[]>(idx.Length);
                foreach (int i in idx) sorted.Add(rs.Rows[i]);
                rs = new RowSet(rs.Schema, sorted);
            }
            if (q.Offset != null) rs = new RowSet(rs.Schema, OffsetFetch(rs.Rows, q.Offset, q.Fetch, Detached(outer, env)));
            return rs;
        }
    }

    /// <summary>
    /// Context for expressions of a query that are not evaluated per source row (TOP, OFFSET, VALUES rows,
    /// function arguments). Going through a context of its own makes a reference to the enclosing query
    /// register as correlation, so that the subquery's result is not cached across outer rows.
    /// </summary>
    static EvalContext? Detached(EvalContext? outer, QueryEnv? env) => outer == null ? null : new EvalContext(null, outer, env);

    List<object?[]> OffsetFetch(List<object?[]> rows, Expr offset, Expr? fetch, EvalContext? outer)
    {
        long off = TypeSystem.ToLong(Eval(offset, outer) ?? 0);
        if (off < 0) throw Err.New(10742, "The offset specified in a OFFSET clause may not be negative.", 15);
        long count = long.MaxValue;
        if (fetch != null)
        {
            count = TypeSystem.ToLong(Eval(fetch, outer) ?? 0);
            if (count < 1) throw Err.New(10744, "The number of rows provided for a FETCH clause must be greater then zero.", 15);
        }
        if (off >= rows.Count) return [];
        return rows.GetRange((int)off, (int)Math.Min(count, rows.Count - off));
    }

    RowSet ExecQueryExpr(QueryExpr body, EvalContext? outer, QueryEnv? env)
    {
        switch (body)
        {
            case SelectSpec spec: return ExecSpec(spec, outer, env, null, null, null, int.MaxValue);
            case NestedQuery nq: return ExecQuery(nq.Q, outer, env);
            case SetOp so:
            {
                var l = ExecQueryExpr(so.Left, outer, env);
                var r = ExecQueryExpr(so.Right, outer, env);
                return CombineSets(so.Op, so.All, l, r);
            }
            default: throw Err.NotSupported(body.GetType().Name);
        }
    }

    static RowKey RowKeyOf(object?[] row)
    {
        var k = new object[row.Length];
        for (int i = 0; i < row.Length; i++) k[i] = TypeSystem.NormalizeKey(row[i]);
        return new RowKey(k);
    }

    static List<object?[]> Coerce(RowSet rs, SqlType?[] target)
    {
        List<int>? convert = null;
        for (int j = 0; j < target.Length; j++)
        {
            var from = rs.Schema.Cols[j].Type;
            var to = target[j];
            if (to == null || from == null || from == to) continue;
            if (from.IsString && to.IsString && to.Kind is SqlTypeKind.VarChar or SqlTypeKind.NVarChar) continue;
            (convert ??= []).Add(j);
        }
        if (convert == null) return rs.Rows;
        var rows = new List<object?[]>(rs.Rows.Count);
        foreach (var row in rs.Rows)
        {
            var copy = (object?[])row.Clone();
            foreach (int j in convert) copy[j] = TypeSystem.ConvertTo(copy[j], target[j]!);
            rows.Add(copy);
        }
        return rows;
    }

    static RowSet CombineSets(string op, bool all, RowSet l, RowSet r)
    {
        if (l.Schema.Count != r.Schema.Count)
            throw Err.New(205, "All queries combined using a UNION, INTERSECT or EXCEPT operator must have an equal number of expressions in their target lists.");
        int n = l.Schema.Count;
        var types = new SqlType?[n];
        var cols = new List<ColInfo>(n);
        for (int j = 0; j < n; j++)
        {
            types[j] = Unify(l.Schema.Cols[j].Type, r.Schema.Cols[j].Type);
            cols.Add(new ColInfo(null, l.Schema.Cols[j].Name, types[j]));
        }
        var lrows = Coerce(l, types);
        var rrows = Coerce(r, types);
        var result = new List<object?[]>();
        switch (op)
        {
            case "UNION":
                if (all)
                {
                    result.AddRange(lrows);
                    result.AddRange(rrows);
                }
                else
                {
                    var seen = new HashSet<RowKey>();
                    foreach (var row in lrows) if (seen.Add(RowKeyOf(row))) result.Add(row);
                    foreach (var row in rrows) if (seen.Add(RowKeyOf(row))) result.Add(row);
                }
                break;
            case "INTERSECT":
            case "EXCEPT":
            {
                var right = new HashSet<RowKey>();
                foreach (var row in rrows) right.Add(RowKeyOf(row));
                var seen = new HashSet<RowKey>();
                bool keepIfPresent = op == "INTERSECT";
                foreach (var row in lrows)
                {
                    var k = RowKeyOf(row);
                    if (right.Contains(k) == keepIfPresent && seen.Add(k)) result.Add(row);
                }
                break;
            }
        }
        return new RowSet(new RowSchema(cols), result);
    }

    // ------------------------------------------------------------ expression analysis helpers

    static void Walk(Expr? e, Func<Expr, bool> visit)
    {
        if (e == null || !visit(e)) return;
        switch (e)
        {
            case Unary u: Walk(u.E, visit); break;
            case Binary b:
            {
                // iterate down the left spine so that very long AND / OR / + chains do not exhaust the stack
                var pending = new List<Expr> { b.R };
                Expr left = b.L;
                while (left is Binary lb && visit(lb))
                {
                    pending.Add(lb.R);
                    left = lb.L;
                }
                if (left is not Binary) Walk(left, visit);
                for (int i = pending.Count - 1; i >= 0; i--) Walk(pending[i], visit);
                break;
            }
            case IsNullExpr n: Walk(n.E, visit); break;
            case BetweenExpr b: Walk(b.E, visit); Walk(b.Lo, visit); Walk(b.Hi, visit); break;
            case InExpr i:
                Walk(i.E, visit);
                if (i.List != null) foreach (var x in i.List) Walk(x, visit);
                break;
            case LikeExpr l: Walk(l.E, visit); Walk(l.Pattern, visit); Walk(l.Escape, visit); break;
            case QuantifiedExpr q: Walk(q.E, visit); break;
            case CaseExpr c:
                Walk(c.Operand, visit);
                foreach (var (w, t) in c.Whens) { Walk(w, visit); Walk(t, visit); }
                Walk(c.Else, visit);
                break;
            case CastExpr c: Walk(c.E, visit); Walk(c.Style, visit); break;
            case FuncCall f:
                foreach (var a in f.Args) Walk(a, visit);
                if (f.Over != null)
                {
                    foreach (var p in f.Over.Partition) Walk(p, visit);
                    foreach (var o in f.Over.Order) Walk(o.E, visit);
                }
                if (f.WithinGroup != null) foreach (var o in f.WithinGroup) Walk(o.E, visit);
                break;
        }
    }

    static bool IsAggregateCall(Expr e) => e is FuncCall { Over: null, Schema: null } f && Aggregates.IsAggregate(f.Name);

    static bool ContainsAggregate(Expr? e)
    {
        bool found = false;
        Walk(e, x =>
        {
            if (IsAggregateCall(x)) found = true;
            return !found;
        });
        return found;
    }

    static bool ContainsSubquery(Expr? e)
    {
        bool found = false;
        Walk(e, x =>
        {
            if (x is SubqueryExpr or ExistsExpr or QuantifiedExpr or InExpr { Sub: not null }) found = true;
            return !found;
        });
        return found;
    }

    static void CollectWindows(Expr? e, List<FuncCall> list)
    {
        Walk(e, x =>
        {
            if (x is FuncCall { Over: not null } f)
            {
                list.Add(f);
                return false;
            }
            return true;
        });
    }

    static void SplitAnd(Expr e, List<Expr> list)
    {
        int start = list.Count;
        while (e is Binary { Op: "AND" } b)
        {
            if (b.R is Binary { Op: "AND" }) SplitAnd(b.R, list);
            else list.Add(b.R);
            e = b.L;
        }
        list.Add(e);
        list.Reverse(start, list.Count - start);
    }

    /// <summary>True if the expression references at least one column, and all its columns belong to 'mine' and not 'other'.</summary>
    static bool RefsOnly(Expr e, RowSchema mine, RowSchema other)
    {
        bool ok = true, any = false;
        Walk(e, x =>
        {
            switch (x)
            {
                case ColumnRef c:
                    any = true;
                    if (mine.TryResolve(c.Table, c.Name) < 0 || other.TryResolve(c.Table, c.Name) != -1) ok = false;
                    break;
                case SubqueryExpr or ExistsExpr or QuantifiedExpr or InExpr { Sub: not null }:
                    ok = false;
                    break;
                case FuncCall f when f.Over != null || IsAggregateCall(f):
                    ok = false;
                    break;
            }
            return ok;
        });
        return ok && any;
    }

    /// <summary>True if every column reference in the expression resolves in the given schema (and there are no subqueries).</summary>
    static bool ResolvableIn(Expr e, RowSchema schema)
    {
        bool ok = true;
        Walk(e, x =>
        {
            if (x is ColumnRef c && schema.TryResolve(c.Table, c.Name) < 0) ok = false;
            else if (x is SubqueryExpr or ExistsExpr or QuantifiedExpr or InExpr { Sub: not null }) ok = false;
            else if (x is FuncCall f && (f.Over != null || IsAggregateCall(f))) ok = false;
            return ok;
        });
        return ok;
    }

    // ------------------------------------------------------------ sorting

    /// <summary>'unicode' flags the keys that hold nvarchar data, which sorts by Unicode rules.</summary>
    static int CompareKeys(object?[] a, object?[] b, List<OrderItem> order, bool[]? unicode = null)
    {
        for (int i = 0; i < a.Length; i++)
        {
            object? x = a[i], y = b[i];
            int c = x == null ? (y == null ? 0 : -1) : y == null ? 1
                : unicode != null && unicode[i] ? TypeSystem.CompareForSort(x, y, true) : TypeSystem.Compare(x, y);
            if (c != 0) return order[i].Desc ? -c : c;
        }
        return 0;
    }

    static int[] SortIndexes(object?[][] keys, List<OrderItem> order, bool[]? unicode = null)
    {
        var idx = new int[keys.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (i, j) =>
        {
            int c = CompareKeys(keys[i], keys[j], order, unicode);
            return c != 0 ? c : i.CompareTo(j);
        });
        return idx;
    }

    static bool[]? UnicodeFlags(SqlType?[] types)
    {
        bool any = false;
        var flags = new bool[types.Length];
        for (int i = 0; i < types.Length; i++) any |= flags[i] = types[i]?.IsUnicode == true;
        return any ? flags : null;
    }

    // ------------------------------------------------------------ SELECT specification

    RowSet ExecFromWhere(TableSource? from, Expr? where, EvalContext? outer, QueryEnv? env, int limit, out EvalContext ctx)
    {
        RowSet src;
        if (from == null) src = new RowSet(RowSchema.Empty, [[]]);
        else if (where != null && from is TableRef tr && TryLookup(tr, where, outer, env, out var looked)) src = looked;
        else if (where != null && from is JoinSource { Kind: JoinKind.Cross } cross)
        {
            var conjuncts = new List<Expr>();
            SplitAnd(where, conjuncts);
            src = ExecCrossWithPredicates(cross, conjuncts, outer, env);
        }
        else src = ExecSource(from, outer, env);
        ctx = new EvalContext(src.Schema, outer, env);
        if (where == null) return src;
        var rows = new List<object?[]>();
        foreach (var row in src.Rows)
        {
            ctx.Row = row;
            if (!IsTrue(Eval(where, ctx))) continue;
            rows.Add(row);
            if (rows.Count >= limit) break;
        }
        return new RowSet(src.Schema, rows);
    }

    RowSet ExecSpec(SelectSpec s, EvalContext? outer, QueryEnv? env, List<OrderItem>? orderBy, Expr? offset, Expr? fetch, int limit)
    {
        if (s.HasAggregates == null)
        {
            s.HasAggregates = s.GroupBy.Count > 0 || s.Having != null || s.Items.Exists(i => ContainsAggregate(i.E));
            var w = new List<FuncCall>();
            foreach (var item in s.Items) CollectWindows(item.E, w);
            s.Windows = w;
        }
        bool agg = s.HasAggregates.Value;
        var windows = s.Windows!;
        if (orderBy != null)
        {
            foreach (var o in orderBy)
            {
                if (ContainsAggregate(o.E)) agg = true;
                List<FuncCall>? extra = null;
                CollectWindows(o.E, extra ??= []);
                if (extra.Count > 0) windows = [.. windows, .. extra];
            }
        }

        long top = long.MaxValue;
        if (s.Top != null)
        {
            object? tv = Eval(s.Top, Detached(outer, env));
            if (tv == null) throw Err.New(1060, "The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.", 15);
            if (!s.TopPercent)
            {
                top = TypeSystem.ToLong(tv);
                if (top < 0) throw Err.New(1028, "A TOP or FETCH clause contains an invalid value.", 15);
            }
        }
        bool simple = !agg && windows.Count == 0 && orderBy == null && !s.Distinct && !s.TopPercent;
        int scanLimit = simple ? (int)Math.Min(Math.Min(top, limit), int.MaxValue) : int.MaxValue;

        var src = ExecFromWhere(s.From, s.Where, outer, env, scanLimit, out var ctx);
        List<object?[]> itemRows = src.Rows;
        List<List<object?[]>>? itemGroups = null;

        if (agg)
        {
            itemRows = [];
            itemGroups = [];
            if (s.GroupingSets != null) GroupBySets(s, orderBy, src, ctx, itemRows, itemGroups);
            else if (s.GroupBy.Count == 0)
            {
                itemRows.Add(src.Rows.Count > 0 ? src.Rows[0] : null!);
                itemGroups.Add(src.Rows);
            }
            else
            {
                var map = new Dictionary<RowKey, List<object?[]>>();
                int gn = s.GroupBy.Count;
                foreach (var row in src.Rows)
                {
                    ctx.Row = row;
                    var k = new object[gn];
                    for (int g = 0; g < gn; g++) k[g] = TypeSystem.NormalizeKey(Eval(s.GroupBy[g], ctx));
                    var key = new RowKey(k);
                    if (!map.TryGetValue(key, out var list))
                    {
                        map[key] = list = [];
                        itemRows.Add(row);
                        itemGroups.Add(list);
                    }
                    list.Add(row);
                }
            }
            if (s.Having != null)
            {
                var keptRows = new List<object?[]>();
                var keptGroups = new List<List<object?[]>>();
                var keptNulled = ctx.ItemNulled != null ? new List<HashSet<Expr>?>() : null;
                var keptOut = ctx.ItemGroupedOut != null ? new List<HashSet<string>?>() : null;
                for (int i = 0; i < itemRows.Count; i++)
                {
                    ctx.Row = itemRows[i];
                    ctx.Group = itemGroups[i];
                    ctx.ItemIndex = i;
                    if (!IsTrue(Eval(s.Having, ctx))) continue;
                    keptRows.Add(itemRows[i]);
                    keptGroups.Add(itemGroups[i]);
                    keptNulled?.Add(ctx.ItemNulled![i]);
                    keptOut?.Add(ctx.ItemGroupedOut![i]);
                }
                itemRows = keptRows;
                itemGroups = keptGroups;
                if (keptNulled != null) { ctx.ItemNulled = keptNulled; ctx.ItemGroupedOut = keptOut; }
            }
        }

        if (windows.Count > 0)
        {
            var values = new Dictionary<Expr, object?[]>();
            foreach (var f in windows) values[f] = ComputeWindow(f, itemRows, itemGroups, ctx);
            ctx.WindowValues = values;
        }

        // ---- output columns
        var outCols = new List<ColInfo>();
        var proj = new List<(int Index, SelectItem Item)>();
        foreach (var item in s.Items)
        {
            if (item.IsStar)
            {
                bool any = false;
                for (int i = 0; i < src.Schema.Count; i++)
                {
                    var c = src.Schema.Cols[i];
                    if (c.Hidden) continue;
                    if (item.StarTable != null && !string.Equals(c.Table, item.StarTable, StringComparison.OrdinalIgnoreCase)) continue;
                    any = true;
                    outCols.Add(new ColInfo(null, c.Name, c.Type));
                    proj.Add((i, item));
                }
                if (!any && item.StarTable != null)
                    throw Err.New(107, $"The column prefix '{item.StarTable}' does not match with a table name or alias name used in the query.", 15);
                if (!any && s.From == null) throw Err.New(263, "Must specify table to select from.");
            }
            else
            {
                string name = item.Alias ?? (item.E is ColumnRef cr ? cr.Name : "");
                ctx.Row = itemRows.Count > 0 ? itemRows[0] : null;
                outCols.Add(new ColInfo(null, name, InferType(item.E!, ctx)));
                proj.Add((-1, item));
            }
        }
        bool assign = s.Items.Exists(i => i.Variable != null);
        if (assign && s.Items.Exists(i => i.Variable == null))
            throw Err.New(141, "A SELECT statement that assigns a value to a variable must not be combined with data-retrieval operations.", 15);
        if (_carryRowRef && !agg && !s.Distinct && src.Schema.HiddenIndex() is >= 0 and var hiddenIndex)
        {
            // an UPDATE / DELETE through a CTE or view needs to get back to the stored rows
            outCols.Add(new ColInfo(null, RowSchema.RowRefName, null) { Hidden = true });
            proj.Add((hiddenIndex, new SelectItem { IsStar = true }));
        }

        // ---- ORDER BY binding: ordinal, output alias, or source expression
        int[]? ordOut = null;
        if (orderBy != null)
        {
            ordOut = new int[orderBy.Count];
            for (int k = 0; k < ordOut.Length; k++)
            {
                ordOut[k] = -1;
                var e = orderBy[k].E;
                if (e is Literal { Value: int n })
                {
                    if (n < 1 || n > outCols.Count) throw Err.New(108, $"The ORDER BY position number {n} is out of range of the number of items in the select list.", 15);
                    ordOut[k] = n - 1;
                }
                else if (e is ColumnRef { Table: null } c)
                {
                    for (int j = 0; j < outCols.Count; j++)
                    {
                        if (!outCols[j].Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        // prefer an explicit alias; a pass-through column is equivalent either way
                        if (proj[j].Item.Alias != null || proj[j].Index >= 0 || proj[j].Item.E is ColumnRef) { ordOut[k] = j; break; }
                    }
                }
            }
        }

        // ---- projection
        bool isTop = ReferenceEquals(s, _topSpec);
        if (isTop) _topSpec = null;
        try
        {
            return Project();
        }
        catch (TSqlException ex) when (isTop && ex.PartialColumns == null)
        {
            ex.PartialColumns = outCols.ConvertAll(c => new ResultColumn(c.Name, c.Type ?? SqlType.Int));
            throw;
        }

        RowSet Project()
        {
        bool[]? unicodeKeys = null;
        if (orderBy != null)
        {
            var keyTypes = new SqlType?[orderBy.Count];
            for (int m = 0; m < keyTypes.Length; m++) keyTypes[m] = ordOut![m] >= 0 ? outCols[ordOut[m]].Type : InferType(orderBy[m].E, ctx);
            unicodeKeys = UnicodeFlags(keyTypes);
        }
        if (assign && orderBy != null)
        {
            // variable assignment happens row by row in ORDER BY order, so sort the source rows first
            var sourceKeys = new object?[itemRows.Count][];
            for (int i = 0; i < sourceKeys.Length; i++)
            {
                ctx.Row = itemRows[i];
                ctx.Group = itemGroups?[i];
                ctx.ItemIndex = i;
                var k = new object?[orderBy.Count];
                for (int m = 0; m < k.Length; m++)
                {
                    var keyExpr = ordOut![m] >= 0 ? proj[ordOut[m]].Item.E : orderBy[m].E;
                    k[m] = ordOut[m] >= 0 && proj[ordOut[m]].Index >= 0 ? ctx.Row?[proj[ordOut[m]].Index] : Eval(keyExpr!, ctx);
                }
                sourceKeys[i] = k;
            }
            var visit = SortIndexes(sourceKeys, orderBy, unicodeKeys);
            var sortedRows = new List<object?[]>(visit.Length);
            var sortedGroups = itemGroups != null ? new List<List<object?[]>>(visit.Length) : null;
            foreach (int i in visit)
            {
                sortedRows.Add(itemRows[i]);
                sortedGroups?.Add(itemGroups![i]);
            }
            itemRows = sortedRows;
            itemGroups = sortedGroups;
            orderBy = null;
        }
        if (assign)
        {
            // only the rows that TOP / OFFSET-FETCH let through take part in the assignment
            if (offset != null)
            {
                int before = itemRows.Count;
                var kept = OffsetFetch(itemRows, offset, fetch, Detached(outer, env));
                int skip = Math.Min((int)Math.Min(TypeSystem.ToLong(Eval(offset, Detached(outer, env)) ?? 0), before), before);
                itemGroups = itemGroups?.GetRange(skip, kept.Count);
                itemRows = kept;
                offset = null;
            }
            if (s.Top != null && !s.TopPercent && top < itemRows.Count)
            {
                itemRows = itemRows.GetRange(0, (int)top);
                itemGroups = itemGroups?.GetRange(0, (int)top);
            }
        }
        int total = itemRows.Count;
        int take = simple ? (int)Math.Min(total, Math.Min(top, limit)) : total;
        var outRows = new List<object?[]>(take);
        object?[][]? keys = orderBy != null ? new object?[take][] : null;
        int width = proj.Count;
        for (int i = 0; i < take; i++)
        {
            ctx.Row = itemRows[i];
            ctx.Group = itemGroups?[i];
            ctx.ItemIndex = i;
            var o = new object?[width];
            for (int j = 0; j < width; j++)
            {
                var (index, item) = proj[j];
                if (index >= 0) o[j] = ctx.Row?[index];
                else
                {
                    o[j] = Eval(item.E!, ctx);
                    if (item.Variable != null)
                    {
                        if (o[j] is DateTime or DateTimeOffset or TimeSpan or decimal) o[j] = AdaptImplicit(o[j], InferType(item.E!, ctx), FindVar(item.Variable).Type);
                        SetVar(item.Variable, item.VarOp, o[j]);
                    }
                }
            }
            outRows.Add(o);
            if (keys != null)
            {
                var k = new object?[orderBy!.Count];
                for (int m = 0; m < k.Length; m++) k[m] = ordOut![m] >= 0 ? o[ordOut[m]] : Eval(orderBy[m].E, ctx);
                keys[i] = k;
            }
        }

        // ---- fill in column types that could not be inferred statically
        for (int j = 0; j < outCols.Count; j++)
        {
            if (outCols[j].Type != null) continue;
            if (proj[j].Item.E is SubqueryExpr sq)
            {
                if (!_typeCache.TryGetValue(sq, out var subType) || subType == null)
                {
                    // the subquery never ran (no rows): run it once against an all-NULL row to learn its type
                    try
                    {
                        ctx.Row = new object?[src.Schema.Count];
                        ctx.Group = null;
                        subType = ExecSubquery(sq.Q, ctx, 1).Schema.Cols[0].Type;
                    }
                    catch (TSqlException) { }
                }
                if (subType != null)
                {
                    outCols[j].Type = subType;
                    continue;
                }
            }
            SqlType? seen = null;
            Type? lastClr = null;
            int lastScale = -1;
            foreach (var row in outRows)
            {
                object? v = row[j];
                if (v == null) continue;
                if (v.GetType() == lastClr && (v is not decimal dv || dv.Scale == lastScale) && v is not string) continue;
                lastClr = v.GetType();
                lastScale = v is decimal d2 ? d2.Scale : -1;
                seen = Unify(seen, TypeSystem.TypeOf(v));
            }
            if (seen is { Kind: SqlTypeKind.Decimal }) seen = SqlType.Decimal(38, seen.Scale);
            outCols[j].Type = seen;
            if (seen != null)
                foreach (var row in outRows) row[j] = CoerceTo(row[j], seen);
        }

        if (s.Distinct)
        {
            var seen = new HashSet<RowKey>();
            var rows2 = new List<object?[]>();
            var keys2 = keys != null ? new List<object?[]>() : null;
            for (int i = 0; i < outRows.Count; i++)
            {
                if (!seen.Add(RowKeyOf(outRows[i]))) continue;
                rows2.Add(outRows[i]);
                keys2?.Add(keys![i]);
            }
            outRows = rows2;
            keys = keys2?.ToArray();
        }

        if (orderBy != null)
        {
            var idx = SortIndexes(keys!, orderBy, unicodeKeys);
            var sorted = new List<object?[]>(idx.Length);
            var sortedKeys = new object?[idx.Length][];
            for (int i = 0; i < idx.Length; i++)
            {
                sorted.Add(outRows[idx[i]]);
                sortedKeys[i] = keys![idx[i]];
            }
            outRows = sorted;
            keys = sortedKeys;
        }

        if (s.Top != null)
        {
            if (s.TopPercent)
            {
                double pct = TypeSystem.ToDouble(Eval(s.Top, Detached(outer, env))!);
                if (pct < 0 || pct > 100) throw Err.New(1031, "Percent values must be between 0 and 100.", 15);
                top = (long)Math.Ceiling(outRows.Count * pct / 100.0);
            }
            if (top < outRows.Count)
            {
                int count = (int)top;
                if (s.TopWithTies && keys != null && count > 0)
                    while (count < outRows.Count && CompareKeys(keys[count], keys[count - 1], orderBy!, unicodeKeys) == 0) count++;
                outRows = outRows.GetRange(0, count);
            }
        }
        if (offset != null) outRows = OffsetFetch(outRows, offset, fetch, Detached(outer, env));
        if (outRows.Count > limit) outRows = outRows.GetRange(0, limit);
        return new RowSet(new RowSchema(outCols), outRows);
        }
    }

    /// <summary>
    /// GROUP BY ROLLUP / CUBE / GROUPING SETS: groups the source once per grouping set. Expressions that are
    /// not part of a set are "aggregated away": they read as NULL in that set's rows and GROUPING() reports 1.
    /// </summary>
    void GroupBySets(SelectSpec s, List<OrderItem>? orderBy, RowSet src, EvalContext ctx, List<object?[]> itemRows, List<List<object?[]>> itemGroups)
    {
        var allKeys = s.GroupBy.Select(e => ExprKey(e, src.Schema)).Distinct().ToList();
        var nulledPerItem = new List<HashSet<Expr>?>();
        var outPerItem = new List<HashSet<string>?>();
        var scratch = new EvalContext(src.Schema, ctx.Outer, ctx.Env);
        foreach (var set in s.GroupingSets!)
        {
            var setKeys = set.Select(e => ExprKey(e, src.Schema)).ToHashSet();
            var outKeys = allKeys.Where(k => !setKeys.Contains(k)).ToHashSet();

            // every occurrence of an aggregated-away expression outside aggregate arguments reads as NULL
            var nulled = new HashSet<Expr>();
            void Mark(Expr? root) => Walk(root, x =>
            {
                if (IsAggregateCall(x)) return false;
                if (x is FuncCall { Name: "GROUPING" or "GROUPING_ID", Schema: null }) return false;
                if (outKeys.Contains(ExprKey(x, src.Schema))) { nulled.Add(x); return false; }
                return true;
            });
            foreach (var item in s.Items) Mark(item.E);
            Mark(s.Having);
            if (orderBy != null) foreach (var o in orderBy) Mark(o.E);

            var exprs = set.GroupBy(e => ExprKey(e, src.Schema)).Select(g => g.First()).ToList();
            if (exprs.Count == 0)
            {
                // grand total: one group over everything (omitted for empty input unless it is the only set)
                if (src.Rows.Count == 0) continue;
                itemRows.Add(src.Rows.Count > 0 ? src.Rows[0] : null!);
                itemGroups.Add(src.Rows);
                nulledPerItem.Add(nulled);
                outPerItem.Add(outKeys);
                continue;
            }
            var map = new Dictionary<RowKey, List<object?[]>>();
            foreach (var row in src.Rows)
            {
                scratch.Row = row;
                var k = new object[exprs.Count];
                for (int g = 0; g < k.Length; g++) k[g] = TypeSystem.NormalizeKey(Eval(exprs[g], scratch));
                var key = new RowKey(k);
                if (!map.TryGetValue(key, out var list))
                {
                    map[key] = list = [];
                    itemRows.Add(row);
                    itemGroups.Add(list);
                    nulledPerItem.Add(nulled);
                    outPerItem.Add(outKeys);
                }
                list.Add(row);
            }
        }
        ctx.ItemNulled = nulledPerItem;
        ctx.ItemGroupedOut = outPerItem;
    }

    // ------------------------------------------------------------ FOR JSON

    const string JsonColumnName = "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";

    static void WriteJsonValue(System.Text.StringBuilder sb, object v, SqlType? type, bool raw)
    {
        switch (v)
        {
            case string s when raw: sb.Append(s); break;
            case string s: sb.Append('"').Append(System.Text.Json.JsonEncodedText.Encode(s, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)).Append('"'); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case DateTime d:
            {
                string text = type?.Kind == SqlTypeKind.Date ? d.ToString("yyyy-MM-dd")
                    : d.ToString(d.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : type?.Kind == SqlTypeKind.DateTime2 ? "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" : "yyyy-MM-dd'T'HH:mm:ss.fff");
                sb.Append('"').Append(text).Append('"');
                break;
            }
            case DateTimeOffset o: sb.Append('"').Append(o.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz")).Append('"'); break;
            case TimeSpan t: sb.Append('"').Append(new DateTime(t.Ticks).ToString("HH:mm:ss.FFFFFFF")).Append('"'); break;
            case Guid g: sb.Append('"').Append(g.ToString().ToUpperInvariant()).Append('"'); break;
            case byte[] bytes: sb.Append('"').Append(Convert.ToBase64String(bytes)).Append('"'); break;
            case double d: sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
            case float f: sb.Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
            default: sb.Append(TypeSystem.ToStr(v)); break;
        }
    }

    sealed class JsonNode
    {
        public readonly List<(string Name, JsonNode? Child, object? Value, SqlType? Type, bool Raw)> Members = [];
    }

    RowSet ForJson(Query q, RowSet rs)
    {
        var opt = q.ForJson!;
        var schema = new RowSchema([new ColInfo(null, JsonColumnName, SqlType.NVarCharMax)]);
        if (rs.Rows.Count == 0) return new RowSet(schema, []);

        // nested FOR JSON subqueries and JSON_QUERY() results are embedded as JSON, not as strings
        var raw = new bool[rs.Schema.Count];
        if (q.Body is SelectSpec spec && spec.Items.Count == raw.Length && !spec.Items.Exists(i => i.IsStar))
            for (int j = 0; j < raw.Length; j++)
                raw[j] = spec.Items[j].E is SubqueryExpr { Q.ForJson: not null } or FuncCall { Name: "JSON_QUERY", Schema: null };

        var sb = new System.Text.StringBuilder();
        if (opt.Root != null) sb.Append("{\"").Append(System.Text.Json.JsonEncodedText.Encode(opt.Root)).Append("\":");
        if (!opt.WithoutArrayWrapper) sb.Append('[');
        bool firstRow = true;
        foreach (var row in rs.Rows)
        {
            if (!firstRow) sb.Append(',');
            firstRow = false;
            var root = new JsonNode();
            for (int j = 0; j < row.Length; j++)
            {
                var col = rs.Schema.Cols[j];
                if (col.Name.Length == 0)
                    throw Err.New(13605, "Unnamed tables cannot be used as JSON identifiers as well as unnamed columns cannot be used as key names. Add alias to the unnamed column or table.");
                if (row[j] == null && !opt.IncludeNulls) continue;
                var node = root;
                string[] path = opt.Auto ? [col.Name] : col.Name.Split('.');
                for (int p = 0; p < path.Length - 1; p++)
                {
                    int ix = node.Members.FindIndex(m => m.Name == path[p] && m.Child != null);
                    if (ix < 0)
                    {
                        node.Members.Add((path[p], new JsonNode(), null, null, false));
                        ix = node.Members.Count - 1;
                    }
                    node = node.Members[ix].Child!;
                }
                node.Members.Add((path[^1], null, row[j], col.Type, raw[j]));
            }
            WriteNode(root);
        }
        if (!opt.WithoutArrayWrapper) sb.Append(']');
        if (opt.Root != null) sb.Append('}');
        return new RowSet(schema, [[sb.ToString()]]);

        void WriteNode(JsonNode node)
        {
            sb.Append('{');
            bool first = true;
            foreach (var (name, child, value, type, isRaw) in node.Members)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(System.Text.Json.JsonEncodedText.Encode(name, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)).Append("\":");
                if (child != null) WriteNode(child);
                else if (value == null) sb.Append("null");
                else WriteJsonValue(sb, value, type, isRaw);
            }
            sb.Append('}');
        }
    }

    // ------------------------------------------------------------ PIVOT / UNPIVOT

    static SqlType? AggregateType(string name, SqlType? a)
    {
        switch (name)
        {
            case "COUNT": return SqlType.Int;
            case "COUNT_BIG": return SqlType.BigInt;
            case "MIN": case "MAX": return a;
            case "SUM": case "AVG":
                if (a == null) return null;
                if (a.Kind is SqlTypeKind.TinyInt or SqlTypeKind.SmallInt or SqlTypeKind.Int or SqlTypeKind.Bit) return SqlType.Int;
                if (a.Kind == SqlTypeKind.BigInt) return SqlType.BigInt;
                if (a.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney) return SqlType.Money;
                if (a.Kind == SqlTypeKind.Decimal) return name == "SUM" ? SqlType.Decimal(38, a.Scale) : SqlType.Decimal(38, Math.Max(a.Scale, 6));
                return a.IsApproximate ? SqlType.Float : null;
            default: return SqlType.Float;
        }
    }

    RowSet ExecPivot(PivotSource p, EvalContext? outer, QueryEnv? env)
    {
        var src = ExecSource(p.Source, outer, env);
        var agg = p.Aggregate;
        if (!Aggregates.IsAggregate(agg.Name) || agg.Name == "STRING_AGG") throw Err.New(102, $"Incorrect syntax near '{agg.Name}'.", 15);
        int valIdx = -1;
        if (!agg.Star)
        {
            if (agg.Args.Count != 1 || agg.Args[0] is not ColumnRef vc) throw Err.NotSupported("PIVOT over an expression");
            valIdx = src.Schema.TryResolve(vc.Table, vc.Name);
            if (valIdx < 0) throw Err.InvalidColumn(vc.Name);
        }
        int forIdx = src.Schema.TryResolve(null, p.ForColumn);
        if (forIdx < 0) throw Err.InvalidColumn(p.ForColumn);
        var groupCols = new List<int>();
        for (int i = 0; i < src.Schema.Count; i++)
            if (i != valIdx && i != forIdx && !src.Schema.Cols[i].Hidden) groupCols.Add(i);

        var groups = new Dictionary<RowKey, List<object?[]>>();
        var order = new List<List<object?[]>>();
        foreach (var row in src.Rows)
        {
            var k = new object[groupCols.Count];
            for (int g = 0; g < k.Length; g++) k[g] = TypeSystem.NormalizeKey(row[groupCols[g]]);
            var key = new RowKey(k);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(list);
            }
            list.Add(row);
        }

        var valType = valIdx >= 0 ? src.Schema.Cols[valIdx].Type : null;
        var cols = new List<ColInfo>();
        foreach (int g in groupCols) cols.Add(new ColInfo(p.Alias, src.Schema.Cols[g].Name, src.Schema.Cols[g].Type));
        foreach (string v in p.InValues) cols.Add(new ColInfo(p.Alias, v, AggregateType(agg.Name, valType)));

        var rows = new List<object?[]>(order.Count);
        foreach (var list in order)
        {
            var o = new object?[cols.Count];
            for (int g = 0; g < groupCols.Count; g++) o[g] = list[0][groupCols[g]];
            for (int v = 0; v < p.InValues.Count; v++)
            {
                var a = Aggregates.Create(agg.Name, valType, agg.Star);
                bool any = false;
                foreach (var row in list)
                {
                    object? key = row[forIdx];
                    if (key == null) continue;
                    bool match;
                    try { match = TypeSystem.Compare(key, p.InValues[v]) == 0; }
                    catch (TSqlException) { match = false; }
                    if (!match) continue;
                    any = true;
                    a.Add(valIdx >= 0 ? row[valIdx] : 1);
                }
                o[groupCols.Count + v] = any || agg.Name is "COUNT" or "COUNT_BIG" ? a.Result() : null;
            }
            rows.Add(o);
        }
        return new RowSet(new RowSchema(cols), rows);
    }

    RowSet ExecUnpivot(UnpivotSource u, EvalContext? outer, QueryEnv? env)
    {
        var src = ExecSource(u.Source, outer, env);
        var idx = new List<int>();
        SqlType? valueType = null;
        foreach (string c in u.Columns)
        {
            int i = src.Schema.TryResolve(null, c);
            if (i < 0) throw Err.InvalidColumn(c);
            idx.Add(i);
            valueType = Unify(valueType, src.Schema.Cols[i].Type);
        }
        var keep = new List<int>();
        for (int i = 0; i < src.Schema.Count; i++)
            if (!idx.Contains(i) && !src.Schema.Cols[i].Hidden) keep.Add(i);
        var cols = new List<ColInfo>();
        foreach (int k in keep) cols.Add(new ColInfo(u.Alias, src.Schema.Cols[k].Name, src.Schema.Cols[k].Type));
        // SQL Server lists the value column before the name column
        cols.Add(new ColInfo(u.Alias, u.ValueColumn, valueType));
        cols.Add(new ColInfo(u.Alias, u.NameColumn, SqlType.NVarChar(128)));
        var rows = new List<object?[]>();
        foreach (var row in src.Rows)
        {
            foreach (int i in idx)
            {
                if (row[i] == null) continue;
                var o = new object?[cols.Count];
                for (int k = 0; k < keep.Count; k++) o[k] = row[keep[k]];
                o[keep.Count] = row[i];
                o[keep.Count + 1] = src.Schema.Cols[i].Name;
                rows.Add(o);
            }
        }
        return new RowSet(new RowSchema(cols), rows);
    }

    // ------------------------------------------------------------ OPENJSON

    RowSet ExecOpenJson(FunctionTable ft, object?[] args, string alias)
    {
        if (args.Length < 1 || args.Length > 2) throw Err.New(174, "The openjson function requires 1 to 2 argument(s).", 15);
        var explicitCols = ft.JsonColumns;
        var cols = new List<ColInfo>();
        if (explicitCols != null)
            foreach (var c in explicitCols) cols.Add(new ColInfo(alias, c.Name, c.AsJson ? SqlType.NVarCharMax : c.Type));
        else
        {
            cols.Add(new ColInfo(alias, "key", SqlType.NVarChar(4000)));
            cols.Add(new ColInfo(alias, "value", SqlType.NVarCharMax));
            cols.Add(new ColInfo(alias, "type", SqlType.TinyInt));
        }
        var rows = new List<object?[]>();
        if (args[0] == null) return new RowSet(new RowSchema(cols), rows);

        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(TypeSystem.ToStr(args[0]!)); }
        catch (System.Text.Json.JsonException) { throw Err.New(13609, "JSON text is not properly formatted. Unexpected character is found."); }
        using (doc)
        {
            var root = args.Length > 1 && args[1] != null ? Functions.JsonPath(doc.RootElement, TypeSystem.ToStr(args[1]!)) : doc.RootElement;
            if (root == null) return new RowSet(new RowSchema(cols), rows);

            static string? Text(System.Text.Json.JsonElement e) => e.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => e.GetString(),
                System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
                _ => e.GetRawText(),
            };
            static byte TypeCode(System.Text.Json.JsonElement e) => e.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Null => 0,
                System.Text.Json.JsonValueKind.String => 1,
                System.Text.Json.JsonValueKind.Number => 2,
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => 3,
                System.Text.Json.JsonValueKind.Array => 4,
                _ => 5,
            };

            if (explicitCols == null)
            {
                if (root.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var el in root.Value.EnumerateArray()) rows.Add([(i++).ToString(), Text(el), TypeCode(el)]);
                }
                else if (root.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var prop in root.Value.EnumerateObject()) rows.Add([prop.Name, Text(prop.Value), TypeCode(prop.Value)]);
                return new RowSet(new RowSchema(cols), rows);
            }

            IEnumerable<System.Text.Json.JsonElement> elements = root.Value.ValueKind == System.Text.Json.JsonValueKind.Array ? root.Value.EnumerateArray() : [root.Value];
            foreach (var el in elements)
            {
                var row = new object?[explicitCols.Count];
                for (int c = 0; c < row.Length; c++)
                {
                    var col = explicitCols[c];
                    string path = col.Path ?? "$." + (col.Name.Contains(' ') ? "\"" + col.Name + "\"" : col.Name);
                    var v = Functions.JsonPath(el, path);
                    if (v == null || v.Value.ValueKind is System.Text.Json.JsonValueKind.Null) continue;
                    bool complex = v.Value.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array;
                    if (col.AsJson) row[c] = complex ? v.Value.GetRawText() : null;
                    else if (!complex) row[c] = TypeSystem.ConvertTo(Text(v.Value), col.Type);
                }
                rows.Add(row);
            }
        }
        return new RowSet(new RowSchema(cols), rows);
    }

    // ------------------------------------------------------------ table sources

    static object?[] WithRef(object?[] row)
    {
        var r = new object?[row.Length + 1];
        Array.Copy(row, r, row.Length);
        r[row.Length] = row;
        return r;
    }

    static RowSet Rename(RowSet rs, string? alias, List<string>? columns)
    {
        int visible = rs.Schema.Cols.Count(c => !c.Hidden);
        if (columns != null && columns.Count != visible)
            throw Err.New(8158, $"'{alias}' has {(columns.Count > visible ? "fewer" : "more")} columns than were specified in the column list.");
        var cols = new List<ColInfo>(rs.Schema.Count);
        int next = 0;
        foreach (var c in rs.Schema.Cols)
            cols.Add(new ColInfo(alias, c.Hidden || columns == null ? c.Name : columns[next++], c.Type) { Hidden = c.Hidden });
        return new RowSet(new RowSchema(cols), rs.Rows);
    }

    /// <summary>The inserted / deleted pseudo tables visible inside a trigger body.</summary>
    Table? TriggerPseudoTable(TableName name)
    {
        if (name.Schema != null || name.Database != null || _frames.Count == 0 || Frame.TriggerTable == null) return null;
        if (name.Name.Equals("inserted", StringComparison.OrdinalIgnoreCase)) return Frame.Inserted;
        if (name.Name.Equals("deleted", StringComparison.OrdinalIgnoreCase)) return Frame.Deleted;
        return null;
    }

    Table? TryResolveBaseTable(TableRef tr, QueryEnv? env)
    {
        var name = tr.Name;
        if (TriggerPseudoTable(name) is { } pseudo) return pseudo;
        if (name.IsVariable) return Frame.Vars.GetValueOrDefault(name.Name)?.Table;
        if (name.Schema == null && name.Database == null && env?.FindCte(name.Name, out _) != null) return null;
        return FindObject(name) as Table;
    }

    RowSet ExecTableRef(TableRef tr, QueryEnv? env)
    {
        var name = tr.Name;
        string alias = tr.Alias ?? name.Name;
        Table? table;
        if (name.IsVariable)
        {
            table = Frame.Vars.GetValueOrDefault(name.Name)?.Table
                    ?? throw Err.New(1087, $"Must declare the table variable \"{name.Name}\".", 15);
        }
        else if (TriggerPseudoTable(name) is { } pseudo) table = pseudo;
        else
        {
            if (name.Schema == null && name.Database == null && env != null)
            {
                var cte = env.FindCte(name.Name, out var owner);
                if (cte != null) return Rename(EvalCte(cte, owner!), alias, null);
            }
            var obj = FindObject(name);
            if (obj is View v) return Rename(ExecView(v, env), alias, v.Columns);
            table = obj as Table;
            if (table == null)
            {
                var sys = SystemView(name);
                if (sys != null) return Rename(sys, alias, null);
                throw Err.InvalidObject(name.ToString());
            }
        }
        bool withRef = ReferenceEquals(tr, _rowRefTarget);
        var schema = table.GetSchema(alias, withRef);
        if (!withRef) return new RowSet(schema, table.Rows);
        var rows = new List<object?[]>(table.Rows.Count);
        foreach (var row in table.Rows) rows.Add(WithRef(row));
        return new RowSet(schema, rows);
    }

    RowSet ExecView(View v, QueryEnv? env)
    {
        var savedDb = Db;
        if (v.Database != null) Db = v.Database;
        EnterNest();
        try
        {
            return ExecQuery(v.Query, null, new QueryEnv(null) { Marker = env?.Marker });
        }
        finally
        {
            _nestLevel--;
            Db = savedDb;
        }
    }

    /// <summary>Splits "a UNION ALL b UNION ALL c" into its members; false if the body is anything else.</summary>
    static bool FlattenUnionAll(QueryExpr body, List<QueryExpr> members)
    {
        if (body is SetOp { Op: "UNION", All: true } so)
        {
            if (!FlattenUnionAll(so.Left, members)) return false;
            members.Add(so.Right);
            return true;
        }
        if (body is SetOp) return false;
        members.Add(body);
        return true;
    }

    static bool RefersTo(QueryExpr body, string cte) => body switch
    {
        SelectSpec s => s.From != null && RefersTo(s.From, cte),
        SetOp so => RefersTo(so.Left, cte) || RefersTo(so.Right, cte),
        NestedQuery n => RefersTo(n.Q.Body, cte),
        _ => false,
    };

    static bool RefersTo(TableSource source, string cte) => source switch
    {
        TableRef t => t.Name.Schema == null && t.Name.Name.Equals(cte, StringComparison.OrdinalIgnoreCase),
        JoinSource j => RefersTo(j.Left, cte) || RefersTo(j.Right, cte),
        DerivedTable d => RefersTo(d.Query.Body, cte),
        PivotSource p => RefersTo(p.Source, cte),
        UnpivotSource u => RefersTo(u.Source, cte),
        _ => false,
    };

    RowSet EvalCte(CteState state, QueryEnv owner)
    {
        if (state.Result != null) return state.Result;
        string name = state.Def.Name;
        if (state.Evaluating)
        {
            state.Referenced = true;
            return state.Working ?? throw Err.New(253, $"Recursive member of a common table expression '{name}' has multiple recursive references.");
        }
        state.Evaluating = true;
        try
        {
            var q = state.Def.Query;
            var bodyEnv = new QueryEnv(owner, q.Ctes);
            RowSet result;
            var members = new List<QueryExpr>();
            int firstRecursive = -1;
            if (q.OrderBy == null && FlattenUnionAll(q.Body, members))
                firstRecursive = members.FindIndex(m => RefersTo(m, name));
            if (firstRecursive > 0)
            {
                // anchor members first, then every recursive member is applied to the rows of the previous round
                RowSet? anchor = null;
                for (int i = 0; i < firstRecursive; i++)
                {
                    var part = ExecQueryExpr(members[i], null, bodyEnv);
                    anchor = anchor == null ? part : CombineSets("UNION", true, anchor, part);
                }
                anchor = Rename(anchor!, name, state.Def.Columns);
                var types = anchor.Schema.Cols.Select(c => c.Type).ToArray();
                var all = new List<object?[]>(anchor.Rows);
                state.Working = anchor;
                int iteration = 0;
                while (state.Working.Rows.Count > 0)
                {
                    if (_maxRecursion > 0 && ++iteration > _maxRecursion)
                        throw Err.New(530, $"The statement terminated. The maximum recursion {_maxRecursion} has been exhausted before statement completion.");
                    var round = new List<object?[]>();
                    for (int i = firstRecursive; i < members.Count; i++)
                    {
                        var rec = ExecQueryExpr(members[i], null, bodyEnv);
                        if (rec.Schema.Count != anchor.Schema.Count)
                            throw Err.New(205, "All queries combined using a UNION, INTERSECT or EXCEPT operator must have an equal number of expressions in their target lists.");
                        round.AddRange(Coerce(rec, types));
                    }
                    all.AddRange(round);
                    state.Working = new RowSet(anchor.Schema, round);
                }
                result = new RowSet(anchor.Schema, all);
            }
            else if (q.Body is SetOp { Op: "UNION" } so && q.OrderBy == null)
            {
                var anchor = Rename(ExecQueryExpr(so.Left, null, bodyEnv), name, state.Def.Columns);
                state.Working = anchor;
                state.Referenced = false;
                var rec = ExecQueryExpr(so.Right, null, bodyEnv);
                if (!state.Referenced) result = CombineSets("UNION", so.All, anchor, rec);
                else
                {
                    if (!so.All) throw Err.New(252, $"Recursive common table expression '{name}' does not contain a top-level UNION ALL operator.");
                    var types = anchor.Schema.Cols.Select(c => c.Type).ToArray();
                    var all = new List<object?[]>(anchor.Rows);
                    int iteration = 0;
                    while (rec.Rows.Count > 0)
                    {
                        if (rec.Schema.Count != anchor.Schema.Count)
                            throw Err.New(205, "All queries combined using a UNION, INTERSECT or EXCEPT operator must have an equal number of expressions in their target lists.");
                        if (_maxRecursion > 0 && ++iteration > _maxRecursion)
                            throw Err.New(530, $"The statement terminated. The maximum recursion {_maxRecursion} has been exhausted before statement completion.");
                        var rows = Coerce(rec, types);
                        all.AddRange(rows);
                        state.Working = new RowSet(anchor.Schema, rows);
                        rec = ExecQueryExpr(so.Right, null, bodyEnv);
                    }
                    result = new RowSet(anchor.Schema, all);
                }
            }
            else result = ExecQuery(new Query { Body = q.Body, OrderBy = q.OrderBy, Offset = q.Offset, Fetch = q.Fetch }, null, bodyEnv);
            state.Working = null;
            return state.Result = Rename(result, name, state.Def.Columns);
        }
        finally
        {
            state.Evaluating = false;
        }
    }

    RowSet ExecSource(TableSource ts, EvalContext? outer, QueryEnv? env)
    {
        switch (ts)
        {
            case TableRef tr: return ExecTableRef(tr, env);
            case DerivedTable dt: return Rename(ExecQuery(dt.Query, outer, env), dt.Alias, dt.Columns);
            case ValuesTable vt: return ExecValues(vt, Detached(outer, env));
            case FunctionTable ft: return ExecTableFunction(ft, outer, env);
            case PivotSource p: return ExecPivot(p, outer, env);
            case UnpivotSource u: return ExecUnpivot(u, outer, env);
            case JoinSource j:
            {
                var left = ExecSource(j.Left, outer, env);
                if (j.Kind is JoinKind.CrossApply or JoinKind.OuterApply) return ExecApply(left, j, outer, env);
                var right = ExecSource(j.Right, outer, env);
                return JoinRowSets(left, right, j.Kind, j.On, outer, env);
            }
            default: throw Err.NotSupported(ts.GetType().Name);
        }
    }

    RowSet ExecValues(ValuesTable vt, EvalContext? outer)
    {
        int n = vt.Rows[0].Count;
        var rows = new List<object?[]>(vt.Rows.Count);
        var types = new SqlType?[n];
        foreach (var r in vt.Rows)
        {
            if (r.Count != n) throw Err.New(10709, "The number of columns for each row in a table value constructor must be the same.", 15);
            var row = new object?[n];
            for (int j = 0; j < n; j++)
            {
                row[j] = Eval(r[j], outer);
                var t = InferType(r[j], outer) ?? (row[j] == null ? null : TypeSystem.TypeOf(row[j]!));
                types[j] = Unify(types[j], t);
            }
            rows.Add(row);
        }
        var cols = new List<ColInfo>(n);
        for (int j = 0; j < n; j++)
        {
            cols.Add(new ColInfo(vt.Alias, vt.Columns != null && j < vt.Columns.Count ? vt.Columns[j] : "Column" + (j + 1), types[j]));
            if (types[j] == null) continue;
            var clr = types[j]!.ClrType;
            foreach (var row in rows)
                if (row[j] != null && (row[j]!.GetType() != clr || row[j] is decimal)) row[j] = CoerceTo(row[j], types[j]!);
        }
        return new RowSet(new RowSchema(cols), rows);
    }

    RowSet ExecCrossWithPredicates(JoinSource j, List<Expr> conjuncts, EvalContext? outer, QueryEnv? env)
    {
        var left = j.Left is JoinSource { Kind: JoinKind.Cross } lj ? ExecCrossWithPredicates(lj, conjuncts, outer, env) : ExecSource(j.Left, outer, env);
        var right = ExecSource(j.Right, outer, env);
        var schema = RowSchema.Concat(left.Schema, right.Schema);
        Expr? on = null;
        foreach (var c in conjuncts)
        {
            if (!ResolvableIn(c, schema)) continue;
            on = on == null ? c : new Binary("AND", on, c);
        }
        return JoinRowSets(left, right, on == null ? JoinKind.Cross : JoinKind.Inner, on, outer, env);
    }

    static object?[] Combine(object?[]? l, int ln, object?[]? r, int rn)
    {
        var row = new object?[ln + rn];
        if (l != null) Array.Copy(l, row, ln);
        if (r != null) Array.Copy(r, 0, row, ln, rn);
        return row;
    }

    internal RowSet JoinRowSets(RowSet left, RowSet right, JoinKind kind, Expr? on, EvalContext? outer, QueryEnv? env)
    {
        var schema = RowSchema.Concat(left.Schema, right.Schema);
        int ln = left.Schema.Count, rn = right.Schema.Count;
        var result = new List<object?[]>();
        if (kind == JoinKind.Cross || on == null)
        {
            foreach (var l in left.Rows)
                foreach (var r in right.Rows) result.Add(Combine(l, ln, r, rn));
            return new RowSet(schema, result);
        }

        var ctx = new EvalContext(schema, outer, env);
        bool keepLeft = kind is JoinKind.Left or JoinKind.Full;
        bool keepRight = kind is JoinKind.Right or JoinKind.Full;
        bool[]? rightMatched = keepRight ? new bool[right.Rows.Count] : null;

        // ---- try a hash join on equality conjuncts
        var conjuncts = new List<Expr>();
        SplitAnd(on, conjuncts);
        var lk = new List<Expr>();
        var rk = new List<Expr>();
        var residual = new List<Expr>();
        foreach (var c in conjuncts)
        {
            if (c is Binary { Op: "=" } b)
            {
                if (RefsOnly(b.L, left.Schema, right.Schema) && RefsOnly(b.R, right.Schema, left.Schema)) { lk.Add(b.L); rk.Add(b.R); continue; }
                if (RefsOnly(b.R, left.Schema, right.Schema) && RefsOnly(b.L, right.Schema, left.Schema)) { lk.Add(b.R); rk.Add(b.L); continue; }
            }
            residual.Add(c);
        }

        if (lk.Count > 0 && (long)left.Rows.Count * right.Rows.Count > 64)
        {
            int kn = lk.Count;
            var masks = new int[kn];
            RowKey?[] BuildKeys(RowSet rs, List<Expr> exprs)
            {
                var kctx = new EvalContext(rs.Schema, outer, env);
                var keys = new RowKey?[rs.Rows.Count];
                for (int i = 0; i < keys.Length; i++)
                {
                    kctx.Row = rs.Rows[i];
                    var k = new object[kn];
                    bool hasNull = false;
                    for (int m = 0; m < kn; m++)
                    {
                        object? v = Eval(exprs[m], kctx);
                        if (v == null) { hasNull = true; break; }
                        k[m] = TypeSystem.NormalizeKey(v);
                        masks[m] |= TypeSystem.KeyClass(k[m]);
                    }
                    keys[i] = hasNull ? null : new RowKey(k);
                }
                return keys;
            }
            var lkeys = BuildKeys(left, lk);
            var rkeys = BuildKeys(right, rk);
            if (Array.TrueForAll(masks, TypeSystem.HashCompatible))
            {
                var hash = new Dictionary<RowKey, List<int>>();
                for (int i = 0; i < rkeys.Length; i++)
                {
                    if (rkeys[i] == null) continue;
                    if (!hash.TryGetValue(rkeys[i]!, out var list)) hash[rkeys[i]!] = list = [];
                    list.Add(i);
                }
                for (int i = 0; i < lkeys.Length; i++)
                {
                    var l = left.Rows[i];
                    bool matched = false;
                    if (lkeys[i] != null && hash.TryGetValue(lkeys[i]!, out var candidates))
                    {
                        foreach (int ri in candidates)
                        {
                            var row = Combine(l, ln, right.Rows[ri], rn);
                            if (residual.Count > 0)
                            {
                                ctx.Row = row;
                                bool ok = true;
                                foreach (var c in residual)
                                    if (!IsTrue(Eval(c, ctx))) { ok = false; break; }
                                if (!ok) continue;
                            }
                            matched = true;
                            if (rightMatched != null) rightMatched[ri] = true;
                            result.Add(row);
                        }
                    }
                    if (!matched && keepLeft) result.Add(Combine(l, ln, null, rn));
                }
                AddUnmatchedRight();
                return new RowSet(schema, result);
            }
        }

        // ---- nested loops
        var scratch = new object?[ln + rn];
        foreach (var l in left.Rows)
        {
            Array.Copy(l, scratch, ln);
            bool matched = false;
            for (int ri = 0; ri < right.Rows.Count; ri++)
            {
                Array.Copy(right.Rows[ri], 0, scratch, ln, rn);
                ctx.Row = scratch;
                if (!IsTrue(Eval(on, ctx))) continue;
                matched = true;
                if (rightMatched != null) rightMatched[ri] = true;
                result.Add((object?[])scratch.Clone());
            }
            if (!matched && keepLeft) result.Add(Combine(l, ln, null, rn));
        }
        AddUnmatchedRight();
        return new RowSet(schema, result);

        void AddUnmatchedRight()
        {
            if (rightMatched == null) return;
            for (int ri = 0; ri < rightMatched.Length; ri++)
                if (!rightMatched[ri]) result.Add(Combine(null, ln, right.Rows[ri], rn));
        }
    }

    RowSet ExecApply(RowSet left, JoinSource j, EvalContext? outer, QueryEnv? env)
    {
        var lctx = new EvalContext(left.Schema, outer, env);
        int ln = left.Schema.Count;
        RowSchema? rschema = null;
        var result = new List<object?[]>();
        foreach (var l in left.Rows)
        {
            lctx.Row = l;
            var r = ExecSource(j.Right, lctx, env);
            rschema ??= r.Schema;
            foreach (var rr in r.Rows) result.Add(Combine(l, ln, rr, rr.Length));
            if (r.Rows.Count == 0 && j.Kind == JoinKind.OuterApply) result.Add(Combine(l, ln, null, rschema.Count));
        }
        if (rschema == null)
        {
            // no left rows: probe the right side with an all-NULL row to learn its shape
            lctx.Row = new object?[ln];
            try { rschema = ExecSource(j.Right, lctx, env).Schema; }
            catch (TSqlException) { rschema = RowSchema.Empty; }
        }
        return new RowSet(RowSchema.Concat(left.Schema, rschema), result);
    }

    // ------------------------------------------------------------ equality lookups

    static bool IsProbeExpr(Expr e, RowSchema schema)
    {
        switch (e)
        {
            case Literal or VarRef: return true;
            case ColumnRef c: return schema.TryResolve(c.Table, c.Name) == -1;
            case Unary { Op: not "NOT" } u: return IsProbeExpr(u.E, schema);
            case Binary { Op: "+" or "-" or "*" or "/" or "%" } b: return IsProbeExpr(b.L, schema) && IsProbeExpr(b.R, schema);
            case CastExpr c: return c.Style == null && IsProbeExpr(c.E, schema);
            default: return false;
        }
    }

    static bool TryProbeKey(SqlType colType, object value, out object key)
    {
        key = value;
        try
        {
            int rank = TypeSystem.NumRank(value);
            switch (colType.Kind)
            {
                case SqlTypeKind.Bit: case SqlTypeKind.TinyInt: case SqlTypeKind.SmallInt: case SqlTypeKind.Int: case SqlTypeKind.BigInt:
                    if (value is string s) value = TypeSystem.ToLong(s);
                    else if (rank == 0 || rank >= 7) return false;
                    key = TypeSystem.NormalizeKey(value);
                    return true;
                case SqlTypeKind.Decimal: case SqlTypeKind.Money: case SqlTypeKind.SmallMoney:
                    if (value is string s2) value = TypeSystem.ConvertTo(s2, colType)!;
                    else if (rank == 0 || rank >= 7) return false;
                    key = TypeSystem.NormalizeKey(value);
                    return true;
                case SqlTypeKind.Char: case SqlTypeKind.VarChar: case SqlTypeKind.NChar: case SqlTypeKind.NVarChar:
                    if (value is not string) return false;
                    key = TypeSystem.NormalizeKey(value);
                    return true;
                case SqlTypeKind.Date: case SqlTypeKind.SmallDateTime: case SqlTypeKind.DateTime: case SqlTypeKind.DateTime2:
                    if (value is string s3) value = TypeSystem.ConvertTo(s3, colType)!;
                    else if (value is not DateTime) return false;
                    key = value;
                    return true;
                case SqlTypeKind.UniqueIdentifier:
                    if (value is string s4) value = TypeSystem.ConvertTo(s4, SqlType.UniqueIdentifier)!;
                    else if (value is not Guid) return false;
                    key = value;
                    return true;
                default: return false;
            }
        }
        catch (TSqlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Answers "FROM table WHERE col = value AND ..." through a unique index or a per-column hash instead of a scan.
    /// The caller still applies the full WHERE clause to the candidate rows.
    /// </summary>
    bool TryLookup(TableRef tr, Expr where, EvalContext? outer, QueryEnv? env, out RowSet result)
    {
        result = null!;
        var table = TryResolveBaseTable(tr, env);
        if (table == null || table.Rows.Count < 16) return false;
        bool withRef = ReferenceEquals(tr, _rowRefTarget);
        var schema = table.GetSchema(tr.Alias ?? tr.Name.Name, withRef);
        var conjuncts = new List<Expr>();
        SplitAnd(where, conjuncts);
        int bestCol = -1;
        Expr? bestProbe = null;
        UniqueIndex? bestIndex = null;
        foreach (var c in conjuncts)
        {
            if (c is not Binary { Op: "=" } b) continue;
            for (int side = 0; side < 2; side++)
            {
                var colExpr = side == 0 ? b.L : b.R;
                var probe = side == 0 ? b.R : b.L;
                if (colExpr is not ColumnRef cr) continue;
                int ci = schema.TryResolve(cr.Table, cr.Name);
                if (ci < 0 || !IsProbeExpr(probe, schema)) continue;
                var ux = table.Unique.FirstOrDefault(u => u.Cols.Length == 1 && u.Cols[0] == ci);
                if (bestCol >= 0 && (bestIndex != null || ux == null)) continue;
                bestCol = ci;
                bestProbe = probe;
                bestIndex = ux;
            }
        }
        if (bestCol < 0) return false;
        object? value = Eval(bestProbe!, new EvalContext(schema, outer, env));
        List<object?[]> rows;
        if (value == null) rows = [];
        else
        {
            if (!TryProbeKey(table.Columns[bestCol].Type, value, out object key)) return false;
            if (bestIndex != null) rows = bestIndex.Map.TryGetValue(new RowKey([key]), out var one) ? [one] : [];
            else rows = table.Lookup(bestCol, key) ?? [];
        }
        if (withRef)
        {
            var withRefs = new List<object?[]>(rows.Count);
            foreach (var row in rows) withRefs.Add(WithRef(row));
            rows = withRefs;
        }
        result = new RowSet(schema, rows);
        return true;
    }

    // ------------------------------------------------------------ table-valued functions

    Frame BindFunctionArgs(Function fn, object?[] args)
    {
        var def = fn.Def;
        if (args.Length > def.Params.Count)
            throw Err.New(8144, $"Procedure or function {fn.FullName} has too many arguments specified.");
        var frame = new Frame { Procedure = fn.Name };
        for (int i = 0; i < def.Params.Count; i++)
        {
            var p = def.Params[i];
            object? v;
            if (i < args.Length && args[i] is not DefaultMarker) v = args[i];
            else if (p.Default != null) v = Eval(p.Default, null);
            else throw Err.New(313, $"An insufficient number of arguments were supplied for the procedure or function {fn.FullName}.");
            BindParameter(frame, p, v);
        }
        return frame;
    }

    RowSet ExecTableFunction(FunctionTable ft, EvalContext? outer, QueryEnv? env)
    {
        string alias = ft.Alias ?? ft.Name.Name;
        var args = new object?[ft.Args.Count];
        for (int i = 0; i < args.Length; i++) args[i] = ft.Args[i] is DefaultExpr ? DefaultMarker.Instance : EvalArgument(ft.Args[i], Detached(outer, env));

        if (ft.Name.Schema == null)
        {
            switch (ft.Name.Name.ToUpperInvariant())
            {
                case "STRING_SPLIT":
                {
                    if (args.Length < 2) throw Err.New(174, "The STRING_SPLIT function requires 2 to 3 argument(s).", 15);
                    bool ordinal = args.Length > 2 && args[2] != null && TypeSystem.ToBool(args[2]!);
                    var cols = new List<ColInfo> { new(alias, ft.Columns?[0] ?? "value", SqlType.NVarCharMax) };
                    if (ordinal) cols.Add(new ColInfo(alias, "ordinal", SqlType.BigInt));
                    var rows = new List<object?[]>();
                    if (args[0] != null && args[1] != null)
                    {
                        string sep = TypeSystem.ToStr(args[1]!);
                        if (sep.Length != 1) throw Err.New(214, "Procedure expects parameter 'separator' of type 'nchar(1)/nvarchar(1)'.");
                        long n = 0;
                        foreach (string part in TypeSystem.ToStr(args[0]!).Split(sep[0]))
                            rows.Add(ordinal ? [part, ++n] : [part]);
                    }
                    return new RowSet(new RowSchema(cols), rows);
                }
                case "GENERATE_SERIES":
                {
                    if (args.Length < 2) throw Err.New(174, "The GENERATE_SERIES function requires 2 to 3 argument(s).", 15);
                    var rows = new List<object?[]>();
                    SqlType type = SqlType.Int;
                    if (args[0] != null && args[1] != null)
                    {
                        object start = args[0]!, stop = args[1]!;
                        type = Unify(TypeSystem.TypeOf(start), TypeSystem.TypeOf(stop))!;
                        object step = args.Length > 2 && args[2] != null ? args[2]! : TypeSystem.Compare(start, stop) <= 0 ? 1 : -1;
                        int dir = TypeSystem.Compare(step, 0);
                        if (dir == 0) throw Err.New(4199, "Argument value 0 is invalid for argument 3 of generate_series function.");
                        for (object? v = TypeSystem.ConvertTo(start, type); dir > 0 ? TypeSystem.Compare(v!, stop) <= 0 : TypeSystem.Compare(v!, stop) >= 0;
                             v = TypeSystem.ConvertTo(TypeSystem.Arith("+", v, step), type))
                            rows.Add([v]);
                    }
                    return new RowSet(new RowSchema([new ColInfo(alias, ft.Columns?[0] ?? "value", type)]), rows);
                }
                case "OPENJSON": return ExecOpenJson(ft, args, alias);
                case "OPENROWSET": case "OPENQUERY": case "OPENXML":
                    throw Err.NotSupported(ft.Name.Name.ToUpperInvariant());
            }
        }

        if (FindObject(ft.Name) is not Function fn || fn.Def.ReturnType != null) throw Err.InvalidObject(ft.Name.ToString());
        var frame = BindFunctionArgs(fn, args);
        var savedDb = Db;
        if (fn.Database != null) Db = fn.Database;
        try
        {
            if (fn.Def.InlineQuery != null)
            {
                RowSet rs = null!;
                RunInFrame(frame, () => rs = ExecQuery(fn.Def.InlineQuery, null, new QueryEnv(null) { Marker = env?.Marker }));
                return Rename(rs, alias, ft.Columns);
            }
            var table = BuildTable(fn.Def.TableDef!, "dbo", fn.Def.TableVar!, Db);
            table.IsVariable = true;
            frame.Vars[fn.Def.TableVar!] = new Variable { Table = table };
            var savedReturn = _returnValue;
            _functionDepth++;
            try { RunInFrame(frame, () => ExecBlock(fn.Def.Body!)); }
            finally { _functionDepth--; }
            _returnValue = savedReturn;
            return Rename(new RowSet(table.GetSchema(alias, false), [.. table.Rows]), alias, ft.Columns);
        }
        finally
        {
            Db = savedDb;
        }
    }

    // ------------------------------------------------------------ window functions

    object?[] ComputeWindow(FuncCall f, List<object?[]> itemRows, List<List<object?[]>>? itemGroups, EvalContext ctx)
    {
        var over = f.Over!;
        int n = itemRows.Count;
        var result = new object?[n];
        var okeys = new object?[n][];
        var partitions = new List<List<int>>();
        var pmap = new Dictionary<RowKey, List<int>>();

        void SetItem(int i)
        {
            ctx.Row = itemRows[i];
            ctx.Group = itemGroups?[i];
            ctx.ItemIndex = i;
        }

        for (int i = 0; i < n; i++)
        {
            SetItem(i);
            var pk = new object[over.Partition.Count];
            for (int m = 0; m < pk.Length; m++) pk[m] = TypeSystem.NormalizeKey(Eval(over.Partition[m], ctx));
            var key = new RowKey(pk);
            if (!pmap.TryGetValue(key, out var list))
            {
                pmap[key] = list = [];
                partitions.Add(list);
            }
            list.Add(i);
            var ok = new object?[over.Order.Count];
            for (int m = 0; m < ok.Length; m++) ok[m] = Eval(over.Order[m].E, ctx);
            okeys[i] = ok;
        }

        string name = f.Name;
        ctx.Row = n > 0 ? itemRows[0] : null;
        var unicodeOrder = UnicodeFlags(over.Order.Select(o => InferType(o.E, ctx)).ToArray());
        bool ranking = name is "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "NTILE" or "PERCENT_RANK" or "CUME_DIST";
        if ((ranking || name is "LAG" or "LEAD") && over.Order.Count == 0)
            throw Err.New(4112, $"The function '{name}' must have an OVER clause with ORDER BY.");

        foreach (var part in partitions)
        {
            int[] idx = part.ToArray();
            if (over.Order.Count > 0)
                Array.Sort(idx, (a, b) =>
                {
                    int c = CompareKeys(okeys[a], okeys[b], over.Order, unicodeOrder);
                    return c != 0 ? c : a.CompareTo(b);
                });
            int m = idx.Length;
            bool Peer(int a, int b) => CompareKeys(okeys[idx[a]], okeys[idx[b]], over.Order, unicodeOrder) == 0;

            switch (name)
            {
                case "ROW_NUMBER":
                    for (int k = 0; k < m; k++) result[idx[k]] = (long)(k + 1);
                    continue;
                case "RANK":
                case "DENSE_RANK":
                {
                    long rank = 1, dense = 1;
                    for (int k = 0; k < m; k++)
                    {
                        if (k > 0 && !Peer(k, k - 1)) { rank = k + 1; dense++; }
                        result[idx[k]] = name == "RANK" ? rank : dense;
                    }
                    continue;
                }
                case "PERCENT_RANK":
                case "CUME_DIST":
                {
                    int start = 0;
                    for (int k = 0; k <= m; k++)
                    {
                        if (k < m && (k == 0 || Peer(k, k - 1))) continue;
                        for (int q = start; q < k; q++)
                            result[idx[q]] = name == "CUME_DIST" ? (double)k / m : m == 1 ? 0.0 : (double)start / (m - 1);
                        start = k;
                    }
                    continue;
                }
                case "NTILE":
                {
                    SetItem(idx[0]);
                    long buckets = TypeSystem.ToLong(Eval(f.Args[0], ctx) ?? 1);
                    if (buckets <= 0) throw Err.New(4184, "The NTILE function requires a positive bigint argument.");
                    long size = m / buckets, rem = m % buckets;
                    int pos = 0;
                    for (long b = 1; b <= buckets && pos < m; b++)
                    {
                        long count = size + (b <= rem ? 1 : 0);
                        for (long c = 0; c < count && pos < m; c++) result[idx[pos++]] = b;
                    }
                    continue;
                }
                case "LAG":
                case "LEAD":
                {
                    for (int k = 0; k < m; k++)
                    {
                        SetItem(idx[k]);
                        long off = f.Args.Count > 1 ? TypeSystem.ToLong(Eval(f.Args[1], ctx) ?? 1) : 1;
                        long target = name == "LAG" ? k - off : k + off;
                        if (target >= 0 && target < m)
                        {
                            SetItem(idx[target]);
                            result[idx[k]] = Eval(f.Args[0], ctx);
                        }
                        else result[idx[k]] = f.Args.Count > 2 ? Eval(f.Args[2], ctx) : null;
                    }
                    continue;
                }
            }

            // ---- framed functions: aggregates, FIRST_VALUE, LAST_VALUE
            bool isAgg = Aggregates.IsAggregate(name) && name != "STRING_AGG";
            if (!isAgg && name is not ("FIRST_VALUE" or "LAST_VALUE"))
                throw Err.New(10753, $"The function '{name}' is not a valid windowing function, and cannot be used with the OVER clause.", 15);

            var vals = new object?[m];
            for (int k = 0; k < m; k++)
            {
                if (f.Star || f.Args.Count == 0) { vals[k] = 1; continue; }
                SetItem(idx[k]);
                vals[k] = Eval(f.Args[0], ctx);
            }

            // peer group boundaries (rows with equal ORDER BY keys)
            var peerStart = new int[m];
            var peerEnd = new int[m];
            for (int k = 0; k < m; k++) peerStart[k] = k > 0 && Peer(k, k - 1) ? peerStart[k - 1] : k;
            for (int k = m - 1; k >= 0; k--) peerEnd[k] = k < m - 1 && Peer(k, k + 1) ? peerEnd[k + 1] : k;

            (int Lo, int Hi) FrameOf(int k)
            {
                if (!over.HasFrame) return over.Order.Count == 0 ? (0, m - 1) : (0, peerEnd[k]);
                int lo = over.StartKind switch
                {
                    BoundKind.UnboundedPreceding => 0,
                    BoundKind.Preceding => over.Rows ? (int)Math.Max((long)k - over.StartOffset, 0) : throw Err.NotSupported("RANGE with an offset"),
                    BoundKind.CurrentRow => over.Rows ? k : peerStart[k],
                    BoundKind.Following => over.Rows ? (int)Math.Min((long)k + over.StartOffset, m) : throw Err.NotSupported("RANGE with an offset"),
                    _ => m,
                };
                int hi = over.EndKind switch
                {
                    BoundKind.UnboundedFollowing => m - 1,
                    BoundKind.Following => over.Rows ? (int)Math.Min((long)k + over.EndOffset, m - 1) : throw Err.NotSupported("RANGE with an offset"),
                    BoundKind.CurrentRow => over.Rows ? k : peerEnd[k],
                    BoundKind.Preceding => over.Rows ? (int)Math.Max((long)k - over.EndOffset, -1) : throw Err.NotSupported("RANGE with an offset"),
                    _ => -1,
                };
                return (Math.Max(lo, 0), Math.Min(hi, m - 1));
            }

            SqlType? argType = f.Args.Count > 0 ? InferType(f.Args[0], ctx) : null;
            if (!isAgg)
            {
                for (int k = 0; k < m; k++)
                {
                    var (lo, hi) = FrameOf(k);
                    result[idx[k]] = lo > hi ? null : vals[name == "FIRST_VALUE" ? lo : hi];
                }
                continue;
            }

            bool growing = !over.HasFrame || over.StartKind == BoundKind.UnboundedPreceding;
            if (growing)
            {
                var agg = Aggregates.Create(name, argType, f.Star);
                int added = 0;
                for (int k = 0; k < m; k++)
                {
                    var (_, hi) = FrameOf(k);
                    while (added <= hi) agg.Add(vals[added++]);
                    result[idx[k]] = agg.Result();
                }
            }
            else
            {
                for (int k = 0; k < m; k++)
                {
                    var (lo, hi) = FrameOf(k);
                    var agg = Aggregates.Create(name, argType, f.Star);
                    for (int q = lo; q <= hi; q++) agg.Add(vals[q]);
                    result[idx[k]] = agg.Result();
                }
            }
        }
        return result;
    }
}

internal sealed class DefaultMarker
{
    public static readonly DefaultMarker Instance = new();
}
