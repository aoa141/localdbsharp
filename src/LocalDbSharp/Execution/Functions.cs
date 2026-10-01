using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalDbSharp.Execution;

/// <summary>Built-in scalar functions.</summary>
internal static partial class Functions
{
    public delegate object? Fn(Session s, object?[] a);

    public static readonly Dictionary<string, Fn> Table = new(StringComparer.Ordinal);

    static readonly HashSet<string> SpecialForms = new(StringComparer.Ordinal)
    {
        "COALESCE", "ISNULL", "IIF", "NULLIF", "CHOOSE", "CONCAT", "CONCAT_WS", "DATALENGTH", "SQL_VARIANT_PROPERTY", "GROUPING", "GROUPING_ID", "UPDATE",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "PERCENT_RANK", "CUME_DIST",
    };

    public static bool IsBuiltin(string name) => Table.ContainsKey(name) || SpecialForms.Contains(name) || Aggregates.IsAggregate(name);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    static string S(object? v) => TypeSystem.ToStr(v!);
    static long L(object? v) => TypeSystem.ToLong(v!);
    static int I(object? v)
    {
        long l = TypeSystem.ToLong(v!, "int");
        if (l < int.MinValue || l > int.MaxValue) throw Err.Overflow("int");
        return (int)l;
    }

    static readonly HashSet<string> TextFunctions = new(StringComparer.Ordinal)
    {
        "LEN", "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "SUBSTRING", "LEFT", "RIGHT", "CHARINDEX", "PATINDEX", "REPLACE", "REPLICATE",
        "REVERSE", "STUFF", "QUOTENAME", "TRANSLATE", "ASCII", "UNICODE", "SOUNDEX", "DIFFERENCE", "STRING_ESCAPE",
    };

    /// <summary>Functions whose arguments are implicitly converted to strings.</summary>
    public static bool TakesText(string name) => TextFunctions.Contains(name);

    public static bool IsTimePart(string part) => DatePart(part) is "hour" or "minute" or "second" or "millisecond" or "microsecond" or "nanosecond";
    static double D(object? v) => TypeSystem.ToDouble(v!);

    /// <summary>Registers a function. Unless nullable is set, a NULL argument yields NULL without calling the body.</summary>
    static void Reg(string name, int min, int max, Fn fn, bool nullable = false)
    {
        Table[name] = (s, a) =>
        {
            if (a.Length < min || a.Length > max)
                throw Err.New(174, $"The {name.ToLowerInvariant()} function requires {(min == max ? min.ToString() : min + " to " + max)} argument(s).", 15);
            if (!nullable)
                foreach (var x in a) if (x == null) return null;
            return fn(s, a);
        };
    }

    static Functions()
    {
        RegisterStrings();
        RegisterMath();
        RegisterDates();
        RegisterSystem();
    }

    // ------------------------------------------------------------ strings

    static void RegisterStrings()
    {
        Reg("LEN", 1, 1, (_, a) => S(a[0]).AsSpan().TrimEnd(' ').Length);
        Reg("UPPER", 1, 1, (_, a) => S(a[0]).ToUpperInvariant());
        Reg("LOWER", 1, 1, (_, a) => S(a[0]).ToLowerInvariant());
        Reg("LTRIM", 1, 1, (_, a) => S(a[0]).TrimStart(' '));
        Reg("RTRIM", 1, 1, (_, a) => S(a[0]).TrimEnd(' '));
        Reg("TRIM", 1, 2, (_, a) => a.Length == 1 ? S(a[0]).Trim(' ') : S(a[1]).Trim(S(a[0]).ToCharArray()));
        Reg("SUBSTRING", 3, 3, (_, a) =>
        {
            long start = L(a[1]), len = L(a[2]);
            if (len < 0) throw Err.New(536, "Invalid length parameter passed to the substring function.");
            if (a[0] is byte[] bytes)
            {
                long bs = Math.Max(start - 1, 0), be = Math.Min(start - 1 + len, bytes.Length);
                return be <= bs ? Array.Empty<byte>() : bytes[(int)bs..(int)be];
            }
            string s = S(a[0]);
            long st = Math.Max(start - 1, 0), end = Math.Min(start - 1 + len, s.Length);
            return end <= st ? "" : s[(int)st..(int)end];
        });
        Reg("LEFT", 2, 2, (_, a) =>
        {
            string s = S(a[0]);
            long n = L(a[1]);
            if (n < 0) throw Err.New(536, "Invalid length parameter passed to the left function.");
            return n >= s.Length ? s : s[..(int)n];
        });
        Reg("RIGHT", 2, 2, (_, a) =>
        {
            string s = S(a[0]);
            long n = L(a[1]);
            if (n < 0) throw Err.New(536, "Invalid length parameter passed to the right function.");
            return n >= s.Length ? s : s[^(int)n..];
        });
        Reg("CHARINDEX", 2, 3, (_, a) =>
        {
            string find = S(a[0]), s = S(a[1]);
            long from = a.Length > 2 ? Math.Max(L(a[2]) - 1, 0) : 0;
            if (from > s.Length) return 0;
            int start = (int)from;
            if (find.Length == 0) return 0;
            return s.IndexOf(find, start, StringComparison.OrdinalIgnoreCase) + 1;
        });
        Reg("PATINDEX", 2, 2, (_, a) =>
        {
            return TypeSystem.LikePatternOf(S(a[0])).IndexIn(S(a[1]));
        });
        Reg("REPLACE", 3, 3, (_, a) =>
        {
            string s = S(a[0]), find = S(a[1]);
            return find.Length == 0 ? s : s.Replace(find, S(a[2]), StringComparison.OrdinalIgnoreCase);
        });
        Reg("REPLICATE", 2, 2, (_, a) =>
        {
            long n = L(a[1]);
            if (n < 0) return null;
            string s = S(a[0]);
            if (s.Length * n > 100_000_000) throw Err.New(8115, "Arithmetic overflow error converting expression to data type varchar.");
            return new StringBuilder().Insert(0, s, (int)n).ToString();
        });
        Reg("REVERSE", 1, 1, (_, a) =>
        {
            var chars = S(a[0]).ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        });
        Reg("STUFF", 4, 4, (_, a) =>
        {
            if (a[0] == null || a[1] == null || a[2] == null) return null;
            string s = S(a[0]), repl = a[3] == null ? "" : S(a[3]);
            long start = L(a[1]), len = L(a[2]);
            if (start < 1 || start > s.Length || len < 0) return null;
            long end = Math.Min(start - 1 + len, s.Length);
            return s[..(int)(start - 1)] + repl + s[(int)end..];
        }, nullable: true);
        Reg("SPACE", 1, 1, (_, a) => L(a[0]) < 0 ? null : new string(' ', I(a[0])));
        Reg("STR", 1, 3, (_, a) =>
        {
            int length = a.Length > 1 ? I(a[1]) : 10, dec = a.Length > 2 ? Math.Clamp(I(a[2]), 0, 16) : 0;
            if (length <= 0) return null;
            double value = D(a[0]);
            // the number of decimals shrinks until the result fits; asterisks only if the integer part does not fit
            string whole = Math.Truncate(value).ToString("F0", Inv);
            if (value < 0 && !whole.StartsWith('-')) whole = "-" + whole;
            if (whole.Length > length) return new string('*', length);
            dec = Math.Max(Math.Min(dec, length - whole.Length - 1), 0);
            string text = Math.Abs(value) < 1e9
                ? Math.Round(TypeSystem.ToDecimal(value), dec, MidpointRounding.AwayFromZero).ToString("F" + dec, Inv)
                : Math.Round(value, Math.Min(dec, 15), MidpointRounding.AwayFromZero).ToString("F" + dec, Inv);
            return text.Length > length ? new string('*', length) : text.PadLeft(length);
        });
        Reg("ASCII", 1, 1, (_, a) => S(a[0]) is { Length: > 0 } s ? (int)s[0] : null);
        Reg("UNICODE", 1, 1, (_, a) => S(a[0]) is { Length: > 0 } s ? (char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1]) ? char.ConvertToUtf32(s, 0) : s[0]) : null);
        Reg("CHAR", 1, 1, (_, a) => L(a[0]) is >= 0 and <= 255 and var n ? ((char)n).ToString() : null);
        Reg("NCHAR", 1, 1, (_, a) => L(a[0]) is >= 0 and <= 0x10FFFF and var n ? (n < 0x10000 ? ((char)n).ToString() : char.ConvertFromUtf32((int)n)) : null);
        Reg("QUOTENAME", 1, 2, (_, a) =>
        {
            string s = S(a[0]), q = a.Length > 1 ? S(a[1]) : "[";
            if (s.Length > 128) return null;
            return q switch
            {
                "[" or "]" => "[" + s.Replace("]", "]]") + "]",
                "'" => "'" + s.Replace("'", "''") + "'",
                "\"" => "\"" + s.Replace("\"", "\"\"") + "\"",
                "(" or ")" => "(" + s.Replace(")", "))") + ")",
                "<" or ">" => "<" + s.Replace(">", ">>") + ">",
                "{" or "}" => "{" + s.Replace("}", "}}") + "}",
                "`" => "`" + s.Replace("`", "``") + "`",
                _ => null,
            };
        });
        Reg("TRANSLATE", 3, 3, (_, a) =>
        {
            string s = S(a[0]), from = S(a[1]), to = S(a[2]);
            if (from.Length != to.Length) throw Err.New(9828, "The second and third arguments of the TRANSLATE built-in function must contain an equal number of characters.");
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                int i = from.IndexOf(c.ToString(), StringComparison.OrdinalIgnoreCase);
                sb.Append(i >= 0 ? to[i] : c);
            }
            return sb.ToString();
        });
        Reg("FORMAT", 2, 3, (_, a) =>
        {
            CultureInfo culture = EnUs;
            if (a.Length > 2)
            {
                try { culture = CultureInfo.GetCultureInfo(S(a[2])); }
                catch (CultureNotFoundException) { throw Err.New(9818, $"The culture parameter '{S(a[2])}' provided in the function call is not supported."); }
            }
            object v = a[0]!;
            string fmt = S(a[1]);
            if (v is TimeSpan ts) return ts.ToString(fmt, culture);
            if (v is IFormattable f)
            {
                try { return f.ToString(fmt, culture); }
                catch (FormatException) { return null; }
            }
            throw Err.New(8116, "Argument data type varchar is invalid for argument 1 of format function.");
        });
        Reg("FORMATMESSAGE", 1, 30, (_, a) => FormatMessage(S(a[0]), a.Skip(1).ToArray()), nullable: true);
        Reg("STRING_ESCAPE", 2, 2, (_, a) => JsonEncodedText.Encode(S(a[0]), System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());
        Reg("SOUNDEX", 1, 1, (_, a) => Soundex(S(a[0])));
    }

    static string Soundex(string s)
    {
        const string codes = "01230120022455012623010202";
        var sb = new StringBuilder();
        char last = '0';
        foreach (char raw in s.ToUpperInvariant())
        {
            if (raw < 'A' || raw > 'Z') { if (sb.Length > 0) break; continue; }
            char code = codes[raw - 'A'];
            if (sb.Length == 0) sb.Append(raw);
            else if (code != '0' && code != last) sb.Append(code);
            if (raw != 'H' && raw != 'W') last = code;
            if (sb.Length == 4) break;
        }
        return sb.Length == 0 ? "0000" : sb.ToString().PadRight(4, '0');
    }

    [GeneratedRegex(@"%([-+0 #]*)(\d+)?(?:\.(\d+))?(?:h|ll|l|I64)?([diouxXs%])")]
    private static partial Regex PrintfRegex();

    public static string FormatMessage(string format, object?[] args)
    {
        int next = 0;
        return PrintfRegex().Replace(format, m =>
        {
            char kind = m.Groups[4].Value[0];
            if (kind == '%') return "%";
            object? arg = next < args.Length ? args[next++] : null;
            string text = arg == null ? "(null)" : kind switch
            {
                's' => S(arg),
                'x' => L(arg).ToString("x", Inv),
                'X' => L(arg).ToString("X", Inv),
                'o' => Convert.ToString(L(arg), 8),
                _ => arg is string str ? str : L(arg).ToString(Inv),
            };
            if (kind == 's' && m.Groups[3].Success && int.Parse(m.Groups[3].Value, Inv) < text.Length)
                text = text[..int.Parse(m.Groups[3].Value, Inv)];
            if (m.Groups[2].Success)
            {
                int width = int.Parse(m.Groups[2].Value, Inv);
                string flags = m.Groups[1].Value;
                text = flags.Contains('-') ? text.PadRight(width) : text.PadLeft(width, flags.Contains('0') && kind != 's' ? '0' : ' ');
            }
            return text;
        });
    }

    public static int DataLength(object v, SqlType? type) => v switch
    {
        string s => type is { IsUnicode: true } ? s.Length * 2 : s.Length,
        byte[] b => b.Length,
        bool or byte => 1,
        short => 2,
        int or float => 4,
        long or double => 8,
        decimal d => type?.Kind is SqlTypeKind.Money ? 8 : type?.Kind is SqlTypeKind.SmallMoney ? 4 : DecimalDataLength(d),
        DateTime => type?.Kind switch { SqlTypeKind.Date => 3, SqlTypeKind.SmallDateTime => 4, SqlTypeKind.DateTime2 => type.Scale <= 2 ? 6 : type.Scale <= 4 ? 7 : 8, _ => 8 },
        DateTimeOffset => 10,
        TimeSpan => 5,
        Guid => 16,
        _ => 0,
    };

    /// <summary>A decimal value occupies a sign byte plus as many 4-byte words as its unscaled magnitude needs.</summary>
    static int DecimalDataLength(decimal d)
    {
        var bits = decimal.GetBits(d);
        return bits[2] != 0 ? 13 : bits[1] != 0 ? 9 : 5;
    }

    // ------------------------------------------------------------ math

    static TSqlException InvalidFloat() => Err.New(3623, "An invalid floating point operation occurred.");

    static object RoundValue(object v, int digits, bool truncate)
    {
        var mode = truncate ? MidpointRounding.ToZero : MidpointRounding.AwayFromZero;
        switch (v)
        {
            case decimal d:
            {
                decimal r;
                if (digits >= 0) r = digits >= 28 ? d : Math.Round(d, digits, mode);
                else if (-digits > 28) r = 0;
                else
                {
                    decimal f = (decimal)Math.Pow(10, -digits);
                    r = Math.Round(d / f, 0, mode) * f;
                }
                return TypeSystem.SetScale(r, d.Scale);
            }
            case double x:
            {
                // round the exact binary value (2.675E0 is really 2.67499999...), as SQL Server does
                if (Math.Abs(x) < 1e15 && decimal.TryParse(x.ToString("F25", Inv), NumberStyles.Float, Inv, out decimal exact))
                {
                    // go back through text: a direct decimal -> double cast of a high-scale decimal is not correctly rounded
                    double rounded = double.Parse(((decimal)RoundValue(exact, digits, truncate)).ToString(Inv), Inv);
                    return rounded + 0.0; // never negative zero
                }
                if (digits >= 0) return digits > 15 ? x : Math.Round(x, digits, mode);
                double f = Math.Pow(10, -digits);
                return Math.Round(x / f, 0, mode) * f;
            }
            case float x: return RoundValue((double)x, digits, truncate); // ROUND of a real yields float
            default:
            {
                long l = TypeSystem.ToLong(v);
                if (digits < 0)
                {
                    if (-digits > 18) l = 0;
                    else
                    {
                        long f = (long)Math.Pow(10, -digits);
                        long q = l / f, rem = Math.Abs(l % f);
                        if (!truncate && rem * 2 >= f) q += Math.Sign(l);
                        l = q * f;
                    }
                }
                return TypeSystem.ConvertTo(l, TypeSystem.TypeOf(v))!;
            }
        }
    }

    static void RegisterMath()
    {
        Reg("ABS", 1, 1, (_, a) =>
        {
            try
            {
                return a[0] switch
                {
                    int i => Math.Abs(i),
                    long l => Math.Abs(l),
                    short s => Math.Abs(s),
                    decimal d => Math.Abs(d),
                    double d => Math.Abs(d),
                    float f => Math.Abs(f),
                    byte or bool => a[0],
                    _ => Math.Abs(D(a[0])),
                };
            }
            catch (OverflowException) { throw Err.Overflow("int"); }
        });
        // "+ 0.0" turns IEEE negative zero into plain zero, which is all SQL Server ever shows
        Reg("CEILING", 1, 1, (_, a) => a[0] switch
        {
            decimal d => Math.Ceiling(d),
            double d => Math.Ceiling(d) + 0.0,
            float f => (float)Math.Ceiling(f) + 0f,
            string s => Math.Ceiling(D(s)) + 0.0,
            _ => a[0],
        });
        Reg("FLOOR", 1, 1, (_, a) => a[0] switch
        {
            decimal d => Math.Floor(d),
            double d => Math.Floor(d) + 0.0,
            float f => (float)Math.Floor(f) + 0f,
            string s => Math.Floor(D(s)) + 0.0,
            _ => a[0],
        });
        Reg("ROUND", 2, 3, (_, a) => RoundValue(a[0] is string s ? D(s) : a[0]!, I(a[1]), a.Length > 2 && L(a[2]) != 0));
        Reg("POWER", 2, 2, (_, a) =>
        {
            double r = Math.Pow(D(a[0]), D(a[1]));
            if (double.IsNaN(r) || double.IsInfinity(r)) throw InvalidFloat();
            switch (a[0])
            {
                case int:
                    if (r > int.MaxValue || r < int.MinValue) throw Err.New(232, $"Arithmetic overflow error for type int, value = {r:F6}.");
                    return (int)r;
                case long:
                    if (r > long.MaxValue || r < long.MinValue) throw Err.New(232, $"Arithmetic overflow error for type bigint, value = {r:F6}.");
                    return (long)r;
                case byte or short or bool: return TypeSystem.ConvertTo((long)r, TypeSystem.TypeOf(a[0]!));
                case decimal d:
                    try { return TypeSystem.SetScale(Math.Round((decimal)r, d.Scale, MidpointRounding.AwayFromZero), d.Scale); }
                    catch (OverflowException) { throw Err.Overflow("numeric"); }
                case float: return (float)r;
                default: return r;
            }
        });
        Reg("SQRT", 1, 1, (_, a) => D(a[0]) < 0 ? throw InvalidFloat() : Math.Sqrt(D(a[0])));
        Reg("SQUARE", 1, 1, (_, a) => D(a[0]) * D(a[0]));
        Reg("EXP", 1, 1, (_, a) => Math.Exp(D(a[0])) is var r && double.IsFinite(r) ? r : throw Err.Overflow("float"));
        Reg("LOG", 1, 2, (_, a) =>
        {
            double x = D(a[0]);
            if (x <= 0) throw InvalidFloat();
            if (a.Length == 1) return Math.Log(x);
            double r = Math.Log(x) / Math.Log(D(a[1]));
            return double.IsFinite(r) ? r : throw InvalidFloat();
        });
        Reg("LOG10", 1, 1, (_, a) => D(a[0]) <= 0 ? throw InvalidFloat() : Math.Log10(D(a[0])));
        Reg("SIGN", 1, 1, (_, a) => a[0] switch
        {
            decimal d => TypeSystem.SetScale(Math.Sign(d), d.Scale),
            double d => (double)Math.Sign(d),
            float f => (float)Math.Sign(f),
            long l => (long)Math.Sign(l),
            string s => (double)Math.Sign(D(s)),
            _ => Math.Sign(L(a[0])),
        });
        Reg("PI", 0, 0, (_, _) => Math.PI);
        Reg("RAND", 0, 1, (s, a) => a.Length > 0 && a[0] != null ? new Random(I(a[0])).NextDouble() : s.Random.NextDouble(), nullable: true);
        Reg("SIN", 1, 1, (_, a) => Math.Sin(D(a[0])));
        Reg("COS", 1, 1, (_, a) => Math.Cos(D(a[0])));
        Reg("TAN", 1, 1, (_, a) => Math.Tan(D(a[0])));
        Reg("COT", 1, 1, (_, a) => 1.0 / Math.Tan(D(a[0])));
        Reg("ASIN", 1, 1, (_, a) => Math.Abs(D(a[0])) > 1 ? throw InvalidFloat() : Math.Asin(D(a[0])));
        Reg("ACOS", 1, 1, (_, a) => Math.Abs(D(a[0])) > 1 ? throw InvalidFloat() : Math.Acos(D(a[0])));
        Reg("ATAN", 1, 1, (_, a) => Math.Atan(D(a[0])));
        Reg("ATN2", 2, 2, (_, a) => Math.Atan2(D(a[0]), D(a[1])));
        Reg("DEGREES", 1, 1, (_, a) => a[0] switch
        {
            int i => (object)(int)(i * 180.0 / Math.PI),
            long l => (long)(l * 180.0 / Math.PI),
            decimal d => d * 180m / (decimal)Math.PI,
            _ => D(a[0]) * 180.0 / Math.PI,
        });
        Reg("RADIANS", 1, 1, (_, a) => a[0] switch
        {
            int i => (object)(int)(i * Math.PI / 180.0),
            long l => (long)(l * Math.PI / 180.0),
            decimal d => d * (decimal)Math.PI / 180m,
            _ => D(a[0]) * Math.PI / 180.0,
        });
        Reg("GREATEST", 1, 254, (_, a) => Extreme(a, 1), nullable: true);
        Reg("LEAST", 1, 254, (_, a) => Extreme(a, -1), nullable: true);
    }

    static object? Extreme(object?[] a, int sign)
    {
        object? best = null;
        SqlType? type = null;
        foreach (var v in a)
        {
            if (v == null) continue;
            type = Session.Unify(type, TypeSystem.TypeOf(v));
            if (best == null || Math.Sign(TypeSystem.Compare(v, best)) == sign) best = v;
        }
        return best == null || type == null || best is string ? best : TypeSystem.ConvertTo(best, type);
    }

    // ------------------------------------------------------------ dates

    static string DatePart(object? v)
    {
        string p = S(v).ToLowerInvariant();
        return p switch
        {
            "year" or "yy" or "yyyy" => "year",
            "quarter" or "qq" or "q" => "quarter",
            "month" or "mm" or "m" => "month",
            "dayofyear" or "dy" or "y" => "dayofyear",
            "day" or "dd" or "d" => "day",
            "week" or "wk" or "ww" => "week",
            "weekday" or "dw" or "w" => "weekday",
            "hour" or "hh" => "hour",
            "minute" or "mi" or "n" => "minute",
            "second" or "ss" or "s" => "second",
            "millisecond" or "ms" => "millisecond",
            "microsecond" or "mcs" => "microsecond",
            "nanosecond" or "ns" => "nanosecond",
            "iso_week" or "isowk" or "isoww" => "iso_week",
            _ => throw Err.New(155, $"'{p}' is not a recognized datepart option.", 15),
        };
    }

    static DateTime Dt(object? v) => v switch
    {
        DateTime d => d,
        DateTimeOffset o => o.DateTime,
        TimeSpan t => TypeSystem.Epoch1900 + t,
        _ => TypeSystem.ToDateTime(v!),
    };

    static DateTime AddPart(string part, long n, DateTime d)
    {
        try
        {
            return part switch
            {
                "year" => d.AddYears(checked((int)n)),
                "quarter" => d.AddMonths(checked((int)n * 3)),
                "month" => d.AddMonths(checked((int)n)),
                "dayofyear" or "day" or "weekday" => d.AddDays(n),
                "week" => d.AddDays(n * 7),
                "hour" => d.AddHours(n),
                "minute" => d.AddMinutes(n),
                "second" => d.AddSeconds(n),
                "millisecond" => d.AddMilliseconds(n),
                "microsecond" => d.AddTicks(n * 10),
                "nanosecond" => d.AddTicks(n / 100),
                _ => throw Err.New(9810, $"The datepart {part} is not supported by date function dateadd for data type datetime."),
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw Err.New(517, "Adding a value to a 'datetime' column caused an overflow.");
        }
    }

    static long DiffPart(string part, DateTime a, DateTime b)
    {
        static DateTime WeekStart(DateTime x) => x.Date.AddDays(-(int)x.DayOfWeek);
        return part switch
        {
            "year" => b.Year - a.Year,
            "quarter" => (b.Year - a.Year) * 4 + (b.Month - 1) / 3 - (a.Month - 1) / 3,
            "month" => (b.Year - a.Year) * 12 + b.Month - a.Month,
            "dayofyear" or "day" or "weekday" => (b.Date - a.Date).Days,
            "week" => (WeekStart(b) - WeekStart(a)).Days / 7,
            "hour" => b.Ticks / TimeSpan.TicksPerHour - a.Ticks / TimeSpan.TicksPerHour,
            "minute" => b.Ticks / TimeSpan.TicksPerMinute - a.Ticks / TimeSpan.TicksPerMinute,
            "second" => b.Ticks / TimeSpan.TicksPerSecond - a.Ticks / TimeSpan.TicksPerSecond,
            "millisecond" => b.Ticks / TimeSpan.TicksPerMillisecond - a.Ticks / TimeSpan.TicksPerMillisecond,
            "microsecond" => b.Ticks / 10 - a.Ticks / 10,
            "nanosecond" => (b.Ticks - a.Ticks) * 100,
            _ => throw Err.New(9810, $"The datepart {part} is not supported by date function datediff for data type datetime."),
        };
    }

    static int PartOf(string part, DateTime d, int dateFirst = 7) => part switch
    {
        "year" => d.Year,
        "quarter" => (d.Month - 1) / 3 + 1,
        "month" => d.Month,
        "dayofyear" => d.DayOfYear,
        "day" => d.Day,
        "week" => (d.DayOfYear + ((int)new DateTime(d.Year, 1, 1).DayOfWeek - dateFirst + 7) % 7 - 1) / 7 + 1,
        "weekday" => ((int)d.DayOfWeek - dateFirst + 7) % 7 + 1,
        "hour" => d.Hour,
        "minute" => d.Minute,
        "second" => d.Second,
        "millisecond" => d.Millisecond,
        "microsecond" => (int)(d.Ticks % TimeSpan.TicksPerSecond / 10),
        "nanosecond" => (int)(d.Ticks % TimeSpan.TicksPerSecond * 100),
        "iso_week" => ISOWeek.GetWeekOfYear(d),
        _ => throw Err.New(155, $"'{part}' is not a recognized datepart option.", 15),
    };

    static DateTime MakeDate(object?[] a, int count)
    {
        try
        {
            return count == 3
                ? new DateTime(I(a[0]), I(a[1]), I(a[2]))
                : new DateTime(I(a[0]), I(a[1]), I(a[2]), I(a[3]), I(a[4]), I(a[5]));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Err.New(289, "Cannot construct data type date, some of the arguments have values which are not valid.");
        }
    }

    static void RegisterDates()
    {
        Reg("GETDATE", 0, 0, (_, _) => TypeSystem.RoundDatetime(DateTime.Now));
        Reg("CURRENT_TIMESTAMP", 0, 0, (_, _) => TypeSystem.RoundDatetime(DateTime.Now));
        Reg("GETUTCDATE", 0, 0, (_, _) => TypeSystem.RoundDatetime(DateTime.UtcNow));
        Reg("SYSDATETIME", 0, 0, (_, _) => DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified));
        Reg("SYSUTCDATETIME", 0, 0, (_, _) => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
        Reg("SYSDATETIMEOFFSET", 0, 0, (_, _) => DateTimeOffset.Now);
        Reg("DATEADD", 3, 3, (_, a) =>
        {
            string part = DatePart(a[0]);
            long n = L(a[1]);
            return a[2] switch
            {
                DateTimeOffset o => new DateTimeOffset(AddPart(part, n, o.DateTime), o.Offset),
                TimeSpan t => AddPart(part, n, TypeSystem.Epoch1900 + t).TimeOfDay,
                string s => TypeSystem.RoundDatetime(AddPart(part, n, TypeSystem.ToDateTime(s))),
                _ => AddPart(part, n, Dt(a[2])),
            };
        });
        Reg("DATEDIFF", 3, 3, (_, a) =>
        {
            long r = DiffPart(DatePart(a[0]), Dt(a[1]), Dt(a[2]));
            if (r > int.MaxValue || r < int.MinValue)
                throw Err.New(535, "The datediff function resulted in an overflow. The number of dateparts separating two date/time instances is too large. Try to use datediff with a less precise datepart.");
            return (int)r;
        });
        Reg("DATEDIFF_BIG", 3, 3, (_, a) => DiffPart(DatePart(a[0]), Dt(a[1]), Dt(a[2])));
        Reg("DATEPART", 2, 2, (s, a) => PartOf(DatePart(a[0]), Dt(a[1]), s.DateFirst));
        Reg("DATENAME", 2, 2, (s, a) =>
        {
            string part = DatePart(a[0]);
            var d = Dt(a[1]);
            return part switch
            {
                "month" => d.ToString("MMMM", EnUs),
                "weekday" => d.ToString("dddd", EnUs),
                _ => PartOf(part, d, s.DateFirst).ToString(Inv),
            };
        });
        Reg("YEAR", 1, 1, (_, a) => Dt(a[0]).Year);
        Reg("MONTH", 1, 1, (_, a) => Dt(a[0]).Month);
        Reg("DAY", 1, 1, (_, a) => Dt(a[0]).Day);
        Reg("EOMONTH", 1, 2, (_, a) =>
        {
            if (a[0] == null) return null;
            var d = Dt(a[0]);
            if (a.Length > 1 && a[1] != null) d = AddPart("month", L(a[1]), d);
            return new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
        }, nullable: true);
        Reg("DATEFROMPARTS", 3, 3, (_, a) => MakeDate(a, 3));
        Reg("DATETIMEFROMPARTS", 7, 7, (_, a) => MakeDate(a, 6).AddMilliseconds(I(a[6])));
        Reg("DATETIME2FROMPARTS", 8, 8, (_, a) =>
        {
            int precision = I(a[7]);
            long ticks = L(a[6]);
            for (int i = precision; i < 7; i++) ticks *= 10;
            return MakeDate(a, 6).AddTicks(ticks);
        });
        Reg("TIMEFROMPARTS", 5, 5, (_, a) =>
        {
            long ticks = L(a[3]);
            for (int i = I(a[4]); i < 7; i++) ticks *= 10;
            return new TimeSpan(I(a[0]), I(a[1]), I(a[2])) + TimeSpan.FromTicks(ticks);
        });
        Reg("ISDATE", 1, 1, (_, a) => a[0] switch
        {
            DateTime => 1,
            string s => s.Trim().Length > 0 && TypeSystem.TryParseDateTime(s, 0, out DateTime ignored) ? 1 : 0,
            _ => 0,
        }, nullable: true);
        Reg("DATETRUNC", 2, 2, (_, a) =>
        {
            string part = DatePart(a[0]);
            var d = Dt(a[1]);
            return part switch
            {
                "year" => new DateTime(d.Year, 1, 1),
                "quarter" => new DateTime(d.Year, (d.Month - 1) / 3 * 3 + 1, 1),
                "month" => new DateTime(d.Year, d.Month, 1),
                "dayofyear" or "day" => d.Date,
                "week" => d.Date.AddDays(-(int)d.DayOfWeek),
                "iso_week" => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
                "hour" => new DateTime(d.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour),
                "minute" => new DateTime(d.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute),
                "second" => new DateTime(d.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond),
                "millisecond" => new DateTime(d.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond),
                "microsecond" => new DateTime(d.Ticks / 10 * 10),
                _ => throw Err.New(9810, $"The datepart {part} is not supported by date function datetrunc for data type datetime."),
            };
        });
        Reg("TODATETIMEOFFSET", 2, 2, (_, a) =>
        {
            var offset = a[1] is string s ? TimeSpan.Parse(s.TrimStart('+'), Inv) : TimeSpan.FromMinutes(L(a[1]));
            return new DateTimeOffset(DateTime.SpecifyKind(Dt(a[0]), DateTimeKind.Unspecified), offset);
        });
        Reg("SWITCHOFFSET", 2, 2, (_, a) =>
        {
            var offset = a[1] is string s ? TimeSpan.Parse(s.TrimStart('+'), Inv) : TimeSpan.FromMinutes(L(a[1]));
            return ((DateTimeOffset)TypeSystem.ConvertTo(a[0], SqlType.DateTimeOffset)!).ToOffset(offset);
        });
    }

    // ------------------------------------------------------------ system & metadata

    static void RegisterSystem()
    {
        Reg("NEWID", 0, 0, (_, _) => Guid.NewGuid());
        Reg("NEWSEQUENTIALID", 0, 0, (_, _) => Guid.NewGuid());
        Reg("ISNUMERIC", 1, 1, (_, a) => a[0] switch
        {
            null => 0,
            string s when s.Trim() is "-" or "+" or "." or "$" => 1,
            string s => s.Trim().Length > 0 && (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, EnUs, out decimal ignored1)
                                               || double.TryParse(s, NumberStyles.Float, Inv, out double ignored2)) ? 1 : 0,
            _ => TypeSystem.NumRank(a[0]!) > 0 ? 1 : 0,
        }, nullable: true);
        Reg("SCOPE_IDENTITY", 0, 0, (s, _) => s.ScopeIdentity);
        Reg("IDENT_CURRENT", 1, 1, (s, a) => s.IdentCurrent(S(a[0])));
        Reg("ROWCOUNT_BIG", 0, 0, (s, _) => (long)s.LastRowCount);
        Reg("XACT_STATE", 0, 0, (s, _) => s.XactState);
        Reg("DB_NAME", 0, 1, (s, a) => a.Length == 0 ? s.Database : s.DatabaseName(I(a[0])));
        Reg("DB_ID", 0, 1, (s, a) => s.DatabaseId(a.Length == 0 ? s.Database : S(a[0])), nullable: false);
        Reg("OBJECT_ID", 1, 2, (s, a) => a[0] == null ? null : s.ObjectId(S(a[0]), a.Length > 1 && a[1] != null ? S(a[1]) : null), nullable: true);
        Reg("OBJECT_NAME", 1, 2, (s, a) => s.FindObjectById(I(a[0]))?.Name);
        Reg("OBJECT_SCHEMA_NAME", 1, 2, (s, a) => s.FindObjectById(I(a[0]))?.Schema);
        Reg("SCHEMA_NAME", 0, 1, (s, a) => a.Length == 0 ? "dbo" : s.SchemaName(I(a[0])));
        Reg("SCHEMA_ID", 0, 1, (s, a) => s.SchemaId(a.Length == 0 ? "dbo" : S(a[0])));
        foreach (string n in new[] { "USER_NAME", "CURRENT_USER", "SESSION_USER", "USER" }) Reg(n, 0, 1, (_, _) => "dbo", nullable: true);
        foreach (string n in new[] { "SUSER_SNAME", "SUSER_NAME", "SYSTEM_USER", "ORIGINAL_LOGIN" }) Reg(n, 0, 1, (_, _) => "sa", nullable: true);
        Reg("HOST_NAME", 0, 0, (_, _) => Environment.MachineName);
        Reg("SESSION_CONTEXT", 1, 1, (s, a) => s.SessionContext(S(a[0])));
        Reg("COL_LENGTH", 2, 2, (s, a) => s.ColumnLength(S(a[0]), S(a[1])));
        Reg("COL_NAME", 2, 2, (s, a) => s.FindObjectById(I(a[0])) is Storage.Table t && I(a[1]) >= 1 && I(a[1]) <= t.Columns.Count ? t.Columns[I(a[1]) - 1].Name : null);
        Reg("OBJECTPROPERTY", 2, 2, (s, a) => s.ObjectProperty(I(a[0]), S(a[1])));
        Reg("DIFFERENCE", 2, 2, (_, a) =>
        {
            string x = Soundex(S(a[0])), y = Soundex(S(a[1]));
            int same = 0;
            for (int i = 0; i < 4; i++) if (x[i] == y[i]) same++;
            return same;
        });
        Reg("APP_NAME", 0, 0, (_, _) => "LocalDbSharp");
        Reg("ERROR_MESSAGE", 0, 0, (s, _) => s.CurrentError?.Message);
        Reg("ERROR_NUMBER", 0, 0, (s, _) => s.CurrentError?.Number);
        Reg("ERROR_SEVERITY", 0, 0, (s, _) => s.CurrentError is { } e ? (int)e.Class : null);
        Reg("ERROR_STATE", 0, 0, (s, _) => s.CurrentError is { } e ? (int)e.State : null);
        Reg("ERROR_LINE", 0, 0, (s, _) => s.CurrentError?.LineNumber);
        Reg("ERROR_PROCEDURE", 0, 0, (s, _) => s.CurrentError?.Procedure);
        Reg("SERVERPROPERTY", 1, 1, (_, a) => S(a[0]).ToUpperInvariant() switch
        {
            "PRODUCTVERSION" => "1.0.0.0",
            "PRODUCTLEVEL" => "RTM",
            "EDITION" => "LocalDbSharp (in-memory)",
            "ENGINEEDITION" => 4,
            "ISLOCALDB" => 1,
            "MACHINENAME" => Environment.MachineName,
            "SERVERNAME" => "LOCALDBSHARP",
            "COLLATION" => "SQL_Latin1_General_CP1_CI_AS",
            _ => null,
        });
        Reg("HASHBYTES", 2, 2, (_, a) =>
        {
            byte[] data = a[1] as byte[] ?? Encoding.Latin1.GetBytes(S(a[1]));
            return S(a[0]).ToUpperInvariant() switch
            {
                "MD5" => MD5.HashData(data),
                "SHA" or "SHA1" => SHA1.HashData(data),
                "SHA2_256" => SHA256.HashData(data),
                "SHA2_512" => SHA512.HashData(data),
                _ => (object?)null,
            };
        });
        Reg("ISJSON", 1, 1, (_, a) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(S(a[0]));
                return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? 1 : 0;
            }
            catch (JsonException) { return 0; }
        });
        Reg("JSON_VALUE", 2, 2, (_, a) =>
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(S(a[0])); }
            catch (JsonException) { throw Err.New(13609, "JSON text is not properly formatted."); }
            using (doc)
            {
                var el = JsonPath(doc.RootElement, S(a[1]));
                return el?.ValueKind switch
                {
                    JsonValueKind.String => el.Value.GetString(),
                    JsonValueKind.Number => el.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
            }
        });
        Reg("PARSENAME", 2, 2, (_, a) =>
        {
            var parts = S(a[0]).Split('.');
            int piece = I(a[1]);
            if (piece < 1 || piece > 4 || parts.Length > 4 || piece > parts.Length) return null;
            return parts[^piece].Trim('[', ']');
        });
        Reg("JSON_QUERY", 1, 2, (_, a) =>
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(S(a[0])); }
            catch (JsonException) { throw Err.New(13609, "JSON text is not properly formatted."); }
            using var owned = doc;
            var el = a.Length > 1 ? JsonPath(doc.RootElement, S(a[1])) : doc.RootElement;
            return el?.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? el.Value.GetRawText() : null;
        });
    }

    internal static JsonElement? JsonPath(JsonElement root, string path)
    {
        path = path.Trim();
        if (path.StartsWith("lax ", StringComparison.OrdinalIgnoreCase)) path = path[4..].Trim();
        else if (path.StartsWith("strict ", StringComparison.OrdinalIgnoreCase)) path = path[7..].Trim();
        if (!path.StartsWith('$')) throw Err.New(13607, "JSON path is not properly formatted. Unexpected character '" + (path.Length > 0 ? path[0] : ' ') + "' is found at position 0.");
        var cur = root;
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                string key;
                if (i < path.Length && path[i] == '"')
                {
                    int close = path.IndexOf('"', i + 1);
                    if (close < 0) return null;
                    key = path[(i + 1)..close];
                    i = close + 1;
                }
                else
                {
                    int start = i;
                    while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                    key = path[start..i];
                }
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out cur)) return null;
            }
            else if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0 || !int.TryParse(path[(i + 1)..close], out int index)) return null;
                if (cur.ValueKind != JsonValueKind.Array || index < 0 || index >= cur.GetArrayLength()) return null;
                cur = cur[index];
                i = close + 1;
            }
            else return null;
        }
        return cur.Clone();
    }
}
