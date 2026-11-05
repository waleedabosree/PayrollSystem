using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace Payroll.Shared.Csv;

public sealed record ParsedPayrollRow(
    int SourceRowNumber,
    string EmployeeId,
    string EmployeeName,
    DateOnly PayPeriodStart,
    DateOnly PayPeriodEnd,
    decimal RegularHours,
    decimal OvertimeHours,
    decimal HourlyRate,
    decimal Deductions,
    string Currency)
{
    /// <summary>The single documented pay rule, shared so the Function and tests cannot diverge.</summary>
    public decimal GrossPay =>
        (RegularHours * HourlyRate) + (OvertimeHours * HourlyRate * PayrollFileSchema.OvertimeMultiplier);

    public decimal NetPay => GrossPay - Deductions;
}

public interface IPayrollFileParser
{
    /// <summary>
    /// Parses a file that has ALREADY passed <see cref="ValidationDepth.Full"/> validation.
    /// Callers must validate first; this method assumes every field is well-formed.
    /// </summary>
    IReadOnlyList<ParsedPayrollRow> Parse(Stream content);
}

public sealed class PayrollFileParser : IPayrollFileParser
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy"];

    public IReadOnlyList<ParsedPayrollRow> Parse(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, true, 8192, leaveOpen: true);
        using var parser = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = PayrollFileSchema.Delimiter.ToString(),
            HasHeaderRecord = false,
            MissingFieldFound = null,
            BadDataFound = null
        });

        if (!parser.Read() || parser.Record is null)
        {
            return [];
        }

        var header = parser.Record.Select(h => h.Trim().TrimStart('\uFEFF')).ToArray();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            index.TryAdd(header[i], i);
        }

        var rows = new List<ParsedPayrollRow>();
        while (parser.Read())
        {
            var r = parser.Record!;
            string F(string name) => r[index[name]].Trim();

            rows.Add(new ParsedPayrollRow(
                parser.Row - 1,
                F(PayrollFileSchema.EmployeeId),
                F(PayrollFileSchema.EmployeeName),
                ParseDate(F(PayrollFileSchema.PayPeriodStart)),
                ParseDate(F(PayrollFileSchema.PayPeriodEnd)),
                ParseDecimal(F(PayrollFileSchema.RegularHours)),
                ParseDecimal(F(PayrollFileSchema.OvertimeHours)),
                ParseDecimal(F(PayrollFileSchema.HourlyRate)),
                ParseDecimal(F(PayrollFileSchema.Deductions)),
                F(PayrollFileSchema.Currency)));
        }

        return rows;
    }

    private static DateOnly ParseDate(string raw) =>
        DateOnly.ParseExact(raw, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None);

    // InvariantCulture for the same reason as the validator: "1,500" must never become 1.5.
    private static decimal ParseDecimal(string raw) =>
        decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture);
}
