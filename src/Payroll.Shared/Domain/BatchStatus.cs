namespace Payroll.Shared.Domain;

/// <summary>
/// The batch lifecycle state machine.
///
/// Stored as a string in the database (via EF value conversion) so that adding a new
/// state later never renumbers existing rows -- a classic migration trap with enums stored as ints.
/// </summary>
public enum BatchStatus
{
    /// <summary>Row inserted in the same transaction as the outbox message. Blob may or may not be written yet.</summary>
    Received,

    /// <summary>Blob written and the outbox message has been handed to the broker.</summary>
    Queued,

    /// <summary>The Function took a lock and started work.</summary>
    Processing,

    /// <summary>Terminal success.</summary>
    Completed,

    /// <summary>Terminal -- a business/validation rule failed. Not retried automatically.</summary>
    Failed,

    /// <summary>Reached MaxDeliveryCount. Requires a human fix, then manual replay.</summary>
    DeadLettered,

    /// <summary>Transient failure with attempts still remaining; eligible for re-enqueue.</summary>
    Retryable
}

public static class BatchStatusExtensions
{
    /// <summary>
    /// Statuses from which a Function may legitimately transition a batch into <see cref="BatchStatus.Processing"/>.
    /// This guard is the single most important line of defence against duplicate message delivery
    /// corrupting results -- see ARCHITECTURE.md, failure F4.
    /// </summary>
    public static readonly BatchStatus[] Claimable = [BatchStatus.Queued, BatchStatus.Received, BatchStatus.Retryable];

    public static bool IsTerminal(this BatchStatus status)
        => status is BatchStatus.Completed or BatchStatus.Failed or BatchStatus.DeadLettered;

    public static bool IsRetriable(this BatchStatus status)
        => status is BatchStatus.Failed or BatchStatus.DeadLettered or BatchStatus.Retryable;
}