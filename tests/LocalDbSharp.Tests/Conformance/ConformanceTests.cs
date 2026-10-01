namespace LocalDbSharp.Tests.Conformance;

/// <summary>
/// Replays every case under Conformance/Cases against LocalDbSharp and checks the output
/// (column names, types, values, or error number) against what a real SQL Server produced.
/// </summary>
public class ConformanceTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string file in ConformanceHarness.Files())
            foreach (var (name, _) in ConformanceHarness.ReadCases(file))
                yield return [file, name];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesSqlServer(string file, string name)
    {
        var expected = ConformanceHarness.GetExpected(file);
        var actual = ConformanceHarness.GetActual(file);
        Assert.True(expected.ContainsKey(name), $"No recorded SQL Server output for '{name}'. Re-record with LOCALDBSHARP_RECORD=1.");
        string? diff = ConformanceHarness.Diff(expected[name], actual[name]);
        if (diff != null && actual[name].Error != null) diff += "\nLocalDbSharp error: " + actual[name].ErrorMessage;
        Assert.True(diff == null, $"{file} / {name}: {diff}");
    }
}
