using Payroll.Shared.Domain;

namespace Payroll.Api.Contracts;

/// <summary>
/// List projection. Excludes blob internals and failure internals -- the summary view runs on
/// every dashboard poll, so it stays narrow on purpose.
/// </summary>
public sealed record PayrollBatchSummary(
    Guid Id,
    string EntityId,
    string OriginalFileName,
    long FileSizeBytes,
    BatchStatus Status,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    double? ProcessingDurationMs,
    int? ProcessedRows,
    string? FailureStage);

/// <summary>Detail view: everything the troubleshooting panel needs, including the error text.</summary>
public sealed record PayrollBatchDetail(
    Guid Id,
    string EntityId,
    string OriginalFileName,
    long FileSizeBytes,
    string? BlobContainer,
    string? BlobName,
    string CorrelationId,
    BatchStatus Status,
    int? ExpectedRows,
    int? ProcessedRows,
    int DeliveryCount,
    string? FailureStage,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    double? ProcessingDurationMs);

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize)
{
    public bool HasNext => Page * PageSize < Total;
    public bool HasPrevious => Page > 1;
}

/// <summary>
/// Raw query as it arrives from the URL. All sanitisation happens in <see cref="Normalize"/>,
/// so downstream code can treat every value as legal -- the distinction between "what the client
/// sent" and "what we will actually execute" is worth keeping visible in the type system.
/// </summary>
public sealed record BatchQuery(
    string? Status = null,
    string? Search = null,
    string? EntityId = null,
    int Page = 1,
    int PageSize = 20)
{
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    public static readonly IReadOnlyList<string> AllowedStatuses = Enum.GetNames<BatchStatus>();

    public bool TryNormalize(out ValidatedBatchQuery query, out string? reason)
    {
        query = null!;
        reason = null;

        BatchStatus? status = null;
        if (!string.IsNullOrWhiteSpace(Status))
        {
            if (!Enum.TryParse<BatchStatus>(Status, ignoreCase: true, out var parsed))
            {
                reason = $"'status' must be one of: {string.Join(", ", AllowedStatuses)}.";
                return false;
            }
            status = parsed;
        }

        var search = Search?.Trim();
        if (!string.IsNullOrEmpty(search) && search.Length > MaxSearchLength)
        {
            search = search[..MaxSearchLength];
        }

        var entityId = string.IsNullOrWhiteSpace(EntityId) ? null : EntityId.Trim();

        query = new ValidatedBatchQuery(
            status,
            search,
            entityId,
            Math.Max(1, Page),
            Math.Clamp(PageSize, 1, MaxPageSize));

        return true;
    }
}

/// <summary>Validated, bounded query that is safe to execute.</summary>
public sealed record ValidatedBatchQuery(
    BatchStatus? Status,
    string? Search,
    string? EntityId,
    int Page,
    int PageSize);

/// <summary>Response body for a successful 202. The dashboard reads BatchId and polls from here.</summary>
public sealed record GetBatchResponse(
    Guid BatchId,
    string CorrelationId,
    BatchStatus Status,
    int ValidatedRows,
    DateTimeOffset AcceptedAtUtc);