namespace LocalDbSharp;

/// <summary>SQL Server data type kinds, declared in data type precedence order (lowest first).</summary>
public enum SqlTypeKind
{
    Binary, VarBinary, Char, VarChar, NChar, NVarChar, UniqueIdentifier,
    Bit, TinyInt, SmallInt, Int, BigInt, SmallMoney, Money, Decimal, Real, Float,
    Time, Date, SmallDateTime, DateTime, DateTime2, DateTimeOffset, Variant,
}

/// <summary>A T-SQL data type: kind plus length (-1 = max), precision and scale.</summary>
public sealed record SqlType(SqlTypeKind Kind, int Length = 0, int Precision = 0, int Scale = 0)
{
    public const int Max = -1;

    public static readonly SqlType Bit = new(SqlTypeKind.Bit);
    public static readonly SqlType TinyInt = new(SqlTypeKind.TinyInt, Precision: 3);
    public static readonly SqlType SmallInt = new(SqlTypeKind.SmallInt, Precision: 5);
    public static readonly SqlType Int = new(SqlTypeKind.Int, Precision: 10);
    public static readonly SqlType BigInt = new(SqlTypeKind.BigInt, Precision: 19);
    public static readonly SqlType Money = new(SqlTypeKind.Money, Precision: 19, Scale: 4);
    public static readonly SqlType SmallMoney = new(SqlTypeKind.SmallMoney, Precision: 10, Scale: 4);
    public static readonly SqlType Real = new(SqlTypeKind.Real);
    public static readonly SqlType Float = new(SqlTypeKind.Float);
    public static readonly SqlType Date = new(SqlTypeKind.Date);
    public static readonly SqlType Time = new(SqlTypeKind.Time, Scale: 7);
    public static readonly SqlType DateTime = new(SqlTypeKind.DateTime);
    public static readonly SqlType SmallDateTime = new(SqlTypeKind.SmallDateTime);
    public static readonly SqlType DateTime2 = new(SqlTypeKind.DateTime2, Scale: 7);
    public static readonly SqlType DateTimeOffset = new(SqlTypeKind.DateTimeOffset, Scale: 7);
    public static readonly SqlType UniqueIdentifier = new(SqlTypeKind.UniqueIdentifier);
    public static readonly SqlType Variant = new(SqlTypeKind.Variant);
    public static readonly SqlType NVarCharMax = new(SqlTypeKind.NVarChar, Max);
    public static readonly SqlType VarCharMax = new(SqlTypeKind.VarChar, Max);
    public static readonly SqlType VarBinaryMax = new(SqlTypeKind.VarBinary, Max);

    public static SqlType VarChar(int length) => new(SqlTypeKind.VarChar, length);
    public static SqlType NVarChar(int length) => new(SqlTypeKind.NVarChar, length);
    public static SqlType Decimal(int precision, int scale) => new(SqlTypeKind.Decimal, 0, precision, scale);

    public bool IsString => Kind is SqlTypeKind.Char or SqlTypeKind.VarChar or SqlTypeKind.NChar or SqlTypeKind.NVarChar;
    public bool IsUnicode => Kind is SqlTypeKind.NChar or SqlTypeKind.NVarChar;
    public bool IsBinary => Kind is SqlTypeKind.Binary or SqlTypeKind.VarBinary;
    public bool IsInteger => Kind is SqlTypeKind.TinyInt or SqlTypeKind.SmallInt or SqlTypeKind.Int or SqlTypeKind.BigInt;
    public bool IsDecimalLike => Kind is SqlTypeKind.Decimal or SqlTypeKind.Money or SqlTypeKind.SmallMoney;
    public bool IsApproximate => Kind is SqlTypeKind.Real or SqlTypeKind.Float;
    public bool IsNumeric => IsInteger || IsDecimalLike || IsApproximate || Kind == SqlTypeKind.Bit;
    public bool IsDateTime => Kind is SqlTypeKind.Date or SqlTypeKind.SmallDateTime or SqlTypeKind.DateTime
        or SqlTypeKind.DateTime2 or SqlTypeKind.DateTimeOffset or SqlTypeKind.Time;

    /// <summary>The CLR type used to represent values of this type at runtime.</summary>
    public Type ClrType => Kind switch
    {
        SqlTypeKind.Bit => typeof(bool),
        SqlTypeKind.TinyInt => typeof(byte),
        SqlTypeKind.SmallInt => typeof(short),
        SqlTypeKind.Int => typeof(int),
        SqlTypeKind.BigInt => typeof(long),
        SqlTypeKind.Decimal or SqlTypeKind.Money or SqlTypeKind.SmallMoney => typeof(decimal),
        SqlTypeKind.Real => typeof(float),
        SqlTypeKind.Float => typeof(double),
        SqlTypeKind.Char or SqlTypeKind.VarChar or SqlTypeKind.NChar or SqlTypeKind.NVarChar => typeof(string),
        SqlTypeKind.Binary or SqlTypeKind.VarBinary => typeof(byte[]),
        SqlTypeKind.UniqueIdentifier => typeof(Guid),
        SqlTypeKind.Time => typeof(TimeSpan),
        SqlTypeKind.DateTimeOffset => typeof(DateTimeOffset),
        SqlTypeKind.Date or SqlTypeKind.SmallDateTime or SqlTypeKind.DateTime or SqlTypeKind.DateTime2 => typeof(System.DateTime),
        _ => typeof(object),
    };

    /// <summary>The SQL Server type name without length / precision (e.g. "nvarchar").</summary>
    public string Name => Kind switch
    {
        SqlTypeKind.Variant => "sql_variant",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    public override string ToString() => Kind switch
    {
        SqlTypeKind.Char or SqlTypeKind.VarChar or SqlTypeKind.NChar or SqlTypeKind.NVarChar
            or SqlTypeKind.Binary or SqlTypeKind.VarBinary => $"{Name}({(Length == Max ? "max" : Length.ToString())})",
        SqlTypeKind.Decimal => $"decimal({Precision},{Scale})",
        SqlTypeKind.DateTime2 or SqlTypeKind.Time or SqlTypeKind.DateTimeOffset => $"{Name}({Scale})",
        _ => Name,
    };

    /// <summary>Builds a type from its T-SQL name and optional arguments. Returns null for unknown names.</summary>
    public static SqlType? FromName(string name, int? arg1 = null, int? arg2 = null, bool isMax = false, int defaultLength = 1)
    {
        int len = isMax ? Max : arg1 ?? defaultLength;
        switch (name.ToLowerInvariant())
        {
            case "bit": return Bit;
            case "tinyint": return TinyInt;
            case "smallint": return SmallInt;
            case "int": case "integer": return Int;
            case "bigint": return BigInt;
            case "decimal": case "numeric": case "dec": return Decimal(arg1 ?? 18, arg2 ?? 0);
            case "money": return Money;
            case "smallmoney": return SmallMoney;
            case "float": case "double": return arg1 is <= 24 ? Real : Float;
            case "real": return Real;
            case "char": case "character": return new SqlType(SqlTypeKind.Char, len);
            case "varchar": return new SqlType(SqlTypeKind.VarChar, len);
            case "nchar": return new SqlType(SqlTypeKind.NChar, len);
            case "nvarchar": return new SqlType(SqlTypeKind.NVarChar, len);
            case "sysname": return NVarChar(128);
            case "text": return VarCharMax;
            case "ntext": case "xml": return NVarCharMax;
            case "binary": return new SqlType(SqlTypeKind.Binary, len);
            case "varbinary": return new SqlType(SqlTypeKind.VarBinary, len);
            case "image": return VarBinaryMax;
            case "timestamp": case "rowversion": return new SqlType(SqlTypeKind.Binary, 8);
            case "uniqueidentifier": return UniqueIdentifier;
            case "date": return Date;
            case "time": return new SqlType(SqlTypeKind.Time, Scale: arg1 ?? 7);
            case "datetime": return DateTime;
            case "smalldatetime": return SmallDateTime;
            case "datetime2": return new SqlType(SqlTypeKind.DateTime2, Scale: arg1 ?? 7);
            case "datetimeoffset": return new SqlType(SqlTypeKind.DateTimeOffset, Scale: arg1 ?? 7);
            case "sql_variant": return Variant;
            default: return null;
        }
    }
}
