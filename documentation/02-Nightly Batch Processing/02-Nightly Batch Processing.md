# Submodules of Module: Nightly Batch Processing

## Reference
**Source Document**: `/documentation/modules/01-modulos-plataforma.md` - Section 5
**Analysis Date**: 2026-08-10
**Parent Module**: Nightly Batch Processing

## Summary
Total submodules identified: **4**

### Distribution by Type
- Backend Components: 3
- Frontend Components: 1
- Data Layer: 0
- API/Services: 0
- Configuration: 0
- Utilities: 0

## Submodule Index
1. [Batch Engine - Timer and Concurrency Control](#batch-engine---timer-and-concurrency-control) - Backend Component
2. [Risk Score Recalculation and Auto-Block Logic](#risk-score-recalculation-and-auto-block-logic) - Backend Component
3. [Batch Audit and State Update](#batch-audit-and-state-update) - Backend Component
4. [Batch Monitoring and Manual Trigger UI](#batch-monitoring-and-manual-trigger-ui) - Frontend Component

---

## Submodule Detail

### Batch Engine - Timer and Concurrency Control

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/NightlyBatch.cs` — static class `NightlyBatch`; fields `_timer` and `_running` (lines 8–9); methods `Start` (lines 11–18), `Stop` (lines 20–24), `Tick` (lines 81–84)

**Purpose**: Manages the lifecycle of the background `System.Threading.Timer` that drives periodic batch execution. Reads the interval from `web.config` (`BatchIntervalSeconds`, minimum 10 s), enforces a single-instance constraint using `Interlocked.Exchange` on `_running` to prevent concurrent executions, and exposes `Start`/`Stop` entry points called by `Global.asax` at application boot and shutdown.

**Key Files**:
1. `NightlyBatch.cs` — entire static class; `Start`, `Stop`, and `Tick` methods plus the two private fields
2. `web.config` — supplies `BatchIntervalSeconds`; default falls back to 45 s when the key is absent or below 10

**Dependencies**:
- **External Modules**: Application Bootstrap and Global Lifecycle — `Global.asax` calls `NightlyBatch.Start()` on `Application_Start` and `NightlyBatch.Stop()` on `Application_End`
- **External Modules**: Infrastructure, Configuration and Scripts — `web.config` key `BatchIntervalSeconds`
- **Internal Submodules**: Risk Score Recalculation and Auto-Block Logic — `Tick` delegates to `RunNow`
- **Libraries**: `System.Threading` (`Timer`, `TimerCallback`, `Interlocked`), `System.Configuration` (`ConfigurationManager`)

**Related Components**:
- **Entities**: None directly
- **Services**: None beyond the internal call to `RunNow`
- **APIs**: None
- **UI**: `Batch.aspx` — displays configured interval note ("every 15 seconds") and can trigger `RunNow` manually

**Integration Points**:
- `Start()` called once per application lifetime from `Global.asax:Application_Start`
- `Stop()` called from `Global.asax:Application_End` to dispose the timer cleanly
- Re-entrant protection: if `_running == 1` when `RunNow` is entered (e.g. a slow tick overlapping the next), the method returns immediately with message `"Batch already running"`

---

### Risk Score Recalculation and Auto-Block Logic

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/NightlyBatch.cs` — inside `NightlyBatch.RunNow`, lines 31–61

**Purpose**: Core business logic executed on each batch tick. Iterates over every `Customers` row in a freshly loaded `DataSet`, applies a three-tier additive adjustment to `RiskScore` based on `OverdueDays` and `OutstandingDebt/CreditLimit` ratio, clamps the result to [0, 100], and permanently blocks any customer whose `OverdueDays` reaches or exceeds 90. The batch never reverses a block — customers can only be blocked, never unblocked, by this process.

**Key Files**:
1. `NightlyBatch.cs` — `RunNow` method, lines 27–79 (the recalculation loop spans lines 35–61)

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer — `LegacyRepository.Load()` (line 31) provides the full `DataSet`; `LegacyRepository.Save(ds)` (line 64) persists all changes
- **External Modules**: Global In-Memory State — `LegacyState.SetFlag(customerId, "AUTO_BLOCKED")` (line 58) records the auto-block flag in process memory
- **Internal Submodules**: Batch Engine - Timer and Concurrency Control — this logic runs inside `RunNow`, which is entered via `Tick` or the manual trigger
- **Libraries**: `System` (`Convert`, `DateTime`)

**Related Components**:
- **Entities**: `Customers` table — columns `OverdueDays`, `OutstandingDebt`, `CreditLimit`, `RiskScore`, `Blocked`, `Id` are read and/or mutated
- **Services**: None
- **APIs**: None
- **UI**: None directly; results become visible on `Batch.aspx` (last message) and `Customers.aspx` (blocked status)

**Integration Points**:
- Reads the full customer dataset anew on every tick via `LegacyRepository.Load()` — no caching
- Writes mutated rows back via `LegacyRepository.Save(ds)` at the end of the same `RunNow` call
- Auto-block is one-way: `if (!blocked && overdue >= 90)` (line 55) means already-blocked customers are skipped, and no unblock path exists in this module
- The `AUTO_BLOCKED` flag is written to `LegacyState` (volatile, not persisted) as a runtime signal readable by `Batch.aspx`

**Risk score adjustment rules (documented here as the sole authoritative location)**:

| Condition | Adjustment |
|---|---|
| `OverdueDays > 60` | `+9` |
| `OverdueDays > 30` | `+4` |
| `OverdueDays == 0` AND `debt < limit × 0.25` | `−2` |
| `OutstandingDebt > CreditLimit` | `+7` |
| Clamped to `[0, 100]` | — |

---

### Batch Audit and State Update

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/NightlyBatch.cs` — inside `NightlyBatch.RunNow`, lines 63–68 (success path) and lines 71–73 (error path)

**Purpose**: After the recalculation loop completes, writes an audit record to the `Audit` table documenting the number of customers processed and the number of records changed. Updates the three process-scoped counters in `LegacyState` (`LastBatchRun`, `BatchExecutions`, `LastBatchMessage`). On exception, captures the error message into `LastBatchMessage` without re-throwing, ensuring the timer continues running regardless of individual tick failures.

**Key Files**:
1. `NightlyBatch.cs` — `RunNow` method, lines 63–78 (the `try/catch/finally` conclusion)

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer — `LegacyRepository.AddAudit(ds, "BATCH", "CREDIT_RECALC", detail)` (line 63) appends the audit row; `LegacyRepository.Save(ds)` (line 64) commits all pending changes including the audit row
- **External Modules**: Global In-Memory State — `LegacyState.LastBatchRun`, `LegacyState.BatchExecutions`, `LegacyState.LastBatchMessage` (lines 65–67)
- **Internal Submodules**: Risk Score Recalculation and Auto-Block Logic — produces the `changed` counter consumed in the audit detail string
- **Libraries**: `System` (`DateTime.Now`, `Exception`)

**Related Components**:
- **Entities**: `Audit` table — one row inserted per batch run with type `BATCH`, subject `CREDIT_RECALC`, detail `"Processed=N; Changed=M"`
- **Services**: None
- **APIs**: None
- **UI**: `Batch.aspx` — reads `LegacyState.LastBatchRun`, `LegacyState.BatchExecutions`, and `LegacyState.LastBatchMessage` to populate the status display

**Integration Points**:
- The `finally` block (line 76) unconditionally resets `_running` to `0` via `Interlocked.Exchange`, releasing the concurrency lock even if an exception occurred
- Errors are swallowed at this layer: the exception message is stored in `LastBatchMessage` but is not propagated, so the background timer never terminates due to a business-logic error
- `BatchExecutions` is incremented only on the success path (line 66), so the counter reflects successful runs only

---

### Batch Monitoring and Manual Trigger UI

**Type**: Frontend Component

**Location**:
- **Frontend**: `/Batch.aspx` — ASP.NET markup (lines 1–11)
- **Backend**: `/Batch.aspx.cs` — code-behind, class `BatchPage` (lines 1–40)

**Purpose**: Web Forms page that provides operators with real-time visibility into the batch process and the ability to force an immediate execution. Displays the timestamp of the last run, the cumulative execution count, the last result message, and a table of all runtime customer flags currently held in `LegacyState.CustomerFlags`. Auto-refreshes every 10 seconds via a client-side `setTimeout` redirect. The "Run batch now" button invokes `NightlyBatch.RunNow()` synchronously in the HTTP request thread.

**Key Files**:
1. `Batch.aspx` — markup; defines `LastRun` Label, `Executions` Label, `BatchMessage` Label, `Run` Button, `Flags` Literal controls; inline `<script>` for the auto-refresh
2. `Batch.aspx.cs` — code-behind; `Page_Load` calls `Refresh()` on first load; `Run_Click` calls `NightlyBatch.RunNow()` then `Refresh()`; `Refresh()` reads `LegacyState` and builds the flags HTML table

**Dependencies**:
- **External Modules**: Global In-Memory State — `LegacyState.LastBatchRun`, `LegacyState.BatchExecutions`, `LegacyState.LastBatchMessage`, `LegacyState.CustomerFlags`, `LegacyState.SyncRoot` (all read in `Refresh()`)
- **Internal Submodules**: Batch Engine - Timer and Concurrency Control — `NightlyBatch.RunNow()` called from `Run_Click`
- **Libraries**: `System.Collections` (`DictionaryEntry`), `System.Text` (`StringBuilder`), `System.Web.UI.WebControls`, `System`

**Related Components**:
- **Entities**: None read directly; audit records written indirectly via `RunNow`
- **Services**: `NightlyBatch.RunNow` (invoked on button click)
- **APIs**: None
- **UI**: `Site.master` (master page layout and navigation); `Content/site.css` (CSS class `btn`, `grid`, `box`)

**Integration Points**:
- The auto-refresh JavaScript (`setTimeout(window.location.reload, 10000)`) runs unconditionally in the browser — there is no server-push or SignalR equivalent
- The `CustomerFlags` table is read inside `lock (LegacyState.SyncRoot)` (line 28) to prevent torn reads during concurrent batch writes
- Manually triggering `RunNow` from this page is synchronous: the HTTP response is held open until `RunNow` completes, which can take several seconds on large datasets

---

## Relationship Map

### Data Flow

```text
[Application_Start]
      |
      v NightlyBatch.Start()
[Batch Engine - Timer]  ──── reads BatchIntervalSeconds ──── web.config
      |
      | every N seconds (or on manual trigger from Batch.aspx)
      v NightlyBatch.RunNow()
[Risk Score Recalculation and Auto-Block Logic]
      |
      |── LegacyRepository.Load()  ──────────────── SQLite (Customers)
      |── mutate RiskScore / Blocked in DataSet
      |── LegacyState.SetFlag("AUTO_BLOCKED")  ──── in-memory (LegacyState)
      |
      v
[Batch Audit and State Update]
      |── LegacyRepository.AddAudit(ds, "BATCH", "CREDIT_RECALC", ...) 
      |── LegacyRepository.Save(ds)  ────────────── SQLite (Customers + Audit)
      |── LegacyState.LastBatchRun / BatchExecutions / LastBatchMessage
      |
      v
[Batch Monitoring and Manual Trigger UI]  (Batch.aspx)
      |── reads LegacyState fields on every page load / 10 s auto-refresh
      +── "Run batch now" button → NightlyBatch.RunNow() (synchronous call)
```

### Call Hierarchy
- `Global.asax:Application_Start` calls `NightlyBatch.Start`
- `Global.asax:Application_End` calls `NightlyBatch.Stop`
- `NightlyBatch.Tick` (timer callback) calls `NightlyBatch.RunNow`
- `BatchPage.Run_Click` calls `NightlyBatch.RunNow`
- `NightlyBatch.RunNow` calls `LegacyRepository.Load`
- `NightlyBatch.RunNow` calls `LegacyState.SetFlag`
- `NightlyBatch.RunNow` calls `LegacyRepository.AddAudit`
- `NightlyBatch.RunNow` calls `LegacyRepository.Save`
- `BatchPage.Refresh` reads `LegacyState.LastBatchRun`, `BatchExecutions`, `LastBatchMessage`, `CustomerFlags`

### UI-Backend Mapping
| UI Component | Backend Service | DB Table |
|---|---|---|
| `Batch.aspx` (auto-refresh) | `LegacyState` (read-only) | None (in-memory only) |
| `Batch.aspx` (Run batch now button) | `NightlyBatch.RunNow` | `Customers` (update), `Audit` (insert) |
| `Batch.aspx` (Runtime flags table) | `LegacyState.CustomerFlags` | None (in-memory only) |

---

## Completeness Verification

### File Statistics
- Total backend files in module directory (`/App_Code/NightlyBatch.cs`): 1 — Documented: 1
- Total frontend files (`Batch.aspx`, `Batch.aspx.cs`): 2 — Documented: 2
- Total JavaScript files: 0 (inline `<script>` in `Batch.aspx` is part of the UI submodule) — Documented: 0
- Total CSS files: 0 — Documented: 0 (CSS lives in `Content/site.css`, belongs to Web UI module)
- Total SQL scripts: 0 — Documented: 0
- **Coverage**: 100%

### Directories Analyzed
- [x] `/App_Code/` — 1 file (`NightlyBatch.cs`)
- [x] `/` (root) — 2 files (`Batch.aspx`, `Batch.aspx.cs`)

---

## Technical Notes
- `NightlyBatch` is a static class with no instance or dependency injection. `LegacyRepository` and `LegacyState` are called directly by class name, making the batch logic tightly coupled to the concrete implementations and untestable in isolation without shim techniques.
- The `_running` flag is an `int` used as a boolean via `Interlocked.Exchange`. This is a classic lightweight lock pattern; it is correct but less readable than `Monitor` or `SemaphoreSlim`. A migration should replace this with a proper `CancellationToken`-based approach.
- `LegacyRepository.Load()` is called on every tick, loading the full `DataSet` from SQLite each time. There is no dirty tracking or incremental query. On large datasets this could become a throughput bottleneck.
- Risk score decrements (`−2` for good standing) can only occur when `OverdueDays == 0` AND `debt < limit × 0.25`. All other conditions are additive. Over many ticks, customers with any overdue history will converge toward `RiskScore = 100` unless the underlying data improves.
- Auto-blocking is permanent within a process lifetime. The `Blocked` flag in SQLite is set to `true` and never cleared by this module. The only way to unblock a customer is through `Customers.aspx` (manual toggle via `ManualOverride`), which is a separate module.
- The inline `setTimeout` auto-refresh on `Batch.aspx` uses `window.location.reload()` rather than a partial-page update, causing a full GET on every 10-second cycle. During a long `RunNow` triggered by the button, the page response is blocked until completion.

---

## Complexity and Prioritization

### Submodule Complexity
- **High**: Risk Score Recalculation and Auto-Block Logic — the risk adjustment table encodes multiple overlapping conditions with precedence semantics (only one of the overdue branches fires); the one-way auto-block rule is a critical invariant; all business logic is inline with no unit tests
- **Medium**: Batch Audit and State Update — straightforward but the silent exception-swallowing and split between persistent audit (SQLite) and volatile state (`LegacyState`) is a migration concern
- **Medium**: Batch Monitoring and Manual Trigger UI — simple page, but the synchronous `RunNow` call on the HTTP thread and the full-reload auto-refresh are concurrency and UX issues relevant to a migration target design
- **Low**: Batch Engine - Timer and Concurrency Control — well-isolated lifecycle code; main migration consideration is replacing `System.Threading.Timer` with a hosted background service (`IHostedService`) in a modern .NET target

---

## Next Step
This document serves as the basis for **Prompt 3**, where each submodule will be analyzed in full detail to create the complete functional specification.
