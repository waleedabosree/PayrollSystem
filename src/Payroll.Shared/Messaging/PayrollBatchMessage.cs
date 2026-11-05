using System.Text.Json;
using System.Text.Json.Serialization;

namespace Payroll.Shared.Messaging;

/// <summary>
/// The contract published to the Service Bus queue and consumed by the Function.
///
/// DESIGN RULE: this message carries *pointers*, never payroll data.
/// Putting employee rows in the message body would mean sensitive data sitting in a broker
/// with its own retention, DLQ, and diagnostic capture -- avoid that entirely.
/// </summary>
public sealed record PayrollBatchMessage(
    [property: JsonPropertyName("payrollBatchId")] Guid PayrollBatchId,
    [property: JsonPropertyName("containerName")] string ContainerName,
    [property: JsonPropertyName("blobName")] string BlobName,
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("uploadedAtUtc")] DateTimeOffset UploadedAtUtc,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion
)
{
    /// <summary>
    /// Bump ONLY when the shape changes incompatibly. Consumers must reject versions
    /// they do not understand rather than guess -- otherwise a rolled-forward producer
    /// silently drops fields a rolled-back consumer depended on.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Optional diagnostic aids. Absent on messages deserialised from older versions.</summary>
    [JsonPropertyName("fileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; init; }

    [JsonPropertyName("fileSizeBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FileSizeBytes { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Older or newer producers may omit optional fields; missing required data is
        // caught explicitly by TryValidate rather than by a Klaxon in the deserialiser.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static PayrollBatchMessage? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PayrollBatchMessage>(json, Options);
        }
        catch (JsonException)
        {
            // Malformed payloads must never fault a logic layer with a raw serializer exception.
            return null;
        }
    }

    /// <summary>
    /// Structural validation, deliberately separate from deserialisation so we can produce
    /// actionable errors instead of an unhandled exception, and so we can reject a
    /// future schemaVersion instead of parsing it into nonsense.
    /// </summary>
    public bool TryValidate(out string? reason)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            reason = $"Unsupported message schemaVersion {SchemaVersion}; this consumer supports {CurrentSchemaVersion}.";
            return false;
        }

        if (PayrollBatchId == Guid.Empty)
        {
            reason = "payrollBatchId must be a non-empty GUID.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(BlobName))
        {
            reason = "containerName and blobName are required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(CorrelationId))
        {
            reason = "correlationId is required for end-to-end tracing.";
            return false;
        }

        reason = null;
        return true;
    }
}