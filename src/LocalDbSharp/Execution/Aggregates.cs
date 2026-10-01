namespace LocalDbSharp.Execution;

/// <summary>Incremental aggregate accumulator. Result() may be called repeatedly (running window frames).</summary>
internal abstract class Aggregator
{
    public abstract void Add(object? v);
    public abstract object? Result();
}

internal static class Aggregates
{
    public static bool IsAggregate(string name) => name is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "COUNT_BIG"
        or "STRING_AGG" or "STDEV" or "STDEVP" or "VAR" or "VARP";

    public static Aggregator Create(string name, SqlType? argType, bool star) => name switch
    {
        "COUNT" => new CountAgg(false),
        "COUNT_BIG" => new CountAgg(true),
        "SUM" => new SumAgg(false, argType),
        "AVG" => new SumAgg(true, argType),
        "MIN" => new MinMaxAgg(-1, argType?.IsUnicode == true),
        "MAX" => new MinMaxAgg(1, argType?.IsUnicode == true),
        "STDEV" => new StatAgg(sample: true, sqrt: true),
        "STDEVP" => new StatAgg(sample: false, sqrt: true),
        "VAR" => new StatAgg(sample: true, sqrt: false),
        "VARP" => new StatAgg(sample: false, sqrt: false),
        _ => throw Err.New(195, $"'{name}' is not a recognized built-in function name.", 15),
    };

    sealed class CountAgg(bool big) : Aggregator
    {
        long _n;
        public override void Add(object? v) { if (v != null) _n++; }
        public override object Result()
        {
            if (big) return _n;
            if (_n > int.MaxValue) throw Err.Overflow("int");
            return (int)_n;
        }
    }

    sealed class SumAgg(bool avg, SqlType? argType) : Aggregator
    {
        object? _sum;
        long _n;

        public override void Add(object? v)
        {
            if (v == null) return;
            v = v switch { byte b => (int)b, short s => (int)s, bool b => b ? 1 : 0, float f => (double)f, _ => v };
            if (TypeSystem.NumRank(v) == 0 && v is not string)
                throw Err.New(8117, $"Operand data type {TypeSystem.TypeOf(v).Name} is invalid for {(avg ? "avg" : "sum")} operator.");
            if (v is string s2) v = TypeSystem.ToDouble(s2);
            _sum = _sum == null ? v : TypeSystem.Arith("+", _sum, v);
            _n++;
        }

        public override object? Result()
        {
            if (_sum == null) return null;
            if (!avg) return _sum;
            switch (_sum)
            {
                case int i: return (int)(i / _n);
                case long l: return l / _n;
                case decimal d:
                {
                    if (argType?.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney)
                        return TypeSystem.SetScale(Math.Round(d / _n, 4, MidpointRounding.ToZero), 4);
                    int scale = Math.Max(argType?.Kind == SqlTypeKind.Decimal ? argType.Scale : d.Scale, 6);
                    decimal q = d / _n;
                    if (q.Scale > scale) q = Math.Round(q, scale, MidpointRounding.ToZero);
                    return TypeSystem.SetScale(q, scale);
                }
                case float f: return (double)f / _n;
                default: return TypeSystem.ToDouble(_sum) / _n;
            }
        }
    }

    sealed class MinMaxAgg(int sign, bool unicode) : Aggregator
    {
        object? _best;
        public override void Add(object? v)
        {
            if (v == null) return;
            if (_best == null || Math.Sign(TypeSystem.CompareForSort(v, _best, unicode)) == sign) _best = v;
        }
        public override object? Result() => _best;
    }

    sealed class StatAgg(bool sample, bool sqrt) : Aggregator
    {
        // Welford's online algorithm
        long _n;
        double _mean, _m2;
        public override void Add(object? v)
        {
            if (v == null) return;
            double x = TypeSystem.ToDouble(v);
            _n++;
            double delta = x - _mean;
            _mean += delta / _n;
            _m2 += delta * (x - _mean);
        }
        public override object? Result()
        {
            if (_n == 0 || (sample && _n < 2)) return null;
            double variance = _m2 / (sample ? _n - 1 : _n);
            return sqrt ? Math.Sqrt(variance) : variance;
        }
    }
}
