# Build Plan

Phased delivery. Each phase ends with **verifiable acceptance criteria** — nothing moves forward on "it builds".

**Working agreement**
- Local-first, deploy at the end (Phase 7).
- No authentication/authorization until Phase 8.
- No paid Azure resources created without explicit approval.
- Every phase documents its failure modes before claiming completion.

---

## Prerequisites (before Phase 1) — mostly done

| # | Task | Status |
|---|---|---|
| P0.1 | .NET SDK | ✅ **.NET 9.0.201 chosen** (per your call). EOL 10 Nov 2026 — migration to .NET 10 is a TFM change in `Directory.Build.props` + a new SDK, nothing else. |
| P0.2 | Azure CLI | ⬜ Needed for Phase 7 (Bicep deploy) only |
| P0.3 | Functions Core Tools v4 | ⬜ Needed for Phase 3 local Function runs |
| P0.4 | Azurite | ✅ installed (npm global) |
| P0.5 | Docker running | ✅ SQL Server 2022 + Azurite up via `local/docker-compose.yml` |
| P0.6 | `dotnet-ef` | ✅ 9.0.10 |
| P0.7 | Azure subscription + credit | ⬜ Your side — record remaining credit in the Sponsorships portal |
| P0.8 | `rg-payroll-dev-001` in **eastus** + budget | ⬜ Phase 7 (no paid resources created) |

✅ **Phase 0 done when** the local stack starts cleanly via `docker compose` — **satisfied.**

---

## Phase 1 — Contracts, persistence, and the API skeleton ✅ COMPLETE (29 Oct 2026)

**Build**
- Solution + 3 projects (`Payroll.Api`, `Payroll.Functions`, `Payroll.Shared`) on **net9.0**
- `Payroll.Shared`: `PayrollBatchMessage` (all 7 fields + `SchemaVersion`), `BatchStatus` enum, CSV schema + shared validator, structured error types
- EF Core model: `PayrollBatch`, `PayrollResult`, `OutboxMessage` + migration `20261029101034_InitialCreate`
- ASP.NET Core API: consistent ProblemDetails error envelope, DTOs, hand-rolled validation (explicit, framework-version-proof), OpenAPI, live/ready health checks, correlation-ID middleware
- `POST /api/batches`, `GET /api/batches`, `GET /api/batches/{id}`
- 25 unit tests covering the CSV contract and all business rules

**Acceptance criteria — all verified against a running API on localhost:5088**

- [x] `dotnet build` / `dotnet test` green — **0 warnings, 0 errors, 25/25 tests pass**
- [x] Migration applied to the local SQL container, including the **unique index**
      `IX_PayrollResults_BatchId_EmployeeId` (the idempotency guard)
- [x] `POST /api/batches` rejects bad extension, oversize file, ragged rows, missing
      `entityId` → structured `application/problem+json` with `code`, `errors[]`,
      `correlationId`, `traceId`
- [x] CorrelationId present in every log line (via `BeginScope`) **and** echoed in the
      `X-Correlation-Id` response header, round-tripping a client-supplied value
- [x] OpenAPI serves `/openapi/v1.json` with both endpoints documented
- [x] Status enums serialise as names (`"Received"`), not ordinals
- [x] Filtering, search, pagination, and invalid-query rejection all verified

**Deferred deliberately:** Blob Storage, outbox publish, and Service Bus are Phase 2.
Batches therefore sit legitimately at `Received` until then. Business-rule validation runs in
the Function (Phase 3), not at upload — see `ValidationDepth.Structure` vs `Full`.

**Bugs found and fixed during Phase 1:**
1. `Sdk="Microsoft.NET.Sdk"` instead of `Microsoft.NET.Sdk.Web` — no ASP.NET implicit usings
   or `FrameworkReference`. Diagnosed via `dotnet msbuild -getItem:Using`.
2. CsvHelper 34 was a ground-up rewrite with the documented API removed → pinned to 33.1.0.
3. Custom TFS NuGet feeds in the machine config caused 404s → repo-level `nuget.config` with
   `<clear/>` + nuget.org only.

---

## Phase 2 — Blob upload + outbox + publish

**Build**
- Azurite + Storage SDK: container creation, upload, metadata (batchId, entityId, correlationId)
- **Single transaction**: insert batch row + outbox row
- Outbox dispatcher (background service): scan → publish → mark dispatched, exponential backoff
- Service Bus emulator: `payroll-jobs` queue, `MaxDeliveryCount=5`, DLQ, `Config.json`
- Reconciler: batch `Received` longer than threshold → repair/fail

**Acceptance criteria**
- [ ] Kill the process between commit and publish → restart → **message still delivered**
- [ ] Kill the dispatcher after publish but before mark-dispatched → **duplicate received, downstream idempotent**
- [ ] File lands in blob with correct metadata
- [ ] Batch reaches `Queued` only after both blob upload **and** outbox insert

---

## Phase 3 — Azure Function (isolated worker)

**Build**
- .NET isolated worker Function App, Service Bus queue trigger
- Pipeline: deserialize → validate schema → guard status → download blob → parse → validate → bulk insert → transition to `Completed` + duration
- Structured logging with `CorrelationId` + `BatchId`
- Transient retries; poison → DLQ after `MaxDeliveryCount`
- Idempotency: status guard + `UNIQUE(BatchId, EmployeeId)`

**Acceptance criteria**
- [ ] End-to-end: upload → message → function → results in SQL → status `Completed`
- [ ] Deliberate poison message → **DLQ, not infinite retry**
- [ ] Duplicate delivery → row counts unchanged
- [ ] Transient fault → retried and succeeds
- [ ] Processing duration recorded and visible

---

## Phase 4 — Recovery worker (the anti-stuck guarantee)

- Background service scanning for `Received`/`Queued`/`Processing` beyond threshold
- Re-enqueue with ceiling → `Failed` after N attempts
- Records interventions for auditability
- Retriable failures surfaced as retryable to the UI

**Acceptance criteria**
- [ ] Force a stuck batch → recovered within one scan interval
- [ ] Permanently failing batch → `Failed` after ceiling, **not** looping forever
- [ ] Recovery actions visible in logs with correlation

---

## Phase 5 — Angular dashboard

- Upload with **progress** (`HttpClient` `reportProgress`)
- Batch list, status badges, processing history, detail view
- Filter + paginate (server-side)
- Error/success notifications (toast)
- **Polling** status tracking (5s, backoff when idle)
- Retry button for eligible failed batches
- `CorrelationId` displayed for troubleshooting
- Responsive, accessible, **no auth guards** — but shaped for later addition

**Acceptance criteria**
- [ ] Upload visible at each state: Processing → Queued → Completed
- [ ] Failed batch shows clear reason + CorrelationId + working retry
- [ ] Filtering + pagination round-trip
- [ ] Layout is usable on mobile

---

## Phase 6 — Monitoring & diagnostics

- Application Insights SDK in **both** API and Function (OpenTelemetry-based for .NET 10)
- HTTP request + dependency + exception telemetry
- Custom metrics: batch counts, processing duration
- Service Bus queue-depth + **DLQ length** metrics; alert on repeated failures
- KQL examples for the failure-investigation walkthrough
- Monitor dashboard

**Acceptance criteria**
- [ ] Trace a `CorrelationId` from Angular → API → outbox → Function → SQL
- [ ] Truncated connection string shows **which hop** broke
- [ ] DLQ alert fires on an injected poison message
- [ ] Dashboard shows throughput + latency

---

## Phase 7 — Deployment (Bicep + CI/CD)

- `infra/main.bicep`: full inventory, params, outputs, tags
- GitHub Actions: build/test → deploy infrastructure → deploy apps
- Environment-specific configuration; secrets via **app settings** (Key Vault deferred)
- **Cost review before every resource creation — approval required**

**Acceptance criteria**
- [ ] `az deployment group validate` passes
- [ ] One pipeline run deploys everything from scratch
- [ ] Production path executes end-to-end in Azure
- [ ] Actual cost matches the committed ≈$5/mo scenario

---

## Phase 8 — Security (deferred, as agreed)

Microsoft Entra ID · app authentication (Angular) · API authorization · **Managed Identities** for Service Bus/SQL/Storage · Azure RBAC + least privilege · **Key Vault** · secure Blob access · CORS.

---

## Phase 5b — Redis (only if justified)

`Azure Managed Redis` **Free tier**. Cache-aside on dashboard queries + reference data.

**Gate:** measurable benefit with real metrics, *or* skip it. Never a dependency of the core workflow.

---

## Cross-cutting (every phase)

- `dotnet format` + lint clean
- New failure modes added to the matrix in `docs/ARCHITECTURE.md`
- Structured logs, async all the way, DI, DTO validation
- Tests at the appropriate level