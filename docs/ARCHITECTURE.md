# Architecture — Payroll Processing System

> Design principle for this project: **the API is the only writer of control-plane state.**
> Blob Storage stores bytes; it publishes nothing. Every message is published *explicitly* by
> application code after the durable state is committed.

---

## 1. Corrected high-level architecture

Your original diagram had one misleading edge: it drew Blob Storage → Service Bus, implying an
automatic handoff. Azure has no such implicit link. Below, the publish is explicitly owned by the
API, and the recovery worker is shown as a first-class path rather than an afterthought.

```mermaid
flowchart TD
    subgraph Client["Client tier"]
        UI["Angular Dashboard<br/>upload • status • history • retry"]
    end

    subgraph App["Application tier — App Service (Linux, F1 dev)"]
        API["ASP.NET Core Web API<br/>validation • batch creation • message publish<br/>outbox writer • status queries"]
        Recover["Recovery hosted service<br/>(in-process background worker)"]
    end

    subgraph Data["Data tier"]
        SQL[("Azure SQL Database<br/>payroll batches • results • outbox")]
        Blob[("Azure Blob Storage<br/>payroll-raw container")]
    end

    subgraph Messaging["Messaging tier"]
        SB[("Azure Service Bus<br/>'payroll-jobs' queue — Basic")]
        DLQ[("Dead-letter queue")]
    end

    subgraph Compute["Compute tier — Functions (Consumption)"]
        FN["ProcessPayrollFunction<br/>Service Bus trigger<br/>isolated worker"]
    end

    Observability["Azure Monitor + Application Insights<br/>traces • logs • metrics • alerts"]

    %% --- request path
    UI -- "POST /api/batches (multipart)" --> API
    UI -- "GET /api/batches?filter=… (polling)" --> API
    API -- "GET/PUT /api/batches/{id}/status" --> SQL

    %% --- upload path: API is the only writer to blob
    API -- "UploadAsync" --> Blob
    API -- "EF Core: insert batch row<br/>+ outbox message in ONE transaction" --> SQL

    %% --- explicit publish (the corrected edge)
    SQL -- "outbox dispatcher publishes" --> SB
    SB -- "delivery" --> FN

    %% --- processing path
    FN -- "GetBlobClient → download" --> Blob
    FN -- "SaveAsync + status transitions" --> SQL
    FN -.->|"max retries exceeded"| DLQ

    %% --- recovery
    Recover -- "scan batches stuck > N min<br/>with no active message" --> SQL
    Recover -- "re-enqueue" --> SB

    %% --- health
    UI -.->|status polling| API
    Recover -.->|emits metrics| Observability
    API -.-> Observability
    FN -.-> Observability
    SB -.->|queue depth, DLQ length| Observability

    classDef danger fill:#3b1010,stroke:#e05252,color:#fff
    classDef ok fill:#0d2b45,stroke:#4aa3ff,color:#fff
    class FN,API,UI,Recover ok
    class DLQ danger
```

**What changed relative to your diagram, and why:**

| Change | Reason |
|---|---|
| Removed the Blob → Service Bus edge | Blob Storage cannot publish to Service Bus on its own. (It *can* emit **Event Grid** events on blob create — that is a deliberate alternative design we rejected for v1, see §7.) |
| Added **API → SQLite/SQL outbox → Service Bus** | Makes the atomic-commit + async-publish sequence explicit, and shows where the reliability gap lives. |
| Added **DLQ** as a terminal node | A queue without a DLQ strategy silently drops poison messages. |
| Added **Recovery worker** | The one branch that guarantees a batch can never be permanently stuck. |
| Added **Observability** as an attached layer, not an inline hop | Telemetry is a cross-cutting concern; drawing it inline implies it's part of the happy path. |

---

## 2. Payroll processing sequence

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant UI as Angular
    participant API as ASP.NET Core API
    participant SQ as Azure SQL
    participant BL as Blob Storage
    participant SB as Service Bus
    participant FN as Azure Function
    participant OB as Outbox dispatcher
    participant RC as Recovery worker

    Note over U,RC: Phase A — upload (synchronous, must be fast)

    U->>UI: select payroll file
    UI->>API: POST /api/batches (multipart/form-data)
    API->>API: validate extension, size, header structure
    API->>API: generate PayrollBatchId (GUID) + CorrelationId

    API->>SQ: BEGIN TX<br/>INSERT batch (status=Received)<br/>INSERT outbox message
    SQ-->>API: committed     ═══ single atomic commit ═══

    API->>BL: UploadAsync(blob)
    BL-->>API: ETag
    API->>SQ: UPDATE batch → status=Queued, set BlobName/ETag
    API-->>UI: 202 Accepted {batchId, correlationId}
    UI-->>U: "Uploaded — processing will begin shortly"

    Note over OB,SB: Phase B — asynchronous publish (fire-and-forget loop)

    OB->>SQ: read unread outbox rows (locked)
    OB->>SB: SendMessageAsync(payload, correlationId)
    SB-->>OB: message id (broker ack)
    OB->>SQ: mark outbox row dispatched
    Note over OB: Failure window: row stays undispatched<br/>→ next tick retries → <b>no lost message</b>

    Note over SB,FN: Phase C — processing (lazy, decoupled)

    SB->>FN: deliver message (auto-lock ~5 min)
    FN->>FN: deserialize + validate schema version
    FN->>SQ: UPDATE batch → Processing (guarded WHERE status IN(Received,Queued))
    FN->>BL: download + parse + business validation
    FN->>SQ: bulk-insert results
    FN->>SQ: UPDATE batch → Completed + duration + row counts
    FN->>SB: CompleteMessage (settlement)
    Note over FN,SB: If FN throws → abandon → retry with backoff<br/>→ retries exhausted → DeadLetter<br/><br/><b>Idempotency key:</b> batchId is unique<br/>+ status guard = duplicate delivery is harmless

    Note over RC,SB: Phase D — recovery (always running)

    RC->>SQ: find batches 'Received'/'Queued'/'Processing'<br/>older than threshold
    RC->>SB: peek / re-enqueue
    RC->>SQ: log intervention + set Status
    RC-->>U: surfaced in dashboard as retriable

    Note over U,RC: Phase E — observation (continuous)
    UI->>API: GET /api/batches/{id} (poll every 5s)
    API->>SQ: SELECT status
    SQ-->>API: Completed
    API-->>UI: {status: Completed, durationMs, processedRows}
```

---

## 3. Deployment architecture

```mermaid
flowchart LR
    subgraph GH["GitHub"]
        REPO["payroll-system"]
        WF1[".github/workflows/ci.yml<br/>build + test + lint"]
        WF2[".github/workflows/deploy.yml<br/>az deployment (Bicep) + app deploy"]
    end

    subgraph LOC["Developer machine (Windows)"]
        SDK[".NET 10 SDK"]
        AZR["Azurite"]
        SQLC["SQL Server 2022 container"]
        SBEM["Service Bus emulator v2"]
        FNT["Functions Core Tools"]
        NG["ng serve"]
    end

    subgraph AZ["Azure — subscription: Azure for Students<br/>resource group: rg-payroll-dev-001<br/>region: East US"]
        subgraph RG1["Compute"]
            AS["App Service Plan — <b>Free F1</b> (Linux)<br/>api-payroll-dev-b001"]
            ASAS["Static Web App / App Service<br/>bl-payroll-dev-b001"]
            FA["Function App — Consumption Y1<br/>func-payroll-dev-b001"]
        end
        subgraph RG2["Data"]
            ST["Storage account<br/>stpayrolldevb001<br/>containers: payroll-raw, payroll-results"]
            SQLDB[("Azure SQL Database — Basic DTU 5<br/>sql-payroll-dev-b001")]
        end
        subgraph RG3["Messaging"]
            SBNS["Service Bus namespace — <b>Basic</b><br/>sb-payroll-dev-b001<br/>queue: payroll-jobs + associated DLQ"]
        end
        subgraph RG4["Observability"]
            AI["Application Insights<br/>ai-payroll-dev-b001"]
            LAW["Log Analytics workspace<br/>law-payroll-dev-b001"]
            DASH["Monitor dashboard + alerts"]
        end
        KV["<i>Key Phase 8</i> — Key Vault<br/>kv-payroll-dev-b001"]
    end

    NG -- "proxy /api → localhost:5000" --> SDK
    AZR -.->|"same code path<br/>via Azure SDK"| SDK
    SQLC -.-> SDK
    SBEM -.-> SDK

    WF2 -- "az deployment group create -f main.bicep" --> AZ
    WF2 -- "az webapp deploy / func azure functionapp deploy" --> AS
    WF2 --> FA

    AS --> ST
    AS --> SQLDB
    AS --> SBNS
    FA --> ST
    FA --> SQLDB
    FA --> SBNS
    AS -.-> AI
    FA -.-> AI
    AI --> LAW
    LAW --> DASH
    KV -.->|"later phase"| AS
    KV -.-> FA

    classDef free fill:#102a10,stroke:#4caf50,color:#fff
    classDef pay fill:#3b2a10,stroke:#e0a52e,color:#fff
    class AS,ASAS free
    class ST,SQLDB,SBNS,AI pay
```

Legend: 🟩 **$0** — 🟨 **metered** (contributes to the ~$5/mo committed scenario).

---

## 4. Azure resource inventory

| # | Resource | Type | SKU | Region | Idle cost | Purpose |
|---|---|---|---|---|---|---|
| 1 | `rg-payroll-dev-001` | Resource Group | — | East US | **$0** | Lifecycle + cost boundary for everything below |
| 2 | `stpayrolldevb001` | Storage account (v2) | Standard LRS | East US | ~$0.03 | Blob storage for uploaded payroll files |
| 3 | `sql-payroll-dev-b001` | Azure SQL Database | **Basic DTU** (5 DTU, 2 GB) | East US | **$4.89/mo** | Batches, statuses, results, outbox |
| 4 | `sb-payroll-dev-b001` | Service Bus namespace | **Basic** | East US | ~$0.05/mo | `payroll-jobs` queue + DLQ |
| 5 | `func-payroll-dev-b001` | Function App | **Consumption Y1** | East US | $0 in free grant | Service Bus–triggered processor |
| 6 | `app-payroll-dev-b001` | App Service Plan + Web App | **Free F1 (Linux)** | East US | **$0** | Hosts ASP.NET Core API |
| 7 | `swa-payroll-dev-b001` | Static Web App | **Free** | East US | **$0** | Hosts Angular build *(alt: F1 App Service)* |
| 8 | `ai-payroll-dev-b001` | Application Insights | Free tier | — | $0 within quota | Traces, requests, dependencies, exceptions |
| 9 | `law-payroll-dev-b001` | Log Analytics workspace | Free 30-day grant | East US | $0 within grant | KQL queries, alert rules |
| 10 | `kv-payroll-dev-b001` | Key Vault | Standard | East US | ~$0.02/mo | **Phase 8 only** — secrets & connection strings |
| 11 | *(optional)* `redis-…` | Azure Managed Redis | **Free tier** | East US | **$0** | **Phase 5 only** — *if and only if* we measure a benefit |

**Committed scenario totals ≈ $4.97/month** (SQL $4.89 + Service Bus $0.05 + storage ~$0.03).
Everything else is $0 or within free-tier grants.

### Entities to create in Service Bus (Basic tier)

```text
namespace  sb-payroll-dev-b001        (Basic)
└── queue   payroll-jobs
      ├── LockDuration            PT1M
      ├── MaxDeliveryCount        5
      ├── DeadLetteringOnMessageExpiration  true
      ├── RequiresSession         false
      └── DefaultMessageTimeToLive  P14D
```

> ⚠️ **Basic tier supports queues only** — it does **not** support topics, subscriptions,
> sessions, or scheduled enqueue with arbitrary delay. That is precisely why Basic is $0.05 and
> Standard is $10. If we later need fan-out (multiple independent subscribers), that is the moment
> to move to Standard — and we should do it with a documented cost justification.

---

## 5. Component responsibilities

| Component | Owns | Must never do |
|---|---|---|
| **Angular** | File selection & progress, rendering batch list/status/history, error surfacing, retry intent, displaying `CorrelationId` | Hold secrets, decide business rules, directly touch Azure |
| **ASP.NET Core API** | Input validation, GUID generation, **the single atomic DB commit**, blob upload, outbox insert, read-only status queries | Long-running work, parse payroll files inline, publish directly via fire-and-forget |
| **Outbox dispatcher** | Turning committed DB rows into Service Bus messages, retry with backoff | Skip the DB, lose the message if publish fails |
| **Azure Function** | Download, parse, validate, compute, persist results, transition statuses, record duration | Publish new work without TTL guard, assume exactly-once delivery |
| **Recovery worker** | Detecting stuck batches, re-enqueueing, recording interventions | Retry without a backoff ceiling, silently "fix" a legitimately failed batch |
| **Blob Storage** | Durable bytes only | Publish anything, enforce business rules |
| **Service Bus** | Durable buffering, DLQ, delivery guarantees, lock semantics | Understand payload contents, run business logic |
| **Azure SQL** | System of record: batch state machine, results, outbox | — |
| **App Insights / Monitor** | Correlation across every hop, metrics, alerts | Hold payloads or PII in telemetry |

### Batch state machine

```mermaid
stateDiagram-v2
    [*] --> Received      : API row inserted (TX)
    Received --> Queued   : blob uploaded + outbox dispatched
    Queued --> Processing : Function takes the message
    Processing --> Completed : results persisted
    Processing --> Failed : validation/business rule failure
    Processing --> Retryable : transient failure, delivery count < max
    Retryable --> Queued   : Recovery worker re-enqueues
    Failed --> Queued     : user clicks Retry (eligible only)
    Completed --> [*]
    Queued --> DeadLettered : MaxDeliveryCount reached
    DeadLettered --> Queued : DLQ replay after fix
    note right of Processing
        Every transition is guarded by
        UPDATE … WHERE Status = <expected>
        so duplicate delivery can't corrupt state
    end note
```

---

## 6. Failure scenarios and recovery strategies

This is the section that determines whether the system is *actually* correct, versus merely
passing the happy path.

| # | Failure point | Symptom | Impact | Recovery strategy |
|---|---|---|---|---|
| **F1** | **DB commit succeeds, blob upload fails** | Batch row `Received`, no blob | Orphan row, never processed | Recovery sees `Received` older than N min → attempts upload retry from a temp copy, or marks `Failed` with reason. Never reaches `Queued`. |
| **F2** | **Blob uploaded, then DB status update fails** | Blob exists, row still `Received` | Same as F1 | Idempotent re-upload (same batchId path) is safe — overwrite with same content. |
| **F3** | **Outbox row committed, dispatcher crashes** | Row undispatched | Message delayed, **not lost** | Ticker re-scans undispatched rows → publishes → marks dispatched. Base of the outbox guarantee. |
| **F4** | **Publish succeeds, DB mark-dispatched fails** | Message published *and* row still undispatched | **Duplicate message** | Function is **idempotent**: unique batchId + status guard makes duplicate delivery harmless. Get this wrong and you double-insert payroll rows. |
| **F5** | **Function crashes mid-processing** | Message abandoned, lock expires | Retry after lock but **before** lock-duration timeout | Status guard (`Queued/Processing → Processing`) + results written in a transaction. If lock expires, another instance takes over safely. |
| **F6** | **Retryable failure escaping retryable exception type** | Nothing retries | **Batch stuck silently** | Recovery worker: `Processing` older than threshold → re-enqueue or mark Failed with diagnostics. Backstop for F1–F5. |
| **F7** | **Poison message (unparseable, wrong schema version)** | Repeats forever | Queue clogging, throttling | `MaxDeliveryCount=5` → **auto-dead-letter**. DLQ length is a monitored metric with an alert. |
| **F8** | **Service Bus unavailable at publish time** | Dispatcher throws | Outbox rows accumulate | Retry with exponential backoff + jitter. Rows persist in SQL, so burst recovery is automatic when Service Bus returns. |
| **F9** | **Service Bus unavailable at consume time** | Function can't receive | Queue depth grows | Messages are durable; they replay on reconnect. Alert on queue depth > threshold. |
| **F10** | **SQL unavailable during processing** | Function transaction fails | Retryable → DLQ after max | Transient-fault retry (Polly/exponential) for connection + commands. Distinguish `SqlException` transient vs. permanent. |
| **F11** | **Blob deleted/lease held during processing** | Download 404/412 | Processing failure | Not transient → mark Failed with reason, no pointless retries. |
| **F12** | **Duplicate upload of same file** | Two batches | Duplicate processing | Acceptable: each upload is a distinct `PayrollBatchId` by design. Optionally dedupe by content hash. |
| **F13** | **Message published with DB rollback** | State says Received, message exists | Function acts on non-existent batch | Function validates batch exists before processing; otherwise abandons. |
| **F14** | **Cost runaway** | Unbounded queue / poison requeue loop | Budget burn | `MaxDeliveryCount`, message TTL, budget alert, alert on queue depth and on repeated processing failures. |

### Which failures need an outbox, and which don't

**You asked whether the outbox is necessary for v1.** Honest engineering answer: **not strictly for
v1, but strongly recommended, and it is the correct learning artifact.**

- **Without outbox:** `INSERT batch` (commit) → `SendMessage` (network call).
  If the process dies between them, you have a row permanently marked `Queued` with no message —
  which **your requirement explicitly forbids**: *"a database update or message-publishing failure
  must not silently leave a payroll batch stuck in an inconsistent state."*
  The recovery worker can also plug this, but it's an after-the-fact repair.
- **With outbox:** both happen in **one ACID transaction**. Crash → row undispatched → dispatcher
  republishes. Cost: one extra table, a dispatcher tick, and idempotent consumption.
- **Why it's genuinely worth building:** it's the canonical answer to dual-write, it appears
  constantly in production interviews, and it forces you to confront F4 (at-least-once).

A background recovery worker alone *could* cover v1 — that's the honest trade-off. But it detects
corruption after the fact rather than preventing it.

### Retry cadence to implement

```
Function (SB trigger):  MaxRetryCount 5, exponential backoff handled by host
Dispatcher publish:     3 attempts, 100ms → 400ms → 1.6s, full jitter
SQL transient:          3 attempts, Polly, 2^n * 500ms ± jitter
Recovery scan:          every 60s, threshold 5 min, hard ceiling 10 attempts then Failed
DLQ:                    never auto-retried; manual replay after root cause fix
```

---

## 7. Deliberate design decisions

### 7.1 Upload through the API **vs** short-lived SAS URL (direct browser → Blob)

| | **Option A — upload via API** *(v1 choice)* | **Option B — SAS URL direct upload** |
|---|---|---|
| Path | Browser → API → Blob | Browser → Blob directly, then notifies API |
| API bandwidth | **Full payload passes through the API** — memory and connection pressure | Low; only metadata crosses the API |
| Large files | Streams OK, but App Service F1 is memory-constrained | Scales well |
| Validation | Server validates **before** bytes are stored | Client uploads first; API rejects afterwards → **orphan blob** cleanup needed |
| Authorization hook | Easy seam (`[Authorize]` later) | Requires staking token or a scoped SAS + POST callback contract |
| Credentials in browser | None | A container-scoped SAS appears in the browser — must be genuinely short-lived, bounded to one blob path |
| Complexity | **Low** | Higher (generate SAS, trust boundary, orphan cleanup) |
| Failure mode | Fail fast, nothing stored | Blob written but record never created → orphan garbage |

**Decision: Option A for v1.** It is the simplest correct implementation — bytes never reach Blob
without server-side validation, and there is no orphan-blob cleanup path.

**How it evolves:** when files grow or App Service throughput becomes a constraint, move to
**Option B** with a *single-use, path-scoped SAS* (container-scoped is too broad), short TTL
(5 min), and a required `POST /api/batches/{id}/complete` confirm that creates the DB row only
after upload. The orphan-cleanup becomes an Event Grid `BlobCreated` handler or a scheduled sweeper.
This teaching load-bearing: SAS + Azure Blob container-level access + ExpiryTime/SignedPermissions/
SignedResource are exactly the constructs you need to understand.

### 7.2 Correlation as cross-cutting, not bolted on

- `CorrelationId` generated at HTTP ingress, propagated to Service Bus `CorrelationId`/`ApplicationProperties`
  and to Function `Activity_traceId`
- Appears in **every log line, DB row, blob metadata, and dashboard response**
- This is precisely what makes end-to-end failure investigation possible in Phase 6

### 7.3 Idempotent consumption — the two mechanisms that make it true

1. **Status guard:** `UPDATE payroll_batches SET Status='Processing' WHERE Id=@id AND Status IN ('Queued','Received')` — affects 0 rows if a second delivery arrives.
2. **Unique constraint:** `UNIQUE(BatchId, EmployeeId)` on result rows — makes a duplicate insert fail loudly rather than corrupt silently.

Neither is optional. Together they satisfy the "prevent duplicate processing from corrupting results" requirement.

### 7.4 BFF — **not needed**

**Rejected for this project.** A Backend-for-Frontend exists when you need session/token handling,
BFF-bound tokens, per-client aggregation, or SPA-specific caching. This is a **single SPA + single
API** with a shared session model. A standard ASP.NET Core Web API (mounted at `/api`) is correct.

**When it would become worth revisiting:** if you introduced a mobile client with different
aggregation needs, or adopted **OAuth 2.0 BFF with token binding** in the security phase (a strong
pattern). Add it then, not now — it would be architectural ceremony with no payoff today.

### 7.5 Rejected: Blob Storage → Event Grid → Function

An equally valid design: use **BlobStorage ADLS Gen2 → Event Grid**, triggering the Function on
blob create instead of a Service Bus message.

*Why I'd not choose it for v1:*
- A blob create event fires on **any** write — you'd need to distinguish "file ready" from "partial upload", typically via contiguity or a commit marker.
- **Service Bus gives you a DLQ, delivery count, lock duration, and backpressure** much more naturally for payroll correctness requirements.

**The rounded system:** use **both**, deliberately — Service Bus as the durable job queue
(correctness), Event Grid as an **optional** trigger path in a later phase. Never treat Blob create
as an implicit message.

---

## 8. Document map

| File | Contents |
|---|---|
| `docs/PHASE_ZERO.md` | Azure account, region evidence, budgets, idle costs, cleanup |
| `docs/ARCHITECTURE.md` | *(this file)* diagrams, inventory, responsibilities, failure matrix |
| `docs/PLAN.md` | Build phases, each with tasks and acceptance criteria |