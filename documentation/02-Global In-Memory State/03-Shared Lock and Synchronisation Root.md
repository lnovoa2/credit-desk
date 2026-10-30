# Functional Specification: Shared Lock and Synchronisation Root

**Parent Module**: Global In-Memory State | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Provides the single process-wide lock object (`SyncRoot`) that coordinates all concurrent read and write access to the shared static state in `LegacyState`. It is the foundational synchronisation primitive for the entire in-memory state module.
- **Key Functionality**:
  - Exposes one `public static readonly object SyncRoot` used as the monitor target for all `lock()` statements in `LegacyState`
  - Guarantees that `SetFlag` and `GetFlag` operations on `CustomerFlags` are mutually exclusive
  - Prevents torn reads and writes in a multi-threaded ASP.NET request-handling environment
  - Serves as the single coordination point so callers do not need to maintain independent lock objects
  - Declared `readonly` to prevent accidental replacement of the lock object at runtime
- **Users**: Internal — called exclusively by backend components (`NightlyBatch`, `CreditDecisionService`); no direct user interaction

---

## 2. Business Logic

### Key Processes

**Exclusive Access to Shared State**: Any code that reads or writes a mutable field in `LegacyState` must acquire `SyncRoot` before doing so.
1. Caller enters a `lock(LegacyState.SyncRoot)` block
2. CLR acquires the monitor on `SyncRoot`; competing threads block until it is released
3. Caller performs the read or write operation on the shared field
4. `lock` block exits; CLR releases the monitor

**Auto-Block Flag Write (NightlyBatch path)**:
1. `NightlyBatch.RunNow()` iterates over all customers
2. For each customer with `OverdueDays >= 90` and not yet blocked, calls `LegacyState.SetFlag(customerId, "AUTO_BLOCKED")`
3. `SetFlag` acquires `SyncRoot`, writes to `CustomerFlags[customerId]`, releases `SyncRoot`
4. Customer record in the `DataSet` is also updated (`Blocked = true`) and persisted

**Auto-Block Flag Read (CreditDecisionService path)**:
1. `CreditDecisionService.Evaluate()` is invoked for an incoming order
2. Calls `LegacyState.GetFlag(customerId)`
3. `GetFlag` acquires `SyncRoot`, reads `CustomerFlags[customerId]`, releases `SyncRoot`
4. Returned flag value drives downstream blocking rules

### Critical Validations

| Field | Validation | Behaviour |
|-------|------------|-----------|
| `SyncRoot` | Declared `readonly` — cannot be reassigned | Compile-time and runtime guarantee that the lock identity is stable |
| `customerId` key | `Hashtable` returns `null` for missing keys | `GetFlag` converts `null` to `""` (empty string) to avoid null-reference errors |

### Known Risk: Missing Lock on Batch Counter Fields

`NightlyBatch` updates `LastBatchRun`, `LastBatchMessage`, and `BatchExecutions` **without** acquiring `SyncRoot`. Given that `System.Threading.Timer` is single-fire and `Interlocked.Exchange` prevents overlapping `RunNow()` executions, a race in practice is unlikely — but the contract is not formally enforced. This is a documented migration risk.

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| `LegacyState.SyncRoot` (`LegacyState.cs:6`) | Process-wide lock object | Declared `readonly object`; used as target of all `lock()` statements in `LegacyState` |
| `LegacyState.SetFlag` (`LegacyState.cs:12–18`) | Thread-safe flag write | Acquires `SyncRoot`, writes `CustomerFlags[customerId] = value`, releases lock |
| `LegacyState.GetFlag` (`LegacyState.cs:20–27`) | Thread-safe flag read | Acquires `SyncRoot`, reads `CustomerFlags[customerId]`, returns `""` for missing keys |
| `NightlyBatch.RunNow` (`NightlyBatch.cs:26–79`) | Batch processor (consumer of SyncRoot via SetFlag) | Iterates customers, calls `SetFlag` for auto-blocked customers, updates batch counter fields directly |
| `CreditDecisionService.Evaluate` (`CreditDecisionService.cs:17–83`) | Credit decision evaluator (consumer of SyncRoot via GetFlag) | Calls `GetFlag` to check `AUTO_BLOCKED` status before applying approval rules |

### Frontend

No frontend components interact directly with `SyncRoot`. The effect of synchronisation is observable indirectly through the credit decision outcome displayed on `OrderEntry.aspx` and the batch status shown on `Batch.aspx`.

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| `LegacyState.CustomerFlags` (in-memory `Hashtable`) | Per-customer runtime flag store | Key: `customerId` (string); Value: flag string (e.g. `"AUTO_BLOCKED"`) |

No database tables are involved; `SyncRoot` governs access to in-memory state only.

---

## 4. APIs and Endpoints

This submodule exposes no HTTP endpoints. Access is entirely internal, through direct C# static method calls.

### Internal API Surface

| Method | Signature | Purpose |
|--------|-----------|---------|
| `SetFlag` | `void SetFlag(string customerId, string value)` | Writes a flag for a customer; acquires `SyncRoot` |
| `GetFlag` | `string GetFlag(string customerId)` | Reads a flag for a customer; acquires `SyncRoot`; returns `""` if not set |

**Usage example**:
```csharp
// Write (NightlyBatch)
LegacyState.SetFlag("42", "AUTO_BLOCKED");

// Read (CreditDecisionService)
string flag = LegacyState.GetFlag("42");
// flag == "AUTO_BLOCKED"  →  customer is auto-blocked
// flag == ""              →  no flag set
```

---

## 5. Data Flows

### Main Flow: Auto-Block via Nightly Batch

```
Timer tick / Manual trigger
    → NightlyBatch.RunNow()
        → LegacyRepository.Load() (full DataSet)
        → foreach Customer: OverdueDays >= 90?
            → YES: LegacyState.SetFlag(id, "AUTO_BLOCKED")  [lock acquired/released]
                   DataRow["Blocked"] = true
        → LegacyRepository.Save(ds)
        → LegacyState.LastBatchRun / LastBatchMessage / BatchExecutions updated (no lock)
```

**Steps**:
1. Timer fires `Tick` → `RunNow()` (re-entrant guard via `Interlocked.Exchange`)
2. Full customer table loaded into memory
3. Each customer evaluated; qualifying customers flagged via `SetFlag` (thread-safe)
4. Changed records persisted to SQLite
5. Batch counters updated in-memory (no lock — known risk)

### Main Flow: Credit Decision Reads the Flag

```
HTTP Request → OrderEntry.aspx.cs
    → CreditDecisionService.Evaluate(ds, customer, amount, method, priority)
        → LegacyState.GetFlag(customerId)  [lock acquired/released]
            → flag == "AUTO_BLOCKED"?
                → Contributes to BLOCKED outcome (via Blocked field already set in DB)
```

**Steps**:
1. User submits order on `OrderEntry.aspx`
2. `CreditDecisionService.Evaluate` is called
3. `GetFlag` acquires `SyncRoot`, retrieves flag, releases lock
4. Blocking rules check `customer["Blocked"]` (DB-sourced); `AUTO_BLOCKED` flag in `CustomerFlags` is the in-memory mirror
5. Decision outcome (`APPROVED` / `REVIEW` / `BLOCKED`) returned and persisted

### Error Handling

| Error | Cause | Behaviour |
|-------|-------|-----------|
| Null key in `CustomerFlags` | Customer was never flagged | `GetFlag` returns `""` — no exception |
| `SyncRoot` contention | Two threads attempt simultaneous access | CLR blocks the second thread until the first releases the lock — no data corruption |
| `RunNow` called while already running | Concurrent manual trigger + timer tick | `Interlocked.Exchange` returns 1; second call returns `"Batch already running"` immediately |
| Exception inside `RunNow` | Any repository or processing error | Caught in `catch`; `LastBatchMessage` set to `"ERROR: <message>"`; `_running` reset in `finally` |

---

## 6. Integrations

### Internal

- **Per-Customer Runtime Flag Store** (`LegacyState.CustomerFlags`): `SyncRoot` is the lock that protects all reads and writes to this `Hashtable` — the two submodules are inseparable at runtime.
- **Batch Execution State Counters** (`LastBatchRun`, `LastBatchMessage`, `BatchExecutions`): `SyncRoot` is available as the intended lock for these fields, though existing code does not use it — documented gap.
- **NightlyBatch** (`NightlyBatch.cs`): Calls `SetFlag` (acquires `SyncRoot`) when auto-blocking customers; updates batch counter fields directly (no lock).
- **CreditDecisionService** (`CreditDecisionService.cs`): Calls `GetFlag` (acquires `SyncRoot`) to retrieve `AUTO_BLOCKED` status before evaluating order.

### External

No external system integrations. `SyncRoot` is a purely in-process primitive.

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| *(none)* | `SyncRoot` is a plain CLR `object`; it requires no external configuration | N/A |

The only relevant configuration for downstream consumers is `HardBlockOverdueDays` (`web.config`), which determines the threshold at which `NightlyBatch` triggers the `SetFlag` call — but that configuration belongs to the `NightlyBatch` and `CreditDecisionService` modules.

---

## 8. Summary

- **Components**: 5 backend (SyncRoot field, SetFlag, GetFlag, NightlyBatch.RunNow, CreditDecisionService.Evaluate), 0 frontend, 1 in-memory data structure (CustomerFlags)
- **Endpoints**: 0 HTTP endpoints
- **Business rules**: 1 core rule (all mutations/reads of LegacyState must be wrapped in `lock(SyncRoot)`); 1 known gap (batch counter fields not locked)
- **Integrations**: 4 internal (Per-Customer Flag Store, Batch State Counters, NightlyBatch, CreditDecisionService), 0 external

---
**Note**: Document kept under 1500 lines. All content derived strictly from existing codebase analysis.
