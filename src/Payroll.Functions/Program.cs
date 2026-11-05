using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Payroll.Functions.Processing;
using Payroll.Shared.Csv;
using Payroll.Shared.Data;

// ---------------------------------------------------------------------------
// Isolated worker host. The Functions HOST and this process are separate and talk over
// gRPC, so DI here is ordinary .NET DI -- the host only owns the trigger plumbing.
//
// How it connects to each service (all via configuration, never hard-coded):
//   Service Bus -> "ServiceBusConnection" app setting, read by the trigger extension
//   Blob        -> Storage:ConnectionString (env var Storage__ConnectionString)
//   Azure SQL   -> AzureSql:ConnectionString (env var AzureSql__ConnectionString)
// Managed identity replaces these connection strings in the security phase.
// ---------------------------------------------------------------------------

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        services.Configure<ProcessingOptions>(config.GetSection(ProcessingOptions.Section));
        services.AddSingleton(TimeProvider.System);

        // Shared contract types live in Payroll.Shared so API and Function cannot drift apart
        // on the file format.
        services.AddSingleton<IPayrollFileValidator, PayrollFileValidator>();
        services.AddSingleton<IPayrollFileParser, PayrollFileParser>();

        services.AddSingleton(_ => new BlobServiceClient(
            config["Storage:ConnectionString"] ?? "UseDevelopmentStorage=true"));

        var sql = config["AzureSql:ConnectionString"]
            ?? throw new InvalidOperationException("AzureSql:ConnectionString is not configured.");
        services.AddDbContext<PayrollDbContext>(o => o.UseSqlServer(sql, s => s.CommandTimeout(60)));

        // Scoped: one DbContext per invocation, never shared across concurrent messages.
        services.AddScoped<PayrollBatchProcessor>();
    })
    .Build();

host.Run();
