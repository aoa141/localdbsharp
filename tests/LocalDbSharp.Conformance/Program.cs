using LocalDbSharp.Tests.Conformance;

// Stand-alone runner for the SQL Server conformance cases (the same ones the xUnit project runs).
//   dotnet run --project tests/LocalDbSharp.Conformance [filter] [-v]
// 'filter' restricts the run to files or cases whose name contains the text.
//   dotnet run --project tests/LocalDbSharp.Conformance fuzz [count] [seed] [-v]
//   dotnet run --project tests/LocalDbSharp.Conformance fuzzdml [count] [seed] [-v]
// 'fuzz' generates random queries and 'fuzzdml' random data modifications; both compare the two engines
// live and need the reference SQL Server.
// Expected output is (re)recorded from a real SQL Server when a recording is missing or stale;
// set LOCALDBSHARP_RECORD=1 to force it and LOCALDBSHARP_ORACLE to point at a specific server.

bool verbose = args.Contains("-v");
if (args.Length > 0 && args[0] == "fuzz")
{
    // dotnet run --project tests/LocalDbSharp.Conformance -- fuzz [count] [seed] [-v]
    var numbers = args.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).ToList();
    return Fuzzer.Run(numbers.Count > 0 ? numbers[0] : 500, numbers.Count > 1 ? numbers[1] : Environment.TickCount, verbose);
}
if (args.Length > 0 && args[0] == "fuzzdml")
{
    // dotnet run --project tests/LocalDbSharp.Conformance -- fuzzdml [count] [seed] [-v]
    var numbers = args.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).ToList();
    return DmlFuzzer.Run(numbers.Count > 0 ? numbers[0] : 500, numbers.Count > 1 ? numbers[1] : Environment.TickCount, verbose);
}
string? filter = args.FirstOrDefault(a => !a.StartsWith('-'));
int passed = 0, failed = 0;

foreach (string file in ConformanceHarness.Files())
{
    var cases = ConformanceHarness.ReadCases(file);
    bool fileMatches = filter == null || file.Contains(filter, StringComparison.OrdinalIgnoreCase);
    if (!fileMatches && !cases.Any(c => c.Name.Contains(filter!, StringComparison.OrdinalIgnoreCase))) continue;

    Dictionary<string, CaseResult> expected, actual;
    try
    {
        expected = ConformanceHarness.GetExpected(file);
        actual = ConformanceHarness.GetActual(file);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {file}: {ex.GetType().Name}: {ex.Message}");
        if (verbose) Console.WriteLine(ex);
        failed++;
        continue;
    }

    foreach (var (name, _) in cases)
    {
        if (!fileMatches && !name.Contains(filter!, StringComparison.OrdinalIgnoreCase)) continue;
        string? diff = ConformanceHarness.Diff(expected[name], actual[name]);
        if (diff == null)
        {
            passed++;
            if (verbose) Console.WriteLine($"ok   {file} / {name}");
            continue;
        }
        failed++;
        Console.WriteLine($"FAIL {file} / {name}: {diff}");
        if (actual[name].Error != null) Console.WriteLine("     LocalDbSharp error: " + actual[name].ErrorMessage);
    }
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
