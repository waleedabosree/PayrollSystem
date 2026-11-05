using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payroll.Shared.Data;
using Payroll.Shared.Domain;
using Payroll.Shared.Messaging;

namespace Payroll.Api.Messaging;

public sealed class ServiceBusOptions
{
    public const string Section = "ServiceBus";

    /// <summary>Empty disables dispatching: batches then wait safely in the outbox.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string QueueName { get; set; } = "payroll-jobs";

    public int PollIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 10;

    /// <summary>"AmqpTcp" (port 5671) or "AmqpWebSockets" (port 443, for networks that block AMQP).</summary>
    public string TransportType { get; set; } = "AmqpTcp";
}

/// <summary>
/// Transactional-outbox relay: publishes rows the upload committed, then marks them dispatched.
///
/// Delivery is at-least-once BY DESIGN. If we crash after Send but before the Dispatched
/// update, the row is sent again -- the Function's status guard absorbs that duplicate.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    // A claim older than this is assumed to belong to a dead worker. It doubles as the
    // retry backoff: a failed publish stays "locked" and becomes eligible again after it.
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ServiceBusOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopes, IOptions<ServiceBusOptions> options, TimeProvider clock, ILogger<OutboxDispatcher> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            _logger.LogWarning("ServiceBus:ConnectionString is empty; outbox dispatcher disabled. Batches stay in the outbox.");
            return;
        }

        await using var client = new ServiceBusClient(_options.ConnectionString, new ServiceBusClientOptions
        {
            TransportType = Enum.Parse<ServiceBusTransportType>(_options.TransportType, ignoreCase: true),
            // Fail a blocked/slow send in seconds so the outbox records the error and retries,
            // instead of the dispatcher silently hanging on the default 60s x 3 retry.
            RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(15), MaxRetries = 2 }
        });
        await using var sender = client.CreateSender(_options.QueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = 0;
            try
            {
                dispatched = await DispatchBatchAsync(sender, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatch cycle failed; will retry");
            }

            if (dispatched == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), _clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> DispatchBatchAsync(ServiceBusSender sender, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();

        var now = _clock.GetUtcNow();
        var token = Guid.NewGuid();
        var staleBefore = now - ClaimTimeout;

        var candidates = await db.OutboxMessages
            .Where(m => (m.State == OutboxState.Pending || m.State == OutboxState.Retrying)
                        && (m.LockToken == Guid.Empty || m.LockedAtUtc < staleBefore))
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => m.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var id in candidates)
        {
            // Optimistic claim: only one dispatcher instance can win this UPDATE per row.
            var claimed = await db.OutboxMessages
                .Where(m => m.Id == id && (m.LockToken == Guid.Empty || m.LockedAtUtc < staleBefore))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.LockToken, token)
                    .SetProperty(m => m.LockedAtUtc, now), ct);

            if (claimed == 0) continue;

            var row = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == id, ct);
            await PublishAsync(db, sender, row, ct);
            sent++;
        }

        return sent;
    }

    private async Task PublishAsync(PayrollDbContext db, ServiceBusSender sender, OutboxMessage row, CancellationToken ct)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = row.CorrelationId,
            ["BatchId"] = row.BatchId
        });

        try
        {
            var message = new ServiceBusMessage(row.Payload)
            {
                ContentType = "application/json",
                // MessageId = BatchId lets a queue with duplicate detection (Standard+) drop a
                // re-send inside the window; Basic ignores it, so the consumer stays idempotent.
                MessageId = row.BatchId.ToString(),
                CorrelationId = row.CorrelationId,
                Subject = "payroll.batch.uploaded"
            };
            message.ApplicationProperties["schemaVersion"] = PayrollBatchMessage.CurrentSchemaVersion;

            await sender.SendMessageAsync(message, ct);

            var dispatchedAt = _clock.GetUtcNow();
            var attempts = row.Attempts + 1;
            await db.OutboxMessages.Where(m => m.Id == row.Id).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.State, OutboxState.Dispatched)
                .SetProperty(m => m.DispatchedAtUtc, dispatchedAt)
                .SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.LastError, (string?)null), CancellationToken.None);

            _logger.LogInformation("Published payroll batch {BatchId} to queue {Queue}", row.BatchId, _options.QueueName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempts = row.Attempts + 1;
            var state = attempts >= _options.MaxAttempts ? OutboxState.Abandoned : OutboxState.Retrying;
            var error = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            var failedAt = _clock.GetUtcNow();

            await db.OutboxMessages.Where(m => m.Id == row.Id).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.State, state)
                .SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.LockedAtUtc, failedAt), CancellationToken.None);

            _logger.LogError(ex, "Publishing batch {BatchId} failed (attempt {Attempt}/{Max})",
                row.BatchId, attempts, _options.MaxAttempts);
        }
    }
}
