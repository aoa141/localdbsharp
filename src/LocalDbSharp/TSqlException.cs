using System.Data.Common;

namespace LocalDbSharp;

/// <summary>
/// Error raised by the engine. Mirrors the shape of SqlException (Number / Class / State / LineNumber)
/// and uses the same error numbers as SQL Server for the common cases.
/// </summary>
public sealed class TSqlException : DbException
{
    public int Number { get; }
    public byte Class { get; }
    public byte State { get; }
    public int LineNumber { get; internal set; }
    public string? Procedure { get; internal set; }

    /// <summary>
    /// Columns of a SELECT that failed while producing rows. SQL Server has already sent the result set
    /// header by then, so a client still sees an (empty) result set before the error.
    /// </summary>
    internal IReadOnlyList<ResultColumn>? PartialColumns;

    /// <summary>Set for errors that always end the batch (THROW).</summary>
    internal bool ForceBatchAbort;

    /// <summary>Raised by RAISERROR: never aborts the batch or the transaction by itself, even under XACT_ABORT.</summary>
    internal bool FromRaiserror;

    /// <summary>Errors (data conversion failures) that end the batch and also roll back an open transaction.</summary>
    internal bool RollsBackTransaction => Number is 241 or 242 or 245 or 248 or 8114 or 8169;

    static readonly HashSet<int> BatchAborting =
    [
        137, 195, 206, 207, 208, 209, 217, 241, 242, 245, 248, 529, 911, 1087, 4104, 4121, 8114, 8117, 8169, 40000,
    ];

    /// <summary>
    /// True if the error ends the whole batch. Otherwise only the failing statement is terminated and
    /// execution continues with the next one, as SQL Server does when XACT_ABORT is OFF.
    /// </summary>
    internal bool AbortsBatch => ForceBatchAbort || Class == 15 || Class >= 20 || BatchAborting.Contains(Number);

    public TSqlException(int number, string message, byte severity = 16, byte state = 1)
        : base(message)
    {
        Number = number;
        Class = severity;
        State = state;
    }
}

internal static class Err
{
    public static TSqlException New(int number, string message, byte severity = 16, byte state = 1)
        => new(number, message, severity, state);

    public static TSqlException Syntax(string near, int line)
        => new(102, $"Incorrect syntax near '{near}'.", 15) { LineNumber = line };

    public static TSqlException NotSupported(string what)
        => new(40000, $"{what} is not supported by LocalDbSharp.");

    public static TSqlException InvalidObject(string name) => new(208, $"Invalid object name '{name}'.");
    public static TSqlException InvalidColumn(string name) => new(207, $"Invalid column name '{name}'.");
    public static TSqlException DivideByZero() => new(8134, "Divide by zero error encountered.");
    public static TSqlException Overflow(string type) => new(8115, $"Arithmetic overflow error converting expression to data type {type}.");
}
