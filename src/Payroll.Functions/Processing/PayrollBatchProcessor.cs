using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Payroll.Shared.Csv;
using Payroll.Shared.Data;
using Payroll.Shared.Domain;
using Payroll.Shared.Messaging;

namespace Payroll.Functions.Processing;

public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    /// <summary>Must match the queue's MaxDeliveryCount so the last attempt can record DeadLettered.</summary>
    public int MaxDeliveryCount { get; set; } = 5;

    /// <summary>A batch stuck in Processing longer than this is assumed to belong to a crashed worker.</summary>
    public int StaleProcessingMinutes { get; set; } = 5;

    public int InsertChunkSize { get; set; } = 1000;
}

public enum ProcessingResult
{
    Completed,

    /// <summary>Duplicate or out-of-order delivery: another worker owns it, or it is already terminal.</summary>
    Skipped,

    /// <summary>Permanent, non-retriable problem with the file. Recorded on the batch; the message is settled.</summary>
    Failed,

    /// <summary>No such batch. The message is poison and should be dead-lettered.</summary>
    UnknownBatch
}

public sealed class PayrollBatchProcessor
{
    private readonly PayrollDbContext _db;
    private readonly BlobServiceClient _blobs;
    private readonly IPayrollFileValidator _validator;
    private readonly IPayrollFileParser _parser;
    private readonly ProcessingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PayrollBatchProcessor> _logger;

    public PayrollBatchProcessor(
        PayrollDbContext db,
        BlobServiceClient blobs,
        IPayrollFileValidator validator,
        IPayrollFileParser parser,
        IOptions<ProcessingOptions> options,
        TimeProvider clock,
        ILogger<PayrollBatchProcessor> logger)
    {
        _db = db;
        _blobs = blobs;
        _validator = validator;
        _parser = parser;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Returns for every outcome the message should be SETTLED on. Throws only for transient
    /// faults, so the broker redelivers (Service Bus is the one and only retry loop).
    /// </summary>
    public async Task<ProcessingResult> ProcessAsync(
        PayrollBatchMessage message, int deliveryCount, CancellationToken ct)
    {
        var batchId = message.PayrollBatchId;
        var started = Stopwatch.StartNew();

        // ---- Idempotency gate: one atomic UPDATE decides who owns the batch ----
        var now = _clock.GetUtcNow();
        var staleBefore = now.AddMinutes(-_options.StaleProcessingMinutes);
        var claimable = BatchStatusExtensions.Claimable;

        var claimed = await _db.PayrollBatches
            .Where(b => b.Id == batchId
                        && (claimable.Contains(b.Status)
                            || (b.Status == BatchStatus.Processing && b.StartedAtUtc < staleBefore)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BatchStatus.Processing)
                .SetProperty(b => b.StartedAtUtc, now)
                .SetProperty(b => b.UpdatedAtUtc, now)
                .SetProperty(b => b.DeliveryCount, deliveryCount)
                .SetProperty(b => b.FailureReason, (string?)null)
                .SetProperty(b => b.FailureStage, (string?)null), ct);

        if (claimed == 0)
        {
            var exists = await _db.PayrollBatches.AnyAsync(b => b.Id == batchId, ct);
            if (!exists)
            {
                _logger.LogError("Batch {BatchId} does not exist; message is poison", batchId);
                return ProcessingResult.UnknownBatch;
            }

            _logger.LogInformation(
                "Batch {BatchId} is not claimable (already processed or in progress); skipping duplicate delivery {DeliveryCount}",
                batchId, deliveryCount);
            return ProcessingResult.Skipped;
        }

        try
        {
            // ---- Retrieve ----
            using var buffer = new MemoryStream();
            try
            {
                var blob = _blobs.GetBlobContainerClient(message.ContainerName).GetBlobClient(message.BlobName);
                await blob.DownloadToAsync(buffer, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // A missing file will not appear on retry; retrying only delays the answer.
                return await FailPermanentlyAsync(batchId, "Blob", $"Blob '{message.BlobName}' was not found.", started, ct);
            }

            // ---- Validate (deep: business rules, not just structure) ----
            buffer.Position = 0;
            var outcome = _validator.Validate(buffer, message.FileName, ValidationDepth.Full);
            if (!outcome.IsValid)
            {
                var reason = $"{outcome.Errors.Count} validation problem(s). First: {outcome.Summary}";
                return await FailPermanentlyAsync(batchId, "Validation", reason, started, ct);
            }

            // ---- Process + persist: results and the status flip commit atomically ----
            buffer.Position = 0;
            var rows = _parser.Parse(buffer);
            await SaveResultsAsync(batchId, rows, started, ct);

            _logger.LogInformation(
                "Batch {BatchId} completed: {Rows} rows in {ElapsedMs} ms (delivery {DeliveryCount})",
                batchId, rows.Count, started.Elapsed.TotalMilliseconds, deliveryCount);
            return ProcessingResult.Completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient (or unexpected). Make the batch visible as retryable, then let the
            // broker redeliver. On the final allowed delivery, record DeadLettered because
            // the broker is about to move the message to the DLQ.
            var last = deliveryCount >= _options.MaxDeliveryCount;
            _logger.LogError(ex, "Batch {BatchId} failed on delivery {DeliveryCount}/{Max}; {Outcome}",
                batchId, deliveryCount, _options.MaxDeliveryCount, last ? "dead-lettering" : "will retry");

            _db.ChangeTracker.Clear();
            await _db.PayrollBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, last ? BatchStatus.DeadLettered : BatchStatus.Retryable)
                .SetProperty(b => b.FailureStage, "Processing")
                .SetProperty(b => b.FailureReason, Truncate(ex.Message))
                .SetProperty(b => b.UpdatedAtUtc, _clock.GetUtcNow()), CancellationToken.None);

            throw;
        }
    }

    private async Task SaveResultsAsync(
        Guid batchId, IReadOnlyList<ParsedPayrollRow> rows, Stopwatch started, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Clearing first makes re-processing of a reclaimed batch converge on the same final state.
        await _db.PayrollResults.Where(r => r.BatchId == batchId).ExecuteDeleteAsync(ct);

        var processedAt = _clock.GetUtcNow();
        foreach (var chunk in rows.Chunk(_options.InsertChunkSize))
        {
            _db.PayrollResults.AddRange(chunk.Select(r => new PayrollResult
            {
                BatchId = batchId,
                EmployeeId = r.EmployeeId,
                EmployeeName = r.EmployeeName,
                PayPeriodStart = r.PayPeriodStart,
                PayPeriodEnd = r.PayPeriodEnd,
                RegularHours = r.RegularHours,
                OvertimeHours = r.OvertimeHours,
                HourlyRate = r.HourlyRate,
                Deductions = r.Deductions,
                GrossPay = r.GrossPay,
                NetPay = r.NetPay,
                Currency = r.Currency,
                SourceRowNumber = r.SourceRowNumber,
                ProcessedAtUtc = processedAt
            }));

            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }

        var completedAt = _clock.GetUtcNow();
        var elapsedMs = started.Elapsed.TotalMilliseconds;
        await _db.PayrollBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Status, BatchStatus.Completed)
            .SetProperty(b => b.ProcessedRows, rows.Count)
            .SetProperty(b => b.CompletedAtUtc, completedAt)
            .SetProperty(b => b.UpdatedAtUtc, completedAt)
            .SetProperty(b => b.ProcessingDurationMs, elapsedMs), ct);

        await tx.CommitAsync(ct);
    }

    private async Task<ProcessingResult> FailPermanentlyAsync(
        Guid batchId, string stage, string reason, Stopwatch started, CancellationToken ct)
    {
        _logger.LogWarning("Batch {BatchId} failed permanently at {Stage}: {Reason}", batchId, stage, reason);

        var now = _clock.GetUtcNow();
        var elapsedMs = started.Elapsed.TotalMilliseconds;
        await _db.PayrollBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Status, BatchStatus.Failed)
            .SetProperty(b => b.FailureStage, stage)
            .SetProperty(b => b.FailureReason, Truncate(reason))
            .SetProperty(b => b.CompletedAtUtc, now)
            .SetProperty(b => b.UpdatedAtUtc, now)
            .SetProperty(b => b.ProcessingDurationMs, elapsedMs), ct);

        return ProcessingResult.Failed;
    }

    private static string Truncate(string value) => value.Length <= 1900 ? value : value[..1900];
}
