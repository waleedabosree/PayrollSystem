using Microsoft.AspNetCore.Mvc;
using Payroll.Shared.Csv;

namespace Payroll.Api.Contracts;

/// <summary>
/// One consistent error shape for the whole API.
///
/// ASP.NET Core's ProblemDetails is the platform-standard envelope (RFC 9457), decided against
/// inventing a bespoke one. What we DO control is the extensions: every error carries
/// <c>code</c>, <c>correlationId</c> and <c>traceId</c>, because "it didn't work" is
/// unactionable and "error CSV_HEADER_MISMATCH, correlation 3f2a..., trace 00-..." is not.
/// </summary>
public static class ApiProblem
{
    public const string UnknownErrorCode = "UNEXPECTED_ERROR";

    public static ProblemDetails Validation(
        IReadOnlyList<ValidationError> errors,
        HttpContext http,
        string detail)
    {
        return new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The payroll file was rejected",
            Detail = detail,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            Instance = http.Request.Path,
            Extensions =
            {
                ["code"] = "VALIDATION_FAILED",
                ["correlationId"] = http.RequestServices.GetRequiredService<Observability.ICorrelationContext>().CorrelationId,
                ["traceId"] = http.TraceIdentifier,
                ["errors"] = errors.Select(e => new
                {
                    code = e.Code,
                    message = e.Message,
                    field = e.Field,
                    rowNumber = e.RowNumber
                }).ToArray()
            }
        };
    }

    public static ProblemDetails NotFound(HttpContext http, string detail) => new()
    {
        Status = StatusCodes.Status404NotFound,
        Title = "Resource not found",
        Detail = detail,
        Type = "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        Instance = http.Request.Path,
        Extensions =
        {
            ["code"] = "NOT_FOUND",
            ["correlationId"] = http.RequestServices.GetRequiredService<Observability.ICorrelationContext>().CorrelationId,
            ["traceId"] = http.TraceIdentifier
        }
    };

    public static ProblemDetails BadRequest(HttpContext http, string code, string detail) => new()
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Invalid request",
        Detail = detail,
        Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        Instance = http.Request.Path,
        Extensions =
        {
            ["code"] = code,
            ["correlationId"] = http.RequestServices.GetRequiredService<Observability.ICorrelationContext>().CorrelationId,
            ["traceId"] = http.TraceIdentifier
        }
    };
}

public sealed record EntityIdentifier(string Value)
{
    /// <summary>
    /// Deliberately permissive but bounded: this is an unauthenticated phase, so we constrain
    /// format rather than rely on an identity provider. Length + charset rules here are the
    /// only thing standing between a client and a 64KB "entityId" in every row of the table.
    /// </summary>
    public const int MaxLength = 64;

    public static bool TryNormalize(string? raw, out EntityIdentifier normalized, out string? reason)
    {
        normalized = new EntityIdentifier("unknown");
        reason = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = $"'entityId' is required.";
            return false;
        }

        var value = raw.Trim();
        if (value.Length > MaxLength)
        {
            reason = $"'entityId' must be at most {MaxLength} characters.";
            return false;
        }

        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            reason = "'entityId' may contain only letters, digits, '-', '_' and '.'.";
            return false;
        }

        normalized = new EntityIdentifier(value);
        return true;
    }
}