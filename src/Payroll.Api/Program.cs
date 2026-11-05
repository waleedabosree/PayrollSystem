using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Payroll.Api.Contracts;
using Payroll.Api.Extensions;
using Payroll.Api.Observability;

var builder = WebApplication.CreateBuilder(args);

// Enums serialise as their NAMES ("Received"), not their ordinals (0).
// The dashboard shows these strings; a bare 0/1/2 in the UI would be a meaningless colour
// swatch to whoever is watching the queue. It also keeps an enum reordering from being a
// silently breaking API change for the client.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddPayrollApi(builder.Configuration);

var app = builder.Build();

// ---------------------------------------------------------------------------
// Ordered deliberately. Moving CorrelationIdMiddleware below the exception handler
// means the first log lines of a failing request lose the very ID you need to find them.
// ---------------------------------------------------------------------------

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        // HasStarted means we already committed a status line (e.g. mid-stream write failure);
        // trying to rewrite it as a 500 would throw and mask the original exception.
        if (context.Response.HasStarted)
        {
            throw context.Features.Get<IExceptionHandlerFeature>()?.Error
                ?? new InvalidOperationException("Request failed after the response had started.");
        }

        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var services = context.RequestServices;
        var correlation = services.GetRequiredService<ICorrelationContext>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");

        // The exception object goes in as structured state so Application Insights can group
        // by type/stack without us string-concatenating a stack trace into the message.
        logger.LogError(error, "Unhandled exception handling {Path} [{CorrelationId}]",
            context.Request.Path, correlation.CorrelationId);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = 500,
            title = "An unexpected error occurred.",
            detail = "The request could not be completed. Quote the correlationId when reporting this.",
            code = ApiProblem.UnknownErrorCode,
            correlationId = correlation.CorrelationId,
            traceId = correlation.TraceId
        }));
    });
});

app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    if (response.HasStarted) return;
    if (response.ContentType is not null) return;

    var correlation = statusCodeContext.HttpContext.RequestServices
        .GetService<ICorrelationContext>();

    response.ContentType = "application/problem+json";
    await response.WriteAsync(JsonSerializer.Serialize(new
    {
        status = statusCodeContext.HttpContext.Response.StatusCode,
        title = statusCodeContext.HttpContext.Response.StatusCode,
        detail = "The endpoint does not exist or the request could not be matched.",
        code = statusCodeContext.HttpContext.Response.StatusCode == 404 ? "NOT_FOUND" : "HTTP_ERROR",
        correlationId = correlation?.CorrelationId,
        traceId = correlation?.TraceId
    }));
});

app.UseCors(CorsPolicies.AngularClient);

// OpenAPI document is served unauthenticated in this phase; it is a learning artefact
// and will move behind auth when the security phase lands.
app.MapOpenApi();

// Swagger UI renders the built-in OpenAPI document (no Swashbuckle generator involved).
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Payroll API v1"));

// MVC controllers own the /api surface. MapControllers() publishes whatever [Route]s the
// controllers declare -- changing a route in one place changes both routing and OpenAPI.
app.MapControllers();

// Liveness: is the process running? Never touches dependencies, so Azure does not restart
// an app because SQL had a blip.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

// Readiness: can this instance actually serve traffic? This is what a load balancer or
// Azure App Service should use to decide whether to route to us.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description }),
            totalDurationMs = report.TotalDuration.TotalMilliseconds
        }));
    }
});

app.Run();

/// <summary>Exposes the implicit Program class so WebApplicationFactory-based integration tests can boot the host.</summary>
public partial class Program { }