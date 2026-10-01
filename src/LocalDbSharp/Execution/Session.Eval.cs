using LocalDbSharp.Execution;
using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

public sealed partial class Session
{
    static readonly object True = true, False = false;

    static bool IsTrue(object? v) => v is true;

    static object? Not3(object? v) => v == null ? null : (bool)v ? False : True;

    // ------------------------------------------------------------ expression evaluation

    internal object? Eval(Expr e, EvalContext? ctx)
    {
        if (ctx?.ItemNulled != null && ctx.ItemNulled[ctx.ItemIndex] is { } nulled && nulled.Contains(e)) return null;
        switch (e)
        {
            case Literal l: return l.Value;
            case ColumnRef c: return EvalColumn(c, ctx);
            case VarRef v: return GetVar(v.Name);
            case Binary b: return EvalBinary(b, ctx);
            case Unary u:
            {
                object? v = Eval(u.E, ctx);
                switch (u.Op)
                {
                    case "NOT": return Not3(ToBool3(v));
                    case "-": return TypeSystem.Negate(v);
                    case "~":
                        return v switch
                        {
                            null => null,
                            int i => ~i,
                            long l => ~l,
                            short s => (short)~s,
                            byte b => (byte)~b,
                            bool b => !b,
                            _ => throw Err.New(8117, $"Operand data type {TypeSystem.TypeOf(v).Name} is invalid for '~' operator."),
                        };
                    default: return v;
                }
            }
            case FuncCall f: return EvalFunc(f, ctx);
            case CastExpr c: return EvalCast(c, ctx);
            case CaseExpr c: return EvalCase(c, ctx);
            case IsNullExpr n: return (Eval(n.E, ctx) == null) != n.Not ? True : False;
            case BetweenExpr b:
            {
                object? v = Eval(b.E, ctx), lo = Eval(b.Lo, ctx), hi = Eval(b.Hi, ctx);
                bool uni = v is string && (lo is string || hi is string) && (IsUnicodeCompare(b.E, b.Lo, ctx) || IsUnicodeCompare(b.E, b.Hi, ctx));
                object? ge = v == null || lo == null ? null : TypeSystem.CompareForSort(v, lo, uni) >= 0 ? True : False;
                object? le = v == null || hi == null ? null : TypeSystem.CompareForSort(v, hi, uni) <= 0 ? True : False;
                object? r = And3(ge, le);
                return b.Not ? Not3(r) : r;
            }
            case LikeExpr l:
            {
                object? v = Eval(l.E, ctx), p = Eval(l.Pattern, ctx);
                if (v == null || p == null) return null;
                char esc = '\0';
                if (l.Escape != null)
                {
                    string es = TypeSystem.ToStr(Eval(l.Escape, ctx) ?? "");
                    if (es.Length != 1) throw Err.New(506, "The invalid escape character '" + es + "' was specified in a LIKE predicate.");
                    esc = es[0];
                }
                if (v is not string) v = AdaptImplicit(v, InferType(l.E, ctx), SqlType.VarCharMax);
                bool m = TypeSystem.Like(TypeSystem.ToStr(v!), TypeSystem.ToStr(p), esc, v is string && InferType(l.E, ctx)?.IsUnicode == true);
                return m != l.Not ? True : False;
            }
            case InExpr i: return EvalIn(i, ctx);
            case ExistsExpr x: return ExecSubquery(x.Q, ctx, 1).Rows.Count > 0 ? True : False;
            case SubqueryExpr s:
            {
                var rs = ExecSubquery(s.Q, ctx, 2);
                if (rs.Schema.Count != 1) throw Err.New(116, "Only one expression can be specified in the select list when the subquery is not introduced with EXISTS.");
                if (rs.Rows.Count > 1)
                    throw Err.New(512, "Subquery returned more than 1 value. This is not permitted when the subquery follows =, !=, <, <= , >, >= or when the subquery is used as an expression.");
                // the subquery's column type is only known once it has run; remember it for result metadata
                if (rs.Schema.Cols[0].Type is { } subType) _typeCache[s] = subType;
                return rs.Rows.Count == 0 ? null : rs.Rows[0][0];
            }
            case QuantifiedExpr q: return EvalQuantified(q, ctx);
            case DefaultExpr: return DefaultMarker.Instance;
            case NextValueExpr n: return NextSequenceValue(n.Sequence);
            default: throw Err.NotSupported(e.GetType().Name);
        }
    }

    readonly Dictionary<FuncCall, double> _randCache = [];

    object ToDeclaredType(string s, Expr other, EvalContext? ctx)
    {
        var type = InferType(other, ctx);
        if (type == null || type.IsString || type.IsBinary || type.Kind == SqlTypeKind.Variant || type.IsInteger || type.Kind == SqlTypeKind.Bit) return s;
        return TypeSystem.ConvertTo(s, type)!;
    }

    /// <summary>
    /// Prepares a value for an implicit conversion where the result depends on the declared source type, which
    /// the CLR value alone no longer carries: date / datetime2 / time / money rendered as text, money to integer.
    /// </summary>
    static object? AdaptImplicit(object? v, SqlType? source, SqlType? target)
    {
        if (v == null || source == null || target == null) return v;
        if (target.IsString)
        {
            if (v is DateTime or DateTimeOffset or TimeSpan) return TypeSystem.ToStr(v, 0, source);
            if (v is decimal && source.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney) return TypeSystem.ToStr(v, 0, source);
        }
        else if ((target.IsInteger || target.Kind == SqlTypeKind.Bit) && v is decimal d && source.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney)
            return Math.Round(d, 0, MidpointRounding.AwayFromZero);
        return v;
    }

    /// <summary>Evaluates an expression whose value is about to be stored in something of type 'target'.</summary>
    object? EvalFor(Expr e, EvalContext? ctx, SqlType? target)
    {
        object? v = Eval(e, ctx);
        return v is DateTime or DateTimeOffset or TimeSpan or decimal ? AdaptImplicit(v, InferType(e, ctx), target) : v;
    }

    bool IsUnicodeCompare(Expr l, Expr r, EvalContext? ctx)
        => InferType(l, ctx)?.IsUnicode == true || InferType(r, ctx)?.IsUnicode == true;

    static object? ToBool3(object? v) => v switch
    {
        null => null,
        bool => v,
        _ => throw Err.New(4145, "An expression of non-boolean type specified in a context where a condition is expected."),
    };

    static object? And3(object? a, object? b)
    {
        if (a is false || b is false) return False;
        if (a == null || b == null) return null;
        return True;
    }

    object? EvalColumn(ColumnRef col, EvalContext? ctx)
    {
        for (var c = ctx; c != null; c = c.Outer)
        {
            if (c.Schema == null) continue;
            int i = c.Schema.TryResolve(col.Table, col.Name);
            if (i == -1) continue;
            if (i == -2) throw Err.New(209, $"Ambiguous column name '{col.Name}'.");
            if (!ReferenceEquals(c, ctx))
            {
                // referencing an enclosing query: mark every subquery in between as correlated
                var stop = c.Env?.Marker;
                for (var m = ctx!.Env?.Marker; m != null && m != stop; m = m.Parent) m.Correlated = true;
                if (c.ItemGroupedOut != null && c.ItemGroupedOut[c.ItemIndex] is { } groupedOut && groupedOut.Contains("#" + i)) return null;
            }
            return c.Row?[i];
        }
        if (col.Table != null) throw Err.New(4104, $"The multi-part identifier \"{col}\" could not be bound.");
        throw Err.InvalidColumn(col.Name);
    }

    static bool CompareOp(string op, int c) => op switch
    {
        "=" => c == 0,
        "<>" => c != 0,
        "<" => c < 0,
        "<=" => c <= 0,
        ">" => c > 0,
        _ => c >= 0,
    };

    /// <summary>
    /// Concatenating non-MAX strings yields at most varchar(8000) / nvarchar(4000): longer results are cut off,
    /// a classic T-SQL surprise that scripts nevertheless depend on.
    /// </summary>
    object? CapConcat(Binary b, object? result, EvalContext? ctx)
    {
        if (result is not string { Length: > 4000 } s) return result;
        var type = InferType(b, ctx);
        return type is { IsString: true } && type.Length != SqlType.Max && s.Length > type.Length ? s[..type.Length] : s;
    }

    object? EvalLogicalChain(Binary b, EvalContext? ctx)
    {
        string op = b.Op;
        var terms = new List<Expr>();
        Expr cur = b;
        while (cur is Binary bin && bin.Op == op)
        {
            terms.Add(bin.R);
            cur = bin.L;
        }
        terms.Add(cur);
        bool sawNull = false;
        for (int i = terms.Count - 1; i >= 0; i--)
        {
            object? v = ToBool3(Eval(terms[i], ctx));
            if (v == null) sawNull = true;
            else if ((bool)v == (op == "OR")) return op == "OR" ? True : False; // short circuit
        }
        return sawNull ? null : op == "OR" ? False : True;
    }

    object? EvalBinary(Binary b, EvalContext? ctx)
    {
        switch (b.Op)
        {
            case "AND":
            case "OR":
            {
                // Chains such as "a OR b OR c OR ..." parse into a left-deep tree; generated SQL can make them
                // thousands of terms long, so walk the spine iteratively instead of recursing into it.
                if (b.L is Binary { Op: var op } && op == b.Op) return EvalLogicalChain(b, ctx);
                object? l = ToBool3(Eval(b.L, ctx));
                if (b.Op == "AND")
                {
                    if (l is false) return False;
                    return And3(l, ToBool3(Eval(b.R, ctx)));
                }
                if (l is true) return True;
                object? r = ToBool3(Eval(b.R, ctx));
                if (r is true) return True;
                return l == null || r == null ? null : False;
            }
            case "+" when b.L is Binary { Op: "+" }:
            {
                var terms = new List<Expr>();
                Expr cur = b;
                while (cur is Binary { Op: "+" } plus)
                {
                    terms.Add(plus.R);
                    cur = plus.L;
                }
                object? acc = Eval(cur, ctx);
                for (int i = terms.Count - 1; i >= 0; i--)
                {
                    object? term = Eval(terms[i], ctx);
                    acc = acc == null || term == null ? null : TypeSystem.Arith("+", acc, term);
                }
                return CapConcat(b, acc, ctx);
            }
            case "=": case "<>": case "<": case "<=": case ">": case ">=":
            {
                object? l = Eval(b.L, ctx);
                if (l == null) return null;
                object? r = Eval(b.R, ctx);
                if (r == null) return null;
                // a string compared with a value of another type is first converted to that value's declared
                // type (rounded to its scale, to datetime's resolution, to a date, ...)
                if (l is string ls && r is not string) l = ToDeclaredType(ls, b.R, ctx);
                else if (r is string rs && l is not string) r = ToDeclaredType(rs, b.L, ctx);
                // ordering comparisons of nvarchar data follow Unicode sorting rules
                if (b.Op.Length > 0 && b.Op[0] is '<' or '>' && b.Op != "<>" && l is string && r is string && IsUnicodeCompare(b.L, b.R, ctx))
                    return CompareOp(b.Op, TypeSystem.CompareForSort(l, r, true)) ? True : False;
                return CompareOp(b.Op, TypeSystem.Compare(l, r)) ? True : False;
            }
            default:
            {
                object? l = Eval(b.L, ctx), r = Eval(b.R, ctx);
                if (l == null || r == null) return null;
                if (b.Op is "/" or "*" && (l is decimal || r is decimal) && l is not (double or float) && r is not (double or float))
                {
                    var (lt, rt) = OperandTypes(b, ctx);
                    return TypeSystem.Arith(b.Op, l, r, lt, rt);
                }
                return CapConcat(b, TypeSystem.Arith(b.Op, l, r), ctx);
            }
        }
    }

    object? EvalCast(CastExpr c, EvalContext? ctx)
    {
        object? v = Eval(c.E, ctx);
        if (v == null) return null;
        int style = 0;
        if (c.Style != null)
        {
            object? s = Eval(c.Style, ctx);
            if (s == null) return null;
            style = (int)TypeSystem.ToLong(s);
        }
        // an explicit style 0 is "the default style with a month name", unlike a conversion without a style
        if (c.Style != null && style == 0 && v is DateTime or TimeSpan) style = 100;
        // the textual form of dates / money depends on the declared source type
        SqlType? src = c.Type.IsString && v is DateTime or DateTimeOffset or TimeSpan or decimal || c.Type.IsBinary && v is string
            ? InferType(c.E, ctx)
            : null;
        // money rounds when converted to an integer; other exact numerics truncate
        if (v is decimal money && (c.Type.IsInteger || c.Type.Kind == SqlTypeKind.Bit) && InferType(c.E, ctx)?.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney)
            v = Math.Round(money, 0, MidpointRounding.AwayFromZero);
        if (!c.Try) return TypeSystem.ConvertTo(v, c.Type, style, src);
        try
        {
            return TypeSystem.ConvertTo(v, c.Type, style, src);
        }
        catch (TSqlException)
        {
            return null;
        }
    }

    object? EvalCase(CaseExpr c, EvalContext? ctx)
    {
        object? result = null;
        bool matched = false;
        if (c.Operand != null)
        {
            object? op = Eval(c.Operand, ctx);
            foreach (var (when, then) in c.Whens)
            {
                object? w = Eval(when, ctx);
                if (op == null || w == null || TypeSystem.Compare(op, w) != 0) continue;
                result = Eval(then, ctx);
                matched = true;
                break;
            }
        }
        else
        {
            foreach (var (when, then) in c.Whens)
            {
                if (!IsTrue(Eval(when, ctx))) continue;
                result = Eval(then, ctx);
                matched = true;
                break;
            }
        }
        if (!matched && c.Else != null) result = Eval(c.Else, ctx);
        return CoerceTo(result, InferType(c, ctx));
    }

    /// <summary>Converts a branch result to the statically inferred type of the whole expression.</summary>
    static object? CoerceTo(object? value, SqlType? type)
    {
        if (value == null || type == null || type.Kind == SqlTypeKind.Variant) return value;
        if (value.GetType() == type.ClrType && value is not (decimal or string or DateTime)) return value;
        if (value is string && type.IsString && type.Kind is SqlTypeKind.VarChar or SqlTypeKind.NVarChar) return value;
        if (type.IsDecimalLike && TypeSystem.NumRank(value) is > 0 and < 7)
        {
            // adjust the scale only; the inferred precision is an estimate and must not cause overflow errors
            decimal d = TypeSystem.ToDecimal(value);
            if (d.Scale > type.Scale) d = Math.Round(d, Math.Min(type.Scale, 28), MidpointRounding.AwayFromZero);
            return TypeSystem.SetScale(d, type.Scale);
        }
        return TypeSystem.ConvertTo(value, type);
    }

    // ------------------------------------------------------------ subqueries

    RowSet ExecSubquery(Query q, EvalContext? ctx, int limit = int.MaxValue)
    {
        if (_stmtCache.TryGetValue(q, out var ce) && ReferenceEquals(ce.Frame, Frame) && ce.Version == Frame.Version) return ce.Rows;
        var marker = new Marker(ctx?.Env?.Marker);
        var env = new QueryEnv(ctx?.Env) { Marker = marker };
        var rs = ExecQuery(q, ctx, env, limit);
        if (!marker.Correlated) _stmtCache[q] = (Frame, Frame.Version, rs);
        return rs;
    }

    object? EvalIn(InExpr i, EvalContext? ctx)
    {
        object? v = Eval(i.E, ctx);
        bool found = false, sawNull = false;
        if (i.List != null)
        {
            if (v == null) return null;
            foreach (var item in i.List)
            {
                object? x = Eval(item, ctx);
                if (x == null) sawNull = true;
                else if (TypeSystem.Compare(v, x) == 0) { found = true; break; }
            }
        }
        else
        {
            var rs = ExecSubquery(i.Sub!, ctx);
            if (rs.Schema.Count != 1) throw Err.New(116, "Only one expression can be specified in the select list when the subquery is not introduced with EXISTS.");
            if (rs.Rows.Count == 0) return i.Not ? True : False;
            if (v == null) return null;
            bool cached = _stmtCache.TryGetValue(i.Sub!, out var ce) && ReferenceEquals(ce.Rows, rs);
            bool hashed = false;
            if (cached && rs.Rows.Count > 8)
            {
                var set = rs.InSet;
                if (set == null)
                {
                    set = new InSet();
                    foreach (var row in rs.Rows)
                    {
                        if (row[0] == null) { set.HasNull = true; continue; }
                        object k = TypeSystem.NormalizeKey(row[0]);
                        set.Mask |= TypeSystem.KeyClass(k);
                        set.Keys.Add(k);
                    }
                    rs.InSet = set;
                }
                object key = TypeSystem.NormalizeKey(v);
                if (TypeSystem.HashCompatible(set.Mask | TypeSystem.KeyClass(key)))
                {
                    hashed = true;
                    found = set.Keys.Contains(key);
                    sawNull = set.HasNull;
                }
            }
            if (!hashed)
            {
                foreach (var row in rs.Rows)
                {
                    if (row[0] == null) sawNull = true;
                    else if (TypeSystem.Compare(v, row[0]!) == 0) { found = true; break; }
                }
            }
        }
        object? result = found ? True : sawNull ? null : False;
        return i.Not ? Not3(result) : result;
    }

    object? EvalQuantified(QuantifiedExpr q, EvalContext? ctx)
    {
        object? v = Eval(q.E, ctx);
        var rs = ExecSubquery(q.Q, ctx);
        if (rs.Schema.Count != 1) throw Err.New(116, "Only one expression can be specified in the select list when the subquery is not introduced with EXISTS.");
        bool sawNull = false;
        foreach (var row in rs.Rows)
        {
            if (v == null || row[0] == null) { sawNull = true; continue; }
            bool r = CompareOp(q.Op, TypeSystem.Compare(v, row[0]!));
            if (q.All && !r) return False;
            if (!q.All && r) return True;
        }
        if (sawNull) return null;
        return q.All ? True : False;
    }

    // ------------------------------------------------------------ functions

    object? EvalFunc(FuncCall f, EvalContext? ctx)
    {
        if (f.Over != null)
        {
            for (var c = ctx; c != null; c = c.Outer)
                if (c.WindowValues != null && c.WindowValues.TryGetValue(f, out var values)) return values[c.ItemIndex];
            throw Err.New(4108, "Windowed functions can only appear in the SELECT or ORDER BY clauses.");
        }
        if (f.Schema == null)
        {
            if (Aggregates.IsAggregate(f.Name)) return EvalAggregate(f, ctx);
            switch (f.Name)
            {
                case "COALESCE":
                {
                    foreach (var a in f.Args)
                    {
                        object? v = Eval(a, ctx);
                        if (v != null) return CoerceTo(v, InferType(f, ctx));
                    }
                    return null;
                }
                case "ISNULL":
                {
                    CheckArgs(f, 2, 2);
                    object? v = Eval(f.Args[0], ctx);
                    if (v != null) return v;
                    v = Eval(f.Args[1], ctx);
                    // the result always has the type of the first argument (so strings may be truncated)
                    var t0 = InferType(f.Args[0], ctx);
                    if (v == null || t0 == null) return v;
                    v = AdaptImplicit(v, InferType(f.Args[1], ctx), t0);
                    return t0.IsString ? TypeSystem.ConvertTo(v, t0) : CoerceTo(v, t0);
                }
                case "IIF":
                {
                    CheckArgs(f, 3, 3);
                    object? v = IsTrue(Eval(f.Args[0], ctx)) ? Eval(f.Args[1], ctx) : Eval(f.Args[2], ctx);
                    return CoerceTo(v, InferType(f, ctx));
                }
                case "NULLIF":
                {
                    CheckArgs(f, 2, 2);
                    object? a = Eval(f.Args[0], ctx), b = Eval(f.Args[1], ctx);
                    return a != null && b != null && TypeSystem.Compare(a, b) == 0 ? null : a;
                }
                case "CHOOSE":
                {
                    object? ix = Eval(f.Args[0], ctx);
                    if (ix == null) return null;
                    long n = TypeSystem.ToLong(ix);
                    return n >= 1 && n < f.Args.Count ? CoerceTo(Eval(f.Args[(int)n], ctx), InferType(f, ctx)) : null;
                }
                case "CONCAT":
                case "CONCAT_WS":
                {
                    bool ws = f.Name == "CONCAT_WS";
                    var sb = new System.Text.StringBuilder();
                    string? sep = null;
                    bool first = true;
                    for (int i = 0; i < f.Args.Count; i++)
                    {
                        object? v = Eval(f.Args[i], ctx);
                        if (ws && i == 0)
                        {
                            sep = v == null ? "" : TypeSystem.ToStr(v);
                            continue;
                        }
                        if (v == null) continue;
                        if (ws && !first) sb.Append(sep);
                        first = false;
                        sb.Append(v as string ?? TypeSystem.ToStr(v, 0, InferType(f.Args[i], ctx)));
                    }
                    if (sb.Length > 4000 && InferType(f, ctx) is { } ct && ct.Length != SqlType.Max && sb.Length > ct.Length) sb.Length = ct.Length;
                    return sb.ToString();
                }
                case "GROUPING":
                case "GROUPING_ID":
                {
                    int bits = 0;
                    var owner = ctx;
                    while (owner != null && owner.Group == null) owner = owner.Outer;
                    var groupedOut = owner?.ItemGroupedOut?[owner.ItemIndex];
                    foreach (var a in f.Args)
                        bits = (bits << 1) | (groupedOut != null && groupedOut.Contains(ExprKey(a, owner!.Schema)) ? 1 : 0);
                    return f.Name == "GROUPING" ? (byte)bits : bits;
                }
                case "UPDATE":
                {
                    // UPDATE(column) inside a trigger
                    var frame = Frame;
                    if (frame.TriggerTable == null) throw Err.New(195, "'UPDATE' is not a recognized built-in function name.", 15);
                    int ci = frame.TriggerTable.ColumnIndex((string)((Literal)f.Args[0]).Value!);
                    if (ci < 0) throw Err.InvalidColumn((string)((Literal)f.Args[0]).Value!);
                    return frame.UpdatedColumns == null || frame.UpdatedColumns.Contains(ci) ? True : False;
                }
                case "DATALENGTH":
                {
                    CheckArgs(f, 1, 1);
                    object? v = Eval(f.Args[0], ctx);
                    return v == null ? null : Functions.DataLength(v, InferType(f.Args[0], ctx));
                }
                case "SQL_VARIANT_PROPERTY":
                {
                    object? v = Eval(f.Args[0], ctx);
                    var t = InferType(f.Args[0], ctx) ?? (v == null ? null : TypeSystem.TypeOf(v));
                    return t?.Name;
                }
            }
            if (Functions.Table.TryGetValue(f.Name, out var fn))
            {
                if (f.Name == "RAND" && f.Args.Count == 0)
                {
                    // RAND() without a seed is evaluated once per statement, not once per row
                    if (!_randCache.TryGetValue(f, out double once)) _randCache[f] = once = Random.NextDouble();
                    return once;
                }
                var args = new object?[f.Args.Count];
                bool textual = Functions.TakesText(f.Name);
                for (int i = 0; i < args.Length; i++)
                {
                    args[i] = Eval(f.Args[i], ctx);
                    // dates and money turn into text according to their declared type
                    if (textual && args[i] is DateTime or DateTimeOffset or TimeSpan or decimal)
                        args[i] = AdaptImplicit(args[i], InferType(f.Args[i], ctx), SqlType.VarCharMax);
                }
                if (f.Name == "HASHBYTES" && args.Length == 2 && args[1] is string hashed && InferType(f.Args[1], ctx)?.IsUnicode == true)
                    args[1] = System.Text.Encoding.Unicode.GetBytes(hashed);
                object? result;
                try
                {
                    result = fn(this, args);
                }
                catch (OverflowException)
                {
                    throw Err.Overflow("int");
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or IndexOutOfRangeException or System.Text.Json.JsonException)
                {
                    throw Err.New(8116, $"Argument data type or value is invalid for the {f.Name.ToLowerInvariant()} function.");
                }
                if (f.Name == "DATEADD" && result is DateTime added && f.Args.Count == 3)
                {
                    // the result keeps the type of the date argument
                    switch (InferType(f.Args[2], ctx)?.Kind)
                    {
                        case SqlTypeKind.DateTime: return TypeSystem.RoundDatetime(added);
                        case SqlTypeKind.SmallDateTime: return TypeSystem.ConvertTo(added, SqlType.SmallDateTime);
                        case SqlTypeKind.Date:
                            if (args[0] is string part && Functions.IsTimePart(part))
                                throw Err.New(9810, $"The datepart {part} is not supported by date function dateadd for data type date.");
                            return added.Date;
                    }
                }
                // string functions on non-MAX input cannot produce more than 8000 (4000 Unicode) characters
                if (result is string { Length: > 4000 } text && InferType(f, ctx) is { IsString: true } rt && rt.Length != SqlType.Max && text.Length > rt.Length)
                    return text[..rt.Length];
                return result;
            }
        }
        var udf = FindObject(new TableName { Name = f.Name, Schema = f.Schema }) as Function;
        if (udf == null || udf.Def.ReturnType == null)
        {
            if (f.Schema == null) throw Err.New(195, $"'{f.Name}' is not a recognized built-in function name.", 15);
            throw Err.New(4121, $"Cannot find either column \"{f.Schema}\" or the user-defined function or aggregate \"{f.Schema}.{f.Name}\", or the name is ambiguous.");
        }
        var values2 = new object?[f.Args.Count];
        for (int i = 0; i < values2.Length; i++) values2[i] = EvalArgument(f.Args[i], ctx);
        return CallScalarFunction(udf, values2);
    }

    /// <summary>
    /// Canonical text of an expression, used to recognise "the same expression" in GROUP BY and the select list.
    /// Column references are identified by their position in the given schema.
    /// </summary>
    internal static string ExprKey(Expr e, RowSchema? schema)
    {
        switch (e)
        {
            case ColumnRef c:
            {
                int i = schema?.TryResolve(c.Table, c.Name) ?? -1;
                return i >= 0 ? "#" + i : "c:" + c.ToString().ToLowerInvariant();
            }
            case Literal l: return "l:" + (l.Value == null ? "null" : l.Value.GetType().Name + ":" + TypeSystem.ToStr(l.Value));
            case VarRef v: return v.Name.ToLowerInvariant();
            case Unary u: return "(" + u.Op + ExprKey(u.E, schema) + ")";
            case Binary b: return "(" + ExprKey(b.L, schema) + b.Op + ExprKey(b.R, schema) + ")";
            case CastExpr c: return "cast(" + ExprKey(c.E, schema) + " as " + c.Type + (c.Style != null ? "," + ExprKey(c.Style, schema) : "") + ")";
            case FuncCall { Over: null } f: return f.Name + "(" + (f.Distinct ? "distinct " : "") + string.Join(",", f.Args.Select(a => ExprKey(a, schema))) + ")";
            case IsNullExpr n: return "(" + ExprKey(n.E, schema) + (n.Not ? " is not null)" : " is null)");
            case CaseExpr c:
                return "case " + (c.Operand != null ? ExprKey(c.Operand, schema) : "") + string.Concat(c.Whens.Select(w => " when " + ExprKey(w.When, schema) + " then " + ExprKey(w.Then, schema)))
                       + (c.Else != null ? " else " + ExprKey(c.Else, schema) : "") + " end";
            default: return "?" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
        }
    }

    static void CheckArgs(FuncCall f, int min, int max)
    {
        if (f.Args.Count < min || f.Args.Count > max)
            throw Err.New(174, $"The {f.Name.ToLowerInvariant()} function requires {(min == max ? min.ToString() : min + " to " + max)} argument(s).", 15);
    }

    object? CallScalarFunction(Function fn, object?[] args)
    {
        var frame = BindFunctionArgs(fn, args);
        var savedDb = Db;
        var savedReturn = _returnValue;
        var savedSink = _sink;
        if (fn.Database != null) Db = fn.Database;
        _returnValue = null;
        _sink = new ExecutionResult();
        _functionDepth++;
        try
        {
            RunInFrame(frame, () => ExecBlock(fn.Def.Body!));
            return TypeSystem.ConvertTo(_returnValue, fn.Def.ReturnType!);
        }
        finally
        {
            _functionDepth--;
            Db = savedDb;
            _returnValue = savedReturn;
            _sink = savedSink;
        }
    }

    object? EvalAggregate(FuncCall f, EvalContext? ctx)
    {
        // the nearest enclosing context that is being aggregated owns this call
        var owner = ctx;
        while (owner != null && owner.Group == null) owner = owner.Outer;
        if (owner == null)
            throw Err.New(147, "An aggregate may not appear in the WHERE clause unless it is in a subquery contained in a HAVING clause or a select list, and the column being aggregated is an outer reference.", 15);
        if (!ReferenceEquals(owner, ctx))
        {
            var stop = owner.Env?.Marker;
            for (var m = ctx!.Env?.Marker; m != null && m != stop; m = m.Parent) m.Correlated = true;
        }
        var group = owner.Group!;
        var sub = owner.AggScratch ??= new EvalContext(owner.Schema, owner.Outer, owner.Env);

        if (f.Name == "STRING_AGG")
        {
            if (f.Args.Count != 2) throw Err.New(174, "The STRING_AGG function requires 2 argument(s).", 15);
            var parts = new List<(string Text, object?[]? Keys)>();
            string sep = "";
            foreach (var row in group)
            {
                sub.Row = row;
                object? v = Eval(f.Args[0], sub);
                if (v == null) continue;
                sep = TypeSystem.ToStr(Eval(f.Args[1], sub) ?? "");
                object?[]? keys = null;
                if (f.WithinGroup != null)
                {
                    keys = new object?[f.WithinGroup.Count];
                    for (int k = 0; k < keys.Length; k++) keys[k] = Eval(f.WithinGroup[k].E, sub);
                }
                parts.Add((TypeSystem.ToStr(v, 0, InferType(f.Args[0], sub)), keys));
            }
            if (parts.Count == 0) return null;
            if (f.WithinGroup != null)
            {
                var idx = SortIndexes(parts.Select(p => p.Keys!).ToArray(), f.WithinGroup);
                return string.Join(sep, idx.Select(i => parts[i].Text));
            }
            return string.Join(sep, parts.Select(p => p.Text));
        }

        if (f.Star) return f.Name == "COUNT_BIG" ? (object)(long)group.Count : group.Count;
        if (f.Args.Count != 1) throw Err.New(174, $"The {f.Name} function requires 1 argument(s).", 15);
        var arg = f.Args[0];
        sub.Row = group.Count > 0 ? group[0] : null;
        var agg = Aggregates.Create(f.Name, InferType(arg, sub), false);
        HashSet<object>? seen = f.Distinct ? [] : null;
        int direct = arg is ColumnRef cr && owner.Schema != null ? owner.Schema.TryResolve(cr.Table, cr.Name) : -1;
        foreach (var row in group)
        {
            object? v;
            if (direct >= 0) v = row[direct];
            else
            {
                sub.Row = row;
                v = Eval(arg, sub);
            }
            if (v == null) continue;
            if (seen != null && !seen.Add(TypeSystem.NormalizeKey(v))) continue;
            agg.Add(v);
        }
        return agg.Result();
    }

    // ------------------------------------------------------------ static type inference

    static (int P, int S)? NumericShape(SqlType t) => t.Kind switch
    {
        SqlTypeKind.Bit => (1, 0),
        SqlTypeKind.TinyInt => (3, 0),
        SqlTypeKind.SmallInt => (5, 0),
        SqlTypeKind.Int => (10, 0),
        SqlTypeKind.BigInt => (19, 0),
        SqlTypeKind.Money => (19, 4),
        SqlTypeKind.SmallMoney => (10, 4),
        SqlTypeKind.Decimal => (t.Precision, t.Scale),
        _ => null,
    };

    static SqlType CapDecimal(int p, int s)
    {
        if (p > 38)
        {
            int intPart = p - s;
            s = Math.Max(Math.Min(s, 38 - intPart), Math.Min(s, 6));
            p = 38;
        }
        return SqlType.Decimal(Math.Max(p, 1), Math.Max(s, 0));
    }

    /// <summary>Result type when values of two types are combined (UNION, CASE, COALESCE, VALUES).</summary>
    internal static SqlType? Unify(SqlType? a, SqlType? b)
    {
        if (a == null) return b;
        if (b == null || a == b) return a;
        var hi = a.Kind >= b.Kind ? a : b;
        var lo = a.Kind >= b.Kind ? b : a;
        if (hi.IsString && lo.IsString)
        {
            int len = a.Length == SqlType.Max || b.Length == SqlType.Max ? SqlType.Max : Math.Max(a.Length, b.Length);
            var kind = hi.Kind;
            // char + char stays char (padded to the longer); mixing with a variable-length type gives varchar
            bool bothFixed = a.Kind is SqlTypeKind.Char or SqlTypeKind.NChar && b.Kind is SqlTypeKind.Char or SqlTypeKind.NChar;
            if (!bothFixed && kind is SqlTypeKind.Char) kind = SqlTypeKind.VarChar;
            if (!bothFixed && kind is SqlTypeKind.NChar) kind = SqlTypeKind.NVarChar;
            return new SqlType(kind, len);
        }
        if (hi.IsBinary && lo.IsBinary)
            return new SqlType(SqlTypeKind.VarBinary, a.Length == SqlType.Max || b.Length == SqlType.Max ? SqlType.Max : Math.Max(a.Length, b.Length));
        if (hi.Kind == SqlTypeKind.Decimal && NumericShape(lo) is var (p2, s2))
        {
            int scale = Math.Max(hi.Scale, s2);
            int intDigits = Math.Max(hi.Precision - hi.Scale, p2 - s2);
            return CapDecimal(intDigits + scale, scale);
        }
        if (hi.Kind is SqlTypeKind.DateTime2 or SqlTypeKind.Time or SqlTypeKind.DateTimeOffset && lo.Kind == hi.Kind)
            return hi with { Scale = Math.Max(hi.Scale, lo.Scale) };
        return hi;
    }

    /// <summary>
    /// Type of an arithmetic operand. An integer literal that meets a decimal is typed with the minimal
    /// precision that holds it (3 is numeric(1,0)), which determines the scale of decimal division.
    /// </summary>
    (SqlType? Left, SqlType? Right) OperandTypes(Binary b, EvalContext? ctx)
    {
        SqlType? l = InferType(b.L, ctx), r = InferType(b.R, ctx);
        static SqlType Minimal(int i) => SqlType.Decimal(Math.Abs((long)i).ToString().Length, 0);
        if (b.L is Literal { Value: int li } && r?.Kind == SqlTypeKind.Decimal) l = Minimal(li);
        if (b.R is Literal { Value: int ri } && l?.Kind == SqlTypeKind.Decimal) r = Minimal(ri);
        return (l, r);
    }

    static SqlType? ArithType(string op, SqlType? l, SqlType? r)
    {
        if (l == null || r == null) return l ?? r;
        if (l.IsString && r.IsString)
        {
            if (op != "+") return null;
            bool max = l.Length == SqlType.Max || r.Length == SqlType.Max;
            bool unicode = l.IsUnicode || r.IsUnicode;
            int len = max ? SqlType.Max : l.Length + r.Length;
            if (len > (unicode ? 4000 : 8000)) len = unicode ? 4000 : 8000;
            // two fixed-length operands give a fixed-length result
            bool fixedLength = !max && l.Kind is SqlTypeKind.Char or SqlTypeKind.NChar && r.Kind is SqlTypeKind.Char or SqlTypeKind.NChar;
            return new SqlType(fixedLength ? (unicode ? SqlTypeKind.NChar : SqlTypeKind.Char) : unicode ? SqlTypeKind.NVarChar : SqlTypeKind.VarChar, len);
        }
        if (l.IsString) return r;
        if (r.IsString) return l;
        var hi = l.Kind >= r.Kind ? l : r;
        if (hi.IsDateTime || hi.IsApproximate) return hi;
        if (op is "&" or "|" or "^") return hi;
        if (!l.IsDecimalLike && !r.IsDecimalLike)
            return hi.Kind == SqlTypeKind.Bit ? SqlType.Int : hi;
        // money combined with integers or money stays money; only a true decimal operand makes the result decimal
        if (l.Kind != SqlTypeKind.Decimal && r.Kind != SqlTypeKind.Decimal) return hi;
        if (NumericShape(l) is not var (p1, s1) || NumericShape(r) is not var (p2, s2)) return hi;
        switch (op)
        {
            case "+": case "-":
            {
                int s = Math.Max(s1, s2);
                return CapDecimal(Math.Max(p1 - s1, p2 - s2) + s + 1, s);
            }
            case "*": return CapDecimal(p1 + p2 + 1, s1 + s2);
            case "/":
            {
                int s = Math.Max(6, s1 + p2 + 1);
                return CapDecimal(p1 - s1 + s2 + s, s);
            }
            case "%":
            {
                int s = Math.Max(s1, s2);
                return CapDecimal(Math.Min(p1 - s1, p2 - s2) + s, s);
            }
        }
        return hi;
    }

    static bool IsNullLiteral(Expr e) => e is Literal { Value: null };

    /// <summary>Column layout of a FROM clause made only of tables and plain joins; null for anything else.</summary>
    RowSchema? StaticSchema(TableSource source)
    {
        switch (source)
        {
            case TableRef tr:
            {
                var table = TriggerPseudoTable(tr.Name) ?? (tr.Name.IsVariable
                    ? (_frames.Count > 0 ? Frame.Vars.GetValueOrDefault(tr.Name.Name)?.Table : null)
                    : FindObject(tr.Name) as Table);
                return table?.GetSchema(tr.Alias ?? tr.Name.Name, false);
            }
            case JoinSource { Kind: not (JoinKind.CrossApply or JoinKind.OuterApply) } j:
            {
                var l = StaticSchema(j.Left);
                var r = l == null ? null : StaticSchema(j.Right);
                return r == null ? null : RowSchema.Concat(l!, r);
            }
            default: return null;
        }
    }

    /// <summary>
    /// Common type of alternative result expressions. If any alternative has a type that is not known
    /// statically (and is not just a NULL literal) the answer is "unknown" rather than a guess.
    /// </summary>
    SqlType? UnifyBranches(IEnumerable<Expr> branches, EvalContext? ctx)
    {
        SqlType? r = null;
        foreach (var e in branches)
        {
            var t = InferType(e, ctx);
            if (t == null && !IsNullLiteral(e)) return null;
            r = Unify(r, t);
        }
        return r;
    }

    internal SqlType? InferType(Expr e, EvalContext? ctx)
    {
        if (_typeCache.TryGetValue(e, out var t)) return t;
        t = InferCore(e, ctx);
        _typeCache[e] = t;
        return t;
    }

    SqlType? InferCore(Expr e, EvalContext? ctx)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (e)
        {
            case Literal l: return l.Type;
            case ColumnRef c:
                for (var cx = ctx; cx != null; cx = cx.Outer)
                {
                    if (cx.Schema == null) continue;
                    int i = cx.Schema.TryResolve(c.Table, c.Name);
                    if (i >= 0) return cx.Schema.Cols[i].Type;
                }
                return null;
            case VarRef v:
                if (v.Name.StartsWith("@@", StringComparison.Ordinal))
                {
                    try { return GetGlobal(v.Name) is { } g ? TypeSystem.TypeOf(g) : null; }
                    catch (TSqlException) { return null; }
                }
                return _frames.Count > 0 && Frame.Vars.TryGetValue(v.Name, out var variable) ? variable.Type : null;
            case CastExpr c: return c.Type;
            case Unary u:
                if (u.Op == "NOT") return SqlType.Bit;
                return InferType(u.E, ctx) is { Kind: SqlTypeKind.TinyInt } && u.Op == "-" ? SqlType.SmallInt : InferType(u.E, ctx);
            case Binary b:
                if (b.Op is "AND" or "OR" or "=" or "<>" or "<" or "<=" or ">" or ">=") return SqlType.Bit;
                if (b.L is Binary { L: Binary { L: Binary } })
                {
                    // long left-deep chain: resolve the spine bottom-up so each level finds its left operand cached
                    var spine = new List<Binary>();
                    for (Expr cur = b.L; cur is Binary inner && !_typeCache.ContainsKey(inner); cur = inner.L) spine.Add(inner);
                    for (int i = spine.Count - 1; i >= 0; i--) InferType(spine[i], ctx);
                }
            {
                var (lt, rt) = OperandTypes(b, ctx);
                if ((lt == null && !IsNullLiteral(b.L)) || (rt == null && !IsNullLiteral(b.R))) return null;
                return ArithType(b.Op, lt, rt);
            }
            case CaseExpr c:
            {
                var branches = c.Whens.Select(w => w.Then);
                return UnifyBranches(c.Else != null ? branches.Append(c.Else) : branches, ctx);
            }
            case IsNullExpr or BetweenExpr or InExpr or LikeExpr or ExistsExpr or QuantifiedExpr: return SqlType.Bit;
            case SubqueryExpr { Q.ForJson: not null }: return SqlType.NVarCharMax;
            case SubqueryExpr { Q.Body: SelectSpec { From: null, Items: [{ IsStar: false, E: { } item }] } }: return InferType(item, ctx);
            case SubqueryExpr { Q: { Ctes: null, Body: SelectSpec { From: { } from, Items: [{ IsStar: false, E: { } item2 }] } } }:
            {
                // the column type of a scalar subquery over plain tables can be told without running it
                var schema = StaticSchema(from);
                return schema == null ? null : InferType(item2, new EvalContext(schema, ctx, null));
            }
            case NextValueExpr n: return (FindObject(n.Sequence) as Sequence)?.Type;
            case FuncCall f: return InferFunc(f, ctx);
            default: return null;
        }
    }

    SqlType? InferFunc(FuncCall f, EvalContext? ctx)
    {
        SqlType? Arg(int i) => i < f.Args.Count ? InferType(f.Args[i], ctx) : null;
        if (f.Schema != null || !Functions.IsBuiltin(f.Name))
        {
            var udf = FindObject(new TableName { Name = f.Name, Schema = f.Schema }) as Function;
            return udf?.Def.ReturnType;
        }
        switch (f.Name)
        {
            case "COUNT": case "LEN": case "CHARINDEX": case "PATINDEX": case "DATEPART": case "DATEDIFF": case "YEAR": case "MONTH":
            case "DAY": case "ASCII": case "UNICODE": case "ISNUMERIC": case "ISDATE": case "OBJECT_ID": case "SIGN" when Arg(0) == null:
            case "DATALENGTH": case "ERROR_NUMBER": case "ERROR_SEVERITY": case "ERROR_STATE": case "ERROR_LINE": case "DB_ID":
            case "ISJSON": case "CHECKSUM": case "SCHEMA_ID": case "OBJECTPROPERTY": case "DIFFERENCE":
                if (f.Name is "LEN" or "DATALENGTH" && Arg(0) is { Length: SqlType.Max } max && (max.IsString || max.IsBinary)) return SqlType.BigInt;
                return SqlType.Int;
            case "XACT_STATE": case "COL_LENGTH": return SqlType.SmallInt;
            case "GROUPING": return SqlType.TinyInt;
            case "GROUPING_ID": return SqlType.Int;
            case "COUNT_BIG": case "ROW_NUMBER": case "RANK": case "DENSE_RANK": case "NTILE": case "DATEDIFF_BIG": case "ROWCOUNT_BIG":
                return SqlType.BigInt;
            case "SUM":
            case "AVG":
            {
                var a = Arg(0);
                if (a == null) return null;
                if (a.Kind is SqlTypeKind.TinyInt or SqlTypeKind.SmallInt or SqlTypeKind.Int or SqlTypeKind.Bit) return SqlType.Int;
                if (a.Kind == SqlTypeKind.BigInt) return SqlType.BigInt;
                if (a.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney) return SqlType.Money;
                if (a.Kind == SqlTypeKind.Decimal) return f.Name == "SUM" ? SqlType.Decimal(38, a.Scale) : SqlType.Decimal(38, Math.Max(a.Scale, 6));
                if (a.IsApproximate) return SqlType.Float;
                return null;
            }
            case "MIN": case "MAX": case "LAG": case "LEAD": case "FIRST_VALUE": case "LAST_VALUE": case "NULLIF": case "ABS":
            case "CEILING": case "FLOOR": case "ROUND": case "POWER": case "SIGN":
                if (f.Name is "CEILING" or "FLOOR" && Arg(0) is { Kind: SqlTypeKind.Decimal } d) return SqlType.Decimal(d.Precision, 0);
                if (f.Name is "ABS" or "CEILING" or "FLOOR" or "ROUND" or "POWER" or "SIGN" && Arg(0) is null or { IsString: true }) return SqlType.Float;
                if (f.Name == "ROUND" && Arg(0) is { Kind: SqlTypeKind.Real }) return SqlType.Float;
                return Arg(0);
            case "UPPER": case "LOWER": case "LTRIM": case "RTRIM": case "TRIM": case "REVERSE":
            {
                var a = Arg(f.Name == "TRIM" && f.Args.Count == 2 ? 1 : 0);
                if (a is not { IsString: true }) return SqlType.VarChar(8000);
                return new SqlType(a.IsUnicode ? SqlTypeKind.NVarChar : SqlTypeKind.VarChar, a.Length);
            }
            case "ISNULL":
            {
                var a = Arg(0);
                return a ?? (f.Args.Count > 1 && IsNullLiteral(f.Args[0]) ? Arg(1) : null);
            }
            case "COALESCE": return UnifyBranches(f.Args, ctx);
            case "CHOOSE": return UnifyBranches(f.Args.Skip(1), ctx);
            case "IIF": return UnifyBranches(f.Args.Skip(1), ctx);
            case "SUBSTRING": case "LEFT": case "RIGHT": case "REPLACE": case "STUFF": case "REPLICATE": case "TRANSLATE":
            {
                var a = Arg(0);
                if (a is not { IsString: true }) return SqlType.VarChar(8000);
                // LEFT / RIGHT / SUBSTRING with a constant length produce a string of at most that length
                if (f.Name is "LEFT" or "RIGHT" or "SUBSTRING" && a.Length != SqlType.Max
                    && f.Args.Count > (f.Name == "SUBSTRING" ? 2 : 1) && f.Args[f.Name == "SUBSTRING" ? 2 : 1] is Literal { Value: int n })
                    return new SqlType(a.IsUnicode ? SqlTypeKind.NVarChar : SqlTypeKind.VarChar, Math.Clamp(n, 1, a.Length));
                if (f.Name == "REPLICATE" && a.Length != SqlType.Max && f.Args.Count > 1 && f.Args[1] is Literal { Value: int times })
                    return new SqlType(a.IsUnicode ? SqlTypeKind.NVarChar : SqlTypeKind.VarChar, times <= 0 ? (a.IsUnicode ? 1 : 2) : (int)Math.Min((long)a.Length * times, a.IsUnicode ? 4000 : 8000));
                // STUFF can add at most the length of the inserted string
                if (f.Name == "STUFF" && a.Length != SqlType.Max && Arg(3) is { IsString: true, Length: not SqlType.Max } ins)
                    return new SqlType(a.IsUnicode || ins.IsUnicode ? SqlTypeKind.NVarChar : SqlTypeKind.VarChar, Math.Min(a.Length + ins.Length, a.IsUnicode || ins.IsUnicode ? 4000 : 8000));
                bool grows = f.Name is "REPLACE" or "STUFF" or "REPLICATE";
                return new SqlType(a.IsUnicode ? SqlTypeKind.NVarChar : SqlTypeKind.VarChar, a.Length == SqlType.Max ? SqlType.Max : grows ? (a.IsUnicode ? 4000 : 8000) : a.Length);
            }
            case "CONCAT": case "CONCAT_WS":
            {
                bool unicode = false, anyMax = false;
                for (int i = 0; i < f.Args.Count; i++)
                {
                    var a = Arg(i);
                    if (a is { IsUnicode: true }) unicode = true;
                    if (a is { Length: SqlType.Max } && (a.IsString || a.IsBinary)) anyMax = true;
                }
                return new SqlType(unicode ? SqlTypeKind.NVarChar : SqlTypeKind.VarChar, anyMax ? SqlType.Max : unicode ? 4000 : 8000);
            }
            case "STRING_AGG": case "FORMAT": case "DATENAME": case "QUOTENAME": case "DB_NAME":
            case "OBJECT_NAME": case "SCHEMA_NAME": case "USER_NAME": case "SUSER_SNAME": case "SUSER_NAME": case "ERROR_MESSAGE":
            case "ERROR_PROCEDURE": case "CURRENT_USER": case "SESSION_USER": case "SYSTEM_USER": case "USER": case "HOST_NAME":
            case "APP_NAME": case "ORIGINAL_LOGIN": case "TYPE_NAME": case "OBJECT_SCHEMA_NAME": case "FORMATMESSAGE": case "JSON_VALUE": case "JSON_QUERY":
            case "STRING_ESCAPE": case "SQL_VARIANT_PROPERTY":
                return SqlType.NVarChar(4000);
            case "SPACE": return SqlType.VarChar(f.Args.Count == 1 && f.Args[0] is Literal { Value: int width } ? Math.Clamp(width, 1, 8000) : 8000);
            case "STR": case "CHAR": case "SOUNDEX": return SqlType.VarChar(8000);
            case "NCHAR": return SqlType.NVarChar(1);
            case "GETDATE": case "GETUTCDATE": case "CURRENT_TIMESTAMP": case "DATETIMEFROMPARTS": return SqlType.DateTime;
            case "SYSDATETIME": case "SYSUTCDATETIME": case "DATETIME2FROMPARTS": return SqlType.DateTime2;
            case "SYSDATETIMEOFFSET": return SqlType.DateTimeOffset;
            case "EOMONTH": case "DATEFROMPARTS": return SqlType.Date;
            case "DATEADD": case "DATETRUNC":
            {
                var a = Arg(f.Name == "DATEADD" ? 2 : 1);
                return a is { IsDateTime: true } ? a : SqlType.DateTime;
            }
            case "NEWID": case "NEWSEQUENTIALID": return SqlType.UniqueIdentifier;
            case "SCOPE_IDENTITY": case "IDENT_CURRENT": return SqlType.Decimal(38, 0);
            case "SQRT": case "SQUARE": case "EXP": case "LOG": case "LOG10": case "PI": case "RAND": case "SIN": case "COS": case "TAN":
            case "ASIN": case "ACOS": case "ATAN": case "ATN2": case "COT": case "DEGREES" when Arg(0) is not { IsDecimalLike: true }:
            case "RADIANS" when Arg(0) is not { IsDecimalLike: true }: case "STDEV": case "STDEVP": case "VAR": case "VARP":
            case "PERCENT_RANK": case "CUME_DIST":
                return SqlType.Float;
            case "HASHBYTES": return new SqlType(SqlTypeKind.VarBinary, 8000);
            case "GREATEST": case "LEAST": return UnifyBranches(f.Args, ctx);
            default: return null;
        }
    }
}
