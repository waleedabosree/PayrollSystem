# Phase Zero — Build a Free / Low-Cost Azure Environment

> Status: verified **29 October 2026** against Microsoft Learn, the Azure Retail Prices API
> (`prices.azure.com/api/retail/prices`), and Microsoft's Azure for Students offer terms.

---

## 1. The offer you have: Azure for Students

You have an **Azure for Students** subscription. This is *not* the standard Azure free account.
The difference matters for budgeting, so here are the verified terms:

| Property | Azure for Students | (Standard Azure free account, for comparison) |
|---|---|---|
| Credit | **$100 USD** | $200 USD |
| Credit window | **12 months** | 30 days |
| Credit card | **Not required** | Required for identity verification |
| Renewable | **Yes**, annually while you verify student status | No (one-time) |
| Renewal | **Manual**, ~30 days before expiry, gives a fresh $100 + 12 months | N/A |
| Sign-up requirement | Full-time student at accredited institution, 18+, school email | New Microsoft customer |
| Marketplace spend | **Credit cannot be used** on third-party Marketplace items | Same |

**Rules you must internalise:**

1. **The $100 is a pool, not a monthly allowance.** It depletes as you spend. Unused credit does
   not roll over into the next *annual* period.
2. **Free monthly quotas are separate from the $100.** 20+ services have free monthly quantities
   for the first 12 months; 65+ services are always free. Exceeding a free quota draws from the $100.
3. **There is no card to overcharge.** When credit or the 12-month term ends, Microsoft **disables
   the subscription**. You are never billed. This is the single most important safety property for
   this project — a runaway cost takes your resources offline, it does not create a bill.
4. **After the term ends you have 90 days** to upgrade to pay-as-you-go or the resources are
   decommissioned and you lose them.
5. **Check remaining credit** in the [Azure Sponsorships portal](https://www.microsoftazuresponsorships.com/).
6. **Renewal is manual**, not automatic. Microsoft emails you ~30 days out.

---

## 2. Before you build — local toolchain hardening

Your machine was checked. **Three things need attention.**

### 🔴 Critical: .NET 9 reaches end of support in 12 days

Your machine has **only** `9.0.201` installed. From Microsoft's .NET support policy (last updated
25 Oct 2026):

| Version | Type | Support phase | End of support |
|---|---|---|---|
| .NET 10 | **LTS** | Active | **14 Nov 2028** |
| .NET 9 | STS | Maintenance | **10 Nov 2026** ← 12 days away |
| .NET 8 | LTS | Maintenance | **10 Nov 2026** |

A payroll project you build now should not start life on an out-of-support runtime. **Install the
.NET 10 SDK** before scaffolding anything:

```powershell
winget install Microsoft.DotNet.SDK.10
dotnet --list-sdks   # expect 10.0.xxx and 9.0.201 side by side
```

.NET 10 is LTS until **November 2028** — the whole project lifespan fits comfortably inside it.

### 🟡 Missing tools

| Tool | Needed for | Install |
|---|---|---|
| Azure CLI | Bicep deployment, resource management | `winget install Microsoft.AzureCLI` |
| Azure Functions Core Tools v4 | Local Function runtime | `npm i -g azure-functions-core-tools@4 --unsafe-perm true` |
| Azurite | Local Blob/Queue/Table emulator | `npm i -g azurite` |
| Azure PowerShell (`Az`) | Optional, Bicep/CI scripting | `Install-Module Az -Scope CurrentUser` |

### ✅ Already present

- .NET SDK (needs upgrade to 10), Node **22.19**, npm **11.11**, Angular CLI (global), **Docker 28.0.4**

> **Node note:** Node 22 is in maintenance. Angular's current release line targets Node 22+ so this
> works, but consider `nvm-windows` with Node 24 (current LTS) if you hit engine warnings.

---

## 3. Local development stack (local-first, deploy last)

You chose **local-first**, which is correct: it makes iteration instant and holds cloud spend near
zero while the architecture is still moving.

| Cloud service | Local substitute | Notes |
|---|---|---|
| Azure Blob Storage | **Azurite** | Microsoft's official emulator. Wire-compatible with the Azure SDK. |
| Azure SQL Database | **`mcr.microsoft.com/mssql/server:2022-latest`** in Docker | Real SQL Server — no dialect surprises. |
| Azure Service Bus | **Official Service Bus emulator** (v2.0.0, Jan 2026) | Two containers: emulator + SQL Server dependency. Connection string ends `UseDevelopmentEmulator=true`. |
| Azure Functions | **Functions Core Tools v4** | Runs the real host locally. |
| Azure Cache for Redis | **`redis:alpine`** | Only if we reach Phase 5. |
| Application Insights | **Local Application Insights / OpenTelemetry console** | Full fidelity needs real Azure — deferred to the deployment phase. |

**Service Bus emulator facts you need:**
- Image: `mcr.microsoft.com/azure-messaging/servicebus-emulator`
- Entities are declared in a mounted `Config.json` — **changes require a container restart**
- The preset namespace `name` **cannot be renamed**
- Dev/test only, no Microsoft support
- Health endpoint: `http://localhost:5300/health`

> ⚠️ **Deficiency to plan for:** the emulator does not reproduce every cloud behaviour — notably
> auto-forwarding, some geo-replication semantics, and real partitioning/throughput behaviour.
> Anything about *scale* or *failover* must be validated against real Azure in the deployment phase.

---

## 4. Choosing a region — evidence, not guesswork

Azure prices by region. I queried the **Azure Retail Prices API directly** across 12 candidate
regions for the exact SKUs this project needs. Results (retail, USD):

| Region | App Service **F1** | App Service **B1 Linux** | **SQL Basic DTU** | SQL S0 (10 DTU) | Svc Bus **Basic** | Svc Bus Standard base |
|---|---|---|---|---|---|---|
| **East US** | **$0** | **$12.65/mo** | **$4.89/mo** | $14.71/mo | $0.05/1M ops | $10/mo |
| East US 2 | $0 | $12.65/mo | $4.89/mo | $14.71/mo | $0.05/1M ops | $10/mo |
| West US 3 | $0 | $12.65/mo | $4.89/mo | $14.71/mo | $0.05/1M ops | n/a |
| West Europe | $0 | $13.39/mo | $4.89/mo | $14.71/mo | $0.05/1M ops | $10/mo |
| North Europe | $0 | $13.39/mo | $4.89/mo | $14.71/mo | $0.05/1M ops | $10/mo |
| Central US | $0 | $13.39/mo | $4.89/mo | $14.71/mo | $0.05/1M ops | $10/mo |
| Sweden Central | $0 | $13.39/mo | $4.89/mo | $14.71/mo | $0.05/1M ops | n/a |
| Canada East | $0 | $13.39/mo | **$5.38/mo** | $16.18/mo | $0.05/1M ops | $10/mo |
| **UK South** | $0 | $13.39/mo | **$6.11/mo** | $18.39/mo | $0.05/1M ops | $10/mo |
| **France Central** | $0 | $13.39/mo | **$6.11/mo** | $14.59/mo | $0.05/1M ops | $10/mo |
| Germany West Central | **n/a** ❌ | $13.41/mo | $4.89/mo | $14.71/mo | $0.05/1M ops | $10/mo |

### Recommendation: **East US (`eastus`)**

Reasons, in order:

1. **Cheapest SQL** — tied at $4.89/mo, and SQL is the dominant recurring cost of this project.
2. **Cheapest B1** — $12.65/mo vs $13.39 elsewhere (~6% less).
3. **Cheapest or tied across every other meter** measured.
4. **Full SKUs available** — note Germany West Central is missing the **F1** free App Service tier;
   East US has everything this project needs.
5. It is effectively Azure's **pricing baseline** region and has the broadest service availability,
   so the Bicep templates you write will deploy there without SKU surprises.

**What I'd avoid:** UK South and France Central (SQL ~25% more expensive), Canada East (~10% more).

> 📌 **Caveat on serverless SQL:** the API returns multiple pricing tiers per meter, and the first
> row for serverless was the *free allowance tier* ($0). The effective compute rate appears around
> **$0.52/hr** in East US but I could not cleanly separate vCore count from the meter. **Do not**
> pick serverless on my number alone — compute it in the pricing calculator during the deployment
> phase. Serverless with auto-pause only wins if the database idles a lot; for an always-on dev DB,
> **Basic DTU at $4.89/mo is the predictable choice.**

### Availability verification checklist

Before creating anything, confirm in the portal that `eastus` offers:

- [ ] App Service plans — **Free (F1)** must be listed
- [ ] Function App — **Consumption (Y1)** plan
- [ ] Storage account (v2, GRS or LRS)
- [ ] Service Bus namespace — **Basic** tier
- [ ] Azure SQL Database — **Basic (DTU)** tier
- [ ] Application Insights — **free tier**
- [ ] Monitor workspace / Log Analytics

Run from Azure CLI once installed:

```powershell
az provider show --namespace Microsoft.Web            --query "{state:registrationState,locations:resourceProvider[0].resourceTypes[?contains(name,'workSites')].locations}" -o json
az provider show --namespace Microsoft.ServiceBus     --query registrationState -o tsv
az provider show --namespace Microsoft.Sql             --query registrationState -o tsv
az provider show --namespace Microsoft.Storage         --query registrationState -o tsv
az provider show --namespace Microsoft.Insights        --query registrationState -o tsv
az account list-locations --query "[?name=='eastus'].{name:name, displayName:displayName}" -o json
```

All should return `Registered`. Register with `az provider register --namespace <ns>` if not.

---

## 5. Resource group, budget, and cost alerts

### Naming convention

```
rg-payroll-dev-001          (resource group, region eastus)
payroll-dev-b001            (prefix for resource names → globally unique: payroll-dev-b001.blob.core.windows.net)
```

### Create the foundation

```powershell
# Login (device code flow works well over SSH/remote shells)
az login

# Verify you're on the student subscription before creating anything
az account show --query "{name:name, id:id, state:state, isDefault:isDefault}" -o json
az account list --query "[].{name:name, id:id, state:state}" -o table

# Create the resource group — free to create
az group create --name rg-payroll-dev-001 --location eastus --tags project=payroll env=dev owner=wabosrea
```

### Budget and alerts

> ⚠️ **Azure for Students has no credit card**, and *Student subscriptions historically cannot have
> a budget created through the normal Cost Management blade* in the same way as pay-as-you-go.
> Verify this first — if the budget blade is unavailable, your compensating controls are
> **(a)** the hard credit ceiling that disables the subscription, and **(b)** the daily cost alert
> / cost analysis views. Document whichever path actually works in this subscription.

```powershell
# Attempt a budget (verify support in your subscription)
az consumption budget create \
  --name budget-payroll-monthly \
  --amount 15 \
  --category Cost \
  --time-grain Monthly \
  --start-date 2026-11-01 \
  --end-date 2027-10-31 \
  --resource-group rg-payroll-dev-001
```

Calibration for the budget: your `$100 / 12 months` envelope is **~$8.33/month average**.
A **$15/month** budget is a sensible ceiling that still allows a heavy month.

### Cost analysis commands

```powershell
az consumption usage list --start-date 2026-10-01 --end-date 2026-10-31 -o table
az costmanagement query --type ActualCost \
  --timeframe "MonthToDate" \
  --dataset aggregation '{"totalCost":{"name":"PreTaxCost","function":"Sum"}}' \
  --grouping '[{"type":"Dimension","name":"ResourceGroupName"}]'
```

---

## 6. What will cost money *while idle*

This is the question that actually burns student budgets. Tiered by risk:

### 🔴 Bill continuously from the moment it exists

| Resource | Idle cost | Mitigation |
|---|---|---|
| **App Service plan B1/Basic and above** | **$12.65/mo** — charged whether or not an app is running | **Use the F1 Free tier** ($0) in dev. Acknowledge F1 limits below. |
| **Azure SQL Basic DTU** | **$4.89/mo** — always-on compute | Largest lever. See options below. |
| **Service Bus Standard** | **$10/mo** base unit | **Use Basic** ($0.05/1M ops). Standard is only needed for topics/sessions/scheduled enqueue. |
| **Log Analytics beyond free tier** | Per GB ingested | Set a daily cap; Application Insights free tier is generous for dev volume. |

### 🟡 Metered but trivial at this scale

| Resource | Idle cost |
|---|---|
| Storage account + Blob | ~2.9¢/GB/mo (LRS) — a few pennies |
| Service Bus **Basic** | ~$0.05/mo at student volumes |
| Azure Functions **Consumption** | $0 within the free execution grant, then per-execution |
| Application Insights | $0 within free-tier ingestion |

### 🟢 Always free

Static Web Apps, App Service F1 tier, most Azure free-tooling, Event Grid free grant.

### The honest monthly totals

| Scenario | Monthly | 12-month | Within $100? |
|---|---|---|---|
| **A — Fully free/cheap ($0 compute)** F1 + Functions Consumption + Basic Service Bus + Blob | **~$0.10** | ~$1.20 | ✅ trivially |
| **B — Scenario A + Azure SQL Basic** | **~$5.00** | ~$60 | ✅ |
| **C — Scenario B + App Service B1** | **~$17.65** | ~$212 | ❌ **over budget** |
| **D — Everything (B1 + SQL + Service Bus Standard)** | **~$30** | ~$360 | ❌ badly over |

**Controlled recommendation: deploy at Scenario B ≈ $5/month**, ~$60 over the year, leaving ~$40
headroom on a $100 budget for storage, Function executions, and Application Insights.

**Scenario C/D are why budget alerts exist.** Service Bus Standard ($10) and B1 ($12.65) are the two
individual line items most likely to silently eat your budget.

### Reducing or eliminating the SQL cost

1. **Keep SQL local (Docker) permanently** — `$0`. You lose the "real Azure SQL" experience but
   keep EF Core identical. Defensible for a learning project.
2. **Serverless SQL with auto-pause** — drops to storage-only when idle. *Best fit for a patchy
   student workload*, but confirm real rates in the calculator first.
3. **Basic DTU $4.89/mo** — predictable, always-on, my default recommendation.

---

## 7. Deleting resources — you must do this deliberately

**Deleting a resource group deletes everything inside it.** This is a blunt but effective tool.

```powershell
# Preview first — always
az group show --name rg-payroll-dev-001 --query "{name:name, location:location}" -o json
az resource list --resource-group rg-payroll-dev-001 \
  --query "[].{name:name, type:type, location:location}" -o table

# Then delete
az group delete --name rg-payroll-dev-001 --yes --no-wait
```

**Two deletion traps specific to this subscription:**

1. **`--no-wait` returns before deletion finishes.** You may believe resources are gone while they
   are still billing. Drop the flag when you need certainty.
2. **Some resources are locked to the subscription period.** When the 12-month term ends without
   renewal, resources are decommissioned automatically — but do **not** rely on that. Delete
   deliberately at the end of each phase.

**Resources that are effectively free to leave running:** resource groups, storage accounts with no
data, F1 App Service plans, Consumption Function Apps, Basic Service Bus namespaces.

**Never leave running if you're stepping away:** B1+ App Service plans, Azure SQL, Service Bus
Standard, Premium anything.

---

## 8. Phase Zero checklist

- [ ] Install **.NET 10 SDK** (9 goes EOL 10 Nov 2026)
- [ ] Install Azure CLI, Functions Core Tools v4, Azurite
- [ ] Confirm `az account show` targets your **Azure for Students** subscription
- [ ] Record remaining credit in the Sponsorships portal
- [ ] Verify `eastus` offers every required SKU (checklist in §4)
- [ ] Register required resource providers
- [ ] Create `rg-payroll-dev-001` in `eastus` with tags
- [ ] Create budget at **$15/mo** *or* confirm it's unsupported → document fallback controls
- [ ] Write a one-page `COSTS.md` recording your committed scenario (B is recommended)
- [ ] Set a calendar reminder for **subscription renewal** (~30 days before 12-month expiry)
- [ ] Build and verify the local stack: Azurite + SQL container + Service Bus emulator

---

## 9. Sources

- [Azure for Students offer details](https://azure.microsoft.com/pricing/offers/ms-azr-0170p/)
- [Azure Education Hub FAQ](https://learn.microsoft.com/azure/education-hub/faq)
- [Azure free services](https://azure.microsoft.com/pricing/free-services/)
- [Understanding free charges / avoid charges](https://learn.microsoft.com/azure/cost-management-billing/manage/avoid-charges-free-account)
- [.NET support policy](https://dotnet.microsoft.com/platform/support/policy)
- [Service Bus emulator](https://learn.microsoft.com/azure/service-bus-messaging/overview-emulator)
- Azure Retail Prices API (`https://prices.azure.com/api/retail/prices`) — queried 29 Oct 2026