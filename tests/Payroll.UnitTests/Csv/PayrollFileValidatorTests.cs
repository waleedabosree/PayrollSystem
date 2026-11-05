using System.Text;
using Payroll.Shared.Csv;

namespace Payroll.UnitTests.Csv;

/// <summary>
/// Executable specification of the CSV contract.
///
/// The validator is shared by the uploading API and the processing Function, so a rule that
/// changes behaviour here changes both halves of the system -- which is exactly why it is
/// worth pinning before either half is wired up.
/// </summary>
public class PayrollFileValidatorTests
{
    private const string ValidHeader =
        "EmployeeId,EmployeeName,PayPeriodStart,PayPeriodEnd,RegularHours,OvertimeHours,HourlyRate,Deductions,Currency";

    // Gross = (160 * 50) + (10 * 50 * 1.5) = 8750.  Net = 8750 - 100 = 8650
    private const string ValidRow =
        "E-001,Jane Doe,2026-01-01,2026-01-31,160,10,50.00,100.00,USD";

    private static Stream ToStream(string content)
        => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static PayrollFileValidationOutcome Run(string csv, ValidationDepth depth = ValidationDepth.Full)
    {
        var stream = ToStream(csv);
        return new PayrollFileValidator().Validate(stream, "payroll.csv", depth);
    }

    private static bool HasCode(PayrollFileValidationOutcome outcome, string code)
        => outcome.Errors.Any(e => e.Code == code);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public void ValidFile_PassesWithCorrectRowCount()
    {
        var result = Run($"{ValidHeader}\n{ValidRow}\n");

        Assert.True(result.IsValid, result.Summary);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public void ValidFile_ReportsRowNumbersExcludingHeader()
    {
        var csv = $"""
            {ValidHeader}
            {ValidRow}
            {ValidRow.Replace("E-001", "E-002")}
            """;

        var result = Run(csv);

        Assert.True(result.IsValid, result.Summary);
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public void ColumnOrder_IsQueryByNameNotPosition()
    {
        // Shuffle the columns: header matching must be positional-independent, or every
        // spreadsheet someone re-sorts becomes a "wrong data" incident.
        var shuffled = string.Join(",",
            "Currency", "HourlyRate", "EmployeeName", "Deductions",
            "EmployeeId", "RegularHours", "PayPeriodEnd", "OvertimeHours", "PayPeriodStart");

        var row = "USD,50.00,Jane Doe,100.00,E-001,160,2026-01-31,10,2026-01-01";
        var result = Run($"{shuffled}\n{row}\n");

        Assert.True(result.IsValid, result.Summary);
    }

    [Fact]
    public void ExtraColumns_AreTolerated()
    {
        var header = ValidHeader + ",CostCentre,Notes";
        var row = ValidRow + ",CC-10,n/a";

        var result = Run($"{header}\n{row}\n");

        Assert.True(result.IsValid, result.Summary);
    }

    [Fact]
    public void QuotedFieldsContainingCommas_ParseCorrectly()
    {
        var row = "E-001,\"Doe, Jane\",2026-01-01,2026-01-31,160,10,50.00,100.00,USD";

        var result = Run($"{ValidHeader}\n{row}\n");

        Assert.True(result.IsValid, result.Summary);
    }

    // ---- Structural rejection (cheap gate) ------------------------------------

    [Fact]
    public void WrongExtension_IsRejectedBeforeReading()
    {
        var stream = ToStream(ValidHeader);
        var result = new PayrollFileValidator().Validate(stream, "payroll.xlsx", ValidationDepth.Structure);

        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "CSV_INVALID_EXTENSION"));
    }

    [Fact]
    public void EmptyFile_IsRejected()
    {
        var result = Run(string.Empty);

        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "CSV_EMPTY"));
    }

    [Fact]
    public void HeaderOnlyFile_IsRejectedForHavingNoRows()
    {
        var result = Run(ValidHeader);

        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "CSV_NO_DATA_ROWS"));
    }

    [Fact]
    public void MissingRequiredColumn_IsRejectedWithActionableText()
    {
        var result = Run("EmployeeId,EmployeeName\nE-001,Jane\n");

        Assert.False(result.IsValid);

        // Seven required columns absent means seven HEADER_MISMATCH errors, not one --
        // the intent is that the operator sees the full gap rather than fixing them
        // one at a time against repeated round-trips.
        var errors = result.Errors.Where(e => e.Code == "CSV_HEADER_MISMATCH").ToList();
        Assert.Equal(7, errors.Count);

        // Each message must name its column AND enumerate the contract, otherwise the
        // operator is left guessing at a format we ourselves defined.
        Assert.Contains(errors, e => e.Message.Contains("Currency"));
        Assert.Contains(errors, e => e.Message.Contains("RegularHours"));
    }

    [Fact]
    public void DuplicateHeaderColumn_IsRejected()
    {
        var result = Run($"{ValidHeader},Currency\n{ValidRow},EUR\n");

        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "CSV_DUPLICATE_COLUMN"));
    }

    [Fact]
    public void OversizedFile_IsRejectedByByteLimit()
    {
        var stream = new MemoryStream();
        stream.SetLength(PayrollFileSchema.MaxFileSizeBytes + 1);

        var result = new PayrollFileValidator().Validate(stream, "payroll.csv", ValidationDepth.Structure);

        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "CSV_TOO_LARGE"));
    }

    [Fact]
    public void RaggedRow_IsReportedWithItsRowNumber()
    {
        var csv = $"{ValidHeader}\n{ValidRow},EXTRA_FIELD\n";
        var result = Run(csv);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Code == "CSV_FIELD_COUNT_MISMATCH");
        // Data-row numbering excludes the header: this is file row 1 for whoever opens it
        // in a spreadsheet and counts down.
        Assert.Equal(1, error.RowNumber);
    }

    // ---- Business rules ---------------------------------------------------------

    [Theory]
    [InlineData("E-001,Jane Doe,not-a-date,2026-01-31,160,10,50.00,100.00,USD", "ROW_INVALID_DATE")]
    [InlineData("E-001,Jane Doe,2026-02-01,2026-01-31,160,10,50.00,100.00,USD", "ROW_DATE_RANGE")]
    [InlineData("E-001,Jane Doe,2026-01-01,2026-01-31,abc,10,50.00,100.00,USD", "ROW_INVALID_NUMBER")]
    [InlineData("E-001,Jane Doe,2026-01-01,2026-01-31,9999,10,50.00,100.00,USD", "ROW_OUT_OF_RANGE")]
    [InlineData("E-001,Jane Doe,2026-01-01,2026-01-31,160,10,50.00,100.00,usd", "ROW_INVALID_CURRENCY")]
    [InlineData("E-001,Jane Doe,2026-01-01,2026-01-31,160,10,50.00,999999.00,USD", "ROW_NEGATIVE_NET_PAY")]
    [InlineData(",Jane Doe,2026-01-01,2026-01-31,160,10,50.00,100.00,USD", "ROW_REQUIRED_FIELD")]
    public void InvalidRow_YieldsTheExpectedErrorCode(string row, string expectedCode)
    {
        var result = Run($"{ValidHeader}\n{row}\n");

        Assert.False(result.IsValid);
        Assert.True(HasCode(result, expectedCode), $"Expected {expectedCode}. Got: {result.Summary}");
    }

    [Fact]
    public void DuplicateEmployeeWithinFile_IsRejected()
    {
        var csv = $"""
            {ValidHeader}
            {ValidRow}
            {ValidRow}
            """;

        var result = Run(csv);

        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "ROW_DUPLICATE_EMPLOYEE"));
    }

    [Fact]
    public void DuplicateEmployee_IsCaughtAcrossTheWholeFileNotJustConsecutiveRows()
    {
        var second = ValidRow.Replace("E-001", "E-002");
        var csv = $"""
            {ValidHeader}
            {ValidRow}
            {second}
            {ValidRow}
            """;

        var result = Run(csv);

        Assert.True(HasCode(result, "ROW_DUPLICATE_EMPLOYEE"));
    }

    [Fact]
    public void CommaAsThousandsSeparator_ParsesInvariantly()
    {
        // If this ever parsed under a comma-decimal culture, every figure would be scaled
        // by 1000x -- the worst silent bug a payroll system can ship with.
        var row = "E-001,Jane Doe,2026-01-01,2026-01-31,160,10,\"1,500.00\",100.00,USD";

        var result = Run($"{ValidHeader}\n{row}\n");

        Assert.True(result.IsValid, result.Summary);
    }

    // ---- Depth separation ---------------------------------------------------------

    [Fact]
    public void StructureDepth_SkipsBusinessRulesSoUploadRejectsCheaply()
    {
        // Negative net pay is fine at the structure gate (it is a *business* problem that the
        // Function owns); the file is otherwise well-formed, so it should pass upload.
        var row = "E-001,Jane Doe,2026-01-01,2026-01-31,160,10,50.00,999999.00,USD";

        var structure = Run($"{ValidHeader}\n{row}\n", ValidationDepth.Structure);
        var full = Run($"{ValidHeader}\n{row}\n", ValidationDepth.Full);

        Assert.True(structure.IsValid, structure.Summary);
        Assert.False(full.IsValid);
        Assert.True(HasCode(full, "ROW_NEGATIVE_NET_PAY"));
    }

    [Fact]
    public void StructureDepth_StillRejectsRaggedRows()
    {
        var structure = Run($"{ValidHeader}\n1,2,3\n", ValidationDepth.Structure);

        Assert.False(structure.IsValid);
        Assert.True(HasCode(structure, "CSV_FIELD_COUNT_MISMATCH"));
    }

    [Fact]
    public void HeaderSignature_IsExposedForDiagnostics()
    {
        var result = Run($"{ValidHeader}\n{ValidRow}\n");

        Assert.Contains("EmployeeId", result.HeaderSignature);
        Assert.Contains("Currency", result.HeaderSignature);
    }
}