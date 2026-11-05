using Microsoft.EntityFrameworkCore;
using Payroll.Api.Contracts;
using Payroll.Shared.Data;
using Payroll.Api.Observability;
using Payroll.Shared.Csv;
using Payroll.Shared.Domain;
using Payroll.Shared.Messaging;
using Payroll.Api.Storage;

namespace Payroll.Api.Services;

public sealed class PayrollBatchService : IPayrollBatchService
{
    private readonly PayrollDbContext _db;
    private readonly IPayrollBlobStore _blobs;
    private readonly IPayrollFileValidator _validator;
    private readonly ICorrelationContext _correlation;
    private readonly TimeProvider _clock;
    private readonly ILogger<PayrollBatchService> _logger;

    public PayrollBatchService(
        PayrollDbContext db,
        IPayrollBlobStore blobs,
        IPayrollFileValidator validator,
        ICorrelationContext correlation,
        TimeProvider clock,
        ILogger<PayrollBatchService> logger)
    {
        _db = db;
        _blobs = blobs;
        _validator = validator;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CreateBatchResult> CreateAsync(CreateBatchCommand command, CancellationToken cancellationToken = default)
    {
        // Cheap gate first: structure only. Rejecting here means the user gets an immediate,
        // specific answer instead of a file that queues up and fails where they cannot see it.
        var outcome = _validator.Validate(command.Content, command.FileName, ValidationDepth.Structure);
        if (!outcome.IsValid)
        {
            _logger.LogWarning(
                "Rejected payroll file {FileName} ({Bytes} bytes) from entity {EntityId}: {ErrorCount} problems, first: {FirstError}",
                command.FileName, command.DeclaredLength, command.EntityId.Value, outcome.Errors.Count,
                outcome.Summary);

            return CreateBatchResult.Rejected([.. outcome.Errors]);
        }

        var now = _clock.GetUtcNow();
        var batchId = Guid.NewGuid();
        var safeFileName = Path.GetFileName(command.FileName ?? "upload.csv");

        // Blob first, database second. If we fail after the blob, the worst case is an orphan
        // blob (harmless, cleaned up below); the reverse order could leave a batch row that
        // points at a file that does not exist.
        var blobName = $"batches/{batchId:N}/{safeFileName}";
        command.Content.Position = 0;
        var stored = await _blobs.UploadAsync(blobName, command.Content, new Dictionary<string, string>
        {
            ["batchId"] = batchId.ToString(),
            ["entityId"] = command.EntityId.Value,
            ["correlationId"] = _correlation.CorrelationId,
            // Metadata values must be ASCII, and client file names are not.
            ["originalFileName"] = Uri.EscapeDataString(safeFileName)
        }, cancellationToken);

        var batch = new PayrollBatch
        {
            Id = batchId,
            EntityId = command.EntityId.Value,

            // Path.GetFileName is doing real work: raw client input can contain directory
            // segments. Storing "../..\\windows\\system32\\x.csv" would be an injection into
            // both the database and the blob path.
            OriginalFileName = safeFileName,

            BlobContainer = stored.Container,
            BlobName = stored.Name,
            BlobETag = stored.ETag,
            FileSizeBytes = command.DeclaredLength,
            CorrelationId = _correlation.CorrelationId,
            Status = BatchStatus.Queued,
            ExpectedRows = outcome.RowCount,
            CreatedAtUtc = now,
            QueuedAtUtc = now,
            UpdatedAtUtc = now
        };

        // The message carries pointers only -- never payroll rows.
        var message = new PayrollBatchMessage(
            batchId, stored.Container, stored.Name, batch.EntityId, now,
            _correlation.CorrelationId, PayrollBatchMessage.CurrentSchemaVersion)
        {
            FileName = safeFileName,
            FileSizeBytes = command.DeclaredLength
        };

        _db.PayrollBatches.Add(batch);
        _db.OutboxMessages.Add(new OutboxMessage
        {
            BatchId = batchId,
            Payload = message.Serialize(),
            CorrelationId = _correlation.CorrelationId,
            State = OutboxState.Pending,
            CreatedAtUtc = now
        });

        try
        {
            // One SaveChanges = one atomic transaction: batch row and outbox row commit together
            // or not at all. This is the whole point of the outbox.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist payroll batch {BatchId}; removing orphan blob", batchId);
            try { await _blobs.DeleteAsync(blobName, CancellationToken.None); }
            catch (Exception cleanup) { _logger.LogWarning(cleanup, "Orphan blob {BlobName} cleanup failed", blobName); }
            throw;
        }

        // Structured: BatchId rides in the scope, so every subsequent line for this batch
        // is findable by ID as well as by correlation.
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["BatchId"] = batchId,
            ["EntityId"] = batch.EntityId
        }))
        {
            _logger.LogInformation(
                "Payroll batch {BatchId} created with status {Status}, {RowCount} rows, file {FileName}",
                batchId, batch.Status, outcome.RowCount, batch.OriginalFileName);
        }

        return new CreateBatchResult(true, batchId, outcome.RowCount, Array.Empty<ValidationError>());
    }

    public async Task<PagedResponse<PayrollBatchSummary>> ListAsync(
        ValidatedBatchQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, BatchQuery.MaxPageSize);

        var filters = _db.PayrollBatches.AsNoTracking().AsQueryable();

        if (query.Status is not null)
        {
            filters = filters.Where(b => b.Status == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            filters = filters.Where(b => b.EntityId == query.EntityId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Case-insensitive substring over filename. Indexing LIKE '%x%' gives nothing on
            // SQL Server anyway, but bounding to 100 chars keeps it from being a full scan
            // of a 100k-row file's worth of name.
            var term = query.Search;
            filters = filters.Where(b =>
                b.OriginalFileName.Contains(term) ||
                b.CorrelationId.Contains(term) ||
                b.Id.ToString().Contains(term));
        }

        var total = await filters.CountAsync(cancellationToken);

        var items = await filters
            .OrderByDescending(b => b.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new PayrollBatchSummary(
                b.Id, b.EntityId, b.OriginalFileName, b.FileSizeBytes, b.Status,
                b.CorrelationId, b.CreatedAtUtc, b.UpdatedAtUtc, b.ProcessingDurationMs,
                b.ProcessedRows, b.FailureStage))
            .ToListAsync(cancellationToken);

        return new PagedResponse<PayrollBatchSummary>(items, total, page, pageSize);
    }

    public async Task<PayrollBatchDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var batch = await _db.PayrollBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

        if (batch is null) return null;

        using (_logger.BeginScope(new Dictionary<string, object> { ["BatchId"] = batch.Id }))
        {
            _logger.LogDebug("Read payroll batch {BatchId} in status {Status}", batch.Id, batch.Status);
        }

        return new PayrollBatchDetail(
            batch.Id, batch.EntityId, batch.OriginalFileName, batch.FileSizeBytes,
            batch.BlobContainer, batch.BlobName, batch.CorrelationId, batch.Status,
            batch.ExpectedRows, batch.ProcessedRows, batch.DeliveryCount,
            batch.FailureStage, batch.FailureReason,
            batch.CreatedAtUtc, batch.QueuedAtUtc, batch.StartedAtUtc, batch.CompletedAtUtc,
            batch.UpdatedAtUtc, batch.ProcessingDurationMs);
    }
}