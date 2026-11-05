# Copilot Instructions

Payroll file processing system: ASP.NET Core API + Azure Functions (isolated worker) + Angular dashboard (`client/`, not yet present) on Azure. Auth is deliberately deferred. Work is organised in phases (`docs/PLAN.md`); currently Phase 1 (upload/validate/list/detail/health/OpenAPI) — Blob Storage, Service Bus, outbox dispatcher and the Function trigger are later phases. Detailed design and failure matrix: `docs/ARCHITECTURE.md`.

## Build, test, run

Not a git repo; solution is `PayrollSystem.sln`. Projects currently target `net9.0` (README says the plan is to move to .NET 10).

```powershell
dotnet build
dotnet test                                   # tests/Payroll.UnitTests (xUnit)
dotnet test --filter "FullyQualifiedName~PayrollFileValidatorTests"          # a class
dotnet test --filter "FullyQualifiedName~PayrollFileValidatorTests.<Method>" # a single test

docker compose -f local/docker-compose.yml up -d   # SQL Server 2022 (+ Azurite / Service Bus emulator)
dotnet run --project src/Payroll.Api               # http://localhost:5088
func start --script-root src/Payroll.Functions
./scripts/Invoke-SmokeTest.ps1                     # end-to-end smoke test against a running API
```

No linter beyond analyzers: `Directory.Build.props` sets `EnforceCodeStyleInBuild`, `Nullable`, `AnalysisLevel=latest`. Package versions are centrally managed in `Directory.Packages.props` (add `PackageReference` without versions in csproj files).

## Architecture

- `Payroll.Shared` — the single source of truth for the CSV schema (`Csv/PayrollFileSchema`, `PayrollFileValidator`), domain entities (`PayrollBatch`, `OutboxMessage`, `BatchStatus`) and the Service Bus message contract. Both API and Functions reference it so upload-time and process-time validation cannot drift. Validation has depths (`ValidationDepth.Structure` at upload; deeper at processing).
- `Payroll.Api` — controllers → `IPayrollBatchService` → `PayrollDbContext` (EF Core, migrations in `Migrations/`). DI wiring is all in `Extensions/ServiceCollectionExtensions.AddPayrollApi`.
- `Payroll.Functions` — isolated-worker host (`OutputType=Exe`); trigger not yet implemented.
- Key invariant: Blob Storage publishes nothing; the API is the only writer of control-plane state; messages are published by an outbox dispatcher only after a single atomic DB commit (batch insert + outbox row in one transaction). Processing must be idempotent.

## Conventions

- **Middleware order in `Api/Program.cs` is deliberate**: `CorrelationIdMiddleware` first, then exception handler, status-code pages, CORS. Every error is `application/problem+json` including `correlationId`/`traceId` (see `Contracts/ApiProblem`).
- Correlation: use `ICorrelationContext` (scoped) and include `CorrelationId` in logs and persisted batches; response header is `X-Correlation-Id` and exposed via CORS.
- Enums serialise as names (`JsonStringEnumConverter`), never ordinals.
- Inject `TimeProvider` rather than `DateTime.UtcNow`.
- Treat client file names as hostile: sanitise with `Path.GetFileName` before storing or building blob paths.
- DB provider is chosen by `Database:Provider` (`SqlServer` default, `InMemory` for tests only — it ignores unique indexes/FKs, so it can't verify idempotency constraints). Connection string key is `PayrollDb`.
- EF `EnableRetryOnFailure` is intentionally off until the outbox phase; retry in one place only (`strategy.Execute()` or the Service Bus consumer), not both.
- Health: `/health/live` (tag `live`, no dependencies) vs `/health/ready` (tag `ready`, checks DB). Keep that split.
- OpenAPI served at `/openapi/v1.json`; controllers need both `AddControllers()` and `AddEndpointsApiExplorer()`.
- `public partial class Program` exists in the API so `WebApplicationFactory` tests can host it.
- Secrets go in user-secrets or `appsettings.*.local.json` (excluded from the build via `Directory.Build.props`).
- Code comments in this repo explain the "why" of non-obvious decisions; keep that style.
