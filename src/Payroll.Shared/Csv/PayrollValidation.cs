namespace Payroll.Shared.Csv;

/// <summary>
/// One actionable problem in an uploaded file.
///
/// <c>RowNumber</c> is 1-based and EXCLUDES the header row, so if the user opens the file
/// in Excel and counts down, our "row 12" is their row 13. This mismatch is a classic source
/// of support tickets; reporting data-row numbers keeps the dashboard honest.
/// </summary>
public sealed record ValidationError(
    string Code,
    string Message,
    string? Field = null,
    int? RowNumber = null)
{
    public string Describe()
        => RowNumber is null
            ? (Field is null ? $"{Code}: {Message}" : $"{Code} (field '{Field}'): {Message}")
            : (Field is null
                ? $"{Code} (row {RowNumber}): {Message}"
                : $"{Code} (row {RowNumber}, field '{Field}'): {Message}");

    public override string ToString() => Describe();
}

/// <summary>
/// Result of validating an uploaded file.
/// </summary>
public sealed record PayrollFileValidationOutcome(
    bool IsValid,
    string? HeaderSignature,
    int RowCount,
    IReadOnlyList<ValidationError> Errors,
    long BytesRead)
{
    /// <summary>Headline message shown when summarising a burst of row errors.</summary>
    public string Summary => Errors.Count switch
    {
        0 => "OK",
        1 => Errors[0].Describe(),
        _ => $"{Errors.Count} problems found. First: {Errors[0].Describe()}"
    };

    public static PayrollFileValidationOutcome Failure(params ValidationError[] errors)
        => new(false, null, 0, errors, 0);

    public static PayrollFileValidationOutcome Failure(ValidationError error)
        => new(false, null, 0, [error], 0);
}

/// <summary>
/// Two validation passes exist for a deliberate reason:
///
/// <list type="bullet">
/// <item><see cref="ValidationDepth.Structure"/> runs at upload time -- cheap, and it gives the user
/// an immediate, precise rejection rather than a file that lands in the queue and only later fails.</item>
/// <item><see cref="ValidationDepth.Full"/> runs inside the Function before any write -- the authority
/// on business correctness, and it produces the per-row errors shown on the dashboard.</item>
/// </list>
/// </summary>
public enum ValidationDepth
{
    Structure,
    Full
}