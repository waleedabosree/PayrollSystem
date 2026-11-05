using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace Payroll.Shared.Csv;

public interface IPayrollFileValidator
{
    PayrollFileValidationOutcome Validate(Stream content, string? fileName, ValidationDepth depth);
}

/// <summary>
/// Validates an uploaded payroll CSV.
///
/// Deliberately non-throwing: every failure mode returns a structured
/// <see cref="ValidationError"/> so the API can reject with a consistent envelope and the
/// dashboard can render something specific. Turning a parser exception into a generic 500
/// is exactly what we are avoiding.
/// </summary>
public sealed class PayrollFileValidator : IPayrollFileValidator
{
    public PayrollFileValidationOutcome Validate(Stream content, string? fileName, ValidationDepth depth)
    {
        // ---- Stage 1: cheap gate before reading a single byte ----
        if (!string.IsNullOrWhiteSpace(fileName) && !PayrollFileSchema.IsAllowedExtension(fileName))
        {
            return PayrollFileValidationOutcome.Failure(new ValidationError(
                "CSV_INVALID_EXTENSION",
                $"Only '{PayrollFileSchema.FileExtension}' files are accepted. Received '{Path.GetExtension(fileName)}'.",
                Field: "file"));
        }

        if (content.CanSeek && content.Length > PayrollFileSchema.MaxFileSizeBytes)
        {
            return PayrollFileValidationOutcome.Failure(new ValidationError(
                "CSV_TOO_LARGE",
                $"File is {content.Length:N0} bytes; the limit is {PayrollFileSchema.MaxFileSizeBytes:N0} bytes.",
                Field: "file"));
        }

        // ---- Stage 2: structure ----
        using var reader = new StreamReader(
            content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192, leaveOpen: true);

        var sink = new ErrorSink(PayrollFileSchema.MaxErrorsReportedPerFile);
        string? headerSignature;
        int rowCount;

        // Closed over by BadDataFound so a malformed record can name the row it broke on,
        // regardless of whether CsvHelper hands us its own parsing context.
        var currentDataRow = 0;

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = PayrollFileSchema.Delimiter.ToString(),
            // We read the header ourselves as the first record rather than using CsvHelper's
            // header machinery. Owning header handling means we control the exact error text,
            // can treat header presence/order deliberately, and never hit framework-only
            // exceptions on the most common real-world failure: the wrong file entirely.
            HasHeaderRecord = false,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = _ => sink.Add(new ValidationError(
                "CSV_MALFORMED_ROW",
                "Unbalanced quotes or malformed quoting near row " +
                (currentDataRow > 0 ? currentDataRow.ToString() : "unknown") + ".",
                RowNumber: currentDataRow > 0 ? currentDataRow : null))
        };

        using var parser = new CsvParser(reader, configuration);

        if (!parser.Read() || parser.Record is null || parser.Record.Length == 0)
        {
            return PayrollFileValidationOutcome.Failure(new ValidationError(
                "CSV_EMPTY", "The file contains no data.", Field: "file"));
        }

        var header = parser.Record.Select(h => h.Trim().TrimStart('\uFEFF')).ToArray();
        headerSignature = string.Join(" | ", header);

        // Duplicates are fatal because an indexer silently resolves to one of them.
        foreach (var dup in header.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            sink.Add(new ValidationError("CSV_DUPLICATE_COLUMN",
                $"Column '{dup.Key}' appears more than once.", Field: dup.Key));
        }

        // Missing columns are fatal. Extra columns are tolerated so the contract can grow
        // without invalidating historical files.
        var headerSet = new HashSet<string>(header, StringComparer.OrdinalIgnoreCase);
        foreach (var required in PayrollFileSchema.RequiredColumns)
        {
            if (!headerSet.Contains(required))
            {
                sink.Add(new ValidationError("CSV_HEADER_MISMATCH",
                    $"Required column '{required}' is missing. Expected: {string.Join(", ", PayrollFileSchema.RequiredColumns)}.",
                    Field: required));
            }
        }

        if (sink.HasErrors)
        {
            return new PayrollFileValidationOutcome(false, headerSignature, 0, sink.SortedSnapshot(), BytesRead(content));
        }

        var indexes = BuildColumnMap(header);

        // ---- Stage 3: rows ----
        var seenEmployees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var full = depth == ValidationDepth.Full;
        var overflowed = false;

        while (parser.Read())
        {
            var record = parser.Record;
            if (record is null) continue;

            // CsvParser.Row is 1-based including the header, so data row N sits at Row == N + 1.
            currentDataRow = parser.Row - 1;
            rowCount = currentDataRow;

            if (record.Length != header.Length)
            {
                sink.Add(new ValidationError("CSV_FIELD_COUNT_MISMATCH",
                    $"Expected {header.Length} fields but found {record.Length}.",
                    RowNumber: currentDataRow));
                continue;
            }

            if (currentDataRow > PayrollFileSchema.MaxRows)
            {
                overflowed = true;
            }

            if (full && currentDataRow <= PayrollFileSchema.MaxRows)
            {
                ValidateRow(record, indexes, currentDataRow, seenEmployees, sink);
            }
        }

        rowCount = currentDataRow;

        if (overflowed)
        {
            return new PayrollFileValidationOutcome(false, headerSignature, currentDataRow,
                [.. sink.SortedSnapshot(), new ValidationError("CSV_TOO_MANY_ROWS",
                    $"File exceeds {PayrollFileSchema.MaxRows:N0} rows. Split it into multiple files.", Field: "file")],
                BytesRead(content));
        }

        if (rowCount == 0)
        {
            sink.Add(new ValidationError("CSV_NO_DATA_ROWS", "The file has a header but no data rows.", Field: "file"));
        }

        var errors = sink.SortedSnapshot();
        return new PayrollFileValidationOutcome(errors.Count == 0 && rowCount > 0, headerSignature, rowCount, errors, BytesRead(content));
    }

    // ---------------------------------------------------------------------------------

    private static Dictionary<string, int> BuildColumnMap(string[] header)
    {
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            if (!indexes.ContainsKey(header[i])) indexes[header[i]] = i;
        }
        return indexes;
    }

    private static void ValidateRow(
        string[] record,
        Dictionary<string, int> indexes,
        int dataRow,
        HashSet<string> seenEmployees,
        ErrorSink sink)
    {
        var employeeId = GetField(record, indexes, PayrollFileSchema.EmployeeId).Trim();

        if (string.IsNullOrEmpty(employeeId))
        {
            sink.Add(new ValidationError("ROW_REQUIRED_FIELD", "EmployeeId is required.",
                PayrollFileSchema.EmployeeId, dataRow));
        }
        else if (employeeId.Length > PayrollFileSchema.MaxEmployeeIdLength)
        {
            sink.Add(new ValidationError("ROW_FIELD_TOO_LONG",
                $"EmployeeId must be at most {PayrollFileSchema.MaxEmployeeIdLength} characters.",
                PayrollFileSchema.EmployeeId, dataRow));
        }
        else if (!seenEmployees.Add(employeeId))
        {
            // Duplicate employee in one file almost always means an accidental double-concatenate.
            sink.Add(new ValidationError("ROW_DUPLICATE_EMPLOYEE",
                $"EmployeeId '{employeeId}' appears more than once in this file.",
                PayrollFileSchema.EmployeeId, dataRow));
        }

        var employeeName = GetField(record, indexes, PayrollFileSchema.EmployeeName).Trim();
        if (string.IsNullOrEmpty(employeeName))
        {
            sink.Add(new ValidationError("ROW_REQUIRED_FIELD", "EmployeeName is required.",
                PayrollFileSchema.EmployeeName, dataRow));
        }
        else if (employeeName.Length > PayrollFileSchema.MaxEmployeeNameLength)
        {
            sink.Add(new ValidationError("ROW_FIELD_TOO_LONG",
                $"EmployeeName must be at most {PayrollFileSchema.MaxEmployeeNameLength} characters.",
                PayrollFileSchema.EmployeeName, dataRow));
        }

        var start = ParseDate(record, indexes, PayrollFileSchema.PayPeriodStart, dataRow, sink);
        var end = ParseDate(record, indexes, PayrollFileSchema.PayPeriodEnd, dataRow, sink);

        if (start is not null && end is not null && end < start)
        {
            sink.Add(new ValidationError("ROW_DATE_RANGE",
                "PayPeriodEnd must be on or after PayPeriodStart.",
                PayrollFileSchema.PayPeriodEnd, dataRow));
        }

        var regular = ParseDecimal(record, indexes, PayrollFileSchema.RegularHours, dataRow, sink);
        var overtime = ParseDecimal(record, indexes, PayrollFileSchema.OvertimeHours, dataRow, sink);
        var rate = ParseDecimal(record, indexes, PayrollFileSchema.HourlyRate, dataRow, sink);
        var deductions = ParseDecimal(record, indexes, PayrollFileSchema.Deductions, dataRow, sink);

        CheckRange(sink, PayrollFileSchema.RegularHours, dataRow, regular, 0m, PayrollFileSchema.MaxHoursPerPeriod, isInclusive: true);
        CheckRange(sink, PayrollFileSchema.OvertimeHours, dataRow, overtime, 0m, PayrollFileSchema.MaxHoursPerPeriod, isInclusive: true);
        CheckRange(sink, PayrollFileSchema.HourlyRate, dataRow, rate, 0m, PayrollFileSchema.MaxHourlyRate, isInclusive: false, allowZeroExclusive: true);
        CheckRange(sink, PayrollFileSchema.Deductions, dataRow, deductions, 0m, PayrollFileSchema.MaxAmount, isInclusive: true);

        var currency = GetField(record, indexes, PayrollFileSchema.Currency).Trim();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            sink.Add(new ValidationError("ROW_INVALID_CURRENCY",
                "Currency must be a 3-letter ISO 4217 code in uppercase (for example USD).",
                PayrollFileSchema.Currency, dataRow));
        }

        // The rule that decides whether payroll runs: net pay may never be negative.
        if (regular is not null && overtime is not null && rate is not null && deductions is not null)
        {
            var gross = (regular.Value * rate.Value)
                        + (overtime.Value * rate.Value * PayrollFileSchema.OvertimeMultiplier);

            if (gross - deductions.Value < 0)
            {
                sink.Add(new ValidationError("ROW_NEGATIVE_NET_PAY",
                    $"Gross {gross:0.##} minus deductions {deductions:0.##} is negative; deductions exceed earnings.",
                    PayrollFileSchema.Deductions, dataRow));
            }
        }
    }

    private static void CheckRange(
        ErrorSink sink, string field, int row, decimal? value,
        decimal min, decimal max, bool isInclusive, bool allowZeroExclusive = false)
    {
        if (value is null) return;

        var tooLow = allowZeroExclusive ? value <= min : (isInclusive ? value < min : value < min);
        if (tooLow || value > max)
        {
            var bound = allowZeroExclusive ? $"greater than {min} and at most {max}"
                        : isInclusive ? $"between {min} and {max} inclusive"
                        : $"between {min} and {max}";

            sink.Add(new ValidationError("ROW_OUT_OF_RANGE",
                $"{field} must be {bound}; received '{value}'.", field, row));
        }
    }

    private static DateOnly? ParseDate(
        string[] record, Dictionary<string, int> indexes, string field, int row, ErrorSink sink)
    {
        var raw = GetField(record, indexes, field).Trim();
        if (raw.Length == 0)
        {
            sink.Add(new ValidationError("ROW_REQUIRED_FIELD", $"{field} is required.", field, row));
            return null;
        }

        // Accept both yyyy-MM-dd and spreadsheet-reformatted forms. Rejecting a "correct"
        // file purely on date format is a support trap that costs goodwill for nothing.
        if (DateOnly.TryParseExact(raw, ["yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        sink.Add(new ValidationError("ROW_INVALID_DATE",
            $"{field} '{raw}' is not a recognised date. Prefer yyyy-MM-dd.", field, row));
        return null;
    }

    private static decimal? ParseDecimal(
        string[] record, Dictionary<string, int> indexes, string field, int row, ErrorSink sink)
    {
        var raw = GetField(record, indexes, field).Trim();
        if (raw.Length == 0)
        {
            sink.Add(new ValidationError("ROW_REQUIRED_FIELD", $"{field} is required.", field, row));
            return null;
        }

        // InvariantCulture is mandatory. Reading "1,500" as 1.5 (or the reverse) silently
        // scales every figure in the file by 1000x -- the worst class of payroll bug.
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        sink.Add(new ValidationError("ROW_INVALID_NUMBER",
            $"{field} '{raw}' is not a valid number.", field, row));
        return null;
    }

    private static string GetField(string[] record, Dictionary<string, int> indexes, string field)
        => indexes.TryGetValue(field, out var i) && i < record.Length ? record[i] : string.Empty;

    private static long BytesRead(Stream content) => content.CanSeek ? content.Position : 0;

    private static string Truncate(string? value, int max = 80)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= max ? value
            : value[..max] + "...";

    /// <summary>
    /// A 100k-row spreadsheet with the same mistake would otherwise produce 100k errors.
    /// We keep the first N and flag that truncation happened, rather than allocating a
    /// huge list or (worse) showing the user nothing.
    /// </summary>
    private sealed class ErrorSink(int capacity)
    {
        private readonly List<ValidationError> _items = [];

        public bool Truncated { get; private set; }
        public bool HasErrors => _items.Count > 0;

        public void Add(ValidationError error)
        {
            if (_items.Count >= capacity)
            {
                Truncated = true;
                return;
            }
            _items.Add(error);
        }

        public IReadOnlyList<ValidationError> SortedSnapshot()
            => [.. _items.OrderBy(e => e.RowNumber ?? 0).ThenBy(e => e.Code, StringComparer.Ordinal)];
    }
}