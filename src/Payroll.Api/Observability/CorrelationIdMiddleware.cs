using System.Diagnostics;
using System.Text.Json;

namespace Payroll.Api.Observability;

/// <summary>
/// Establishes CorrelationId for the request, scatters it across logs and the response, and
/// makes it available to every downstream collaborator (including the Service Bus message body,
/// so a failure weeks later can be traced back to the exact request that started it).
///
/// Ordering matters: this must run BEFORE anything that logs, otherwise the first few log
/// lines of a request arrive without the very ID you need to find them.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 64;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers.TryGetValue(HeaderName, out var values)
            ? values.FirstOrDefault() : null;

        var clientSupplied = IsAcceptable(supplied);
        // Generate when the client sends nothing, or sends something hostile/oversized:
        // we never trust an arbitrary client value in a log or an outbound message.
        var correlationId = clientSupplied ? supplied! : Guid.NewGuid().ToString("N");

        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        var state = context.RequestServices.GetRequiredService<CorrelationState>();
        state.Set(correlationId, traceId, clientSupplied);

        // Always emit the ID, even on the error paths, because 4xx/5xx are precisely the
        // responses a user is most likely to quote back at you.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        var stopwatch = Stopwatch.StartNew();

        // One statement, attached to EVERY log line in this request, and handed to
        // Application Insights as a custom dimension when the agent is configured.
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = traceId
        }))
        {
            await _next(context);
        }

        stopwatch.Stop();

        _logger.LogDebug(
            "Request {Method} {Path} -> {StatusCode} in {ElapsedMs}ms [{CorrelationId}]",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            correlationId);
    }

    private static bool IsAcceptable(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not ':') return false;
        }
        return true;
    }
}