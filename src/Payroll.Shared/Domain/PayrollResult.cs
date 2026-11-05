namespace Payroll.Shared.Domain;

/// <summary>
/// One employee line from a processed payroll file.
///
/// NOTE: all values are synthetic. No real salaries, national IDs, or personal data
/// are permitted anywhere in this table (see README, business scenario).
///
/// The unique index on (BatchId, EmployeeId) is what makes duplicate message delivery
/// detectable rather than silently corrupting -- see ARCHITECTURE.md, failure F4.
/// </summary>
public class PayrollResult
{
    public long Id { get; set; }

    public Guid BatchId { get; set; }
    public PayrollBatch? Batch { get; set; }

    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;

    public DateOnly PayPeriodStart { get; set; }
    public DateOnly PayPeriodEnd { get; set; }

    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal Deductions { get; set; }

    /// <summary>
    /// Computed by a single documented rule so the Function and the tests agree:
    ///   GrossPay = (RegularHours * HourlyRate) + (OvertimeHours * HourlyRate * OvertimeMultiplier)
    /// </summary>
    public decimal GrossPay { get; set; }

    /// <summary>GrossPay - Deductions. Business rule: never negative, enforced at parse time.</summary>
    public decimal NetPay { get; set; }

    /// <summary>ISO 4217, three uppercase letters.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Source row number in the uploaded file (1-based, excluding header). Essential for error reporting.</summary>
    public int SourceRowNumber { get; set; }

    public DateTimeOffset ProcessedAtUtc { get; set; }
}