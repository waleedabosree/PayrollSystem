using Microsoft.AspNetCore.Mvc;
using Payroll.Api.Contracts;
using Payroll.Api.Observability;
using Payroll.Api.Services;
using Payroll.Shared.Csv;
using Payroll.Shared.Domain;

namespace Payroll.Api.Controllers;

/// <summary>
/// The payroll API surface, as an MVC controller.
///
/// Route is declared once on the class so the three actions cannot drift onto different
/// prefixes, and every action names its own success and failure response types so the
/// OpenAPI document describes reality rather than just "200 OK, somehow".
/// </summary>
[ApiController]
[Route("api/batches")]
[Tags("Batches")]
[Produces("application/json")]
public class PayrollBatchesController : ControllerBase
{
    private readonly IPayrollBatchService _service;
    private readonly ICorrelationContext _correlation;
    private readonly TimeProvider _clock;
    private readonly ILogger<PayrollBatchesController> _logger;

    public PayrollBatchesController(
        IPayrollBatchService service,
        ICorrelationContext correlation,
        TimeProvider clock,
        ILogger<PayrollBatchesController> logger)
    {
        _service = service;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Validates a payroll CSV and records a new batch.
    /// Returns 202 with the BatchId and CorrelationId for the client to poll against.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(GetBatchResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadAsync(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return FromProblem(ApiProblem.BadRequest(HttpContext, "UNSUPPORTED_MEDIA_TYPE",
                "Expected multipart/form-data containing 'file' and 'entityId'."));
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");

        if (file is null)
        {
            return FromProblem(ApiProblem.BadRequest(HttpContext, "MISSING_FILE",
                "A payroll file is required in the 'file' field."));
        }

        if (file.Length == 0)
        {
            return FromProblem(ApiProblem.BadRequest(HttpContext, "EMPTY_FILE",
                "The uploaded file is empty."));
        }

        if (file.Length > PayrollFileSchema.MaxFileSizeBytes)
        {
            return FromProblem(ApiProblem.BadRequest(HttpContext, "CSV_TOO_LARGE",
                $"File is {file.Length:N0} bytes; the limit is {PayrollFileSchema.MaxFileSizeBytes:N0} bytes."));
        }

        if (!EntityIdentifier.TryNormalize(form["entityId"], out var entityId, out var idReason))
        {
            return FromProblem(ApiProblem.BadRequest(HttpContext, "INVALID_ENTITY_ID", idReason!));
        }

        // Buffer BEFORE any read: the validator scans the file here, and the blob upload that
        // follows in the next phase must re-read it. The 10MB ceiling makes this cost bounded
        // and deliberate rather than a surprise.
        await using var content = new MemoryStream((int)Math.Min(file.Length, 16 * 1024 * 1024));
        await file.CopyToAsync(content, cancellationToken);
        content.Position = 0;

        var result = await ValidateAndCreateAsync(entityId, content, file.FileName, file.Length, cancellationToken);

        if (!result.Succeeded)
        {
            return FromProblem(ApiProblem.Validation(result.Errors, HttpContext,
                $"The file failed validation: {result.Errors[0].Describe()}"));
        }

        // X-Correlation-Id is already written on the response by CorrelationIdMiddleware's
        // OnStarting hook (including on errors) -- writing it again here would be redundant
        // and could race with it.
        var body = new GetBatchResponse(
            result.BatchId,
            _correlation.CorrelationId,
            BatchStatus.Queued,
            result.ValidatedRows,
            _clock.GetUtcNow());

        return Accepted(uri: $"/api/batches/{result.BatchId}", value: body);
    }

    /// <summary>List batches with optional status, search and entity filters, plus pagination.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<PayrollBatchSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListAsync([FromQuery] BatchQuery query, CancellationToken cancellationToken)
    {
        if (!query.TryNormalize(out var validated, out var reason))
        {
            return FromProblem(ApiProblem.BadRequest(HttpContext, "INVALID_QUERY", reason!));
        }

        return Ok(await _service.ListAsync(validated, cancellationToken));
    }

    /// <summary>Fetch a single batch, including failure stage and reason when it failed.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PayrollBatchDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await _service.GetAsync(id, cancellationToken);

        return detail is null
            ? FromProblem(ApiProblem.NotFound(HttpContext, $"No payroll batch with id '{id}'."))
            : Ok(detail);
    }

    // ---------------------------------------------------------------------------------

    private async Task<CreateBatchResult> ValidateAndCreateAsync(
        EntityIdentifier entityId, Stream content, string? fileName, long length, CancellationToken cancellationToken)
    {
        using (_logger.BeginScope(new Dictionary<string, object> { ["EntityId"] = entityId.Value }))
        {
            _logger.LogDebug("Validating uploaded payroll file {FileName} ({Length} bytes)", fileName, length);
        }

        return await _service.CreateAsync(
            new CreateBatchCommand(entityId, content, fileName, length), cancellationToken);
    }

    /// <summary>
    /// Emits an RFC 9457 problem response with an explicit content type.
    /// Returning a raw ProblemDetails through an ObjectResult would otherwise serialise as
    /// plain application/json, and error clients keyed on problem+json would mis-handle it.
    /// </summary>
    private ObjectResult FromProblem(ProblemDetails problem)
        => new(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" }
        };
}