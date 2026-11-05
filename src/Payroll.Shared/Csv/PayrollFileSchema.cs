namespace Payroll.Shared.Csv;

/// <summary>
/// The CSV contract, declared once and shared by the uploader (API) and the processor (Function).
/// If these two ever disagree about the format, files are accepted and then fail downstream --
/// which is the most confusing failure mode in this whole system.
/// </summary>
public static class PayrollFileSchema
{
    public const string EmployeeId = nameof(EmployeeId);
    public const string EmployeeName = nameof(EmployeeName);
    public const string PayPeriodStart = nameof(PayPeriodStart);
    public const string PayPeriodEnd = nameof(PayPeriodEnd);
    public const string RegularHours = nameof(RegularHours);
    public const string OvertimeHours = nameof(OvertimeHours);
    public const string HourlyRate = nameof(HourlyRate);
    public const string Deductions = nameof(Deductions);
    public const string Currency = nameof(Currency);

    /// <summary>Order is the file's canonical order. Header matching is order-insensitive for robustness.</summary>
    public static readonly IReadOnlyList<string> RequiredColumns =
    [
        EmployeeId, EmployeeName, PayPeriodStart, PayPeriodEnd,
        RegularHours, OvertimeHours, HourlyRate, Deductions, Currency
    ];

    public const string FileExtension = ".csv";
    public const char Delimiter = ',';

    /// <summary>Multiplier applied to overtime hours. Documented, testable, changes only by explicit decision.</summary>
    public const decimal OvertimeMultiplier = 1.5m;

    // ---- Hard limits. Cheap DoS and quality guards at the edge. ----
    public const int MaxFileSizeBytes = 10 * 1024 * 1024;
    public const int MaxRows = 100_000;
    public const int MaxErrorsReportedPerFile = 100;

    // ---- Per-field bounds ----
    public const int MaxEmployeeIdLength = 20;
    public const int MaxEmployeeNameLength = 100;
    public const decimal MaxHoursPerPeriod = 744m;   // 31 days x 24h -- blunt but catches sign/keying errors
    public const decimal MaxHourlyRate = 10_000m;
    public const decimal MaxAmount = 100_000_000m;

    public static bool IsAllowedExtension(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && string.Equals(Path.GetExtension(fileName), FileExtension, StringComparison.OrdinalIgnoreCase);
}