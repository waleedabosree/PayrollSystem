using Payroll.Api.Contracts;
using Payroll.Shared.Csv;
using Payroll.Shared.Domain;

namespace Payroll.Api.Services;

public sealed record CreateBatchCommand(
    EntityIdentifier EntityId,
    Stream Content,
    string? FileName,
    long DeclaredLength);

public sealed record CreateBatchResult(
    bool Succeeded,
    Guid BatchId,
    int ValidatedRows,
    IReadOnlyList<ValidationError> Errors)
{
    public static CreateBatchResult Rejected(params ValidationError[] errors)
        => new(false, Guid.Empty, 0, errors);
}

public interface IPayrollBatchService
{
    /// <summary>
    /// Validates the payload and records the batch. Does not yet store the blob or publish a
    /// message -- both arrive with the outbox phase; until then the batch legitimately sits at
    /// <see cref="BatchStatus.Received"/>.
    /// </summary>
    Task<CreateBatchResult> CreateAsync(CreateBatchCommand command, CancellationToken cancellationToken = default);

    Task<PagedResponse<PayrollBatchSummary>> ListAsync(ValidatedBatchQuery query, CancellationToken cancellationToken = default);

    Task<PayrollBatchDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}