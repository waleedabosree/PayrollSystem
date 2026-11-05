namespace Payroll.Api.Observability;

/// <summary>
/// The correlation identity for the current request.
///
/// Registered SCOPED (one instance per request). Collaborators read it through the interface;
/// only the middleware can set it. This beats two common alternatives:
///  - reading HttpContext.Items everywhere, which duplicates the lookup at every call site,
///  - a static/async-local value, which can leak across requests from pooled threads.
/// </summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
    string TraceId { get; }

    /// <summary>True when the caller supplied the ID. Useful when hunting a client-side bug.</summary>
    bool ClientSupplied { get; }
}

internal sealed class CorrelationState : ICorrelationContext
{
    private string _correlationId = string.Empty;

    public string CorrelationId => _correlationId;
    public string TraceId { get; private set; } = string.Empty;
    public bool ClientSupplied { get; private set; }

    internal void Set(string correlationId, string traceId, bool clientSupplied)
    {
        _correlationId = correlationId;
        TraceId = traceId;
        ClientSupplied = clientSupplied;
    }
}