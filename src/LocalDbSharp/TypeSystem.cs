using System.Collections.Concurrent;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalDbSharp;

/// <summary>
/// Runtime value semantics: conversions, comparison, arithmetic and hashing keys.
/// Values are plain CLR objects (null = SQL NULL): bool, byte, short, int, long, decimal, float, double,
/// string, DateTime, DateTimeOffset, TimeSpan, Guid and byte[].
/// </summary>
internal static class TypeSystem
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    static readonly CultureInfo EnGb = CultureInfo.GetCultureInfo("en-GB");
    static readonly CompareInfo Collation = Inv.CompareInfo;
    public static readonly DateTime Epoch1900 = new(1900, 1, 1);

    /// <summary>SET DATEFORMAT dmy / SET LANGUAGE British for the session executing on this thread.</summary>
    [ThreadStatic] public static bool DayFirst;

    // ---------------------------------------------------------------- type of a value

    public static SqlType TypeOf(object v) => v switch
    {
        bool => SqlType.Bit,
        byte => SqlType.TinyInt,
        short => SqlType.SmallInt,
        int => SqlType.Int,
        long => SqlType.BigInt,
        decimal d => SqlType.Decimal(DecPrecision(d), d.Scale),
        float => SqlType.Real,
        double => SqlType.Float,
        string s => s.Length > 4000 ? SqlType.NVarCharMax : SqlType.NVarChar(Math.Max(1, s.Length)),
        DateTime => SqlType.DateTime,
        DateTimeOffset => SqlType.DateTimeOffset,
        TimeSpan => SqlType.Time,
        Guid => SqlType.UniqueIdentifier,
        byte[] b => new SqlType(SqlTypeKind.VarBinary, Math.Max(1, b.Length)),
        _ => SqlType.Variant,
    };

    public static int DecPrecision(decimal d)
    {
        var bits = decimal.GetBits(d);
        var unscaled = new decimal(bits[0], bits[1], bits[2], false, 0);
        int digits = unscaled == 0 ? 1 : unscaled.ToString(Inv).Length;
        return Math.Max(Math.Max(digits, d.Scale), 1);
    }

    /// <summary>0 = not numeric; otherwise position in bool &lt; byte &lt; short &lt; int &lt; long &lt; decimal &lt; float &lt; double.</summary>
    public static int NumRank(object v) => v switch
    {
        bool => 1, byte => 2, short => 3, int => 4, long => 5, decimal => 6, float => 7, double => 8, _ => 0,
    };

    // ---------------------------------------------------------------- conversions

    public static object? ConvertTo(object? v, SqlType t, int style = 0, SqlType? src = null, bool storage = false)
    {
        if (v == null) return null;
        switch (t.Kind)
        {
            case SqlTypeKind.TinyInt or SqlTypeKind.SmallInt or SqlTypeKind.Int or SqlTypeKind.BigInt when v is byte[] raw:
            {
                int width = t.Kind switch { SqlTypeKind.TinyInt => 1, SqlTypeKind.SmallInt => 2, SqlTypeKind.Int => 4, _ => 8 };
                long bits = 0;
                for (int i = Math.Max(0, raw.Length - width); i < raw.Length; i++) bits = (bits << 8) | raw[i];
                return t.Kind switch { SqlTypeKind.TinyInt => (byte)bits, SqlTypeKind.SmallInt => (short)bits, SqlTypeKind.Int => (int)bits, _ => (object)bits };
            }
            case SqlTypeKind.Bit: return v is bool ? v : ToBool(v);
            case SqlTypeKind.TinyInt:
            {
                if (v is byte) return v;
                long l = ToLong(v, "tinyint");
                if (l < 0 || l > 255) throw Err.New(220, $"Arithmetic overflow error for data type tinyint, value = {l}.");
                return (byte)l;
            }
            case SqlTypeKind.SmallInt:
            {
                if (v is short) return v;
                long l = ToLong(v, "smallint");
                if (l < short.MinValue || l > short.MaxValue) throw Err.New(220, $"Arithmetic overflow error for data type smallint, value = {l}.");
                return (short)l;
            }
            case SqlTypeKind.Int:
            {
                if (v is int) return v;
                long l = ToLong(v, "int");
                if (l < int.MinValue || l > int.MaxValue) throw Err.Overflow("int");
                return (int)l;
            }
            case SqlTypeKind.BigInt: return v is long ? v : ToLong(v, "bigint");
            case SqlTypeKind.Decimal: return ToDecimalTyped(ToDecimal(v, "numeric"), t.Precision, t.Scale);
            case SqlTypeKind.Money: return ToDecimalTyped(ToDecimal(v, "money"), 19, 4);
            case SqlTypeKind.SmallMoney: return ToDecimalTyped(ToDecimal(v, "smallmoney"), 10, 4);
            case SqlTypeKind.Real: return v is float ? v : (float)ToDouble(v, "real");
            case SqlTypeKind.Float: return v is double ? v : ToDouble(v, "float");
            case SqlTypeKind.Char:
            case SqlTypeKind.VarChar:
            case SqlTypeKind.NChar:
            case SqlTypeKind.NVarChar:
            {
                string s = v as string ?? ToStr(v, style, src);
                if (v is Guid && t.Length != SqlType.Max && t.Length < 36)
                    throw Err.New(8170, "Insufficient result space to convert uniqueidentifier value to char.");
                if (t.Length != SqlType.Max && s.Length > t.Length && NumRank(v) > 1)
                {
                    // numbers are never silently truncated: short integers show '*', everything else is an error
                    if (v is int or short or byte && !t.IsUnicode) s = "*";
                    else throw Err.New(8115, $"Arithmetic overflow error converting {(v is decimal ? "numeric" : "expression")} to data type {t.Name}.");
                }
                if (t.Length != SqlType.Max && s.Length > t.Length)
                {
                    if (storage && s.AsSpan().TrimEnd(' ').Length > t.Length)
                        throw Err.New(2628, "String or binary data would be truncated.");
                    s = s[..t.Length];
                }
                if (t.Kind is SqlTypeKind.Char or SqlTypeKind.NChar && t.Length != SqlType.Max && s.Length < t.Length)
                    s = s.PadRight(t.Length);
                return s;
            }
            case SqlTypeKind.Binary:
            case SqlTypeKind.VarBinary:
            {
                byte[] b = ToBytes(v, src, style);
                bool numeric = NumRank(v) > 0;
                if (t.Length != SqlType.Max && b.Length > t.Length)
                {
                    if (storage) throw Err.New(2628, "String or binary data would be truncated.");
                    b = numeric ? b[^t.Length..] : b[..t.Length];
                }
                if (t.Kind == SqlTypeKind.Binary && t.Length != SqlType.Max && b.Length < t.Length)
                {
                    var padded = new byte[t.Length];
                    Array.Copy(b, 0, padded, numeric ? t.Length - b.Length : 0, b.Length);
                    b = padded;
                }
                return b;
            }
            case SqlTypeKind.UniqueIdentifier:
                switch (v)
                {
                    case Guid: return v;
                    case string s when s.Trim() is { Length: >= 36 } text && Guid.TryParseExact(text[..36], "D", out var g): return g;
                    case byte[] { Length: 16 } bytes: return new Guid(bytes);
                    case string: throw Err.New(8169, "Conversion failed when converting from a character string to uniqueidentifier.");
                }
                break;
            case SqlTypeKind.Date: return ToDateTime(v, style).Date;
            case SqlTypeKind.SmallDateTime:
            {
                var d = ToDateTime(v, style);
                return new DateTime((d.Ticks + TimeSpan.TicksPerSecond * 30) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute);
            }
            case SqlTypeKind.DateTime: return RoundDatetime(ToDateTime(v, style));
            case SqlTypeKind.DateTime2: return new DateTime(RoundTicks(ToDateTime(v, style).Ticks, t.Scale));
            case SqlTypeKind.Time:
                return v switch
                {
                    TimeSpan ts => RoundTime(ts.Ticks, t.Scale),
                    DateTimeOffset dto => RoundTime(dto.TimeOfDay.Ticks, t.Scale),
                    _ => RoundTime(ToDateTime(v, style).TimeOfDay.Ticks, t.Scale),
                };
            case SqlTypeKind.DateTimeOffset:
                switch (v)
                {
                    case DateTimeOffset dto0: return new DateTimeOffset(RoundTicks(dto0.Ticks, t.Scale), dto0.Offset);
                    case DateTime dt: return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero);
                    case string s:
                        if (DateTimeOffset.TryParse(s, EnUs, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var o))
                            return new DateTimeOffset(RoundTicks(o.Ticks, t.Scale), o.Offset);
                        throw Err.New(241, "Conversion failed when converting date and/or time from character string.");
                }
                break;
            case SqlTypeKind.Variant: return v;
        }
        throw Err.New(529, $"Explicit conversion from data type {TypeOf(v).Name} to {t.Name} is not allowed.");
    }

    static long RoundTicks(long ticks, int scale)
    {
        if (scale >= 7) return ticks;
        long unit = 1;
        for (int i = scale; i < 7; i++) unit *= 10;
        return (ticks + unit / 2) / unit * unit;
    }

    static TimeSpan RoundTime(long ticks, int scale)
    {
        long rounded = RoundTicks(ticks, scale);
        // a time of day never rounds up into the next day
        if (rounded >= TimeSpan.TicksPerDay) rounded = RoundTicks(ticks - TimeSpan.TicksPerSecond, scale);
        return new TimeSpan(rounded);
    }

    /// <summary>datetime has a resolution of 1/300 second, displayed as .000 / .003 / .007.</summary>
    public static DateTime RoundDatetime(DateTime d)
    {
        long dayTicks = d.Ticks % TimeSpan.TicksPerDay;
        long units = (long)Math.Round(dayTicks * 300.0 / TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
        long ms = (long)Math.Round(units * 10 / 3.0, MidpointRounding.AwayFromZero);
        return new DateTime(d.Ticks - dayTicks + ms * TimeSpan.TicksPerMillisecond);
    }

    public static bool ToBool(object v)
    {
        switch (v)
        {
            case bool b: return b;
            case string s:
                s = s.Trim();
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                if (decimal.TryParse(s, NumberStyles.Number, Inv, out var parsed)) return parsed != 0;
                throw Err.New(245, $"Conversion failed when converting the varchar value '{s}' to data type bit.");
            case double d: return d != 0;
            case float f: return f != 0;
            case decimal m: return m != 0;
            default: return ToLong(v, "bit") != 0;
        }
    }

    public static long ToLong(object v, string typeName = "bigint")
    {
        switch (v)
        {
            case int i: return i;
            case long l: return l;
            case bool b: return b ? 1 : 0;
            case byte b: return b;
            case short s: return s;
            case decimal d:
                try { return (long)decimal.Truncate(d); }
                catch (OverflowException) { throw Err.Overflow(typeName); }
            case double d:
                if (double.IsNaN(d) || Math.Abs(d) >= 9.2233720368547758E+18) throw Err.Overflow(typeName);
                return (long)Math.Truncate(d);
            case float f:
                if (float.IsNaN(f) || Math.Abs(f) >= 9.2233720368547758E+18) throw Err.Overflow(typeName);
                return (long)Math.Truncate(f);
            case string s:
                s = s.Trim();
                if (s.Length == 0 || s is "+" or "-") return 0;
                if (long.TryParse(s, NumberStyles.AllowLeadingSign, Inv, out var r)) return r;
                throw Err.New(245, $"Conversion failed when converting the varchar value '{s}' to data type {typeName}.");
            case DateTime dt: return (long)Math.Round((dt - Epoch1900).TotalDays, MidpointRounding.AwayFromZero);
            case byte[] bytes:
            {
                long r2 = 0;
                foreach (byte x in bytes.Length > 8 ? bytes[^8..] : bytes) r2 = (r2 << 8) | x;
                return r2;
            }
        }
        throw Err.New(529, $"Explicit conversion from data type {TypeOf(v).Name} to {typeName} is not allowed.");
    }

    public static decimal ToDecimal(object v, string typeName = "numeric")
    {
        switch (v)
        {
            case decimal d: return d;
            case int i: return i;
            case long l: return l;
            case bool b: return b ? 1 : 0;
            case byte b: return b;
            case short s: return s;
            case double d:
                try
                {
                    if (Math.Abs(d) < 1e9 && decimal.TryParse(d.ToString("F19", Inv), NumberStyles.Float, Inv, out var exact)) return exact;
                    return (decimal)d;
                }
                catch (OverflowException) { throw Err.Overflow(typeName); }
            case float f:
                try { return (decimal)f; }
                catch (OverflowException) { throw Err.Overflow(typeName); }
            case string s:
                var styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
                if (typeName.EndsWith("money", StringComparison.Ordinal))
                {
                    styles |= NumberStyles.AllowThousands | NumberStyles.AllowCurrencySymbol;
                    if (string.IsNullOrWhiteSpace(s)) return 0;
                }
                if (decimal.TryParse(s, styles, EnUs, out var r)) return r;
                if (typeName.EndsWith("money", StringComparison.Ordinal))
                    throw Err.New(235, "Cannot convert a char value to money. The char value has incorrect syntax.");
                throw Err.New(8114, $"Error converting data type varchar to {typeName}.");
        }
        throw Err.New(529, $"Explicit conversion from data type {TypeOf(v).Name} to {typeName} is not allowed.");
    }

    public static double ToDouble(object v, string typeName = "float")
    {
        switch (v)
        {
            case double d: return d;
            case float f: return f;
            case decimal m: return (double)m;
            case int i: return i;
            case long l: return l;
            case bool b: return b ? 1 : 0;
            case byte b: return b;
            case short s: return s;
            case string s:
                if (string.IsNullOrWhiteSpace(s)) return 0;
                if (double.TryParse(s, NumberStyles.Float, Inv, out var r) && double.IsFinite(r)) return r;
                throw Err.New(8114, $"Error converting data type varchar to {typeName}.");
            case DateTime dt: return (dt - Epoch1900).TotalDays;
        }
        throw Err.New(529, $"Explicit conversion from data type {TypeOf(v).Name} to {typeName} is not allowed.");
    }

    static readonly decimal[] Pow10 = BuildPow10();
    static decimal[] BuildPow10()
    {
        var p = new decimal[29];
        p[0] = 1;
        for (int i = 1; i < p.Length; i++) p[i] = p[i - 1] * 10;
        return p;
    }

    public static decimal ToDecimalTyped(decimal d, int precision, int scale)
    {
        scale = Math.Min(scale, 28);
        if (d.Scale > scale) d = Math.Round(d, scale, MidpointRounding.AwayFromZero);
        int intDigits = precision - scale;
        if (intDigits < 29 && Math.Abs(d) >= Pow10[Math.Max(intDigits, 0)])
            throw Err.New(8115, "Arithmetic overflow error converting numeric to data type numeric.");
        return SetScale(d, scale);
    }

    /// <summary>Pads a decimal with trailing zeros so that it carries at least the given scale.</summary>
    public static decimal SetScale(decimal d, int scale)
    {
        if (d.Scale >= scale) return d;
        try { return d + new decimal(0, 0, 0, false, (byte)Math.Min(scale, 28)); }
        catch (OverflowException) { return d; }
    }

    public static byte[] ToBytes(object v, SqlType? src, int style = 0)
    {
        switch (v)
        {
            case byte[] b: return b;
            case string s:
                if (style is 1 or 2)
                {
                    var hex = style == 1 && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
                    try { return Convert.FromHexString(hex); }
                    catch (FormatException) { throw Err.New(8114, "Error converting data type varchar to varbinary."); }
                }
                return src?.IsUnicode == true ? Encoding.Unicode.GetBytes(s) : Encoding.Latin1.GetBytes(s);
            case bool b: return [(byte)(b ? 1 : 0)];
            case byte b: return [b];
            case short s: return [(byte)(s >> 8), (byte)s];
            case int i: return [(byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i];
            case long l:
            {
                var r = new byte[8];
                for (int k = 0; k < 8; k++) r[k] = (byte)(l >> (56 - 8 * k));
                return r;
            }
            case Guid g: return g.ToByteArray();
        }
        throw Err.New(529, $"Explicit conversion from data type {TypeOf(v).Name} to varbinary is not allowed.");
    }

    // ---------------------------------------------------------------- strings

    public static string ToStr(object v, int style = 0, SqlType? src = null)
    {
        switch (v)
        {
            case string s: return s;
            case bool b: return b ? "1" : "0";
            case int i: return i.ToString(Inv);
            case long l: return l.ToString(Inv);
            case byte b: return b.ToString(Inv);
            case short s: return s.ToString(Inv);
            case decimal d:
                if (src?.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney)
                    return style == 1 ? d.ToString("#,##0.00", Inv) : style == 2 ? d.ToString("0.0000", Inv) : d.ToString("0.00", Inv);
                return d.ToString(Inv);
            case double d: return FloatToStr(d, style, 15);
            case float f: return FloatToStr(f, style, 7);
            case DateTime dt: return DateToStr(dt, style, src?.Kind ?? SqlTypeKind.DateTime, src?.Scale ?? 7);
            case DateTimeOffset o:
            {
                int scale = src?.Scale ?? 7;
                return o.ToString("yyyy-MM-dd HH:mm:ss" + Fraction(scale) + " zzz", Inv);
            }
            case TimeSpan ts:
            {
                var clock = new DateTime(ts.Ticks);
                return style switch
                {
                    8 or 108 or 24 => clock.ToString("HH:mm:ss", Inv),
                    14 or 114 => clock.ToString("HH:mm:ss" + Fraction(src?.Scale ?? 7), Inv),
                    100 or 9 or 109 => (clock.Hour % 12 == 0 ? 12 : clock.Hour % 12).ToString(Inv) + clock.ToString(":mm", Inv) + (clock.Hour < 12 ? "AM" : "PM"),
                    _ => clock.ToString("HH:mm:ss" + Fraction(src?.Scale ?? 7), Inv),
                };
            }
            case Guid g: return g.ToString().ToUpperInvariant();
            case byte[] bytes:
                return style switch
                {
                    1 => "0x" + Convert.ToHexString(bytes),
                    2 => Convert.ToHexString(bytes),
                    _ => Encoding.Latin1.GetString(bytes),
                };
        }
        return Convert.ToString(v, Inv) ?? "";
    }

    static string Fraction(int scale) => scale <= 0 ? "" : "." + new string('f', Math.Min(scale, 7));

    static string FloatToStr(double d, int style, int maxDigits)
    {
        if (d == 0) return "0";
        if (double.IsNaN(d) || double.IsInfinity(d)) return d.ToString(Inv);
        int sig = style switch { 1 => 8, 2 => 16, 3 => 17, _ => 6 };
        if (style == 0) sig = Math.Min(sig, maxDigits);
        string e = d.ToString("E" + (sig - 1), Inv); // d.ddddE+ddd
        int ePos = e.IndexOf('E');
        string mant = e[..ePos];
        int exp = int.Parse(e[(ePos + 1)..], Inv);
        if (style == 0)
        {
            if (mant.Contains('.')) mant = mant.TrimEnd('0').TrimEnd('.');
            if (exp >= -4 && exp < sig)
            {
                decimal m = decimal.Parse(mant, Inv);
                m = exp >= 0 ? m * Pow10[exp] : m / Pow10[-exp];
                return m.ToString("0.###############", Inv);
            }
        }
        return mant + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("000", Inv);
    }

    static string DateToStr(DateTime d, int style, SqlTypeKind kind, int scale)
    {
        bool dateOnly = kind == SqlTypeKind.Date;
        string F(string fmt) => d.ToString(fmt, EnUs);
        string AmPm(bool seconds)
        {
            int h = d.Hour % 12;
            if (h == 0) h = 12;
            string t = h.ToString(Inv).PadLeft(2) + ":" + F("mm");
            if (seconds) t += ":" + F("ss") + ":" + F("fff");
            return t + (d.Hour < 12 ? "AM" : "PM");
        }
        if (dateOnly)
        {
            // a date has no time of day: the long styles print just their date part
            switch (style)
            {
                case 100 or 9 or 109: return F("MMM") + " " + d.Day.ToString(Inv).PadLeft(2) + " " + F("yyyy");
                case 13 or 113: return F("dd MMM yyyy");
                case 22: return F("MM'/'dd'/'yy");
            }
        }
        switch (style)
        {
            case 22:
            {
                int h = d.Hour % 12 == 0 ? 12 : d.Hour % 12;
                return F("MM'/'dd'/'yy") + " " + h.ToString(Inv).PadLeft(2) + F(":mm:ss") + (d.Hour < 12 ? " AM" : " PM");
            }
            case 0:
            case 100:
                if (style == 0)
                {
                    if (dateOnly) return F("yyyy-MM-dd");
                    if (kind == SqlTypeKind.DateTime2) return F("yyyy-MM-dd HH:mm:ss" + Fraction(scale));
                }
                return F("MMM") + " " + d.Day.ToString(Inv).PadLeft(2) + " " + F("yyyy") + " " + AmPm(false);
            case 1: return F("MM'/'dd'/'yy");
            case 101: return F("MM'/'dd'/'yyyy");
            case 2: return F("yy.MM.dd");
            case 102: return F("yyyy.MM.dd");
            case 3: return F("dd'/'MM'/'yy");
            case 103: return F("dd'/'MM'/'yyyy");
            case 4: return F("dd.MM.yy");
            case 104: return F("dd.MM.yyyy");
            case 5: return F("dd-MM-yy");
            case 105: return F("dd-MM-yyyy");
            case 6: return F("dd MMM yy");
            case 106: return F("dd MMM yyyy");
            case 7: return F("MMM dd, yy");
            case 107: return F("MMM dd, yyyy");
            case 8: case 24: case 108: return F("HH:mm:ss");
            case 9: case 109:
                if (kind == SqlTypeKind.DateTime2)
                {
                    int h12 = d.Hour % 12 == 0 ? 12 : d.Hour % 12;
                    return F("MMM") + " " + d.Day.ToString(Inv).PadLeft(2) + " " + F("yyyy") + " " + h12.ToString(Inv).PadLeft(2)
                           + F(":mm:ss" + Fraction(scale)) + (d.Hour < 12 ? "AM" : "PM");
                }
                return F("MMM") + " " + d.Day.ToString(Inv).PadLeft(2) + " " + F("yyyy") + " " + AmPm(true);
            case 10: return F("MM-dd-yy");
            case 110: return F("MM-dd-yyyy");
            case 11: return F("yy'/'MM'/'dd");
            case 111: return F("yyyy'/'MM'/'dd");
            case 12: return F("yyMMdd");
            case 112: return F("yyyyMMdd");
            case 13: case 113: return kind == SqlTypeKind.DateTime2 ? F("dd MMM yyyy HH:mm:ss" + Fraction(scale)) : F("dd MMM yyyy HH:mm:ss:fff");
            case 14: case 114: return kind == SqlTypeKind.DateTime2 ? F("HH:mm:ss" + Fraction(scale)) : F("HH:mm:ss:fff");
            case 20: case 120: return dateOnly ? F("yyyy-MM-dd") : F("yyyy-MM-dd HH:mm:ss");
            case 21: case 25: case 121:
                return dateOnly ? F("yyyy-MM-dd")
                    : kind == SqlTypeKind.DateTime2 ? F("yyyy-MM-dd HH:mm:ss" + Fraction(scale)) : F("yyyy-MM-dd HH:mm:ss.fff");
            case 23: return F("yyyy-MM-dd");
            case 126: case 127:
                if (dateOnly) return F("yyyy-MM-dd");
                if (kind == SqlTypeKind.DateTime2) return F("yyyy-MM-dd'T'HH:mm:ss" + Fraction(scale));
                return d.Millisecond == 0 ? F("yyyy-MM-dd'T'HH:mm:ss") : F("yyyy-MM-dd'T'HH:mm:ss.fff");
            default:
                throw Err.New(281, $"{style} is not a valid style number when converting from datetime to a character string.");
        }
    }

    static readonly string[] CompactDateFormats =
    [
        "yyyyMMdd", "yyyyMMdd HH:mm", "yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm:ss:FFF", "yyyy",
    ];

    public static bool TryParseDateTime(string s, int style, out DateTime result)
    {
        s = s.Trim();
        if (s.Length == 0) { result = Epoch1900; return true; }
        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AdjustToUniversal;
        if (DateTime.TryParseExact(s, CompactDateFormats, Inv, DateTimeStyles.AllowWhiteSpaces, out result)) return true;
        var culture = style is 3 or 103 or 4 or 104 or 5 or 105 || (DayFirst && style is 0) ? EnGb : EnUs;
        if (!DateTime.TryParse(s, culture, styles, out result)) return false;
        if (result.Year == 1) result = Epoch1900 + result.TimeOfDay; // time-only strings default to 1900-01-01
        result = DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
        return true;
    }

    public static DateTime ToDateTime(object v, int style = 0)
    {
        switch (v)
        {
            case DateTime d: return d;
            case DateTimeOffset o: return o.DateTime;
            case TimeSpan ts: return Epoch1900 + ts;
            case string s:
                if (TryParseDateTime(s, style, out var r)) return r;
                throw Err.New(241, "Conversion failed when converting date and/or time from character string.");
        }
        if (NumRank(v) > 0)
        {
            try { return Epoch1900.AddDays(ToDouble(v)); }
            catch (ArgumentOutOfRangeException) { throw Err.New(8115, "Arithmetic overflow error converting expression to data type datetime."); }
        }
        throw Err.New(529, $"Explicit conversion from data type {TypeOf(v).Name} to datetime is not allowed.");
    }

    // ---------------------------------------------------------------- comparison

    public static int CompareStrings(string a, string b) => SqlCollation.CompareLatin1(a, b);

    /// <summary>Ordering comparison for ORDER BY, where nvarchar data follows Unicode sorting rules.</summary>
    public static int CompareForSort(object a, object b, bool unicode)
        => unicode && a is string sa && b is string sb ? SqlCollation.CompareUnicode(sa, sb) : Compare(a, b);

    /// <summary>When a string meets a value of a higher-precedence type, the string is converted to that type.</summary>
    static object CoerceStringTo(string s, object other) => other switch
    {
        bool => ToBool(s),
        byte => ConvertTo(s, SqlType.TinyInt)!,
        short => ConvertTo(s, SqlType.SmallInt)!,
        int => ConvertTo(s, SqlType.Int)!,
        long => ToLong(s),
        decimal => ToDecimal(s),
        float or double => ToDouble(s),
        DateTime => ToDateTime(s),
        DateTimeOffset => ConvertTo(s, SqlType.DateTimeOffset)!,
        TimeSpan => ConvertTo(s, SqlType.Time)!,
        Guid => ConvertTo(s, SqlType.UniqueIdentifier)!,
        _ => s,
    };

    /// <summary>Compares two non-null values using T-SQL implicit conversion rules.</summary>
    public static int Compare(object a, object b)
    {
        if (a is string sa)
        {
            if (b is string sb) return CompareStrings(sa, sb);
            a = CoerceStringTo(sa, b);
        }
        else if (b is string sb) b = CoerceStringTo(sb, a);

        int ra = NumRank(a), rb = NumRank(b);
        if (ra > 0 && rb > 0)
        {
            if (ra >= 7 || rb >= 7) return ToDouble(a).CompareTo(ToDouble(b));
            if (ra == 6 || rb == 6) return ToDecimal(a).CompareTo(ToDecimal(b));
            return ToLong(a).CompareTo(ToLong(b));
        }
        switch (a)
        {
            case DateTime da:
                if (b is DateTime db) return da.CompareTo(db);
                if (b is DateTimeOffset dbo) return new DateTimeOffset(da, TimeSpan.Zero).CompareTo(dbo);
                if (rb > 0) return da.CompareTo(ToDateTime(b));
                break;
            case DateTimeOffset oa:
                if (b is DateTimeOffset ob) return oa.CompareTo(ob);
                if (b is DateTime db2) return oa.CompareTo(new DateTimeOffset(db2, TimeSpan.Zero));
                break;
            case TimeSpan ta when b is TimeSpan tb: return ta.CompareTo(tb);
            case Guid ga when b is Guid gb: return new SqlGuid(ga).CompareTo(new SqlGuid(gb));
            case byte[] ba when b is byte[] bb: return TrimZeros(ba).SequenceCompareTo(TrimZeros(bb));
            case byte[] ba when rb > 0: return ToLong(ba).CompareTo(ToLong(b));
            case string s1 when b is string s2: return CompareStrings(s1, s2);
        }
        if (ra > 0 && b is DateTime db3) return ToDateTime(a).CompareTo(db3);
        if (ra > 0 && b is byte[] bytes2) return ToLong(a).CompareTo(ToLong(bytes2));
        throw Err.New(206, $"Operand type clash: {TypeOf(a).Name} is incompatible with {TypeOf(b).Name}");
    }

    public static bool SqlEquals(object a, object b) => Compare(a, b) == 0;

    static ReadOnlySpan<byte> TrimZeros(byte[] b)
    {
        int n = b.Length;
        while (n > 0 && b[n - 1] == 0) n--;
        return b.AsSpan(0, n);
    }

    // ---------------------------------------------------------------- arithmetic

    static string OpName(string op) => op switch
    {
        "+" => "add", "-" => "subtract", "*" => "multiply", "/" => "divide", "%" => "modulo",
        "&" => "boolean AND", "|" => "boolean OR", "^" => "boolean XOR", _ => op,
    };

    static object Narrow(long z, int rank)
    {
        switch (rank)
        {
            case 2:
                if (z < 0 || z > 255) throw Err.New(8115, $"Arithmetic overflow error for data type tinyint, value = {z}.");
                return (byte)z;
            case 3:
                if (z < short.MinValue || z > short.MaxValue) throw Err.New(8115, $"Arithmetic overflow error for data type smallint, value = {z}.");
                return (short)z;
            case 5: return z;
            default:
                if (z < int.MinValue || z > int.MaxValue) throw Err.Overflow("int");
                return (int)z;
        }
    }

    static SqlType NumType(SqlType? declared, object v)
    {
        if (declared != null)
        {
            if (declared.Kind == SqlTypeKind.Bit) return SqlType.Decimal(1, 0);
            if (declared.IsInteger || declared.IsDecimalLike) return declared;
        }
        return v is bool ? SqlType.Decimal(1, 0) : TypeOf(v);
    }

    /// <summary>money combined with money or integers yields money (4 decimals); a true decimal operand wins instead.</summary>
    static bool IsMoneyResult(SqlType? lt, SqlType? rt)
    {
        bool lm = lt?.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney, rm = rt?.Kind is SqlTypeKind.Money or SqlTypeKind.SmallMoney;
        if (!lm && !rm) return false;
        return (lm || lt is { IsInteger: true } or { Kind: SqlTypeKind.Bit }) && (rm || rt is { IsInteger: true } or { Kind: SqlTypeKind.Bit });
    }

    public static decimal DecDivide(decimal x, decimal y, SqlType t1, SqlType t2)
    {
        if (y == 0) throw Err.DivideByZero();
        int scale = Math.Max(6, t1.Scale + t2.Precision + 1);
        int prec = t1.Precision - t1.Scale + t2.Scale + scale;
        if (prec > 38)
        {
            int intPart = prec - scale;
            scale = Math.Max(Math.Min(scale, 38 - intPart), Math.Min(scale, 6));
        }
        scale = Math.Min(scale, 28);
        decimal q = x / y;
        if (q.Scale > scale) q = Math.Round(q, scale, MidpointRounding.ToZero);
        return SetScale(q, scale);
    }

    public static object? Arith(string op, object? a, object? b, SqlType? lt = null, SqlType? rt = null)
    {
        if (a == null || b == null) return null;
        if (a is string sa)
        {
            if (b is string sb)
            {
                if (op == "+") return sa + sb;
                throw Err.New(8117, $"Operand data type varchar is invalid for {OpName(op)} operator.");
            }
            a = CoerceStringTo(sa, b);
        }
        else if (b is string sb) b = CoerceStringTo(sb, a);

        int ra = NumRank(a), rb = NumRank(b);
        if (op == "+" && a is byte[] ba && b is byte[] bb) return (byte[])[.. ba, .. bb];
        // binary combined with a number takes the number's type
        if (a is byte[] abytes && NumRank(b) > 0) { a = ConvertTo(abytes, TypeOf(b))!; ra = NumRank(a); }
        else if (b is byte[] bbytes && NumRank(a) > 0) { b = ConvertTo(bbytes, TypeOf(a))!; rb = NumRank(b); }
        if (ra == 0 || rb == 0) return DateArith(op, a, b);
        int r = Math.Max(Math.Max(ra, rb), 2);

        if (op is "&" or "|" or "^")
        {
            if (r > 5) throw Err.New(8117, $"Operand data type {TypeOf(r == ra ? a : b).Name} is invalid for {OpName(op)} operator.");
            long x = ToLong(a), y = ToLong(b);
            return Narrow(op == "&" ? x & y : op == "|" ? x | y : x ^ y, ra == 1 && rb == 1 ? 4 : r);
        }

        if (r <= 5)
        {
            long x = ToLong(a), y = ToLong(b), z;
            try
            {
                checked
                {
                    switch (op)
                    {
                        case "+": z = x + y; break;
                        case "-": z = x - y; break;
                        case "*": z = x * y; break;
                        case "/": if (y == 0) throw Err.DivideByZero(); z = x / y; break;
                        case "%": if (y == 0) throw Err.DivideByZero(); z = y == -1 ? 0 : x % y; break;
                        default: throw Err.New(102, $"Incorrect syntax near '{op}'.");
                    }
                }
            }
            catch (OverflowException) { throw Err.Overflow(r == 5 ? "bigint" : "int"); }
            return Narrow(z, r);
        }

        if (r == 6)
        {
            decimal x = ToDecimal(a), y = ToDecimal(b);
            try
            {
                if (op is "*" or "/" && IsMoneyResult(lt, rt))
                {
                    if (op == "/" && y == 0) throw Err.DivideByZero();
                    decimal m = op == "*" ? x * y : x / y;
                    return SetScale(Math.Round(m, 4, op == "*" ? MidpointRounding.AwayFromZero : MidpointRounding.ToZero), 4);
                }
                switch (op)
                {
                    case "+": return x + y;
                    case "-": return x - y;
                    case "*":
                    {
                        decimal product = x * y;
                        if (lt == null || rt == null) return product;
                        SqlType t1 = NumType(lt, a), t2 = NumType(rt, b);
                        int precision = t1.Precision + t2.Precision + 1, scale = t1.Scale + t2.Scale;
                        if (precision <= 38) return product;
                        int reduced = Math.Max(Math.Min(scale, 38 - (precision - scale)), Math.Min(scale, 6));
                        return SetScale(Math.Round(product, Math.Min(reduced, 28), MidpointRounding.AwayFromZero), reduced);
                    }
                    case "/": return DecDivide(x, y, NumType(lt, a), NumType(rt, b));
                    case "%": if (y == 0) throw Err.DivideByZero(); return x % y;
                }
            }
            catch (OverflowException) { throw Err.New(8115, "Arithmetic overflow error converting expression to data type numeric."); }
            throw Err.New(102, $"Incorrect syntax near '{op}'.");
        }

        double dx = ToDouble(a), dy = ToDouble(b), dz;
        switch (op)
        {
            case "+": dz = dx + dy; break;
            case "-": dz = dx - dy; break;
            case "*": dz = dx * dy; break;
            case "/": if (dy == 0) throw Err.DivideByZero(); dz = dx / dy; break;
            case "%": if (dy == 0) throw Err.DivideByZero(); dz = dx % dy; break;
            default: throw Err.New(102, $"Incorrect syntax near '{op}'.");
        }
        if (double.IsInfinity(dz)) throw Err.Overflow("float");
        dz += 0.0; // never negative zero
        return r == 7 ? (float)dz : dz;
    }

    static object DateArith(string op, object a, object b)
    {
        try
        {
            if (a is DateTime da)
            {
                if (NumRank(b) > 0) return op == "+" ? da.AddDays(ToDouble(b)) : op == "-" ? da.AddDays(-ToDouble(b)) : throw Clash(op, a, b);
                if (b is DateTime db && op == "-") return Epoch1900 + (da - db);
                if (b is DateTime db2 && op == "+") return da + (db2 - Epoch1900);
            }
            else if (b is DateTime db3 && NumRank(a) > 0 && op == "+") return db3.AddDays(ToDouble(a));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Err.New(517, "Adding a value to a 'datetime' column caused an overflow.");
        }
        throw Clash(op, a, b);
    }

    static TSqlException Clash(string op, object a, object b)
        => Err.New(8117, $"Operand data type {TypeOf(NumRank(a) > 0 ? b : a).Name} is invalid for {OpName(op)} operator.");

    public static object? Negate(object? v)
    {
        try
        {
            checked
            {
                return v switch
                {
                    null => null,
                    int i => -i,
                    long l => -l,
                    decimal d => -d,
                    double d => -d,
                    float f => -f,
                    short s => (short)-s,
                    byte b => (short)-b,
                    bool b => b ? -1 : 0,
                    string s => -ToDouble(s),
                    _ => throw Err.New(8117, $"Operand data type {TypeOf(v).Name} is invalid for minus operator."),
                };
            }
        }
        catch (OverflowException) { throw Err.Overflow("int"); }
    }

    // ---------------------------------------------------------------- hashing keys

    static readonly object NullKey = DBNull.Value;

    /// <summary>
    /// Maps a value to a canonical object whose Equals/GetHashCode agree with SQL equality
    /// (case-insensitive strings ignoring trailing blanks, numerics compared by value).
    /// </summary>
    public static object NormalizeKey(object? v)
    {
        switch (v)
        {
            case null: return NullKey;
            case string s: return SqlCollation.KeyOf(s);
            case int i: return (long)i;
            case long: return v;
            case bool b: return b ? 1L : 0L;
            case byte b: return (long)b;
            case short s: return (long)s;
            case decimal d:
                if (d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue) return (long)d;
                return d;
            case float f: return NormalizeKey((double)f);
            case double d:
                if (d == Math.Truncate(d) && Math.Abs(d) < 9e15) return (long)d;
                return d;
            case byte[] b: return "\00x" + Convert.ToHexString(TrimZeros(b));
            default: return v;
        }
    }

    // Classes of normalized keys, used to decide whether a hash lookup is equivalent to SQL comparison.
    public const int KInt = 1, KDec = 2, KFloat = 4, KStr = 8, KDate = 16, KGuid = 32, KOffset = 64, KTime = 128, KOther = 256;

    public static int KeyClass(object normalized) => normalized switch
    {
        long => KInt,
        decimal => KDec,
        double d => Math.Abs(d) >= 9e15 ? KOther : KFloat, // beyond 2^53 a float no longer compares like an exact number
        string => KStr,
        DateTime => KDate,
        Guid => KGuid,
        DateTimeOffset => KOffset,
        TimeSpan => KTime,
        DBNull => 0,
        _ => KOther,
    };

    /// <summary>True if every key seen (on both sides) belongs to one comparison-compatible family.</summary>
    public static bool HashCompatible(int mask)
        => (mask & KOther) == 0
           && ((mask & ~(KInt | KDec)) == 0 || (mask & ~(KInt | KFloat)) == 0 || (mask & (mask - 1)) == 0);

    // ---------------------------------------------------------------- LIKE

    static readonly ConcurrentDictionary<(string, char), LikePattern> LikeCache = new();

    public static LikePattern LikePatternOf(string pattern, char escape = '\0')
    {
        if (LikeCache.Count > 5000) LikeCache.Clear();
        return LikeCache.GetOrAdd((pattern, escape), k => new LikePattern(k.Item1, k.Item2));
    }

    /// <summary>For varchar data trailing blanks of the tested string are insignificant; for nvarchar they are not.</summary>
    public static bool Like(string s, string pattern, char escape = '\0', bool unicode = false)
    {
        var p = LikePatternOf(pattern, escape);
        if (p.IsMatch(s)) return true;
        if (unicode || !s.EndsWith(' ')) return false;
        // blank-padded semantics: the pattern may also match the string with any number of trailing blanks removed
        int core = s.AsSpan().TrimEnd(' ').Length;
        for (int len = s.Length - 1; len >= core; len--)
            if (p.IsMatch(s.AsSpan(0, len))) return true;
        return false;
    }
}

/// <summary>
/// A compiled LIKE pattern. Characters compare case-insensitively and accent-sensitively, and bracket
/// ranges such as [a-f] follow the collation's sort order (so 'ë' is inside [a-y]), as in SQL Server.
/// </summary>
internal sealed class LikePattern
{
    enum Kind : byte { Char, AnyOne, AnyMany, Set }

    readonly struct Token(Kind kind, char c, (char Lo, char Hi)[]? ranges, bool negate)
    {
        public readonly Kind Kind = kind;
        public readonly char C = c;
        public readonly (char Lo, char Hi)[]? Ranges = ranges;
        public readonly bool Negate = negate;
    }

    readonly Token[] _tokens;

    public LikePattern(string pattern, char escape)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (escape != '\0' && c == escape && i + 1 < pattern.Length)
            {
                tokens.Add(new Token(Kind.Char, pattern[++i], null, false));
                continue;
            }
            switch (c)
            {
                case '%':
                    if (tokens.Count == 0 || tokens[^1].Kind != Kind.AnyMany) tokens.Add(new Token(Kind.AnyMany, c, null, false));
                    break;
                case '_':
                    tokens.Add(new Token(Kind.AnyOne, c, null, false));
                    break;
                case '[':
                {
                    int close = pattern.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        tokens.Add(new Token(Kind.Set, c, [], false));
                        i = pattern.Length;
                        break;
                    }
                    string body = pattern.Substring(i + 1, close - i - 1);
                    bool negate = body.StartsWith('^');
                    if (negate) body = body[1..];
                    var ranges = new List<(char, char)>();
                    for (int k = 0; k < body.Length; k++)
                    {
                        if (k + 2 < body.Length && body[k + 1] == '-')
                        {
                            ranges.Add((body[k], body[k + 2]));
                            k += 2;
                        }
                        else ranges.Add((body[k], body[k]));
                    }
                    // "[]" matches nothing and "[^]" any single character
                    tokens.Add(new Token(Kind.Set, c, ranges.ToArray(), negate));
                    i = close;
                    break;
                }
                default:
                    tokens.Add(new Token(Kind.Char, c, null, false));
                    break;
            }
        }
        _tokens = tokens.ToArray();
    }

    public bool StartsWithAnyMany => _tokens.Length > 0 && _tokens[0].Kind == Kind.AnyMany;

    static bool Matches(in Token t, char c)
    {
        switch (t.Kind)
        {
            case Kind.AnyOne: return true;
            case Kind.Char: return SqlCollation.CharEquals(t.C, c);
            case Kind.Set:
            {
                bool inSet = false;
                foreach (var (lo, hi) in t.Ranges!)
                {
                    if (lo == hi ? SqlCollation.CharEquals(lo, c) : SqlCollation.CompareChars(c, lo) >= 0 && SqlCollation.CompareChars(c, hi) <= 0)
                    {
                        inSet = true;
                        break;
                    }
                }
                return inSet != t.Negate;
            }
            default: return false;
        }
    }

    public bool IsMatch(ReadOnlySpan<char> s) => IsMatch(s, 0);

    bool IsMatch(ReadOnlySpan<char> s, int firstToken)
    {
        int si = 0, pi = firstToken, starToken = -1, starPos = 0;
        while (si < s.Length)
        {
            if (pi < _tokens.Length && _tokens[pi].Kind == Kind.AnyMany)
            {
                starToken = pi++;
                starPos = si;
            }
            else if (pi < _tokens.Length && Matches(_tokens[pi], s[si]))
            {
                pi++;
                si++;
            }
            else if (starToken >= 0)
            {
                pi = starToken + 1;
                si = ++starPos;
            }
            else return false;
        }
        while (pi < _tokens.Length && _tokens[pi].Kind == Kind.AnyMany) pi++;
        return pi == _tokens.Length;
    }

    /// <summary>PATINDEX: 1-based position where the pattern first matches, or 0.</summary>
    public int IndexIn(string s)
    {
        if (!StartsWithAnyMany) return IsMatch(s) ? 1 : 0;
        if (_tokens.Length == 1) return 1; // just '%'
        // the leading % absorbs s[..i]; the rest of the pattern must then match from position i on
        for (int i = 0; i < s.Length; i++)
            if (IsMatch(s.AsSpan(i), 1)) return i + 1;
        return 0;
    }
}

/// <summary>Composite hashing key over normalized values.</summary>
internal sealed class RowKey : IEquatable<RowKey>
{
    public readonly object[] Values;
    readonly int _hash;

    public RowKey(object[] normalized)
    {
        Values = normalized;
        var h = new HashCode();
        foreach (var v in normalized) h.Add(v);
        _hash = h.ToHashCode();
    }

    public bool HasNull
    {
        get
        {
            foreach (var v in Values) if (v is DBNull) return true;
            return false;
        }
    }

    public bool Equals(RowKey? other)
    {
        if (other is null || other._hash != _hash || other.Values.Length != Values.Length) return false;
        for (int i = 0; i < Values.Length; i++)
            if (!Values[i].Equals(other.Values[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RowKey);
    public override int GetHashCode() => _hash;
}
