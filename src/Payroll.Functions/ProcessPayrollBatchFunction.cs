using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Payroll.Functions.Processing;
using Payroll.Shared.Messaging;

namespace Payroll.Functions;

public sealed class ProcessPayrollBatchFunction
{
    private readonly PayrollBatchProcessor _processor;
    private readonly ILogger<ProcessPayrollBatchFunction> _logger;

    public ProcessPayrollBatchFunction(PayrollBatchProcessor processor, ILogger<ProcessPayrollBatchFunction> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    // AutoCompleteMessages stays at its default (true): returning normally completes the
    // message, throwing abandons it (lock released, DeliveryCount++, redelivered, and moved to
    // the DLQ by the broker after MaxDeliveryCount). We only settle by hand to dead-letter
    // messages that can never succeed, so they do not burn all their retries first.
    [Function(nameof(ProcessPayrollBatchFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger("%PayrollQueueName%", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage received,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = received.CorrelationId ?? "unknown",
            ["MessageId"] = received.MessageId
        });

        var message = PayrollBatchMessage.Deserialize(received.Body.ToString());
        string? reason = null;
        if (message is null || !message.TryValidate(out reason))
        {
            _logger.LogError("Rejecting invalid message {MessageId}: {Reason}", received.MessageId, reason ?? "malformed JSON");
            await actions.DeadLetterMessageAsync(
                received, deadLetterReason: "InvalidMessage",
                deadLetterErrorDescription: reason ?? "Body is not valid payroll batch JSON.",
                cancellationToken: cancellationToken);
            return;
        }

        using var batchScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["BatchId"] = message.PayrollBatchId,
            ["EntityId"] = message.EntityId
        });

        _logger.LogInformation("Received batch {BatchId}, delivery {DeliveryCount}",
            message.PayrollBatchId, received.DeliveryCount);

        var result = await _processor.ProcessAsync(message, received.DeliveryCount, cancellationToken);

        if (result == ProcessingResult.UnknownBatch)
        {
            await actions.DeadLetterMessageAsync(
                received, deadLetterReason: "UnknownBatch",
                deadLetterErrorDescription: $"No batch {message.PayrollBatchId} in the database.",
                cancellationToken: cancellationToken);
        }
    }
}
