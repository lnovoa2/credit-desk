# Functional Specification: Batch Engine - Timer and Concurrency Control

**Parent Module**: Nightly Batch Processing | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Manages the lifecycle of the background `System.Threading.Timer` that drives periodic batch execution. Provides the entry points for starting and stopping the timer, reads the execution interval from configuration, and enforces a single-instance constraint to prevent concurrent executions of the batch process.
- **Key Functionality**:
  1. Starts a periodic background timer on application boot, reading the interval from `web.config`.
  2. Enforces a minimum interval floor of 10 seconds regardless of configuration.
  3. Prevents re-entrant execution using a lock-free atomic flag (`Interlocked.Exchange`).
  4. Exposes `RunNow()` as the single execution entry point, callable both by the timer and by the manual trigger UI.
  5. Disposes the timer cleanly on application shutdown.
- **Users**: System (automated timer), Operations staff (via `Batch.aspx` manual trigger button).

---

## 2. Business Logic

### Key Processes

**Timer Startup**:
1. `Global.asax:Application_Start` calls `NightlyBatch.Start()` once at application boot.
2. `Start()` reads `BatchIntervalSeconds` from `web.config`; defaults to 45 s if the key is absent or unparseable; enforces a minimum of 10 s.
3. A `System.Threading.Timer` is created with both `dueTime` and `period` set to the resolved interval (in milliseconds), targeting the private `Tick` callback.
4. Guard `if (_timer != null) return` prevents double-initialisation on any accidental re-call.

**Periodic Execution (Tick)**:
1. At each interval the CLR thread-pool fires the `Tick` callback.
2. `Tick` unconditionally delegates to `RunNow()` — all execution logic lives there.
3. `RunNow()` checks `_running` via `Interlocked.Exchange(ref _running, 1)`. If the previous value was already `1`, it returns `"Batch already running"` immediately — no second execution is started.
4. On completion (success or error), the `finally` block resets `_running` to `0` via `Interlocked.Exchange`, releasing the concurrency slot.

**Timer Shutdown**:
1. `Global.asax:Application_End` calls `NightlyBatch.Stop()`.
2. `Stop()` disposes the `Timer` instance and sets `_timer` to `null`.
3. Any in-progress `RunNow` execution continues to completion independently — disposal of the timer only prevents future ticks.

### Critical Validations

| Field | Validation Rule | Behaviour |
|---|---|---|
| `BatchIntervalSeconds` | Must be parseable `Int32` and ≥ 10 | If missing/invalid: defaults to 45 s; if < 10: clamped to 10 s |
| `_timer` on `Start()` | Must be `null` | If already set, `Start()` returns without creating a second timer |
| `_running` on `RunNow()` | Must be `0` (CAS via `Interlocked.Exchange`) | If `1`, returns `"Batch already running"` — no nested execution |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|---|---|---|
| `NightlyBatch` (static class) | Background batch engine | Timer lifecycle (`Start`/`Stop`), concurrency guard, delegates all business logic to `RunNow` |
| `NightlyBatch.Start()` | Timer initialisation | Reads config interval, enforces floor, creates `System.Threading.Timer` |
| `NightlyBatch.Stop()` | Timer teardown | Disposes timer on application shutdown |
| `NightlyBatch.Tick()` | Timer callback | CLR thread-pool entry point; calls `RunNow()` |
| `NightlyBatch.RunNow()` | Unified execution entry | Atomic lock acquisition, invokes batch logic, releases lock in `finally` |

### Frontend

| Component | Purpose | Key Functionality |
|---|---|---|
| `Batch.aspx` / `Batch.aspx.cs` | Batch status monitor and manual trigger | "Run batch now" button calls `RunNow()` synchronously; displays execution status; auto-refreshes every 10 s |

### Data

| Entity | Purpose | Key Fields |
|---|---|---|
| `LegacyState` (in-memory) | Volatile process-scoped state | `LastBatchRun` (DateTime), `BatchExecutions` (int), `LastBatchMessage` (string), `CustomerFlags` (Hashtable) |

> **Note**: This submodule has no direct database entities. Database interaction belongs to the *Risk Score Recalculation* and *Batch Audit* submodules, which execute inside `RunNow`.

---

## 4. APIs and Endpoints

### Key Methods (Internal API)

| Visibility | Method | Purpose | Thread-safe |
|---|---|---|---|
| `public static` | `NightlyBatch.Start()` | Initialise and start the background timer | No (called once from `Application_Start`) |
| `public static` | `NightlyBatch.Stop()` | Dispose the background timer | No (called once from `Application_End`) |
| `public static string` | `NightlyBatch.RunNow()` | Execute one batch cycle; returns status message | Yes (atomic `_running` flag) |
| `private static void` | `NightlyBatch.Tick(object state)` | Timer callback; delegates to `RunNow()` | Yes (invoked by CLR thread pool) |

> There are no HTTP endpoints in this submodule. `Batch.aspx` invokes `RunNow()` as a direct method call within the HTTP request thread (ASP.NET Web Forms postback).

**RunNow() Return Values**:
```
// Concurrency guard triggered
"Batch already running"

// Successful execution
"Processed N customers; changed M"

// Exception path
"ERROR: <exception message>"
```

---

## 5. Data Flows

### Primary Flow: Periodic Timer Execution

```
CLR Thread Pool → Tick() → RunNow()
                              │
                    ┌─────────▼──────────┐
                    │ Interlocked.Exchange│
                    │  _running: 0 → 1   │
                    └─────────┬──────────┘
                              │ (if already 1: return immediately)
                              ▼
                    [Business Logic - RunNow]
                    (Risk recalculation + Audit — other submodules)
                              │
                    ┌─────────▼──────────┐
                    │ finally block:      │
                    │  _running: 1 → 0   │
                    └────────────────────┘
```

**Steps**:
1. CLR thread pool fires `Tick` at every interval.
2. `Tick` calls `RunNow`; atomic CAS prevents concurrent entry.
3. Business logic executes (delegated to inner submodules).
4. `finally` unconditionally releases the concurrency slot.
5. Return value (status string) is written to `LegacyState.LastBatchMessage`.

### Secondary Flow: Manual Trigger via UI

```
Operator → Batch.aspx "Run batch now" → HTTP POST
                                              │
                                    Run_Click handler
                                              │
                                    NightlyBatch.RunNow()  ← synchronous, blocks HTTP thread
                                              │
                                    Refresh() → reads LegacyState
                                              │
                                    HTTP Response returned to browser
```

**Steps**:
1. Operator clicks "Run batch now" on `Batch.aspx`.
2. ASP.NET fires `Run_Click` event handler in the HTTP request thread.
3. `RunNow()` executes synchronously — the HTTP response is held open until completion.
4. `Refresh()` re-reads `LegacyState` fields and updates the page controls.

### Error Handling

| Error Condition | Cause | Response |
|---|---|---|
| `_running == 1` on entry | Previous tick still executing (slow batch) | Return `"Batch already running"` immediately; timer tick is silently skipped |
| `Exception` inside `RunNow` | DB error, null reference, etc. | Caught in `catch`; message stored in `LegacyState.LastBatchMessage`; exception is **not** re-thrown; timer continues running |
| Double `Start()` call | Accidental re-entry | Guard `if (_timer != null) return` — second call is a no-op |
| `BatchIntervalSeconds` missing or < 10 | Misconfiguration | Interval defaults to 45 s or is clamped to 10 s |

---

## 6. Integrations

### Internal

- **Application Bootstrap (`Global.asax`)**: Calls `NightlyBatch.Start()` on `Application_Start` and `NightlyBatch.Stop()` on `Application_End` — data exchanged: none (method calls only).
- **Risk Score Recalculation and Auto-Block Logic** (inner submodule): Executed within `RunNow()`; this submodule only controls entry/exit and concurrency; business logic is fully delegated — data exchanged: control flow, `DataSet`, `changed` counter.
- **Batch Audit and State Update** (inner submodule): Also executed within `RunNow()` after the recalculation loop; writes audit record and updates `LegacyState` — data exchanged: `DataSet`, `changed` counter, `DateTime.Now`.
- **Global In-Memory State (`LegacyState`)**: `RunNow()` writes `LastBatchRun`, `BatchExecutions`, `LastBatchMessage` on success; `LastBatchMessage` on error — data exchanged: status strings and timestamp.
- **Batch Monitoring UI (`Batch.aspx`)**: Reads `LegacyState` fields for display; calls `RunNow()` on manual trigger — data exchanged: status string, execution count, timestamp, customer flags.

### External

- None. The batch engine has no outbound calls to external systems, APIs, or message queues.

---

## 7. Configuration

| Property | Source | Purpose | Default | Floor |
|---|---|---|---|---|
| `BatchIntervalSeconds` | `web.config` → `<appSettings>` | Period (in seconds) between timer ticks | `45` s (when key is absent or unparseable) | `10` s (enforced in code) |

**Current value in repository**: `15` seconds (`web.config` line 4).

The `_running` flag has no configurable value — it is always initialised to `0` (unlocked) at static class load time.

---

## 8. Summary

- **Components**: 1 static backend class (`NightlyBatch`) with 4 methods; 1 frontend page (`Batch.aspx` / `Batch.aspx.cs`); 1 in-memory state container (`LegacyState`).
- **Endpoints**: 0 HTTP endpoints. `RunNow()` is the sole public execution entry point, invoked by the timer callback and the ASP.NET Web Forms postback.
- **Business Rules**: 3 — (1) single-instance guard via `Interlocked.Exchange`; (2) minimum interval floor of 10 s; (3) guard against double `Start()` call.
- **Integrations**: 5 internal (`Global.asax`, Risk Score submodule, Audit submodule, `LegacyState`, `Batch.aspx`); 0 external.

---
**Note**: Document kept under 1500 lines. All functional information documented as-is from the codebase — no inferred or speculative content.
