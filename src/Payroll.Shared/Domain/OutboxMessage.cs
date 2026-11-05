namespace Payroll.Shared.Domain;

public enum OutboxState
{
    /// <summary>Committed by the upload transaction. Not yet sent to the broker.</summary>
    Pending,

    /// <summary>Awaiting the next dispatcher tick after a failed attempt.</summary>
    Retrying,

    /// <summary>Broker acknowledged. Terminal.</summary>
    Dispatched,

    /// <summary>Exhausted attempts. Shown on the dashboard; requires manual intervention.</summary>
    Abandoned
}

/// <summary>
/// The outbox row: the bridge between "database committed" and "message published".
///
/// The classic dual-write problem is that these two actions cannot share a transaction
/// (SQL and Service Bus are different systems). The outbox solves it by doing only the
/// database write in the user's transaction, then publishing asynchronously afterwards.
///
/// Consequence you MUST design for: if we publish but fail to mark Dispatched, the message
/// is sent TWICE. That is why consumption is idempotent (see BatchStatusExtensions.Claimable).
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }

    public Guid BatchId { get; set; }

    /// <summary>Serialised <see cref="Messaging.PayrollBatchMessage"/>. Contains NO payroll records -- metadata only.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Cached so the broker can be stamped without re-parsing the payload.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    public OutboxState State { get; set; } = OutboxState.Pending;

    /// <summary>Concurrency control for the dispatcher -- prevents two workers racing on the same row.</summary>
    public Guid LockToken { get; set; }

    /// <summary>When the current claim was taken; a stale claim is treated as a dead worker and reclaimed.</summary>
    public DateTimeOffset? LockedAtUtc { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }
}