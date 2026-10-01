using System.Globalization;
using System.Text;
using LocalDbSharp;

// LocalDbSharp command line: a tiny sqlcmd-like shell over the in-memory engine.
//   localdbsharp                 interactive (end each batch with GO)
//   localdbsharp -Q "SELECT 1"   run a query and exit
//   localdbsharp -i script.sql   run a script file and exit (may be repeated)

var engine = new LocalDbEngine();
using var session = engine.CreateSession();
session.InfoMessage += Console.WriteLine;

int exitCode = 0;
bool ranSomething = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-Q" or "-q" when i + 1 < args.Length:
            exitCode |= Run(args[++i]);
            ranSomething = true;
            break;
        case "-i" when i + 1 < args.Length:
            exitCode |= Run(File.ReadAllText(args[++i]));
            ranSomething = true;
            break;
        case "-?" or "-h" or "--help":
            Console.WriteLine("usage: localdbsharp [-Q \"query\"] [-i script.sql]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
            return 2;
    }
}
if (ranSomething) return exitCode;

Console.WriteLine("LocalDbSharp interactive shell. Type GO to execute a batch, EXIT to quit.");
var buffer = new StringBuilder();
int lineNo = 1;
while (true)
{
    Console.Write($"{lineNo}> ");
    string? line = Console.ReadLine();
    if (line == null) break;
    string trimmed = line.Trim();
    if (buffer.Length == 0 && (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase))) break;
    if (trimmed.Equals("go", StringComparison.OrdinalIgnoreCase))
    {
        Run(buffer.ToString());
        buffer.Clear();
        lineNo = 1;
        continue;
    }
    buffer.AppendLine(line);
    lineNo++;
}
return 0;

int Run(string sql)
{
    try
    {
        var result = session.Execute(sql);
        foreach (var rs in result.ResultSets) Print(rs);
        if (result.RecordsAffected >= 0) Console.WriteLine($"({result.RecordsAffected} rows affected)");
        return 0;
    }
    catch (TSqlException ex)
    {
        Console.Error.WriteLine($"Msg {ex.Number}, Level {ex.Class}, State {ex.State}, Line {ex.LineNumber}");
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static string Format(object? v, SqlType type) => v switch
{
    null => "NULL",
    bool b => b ? "1" : "0",
    byte[] bytes => "0x" + Convert.ToHexString(bytes),
    DateTime d => type.Kind switch
    {
        SqlTypeKind.Date => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        SqlTypeKind.DateTime2 => d.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        SqlTypeKind.SmallDateTime => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
    },
    Guid g => g.ToString().ToUpperInvariant(),
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
    _ => v.ToString() ?? "",
};

static void Print(ResultSet rs)
{
    int n = rs.Columns.Count;
    var cells = new List<string[]>(rs.Rows.Count);
    var widths = new int[n];
    for (int j = 0; j < n; j++) widths[j] = Math.Max(rs.Columns[j].Name.Length, 1);
    foreach (var row in rs.Rows)
    {
        var line = new string[n];
        for (int j = 0; j < n; j++)
        {
            line[j] = Format(row[j], rs.Columns[j].Type);
            widths[j] = Math.Min(Math.Max(widths[j], line[j].Length), 60);
        }
        cells.Add(line);
    }
    Console.WriteLine(string.Join(" ", rs.Columns.Select((c, j) => c.Name.PadRight(widths[j]))));
    Console.WriteLine(string.Join(" ", widths.Select(w => new string('-', w))));
    foreach (var line in cells)
    {
        Console.WriteLine(string.Join(" ", line.Select((text, j) =>
        {
            bool numeric = rs.Columns[j].Type.IsNumeric && rs.Columns[j].Type.Kind != SqlTypeKind.Bit;
            return numeric ? text.PadLeft(widths[j]) : text.PadRight(widths[j]);
        })));
    }
    Console.WriteLine();
    Console.WriteLine($"({rs.Rows.Count} rows)");
    Console.WriteLine();
}
