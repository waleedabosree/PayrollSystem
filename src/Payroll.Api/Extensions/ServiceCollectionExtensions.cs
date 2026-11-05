using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payroll.Api.Messaging;
using Payroll.Api.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Payroll.Shared.Data;
using Payroll.Api.Observability;
using Payroll.Api.Services;
using Payroll.Shared.Csv;

namespace Payroll.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPayrollApi(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Correlation ----
        services.AddHttpContextAccessor();
        services.AddScoped<CorrelationState>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationState>());

        // ---- Time ----
        // Injecting TimeProvider instead of DateTime.UtcNow is what makes "the batch sat in
        // Queued for 5 minutes" testable without waiting 5 minutes.
        services.AddSingleton(TimeProvider.System);

        // ---- Domain services ----
        services.AddSingleton<IPayrollFileValidator, PayrollFileValidator>();
        services.AddScoped<IPayrollBatchService, PayrollBatchService>();

        // ---- Blob Storage + Service Bus (outbox relay) ----
        services.Configure<BlobStorageOptions>(configuration.GetSection(BlobStorageOptions.Section));
        services.Configure<ServiceBusOptions>(configuration.GetSection(ServiceBusOptions.Section));
        services.AddSingleton(sp => new BlobServiceClient(
            sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value.ConnectionString));
        services.AddSingleton<IPayrollBlobStore, PayrollBlobStore>();
        services.AddHostedService<OutboxDispatcher>();

        // ---- Persistence ----
        services.AddPayrollDatabase(configuration);

        // ---- Error envelope + API docs ----
        services.AddProblemDetails();

        // AddControllers() gives MVC's ApiExplorer (describes controller actions).
        // AddEndpointsApiExplorer() is what .NET's OpenAPI document provider reads for
        // minimal endpoints. Both feed the same /openapi/v1.json, so omitting either
        // silently drops half the routes from the document.
        services.AddControllers()
            // MVC has its own JSON options; ConfigureHttpJsonOptions only covers minimal APIs.
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        services.AddEndpointsApiExplorer();

        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Payroll Processing API";
                document.Info.Version = "v1";
                document.Info.Description =
                    "Upload, validate and track payroll files. Authentication is intentionally " +
                    "absent in this phase; see docs/ARCHITECTURE.md.";
                return Task.CompletedTask;
            });
        });

        // ---- Health ----
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddDbContextCheck<PayrollDbContext>("azure-sql", tags: ["ready"]);

        // ---- CORS for the Angular dev server ----
        services.AddCors(options =>
        {
            options.AddPolicy(CorsPolicies.AngularClient, policy => policy
                .WithOrigins(
                    configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                    ?? ["http://localhost:4200"])
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                // The dashboard needs to read X-Correlation-Id off the response to surface it
                // for troubleshooting; without this the browser hides it from JavaScript.
                .WithExposedHeaders(CorrelationIdMiddleware.HeaderName));
        });

        return services;
    }

    private static void AddPayrollDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "SqlServer";

        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            // Tests and hosted demos only. EF's InMemory provider does NOT honour unique
            // indexes, foreign keys, decimal precision or SQL date semantics -- so it cannot
            // validate the idempotency constraint that this whole design depends on.
            services.AddDbContext<PayrollDbContext>(options =>
                options.UseInMemoryDatabase(configuration.GetConnectionString("PayrollDb") ?? "PayrollInMemory"));
            return;
        }

        var connectionString = configuration.GetConnectionString("PayrollDb")
            ?? throw new InvalidOperationException(
                "Connection string 'PayrollDb' is missing. Set it in appsettings.json or user-secrets.");

        services.AddDbContext<PayrollDbContext>(options => options.UseSqlServer(connectionString, sql =>
        {
            // The DbContext lives in Payroll.Shared (the Function needs it too), but migrations
            // stay with the API, which owns schema deployment.
            sql.MigrationsAssembly(typeof(ServiceCollectionExtensions).Assembly.GetName().Name);
            sql.CommandTimeout(30);

            // NOTE: EF's transient-fault execution strategy (EnableRetryOnFailure) is
            // deliberately LEFT OFF. It becomes active in the outbox phase, where we must wrap
            // the batch insert and the outbox insert in ONE transaction -- a strategy-managed
            // retry and a user-initiated transaction cannot be combined naively and throw
            // InvalidOperationException. When we get there, either retry inside
            // strategy.Execute() or retry at the message-consumer level (which is where the
            // Service Bus retry policy already lives). Choosing one place for retries, rather
            // than three, is the actual lesson.
        }));
    }
}